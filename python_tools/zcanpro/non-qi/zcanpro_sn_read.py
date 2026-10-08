# -*- coding: utf-8 -*-
"""
ZCANPRO 扩展脚本 — SN 序列号专用读取（DID 0xF18C）

新增原因：用户明确要求"写一个脚本，用于专门读取 SN 码"——用于
zcanpro_sn_write.py 写入后的 SN 验证（sn_write/charge_start/set_power
已放行 pull d770e86 重测，本脚本为写入验证配套）。

固件读门禁验证结论（2026-09-18，逐行取证，file:line）：
  - 22 F18C 默认会话即可读，无需 SecurityAccess：
    · 派发无门禁：uds_process_message 对 UDS_SID_READ_DATA_BY_ID 直接调
      handle_read_data_by_id，无会话/安全检查（can_protocol.c:1785-1787）；
    · handler 无门禁：handle_read_data_by_id 仅做长度检查后逐 DID 调
      fill_did_payload，无会话/安全分支（can_protocol.c:972-1007）；
    · 读失败唯一应答：fill_did_payload 返回 -1 → proto_send_nrc
      UDS_NRC_REQUEST_OUT_OF_RANGE = 0x31 requestOutOfRange
      （can_protocol.c:994，宏定义 can_protocol.h:97）。
    → 脚本不加载私钥、不做签名、不做会话切换（写门禁才要求
      SESSION_PROGRAMMING+security_unlocked，can_protocol.c:1051-1062）。
  - "未写入"判据（固件实况，两条路径）：
    ① NRC 0x31：Device Info 块无效/不存在（magic!="DEVI" / version 不符 /
       CRC32 失败，device_info.c:57-79），fill_did_payload case
       DID_SERIAL_NUMBER 读失败直接 return -1（can_protocol.c:687-690）；
    ② 正响应但 32 字节全 0xFF：Device Info 块存在而 SN 字段未编程——
       块的其他写入路径（如 device_info_write_pubkey）在初始化分支
       memset 0xFF 后不显式初始化 sn 字段（device_info.c:150-151），
       device_info_pad32（device_info.c:40-55）对无 '\\0' 的 0xFF 源按
       原样拷贝，应答即 32×0xFF。
  - Boot safe mode：22 F18C 命中"其他 22 DID"分支回 7F 22 11
    servicesNotSupported（boot_safe_mode.c:31 注释 / :159-165 实现）；
    仅 22 2113 回标记帧 62 21 13 FE <fail_step>（:149-157）。
    → Step 0 探测命中 BOOT_SM 标记即明确提示"无法读取 SN"并退出，
    绝不带病读取；判定不按 NRC 值分叉（qi-can-uds-ota-scripts §2）。

流程：
  0. 运行侧探测（22 2113 / 0x34 正证据判定 + Standby 唤醒收敛；
     BOOT_SM / UNKNOWN 均取证退出）
  1. 读取 SN（22 F18C，默认会话；全程无会话切换 → 无 S3 暴露，
     无需 keepalive）

输出：SN ASCII（去尾部 0x20 空格）+ hex 全量 32 字节 + 有效长度；
异常分级：未写入（NRC 0x31 / 全 0xFF 两判据）/ NRC 明细 / 无应答 /
全空格警告 / 非可打印字节警告。[判定结论] 一行明示。

结构对齐 d770e86 现行功能脚本（常量块 / UdsNrcError / probe_location /
_probe_once / wake_bus / _listen_lifecycle 同构复制，项目惯例=脚本自包含）。
zcanpro_read_app_version.py 在仓库内，但为 d770e86 前旧式 raw-first 模式
（无 probe_location/UdsNrcError/Step 0 门禁），按实现要求取 d770e86 模式；
其 DID 字符串输出格式（可打印映射 + 去尾空格）已对齐到本脚本输出段。

导入: ZCANPRO → 高级功能 → 扩展脚本 → 打开本文件
运行前: 先打开 CAN 通道 (250 kbps, Classical CAN, 扩展帧)
"""

import sys
import time

try:
    import zcanpro
except ImportError:
    zcanpro = None

# ======== UDS 常量（d770e86 三功能脚本同款常量块）========
UDS_REQ_ID = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

SID_RDBI = 0x22   # ReadDataByIdentifier：读 SN（22 F18C）与运行侧探测（22 2113）
SID_RD   = 0x34   # 运行侧探测：仅 APP 实现，Boot 无分支静默
SID_TP   = 0x3E   # TesterPresent：wake_bus 唤醒 burst（3E 80 suppress）
SID_NRC  = 0x7F
SID_PR   = 0x40

NRC_RCRRP = 0x78
NRC_REQUEST_OUT_OF_RANGE = 0x31  # 固件读路径"数据不存在"唯一应答（未写入判据①）

# NRC 明细（读路径异常分级提示用；不用于运行侧定位分叉）
NRC_DESC = {
    0x11: "servicesNotSupported——Boot safe mode 对非 2113 的 22 DID 的应答"
          "（boot_safe_mode.c:159-165）；APP 正常读路径不应出现",
    0x13: "incorrectMessageLength",
    0x22: "conditionsNotCorrect",
    0x31: "requestOutOfRange——Device Info 块无效（magic/version/CRC32 校验失败，"
          "device_info.c:57-79），fill_did_payload 返回 -1（can_protocol.c:687-690/:994）",
    0x33: "securityAccessDenied——读路径无安全门禁，出现即异常",
    0x78: "requestCorrectlyReceived-ResponsePending（ZCANPRO 库通常内部消化）",
}

DID_SERIAL_NUMBER = 0xF18C   # can_protocol.h:123；写门禁见 can_protocol.c:1051-1062


class UdsNrcError(RuntimeError):
    """带 NRC 码的 UDS 异常；读路径异常分级按 e.nrc 判别（如 0x31=未写入）。"""

    def __init__(self, sid, nrc):
        RuntimeError.__init__(self, "NRC SID=0x%02X NRC=0x%02X" % (sid, nrc))
        self.sid = sid
        self.nrc = nrc


stopTask = False


def z_notify(type, obj):
    if type == "stop":
        global stopTask
        stopTask = True


def _log(msg):
    text = str(msg)
    if zcanpro is not None:
        zcanpro.write_log(text)
    else:
        sys.stdout.write(text + "\n")
        sys.stdout.flush()


def _hex(data):
    if data is None:
        return ""
    return " ".join("%02X" % (int(b) & 0xFF) for b in data)


# ======== UDS 通信 ========

def uds_init():
    zcanpro.uds_init({
        "response_timeout_ms": 3000,
        "use_canfd": 0,
        "canfd_brs": 0,
        "trans_ver": 0,
        "fill_byte": 0xCC,
        "frame_type": 1,
        "trans_stmin_valid": 1,
        "trans_stmin": 1,
        "enhanced_timeout_ms": 30000,
    })
    _log("UDS 就绪 0x%08X / 0x%08X 扩展帧" % (UDS_REQ_ID, UDS_RESP_ID))


def uds_req(bus_id, sid, payload, suppress=0, wait_pending_s=0):
    if stopTask:
        raise RuntimeError("用户停止脚本")
    req = {
        "src_addr": UDS_REQ_ID,
        "dst_addr": UDS_RESP_ID,
        "suppress_response": 1 if suppress else 0,
        "sid": sid,
        "data": list(payload),
    }
    t_end = time.time() + float(wait_pending_s)
    logged_tx = False
    while True:
        if stopTask:
            raise RuntimeError("用户停止脚本")
        if not logged_tx:
            _log("[Tx] %02X %s" % (sid, _hex(payload[:16])))
            logged_tx = True
        resp = zcanpro.uds_request(bus_id, req)
        if suppress:
            return None
        data = list((resp or {}).get("data") or [])
        if data:
            _log("[Rx] " + _hex(data[:24]))
        if len(data) >= 3 and data[0] == SID_NRC:
            if data[2] == NRC_RCRRP:
                if wait_pending_s <= 0 or time.time() >= t_end:
                    raise RuntimeError("SID=0x%02X NRC 0x78 超时" % sid)
                _log("NRC 0x78，MCU 忙，继续等待")
                time.sleep(1.0)
                continue
            raise UdsNrcError(data[1], data[2])
        if not resp or not resp.get("result"):
            raise RuntimeError("无应答 SID=0x%02X" % sid)
        if len(data) < 1 or data[0] != (sid + SID_PR):
            raise RuntimeError("非正响应 SID=0x%02X %s" % (sid, _hex(data)))
        return data


def uds_try(bus_id, sid, payload, suppress=0):
    try:
        return uds_req(bus_id, sid, payload, suppress=suppress)
    except Exception as e:
        _log("可忽略: " + str(e))
        return None


# ======== 运行侧探测 / Standby 唤醒收敛（判定只按正证据）========
# 与 zcanpro_charge_start.py / zcanpro_sn_write.py probe_location 同构
# （固件依据文件:行号见脚本头部与提交报告）：绝不按 NRC 值分叉定位——
# 0x34/22 2113 的任何应答（正响应或任意 NRC，且非 0xFE 标记）= 在 APP；
# 22 2113 应答中 62 21 13 后字节为 0xFE = Boot safe mode（APP 的
# DID 0x2113 也正响应，slot 字节单 App 架构恒 0x00，永不 0xFE）；无应答 ≠ 不在 APP，
# 先 Standby 唤醒收敛（3E 80 burst）+ 释放 UDS 通道监听 0x18FF260D
# 生命周期帧再判。

LIFE_ANNOUNCE_ID    = 0x18FF260D
LIFE_ANNOUNCE_MAGIC = (0x01, 0x41, 0x57, 0x4B)  # AWK：Standby 唤醒标识
LIFE_BOOTUP_MAGIC   = (0x01, 0x41, 0x00)        # BOOTUP：上电/复位/Boot 跳转标识
LIFE_ABT_MAGIC      = (0x01, 0x41, 0x42, 0x54)  # ABT：Boot safe mode 心跳
SAFE_MODE_RESP_ID   = UDS_RESP_ID               # 0x18DA030D
SAFE_MODE_MARKER    = (0x62, 0x21, 0x13, 0xFE)  # Boot safe mode 应答标记（不含 ISO-TP PCI）
DID_ACTIVE_SLOT     = 0x2113
PROBE_ROUNDS        = 3
WAKE_BURST_ROUNDS   = 3      # （3E 80 ×3 + 0.2s）×3
WAKE_BURST_COUNT    = 3
WAKE_BURST_GAP_S    = 0.2
LIFE_LISTEN_S       = 2.0
VERDICT_CN = {"APP": "APP", "BOOT_SM": "Boot safe mode", "UNKNOWN": "UNKNOWN"}

FAIL_STEP_DESC = {
    0: "未执行镜像校验 / metadata 无有效状态（单 App 架构无槽选择）",
    1: "镜像 magic 校验失败",
    2: "image_length 为 0 或超出槽范围",
    3: "镜像 CRC32 校验失败",
    4: "Reset handler 不在槽内（跨槽链接镜像）",
    5: "ECDSA 公钥缺失/无效（Device Info 与内置公钥均不可用）",
    6: "ECDSA P-256 验签失败",
}


def _safe_mode_step(dat):
    """识别 Boot safe mode 应答：ISO-TP SF 05 62 21 13 FE <step> 或历史裸帧
    62 21 13 FE <step>（双格式兼容，与 zcanpro_ext_ota_auto.py 同构）。"""
    if (len(dat) >= 6 and dat[0] == 0x05 and dat[1] == 0x62 and dat[2] == 0x21
            and dat[3] == 0x13 and dat[4] == 0xFE):
        return int(dat[5])
    if (len(dat) >= 5 and dat[0] == 0x62 and dat[1] == 0x21
            and dat[2] == 0x13 and dat[3] == 0xFE):
        return int(dat[4])
    return None


def _safe_mode_detail(step):
    desc = FAIL_STEP_DESC.get(step, "未知 fail_step（Boot 固件可能早于标记帧版本）")
    return "22 2113 应答 0xFE 标记 = Boot safe mode，fail_step=%d（%s）" % (step, desc)


def _parse_can_frame(f):
    """与 zcanpro_ext_ota_auto.py 同构：兼容 dict / (id,data) 元组 / 属性对象；
    CAN ID 取 & 0x1FFFFFFF（bit31 为 ZLG 扩展帧标志）。"""
    cid = None
    dat = None
    if isinstance(f, dict):
        cid = f.get("can_id", f.get("id", f.get("ID")))
        dat = f.get("data", f.get("Data"))
    elif isinstance(f, (tuple, list)) and len(f) >= 2:
        cid, dat = f[0], f[1]
    else:
        cid = getattr(f, "can_id", getattr(f, "id", None))
        dat = getattr(f, "data", None)
    if cid is None:
        return None
    if not isinstance(dat, (list, tuple, bytes, bytearray)):
        dat = []
    try:
        return (int(cid) & 0x1FFFFFFF, [int(x) & 0xFF for x in list(dat)])
    except (TypeError, ValueError):
        return None


def _unwrap_receive(raw):
    """与 zcanpro_ext_ota_auto.py / zcanpro_read_app_version.py 同构：
    receive 常见返回 (status, [frames])。"""
    if raw is None:
        return []
    if isinstance(raw, tuple) or (isinstance(raw, list) and len(raw) == 2
                                  and not isinstance(raw[0], dict)
                                  and isinstance(raw[1], (list, tuple))):
        a, b = raw[0], raw[1]
        if isinstance(b, (list, tuple)):
            return list(b)
        if isinstance(a, (list, tuple)):
            return list(a)
    if isinstance(raw, dict) or (not isinstance(raw, (list, tuple))):
        return [raw]
    return list(raw)


def _recv_frames(bus_id):
    try:
        raw = zcanpro.receive(bus_id)
    except TypeError:
        try:
            raw = zcanpro.receive()
        except Exception:
            return []
    except Exception:
        return []
    out = []
    for f in _unwrap_receive(raw):
        p = _parse_can_frame(f)
        if p is not None:
            out.append(p)
    return out


def _lifecycle_check(cid, dat):
    """0x18FF260D 生命周期帧分类：awk / bootup / abt / shutdown / None。
    ABT（01 41 42 54）只有 Boot safe mode 发；AWK/BOOTUP 只有 APP 发。"""
    if cid != LIFE_ANNOUNCE_ID or len(dat) < 3:
        return None
    if len(dat) >= 4 and tuple(dat[:4]) == LIFE_ANNOUNCE_MAGIC:
        return "awk"
    if len(dat) >= 4 and tuple(dat[:4]) == LIFE_ABT_MAGIC:
        return "abt"
    if dat[0] == 0x01 and dat[1] == 0x41 and dat[2] == 0x00:
        return "bootup"
    if dat[0] == 0x06 and dat[1] == 0x41:
        return "shutdown"
    return None


def _listen_lifecycle(bus_id, listen_s=LIFE_LISTEN_S):
    """释放 UDS 通道后 raw 收帧监听生命周期帧（UDS 占用通道时 raw receive
    不可靠，必须先 uds_deinit，finally 恢复）。
    返回 ([(类型,data)...], [(cid,data)...全部收帧])。"""
    found = []
    all_frames = []
    try:
        zcanpro.uds_deinit()
    except Exception as e:
        _log("UDS 通道释放失败（继续监听）: " + str(e))
    try:
        t_end = time.time() + float(listen_s)
        while time.time() < t_end:
            if stopTask:
                raise RuntimeError("用户停止脚本")
            for cid, dat in _recv_frames(bus_id):
                all_frames.append((cid, dat))
                lt = _lifecycle_check(cid, dat)
                if lt is not None:
                    _log("生命周期帧 [%s] 0x%08X %s" % (lt.upper(), cid, _hex(dat[:8])))
                    found.append((lt, dat))
            time.sleep(0.02)
    finally:
        try:
            uds_init()
        except Exception as e:
            _log("UDS 通道恢复失败: " + str(e))
    return found, all_frames


def wake_bus(bus_id, listen_s=LIFE_LISTEN_S):
    """Standby 唤醒收敛：（3E 80 suppress ×3 + 0.2s）×3 后释放 UDS 通道
    监听生命周期帧。返回 (证据列表, 全部收帧)。证据 (类型,data)：
    AWK/BOOTUP=APP 启动正证据；ABT=Boot safe mode 正证据（500ms 心跳，
    无需再发 UDS 探测）。"""
    if stopTask:
        raise RuntimeError("用户停止脚本")
    for _b in range(WAKE_BURST_ROUNDS):
        for _i in range(WAKE_BURST_COUNT):
            uds_try(bus_id, SID_TP, [0x80], suppress=1)
            time.sleep(WAKE_BURST_GAP_S)
    return _listen_lifecycle(bus_id, listen_s=listen_s)


def _probe_once(bus_id, sid, payload):
    """单次探测：直接调库并按【应答字节】分类，绝不按 NRC 值推断运行侧。
    返回 (tag, detail)：
      ("silent",  None)  无应答——不能据此定位（Standby/Boot 静默/链路问题）
      ("boot_sm", str)   22 2113 应答带 0xFE 标记 = Boot safe mode
      ("app",     str)   有应答且非 Boot safe mode 标记 = 在 APP（含任意 NRC）
      ("other",   data)  未识别应答字节，仅取证，不参与定位
    """
    if stopTask:
        raise RuntimeError("用户停止脚本")
    req = {"src_addr": UDS_REQ_ID, "dst_addr": UDS_RESP_ID,
           "suppress_response": 0, "sid": sid, "data": list(payload)}
    _log("[Tx-probe] %02X %s" % (sid, _hex(payload[:16])))
    try:
        resp = zcanpro.uds_request(bus_id, req)
    except Exception as e:
        _log("[Rx-probe] 探测异常（按无应答处理，不据此定位）: %s" % e)
        return ("silent", None)
    data = list((resp or {}).get("data") or [])
    if data:
        _log("[Rx-probe] " + _hex(data[:24]))
    else:
        _log("[Rx-probe] 无应答 (result=%s)" % ((resp or {}).get("result"),))
    if not data:
        return ("silent", None)
    step = _safe_mode_step(data)
    if step is not None:
        return ("boot_sm", _safe_mode_detail(step))
    if len(data) >= 3 and data[0] == SID_NRC and data[1] == sid:
        # 任意 NRC = 对端 UDS 栈应答了该服务 → APP。Boot safe mode 对 22 2113
        # 只回正响应+0xFE 标记，对 0x34 不应答；仅对非 2113 的 22 DID 才回
        # 7F 22 11，而本探测的 22 恒为 DID 2113，不会命中该分支。
        return ("app", "NRC 0x%02X（对 %02X 的应答 = APP 实现该服务）" % (data[2], sid))
    if data[0] == (sid + SID_PR):
        if sid == SID_RDBI:
            return ("app", "22 2113 正响应 slot=%s（APP DID_ACTIVE_SLOT，记录字节非 0xFE）"
                    % _hex(data[3:4]))
        return ("app", "0x34 正响应 %s（APP 实现下载服务）" % _hex(data[:8]))
    # 未识别字节：UDS 响应 ID 上可能出现生命周期双发帧（can_lp_send_ident：
    # 07/03 01 41 57 4B …，APP 侧发出）→ 也是 APP 正证据
    if len(data) >= 3 and data[0] in (0x03, 0x07) and data[1] == 0x01 and data[2] == 0x41:
        return ("app", "生命周期双发帧（UDS 响应 ID）%s = APP 正证据" % _hex(data[:8]))
    return ("other", data)


def probe_location(bus_id):
    """运行侧探测：只按正证据判定，绝不按 NRC 值分叉。
    返回 (verdict, detail)，verdict ∈ ("APP", "BOOT_SM", "UNKNOWN")：
      22 2113 应答带 0xFE 标记 → BOOT_SM；22 2113 / 0x34 任何其他应答 → APP；
      无应答 → wake_bus 唤醒收敛 + 生命周期帧监听（ABT→BOOT_SM，
      AWK/BOOTUP→APP 正证据）后重试，≤3 轮；全部无证据 → UNKNOWN +
      完整取证日志（burst 轮次 / 监听时长 / 收到的所有帧）。"""
    life_evidence = []
    forensics = []
    for attempt in range(1, PROBE_ROUNDS + 1):
        if stopTask:
            raise RuntimeError("用户停止脚本")
        _log("---- 探测运行侧 第 %d/%d 轮 ----" % (attempt, PROBE_ROUNDS))
        tag, detail = _probe_once(bus_id, SID_RDBI,
                                  [(DID_ACTIVE_SLOT >> 8) & 0xFF, DID_ACTIVE_SLOT & 0xFF])
        if tag == "boot_sm":
            return ("BOOT_SM", detail)
        if tag == "app":
            return ("APP", detail)
        if tag == "other":
            _log("22 2113 未识别应答（仅取证，不参与定位）: %s" % _hex(detail))
            forensics.append(("round%d 22 2113" % attempt, detail))
        tag, detail = _probe_once(bus_id, SID_RD, [0x00])
        if tag == "boot_sm":
            return ("BOOT_SM", detail)
        if tag == "app":
            return ("APP", detail)
        if tag == "other":
            _log("0x34 未识别应答（仅取证，不参与定位）: %s" % _hex(detail))
            forensics.append(("round%d 0x34" % attempt, detail))
        # 本轮两探皆无应答 → 唤醒收敛 + 生命周期监听（Standby 首帧只当 WUP）
        _log("两探无应答 → 唤醒 burst（3E 80 ×%d + %.1fs）×%d + 生命周期监听 %.1fs"
             % (WAKE_BURST_COUNT, WAKE_BURST_GAP_S, WAKE_BURST_ROUNDS, LIFE_LISTEN_S))
        found, all_frames = wake_bus(bus_id)
        forensics.append(("round%d listen %.1fs" % (attempt, LIFE_LISTEN_S), all_frames))
        for lt, dat in found:
            life_evidence.append((lt, dat))
            if lt == "abt":
                step = dat[5] if len(dat) > 5 else None
                desc = FAIL_STEP_DESC.get(step, "未知 fail_step") if step is not None else "无 step 字节"
                return ("BOOT_SM", "生命周期 ABT 心跳 %s = Boot safe mode，fail_step=%s（%s）"
                        % (_hex(dat[:8]), step, desc))
        if found:
            _log("生命周期帧证据 %d 个（%s）→ 重试 UDS 探测"
                 % (len(found), ",".join(lt for lt, _d in found)))
        else:
            _log("监听 %.1fs 未见 0x%08X 生命周期帧" % (LIFE_LISTEN_S, LIFE_ANNOUNCE_ID))
    # 全部轮次 UDS 探测无应答
    awk_or_bootup = [e for e in life_evidence if e[0] in ("awk", "bootup")]
    if awk_or_bootup:
        lt, dat = awk_or_bootup[0]
        return ("APP", "生命周期帧 [%s] %s 正证据：APP 已启动"
                "（UDS 探测无应答，链路/会话异常；后续 UDS 步骤若失败请检查链路）"
                % (lt.upper(), _hex(dat[:8])))
    _log("探测取证：共 %d 轮，每轮唤醒 burst（3E 80 ×%d + %.1fs）×%d + 监听 %.1fs；"
         "UDS 探测与生命周期帧均无证据"
         % (PROBE_ROUNDS, WAKE_BURST_COUNT, WAKE_BURST_GAP_S,
            WAKE_BURST_ROUNDS, LIFE_LISTEN_S))
    for tag, frames in forensics:
        if isinstance(frames, list):
            _log("取证[%s] 收帧 %d 个:" % (tag, len(frames)))
            for cid, dat in frames[:40]:
                _log("  0x%08X %s" % (cid, _hex(dat[:8])))
        else:
            _log("取证[%s] %s" % (tag, _hex(frames)))
    return ("UNKNOWN", "设备无应答（已排除 Standby），无法确定运行侧")


# ======== SN 读取流程 ========

def run_sn_read(bus_id):
    _log("======== SN 码读取流程 ========")
    _log("DID: 0x%04X (DID_SERIAL_NUMBER)" % DID_SERIAL_NUMBER)
    _log("读门禁（固件已验证）：默认会话即可读，无需 SecurityAccess——"
         "脚本不加载私钥、不做签名、不做会话切换")

    uds_init()

    # Step 0: 运行侧探测（唤醒收敛 + 正证据判定；与 d770e86 三功能脚本同构）
    _log("---- Step 0: 探测运行侧（唤醒收敛 + 正证据判定）----")
    verdict, detail = probe_location(bus_id)
    _log("[判定结论] %s（依据: %s）" % (VERDICT_CN[verdict], detail))
    if verdict == "BOOT_SM":
        # Boot safe mode 无法读 SN：22 F18C 命中"其他 22 DID"分支回 7F 22 11
        # （boot_safe_mode.c:31/:159-165），脚本在此明确提示并退出，绝不带病读取
        raise RuntimeError(
            "设备处于 Boot safe mode，无法读取 SN——Boot safe mode 仅应答 3E 与 "
            "22 2113（标记帧），对 22 F18C 等其他 22 DID 固件回 7F 22 11 "
            "servicesNotSupported（boot_safe_mode.c:31/:159-165）——%s。"
            "请先用 merge_prod_bin 烧录器重刷或确认 APP 槽有效后再试" % detail)
    if verdict == "UNKNOWN":
        raise RuntimeError("设备无应答（已排除 Standby），无法确定运行侧，无法读取 SN。"
                           "取证见上方日志（burst 轮次 / 监听时长 / 收帧明细）")

    # Step 1: 读 SN——固件已验证默认会话即可（can_protocol.c:1785-1787 派发
    # 无门禁、:972-1007 handler 仅长度检查），全程无会话切换 → 无 S3 暴露，
    # 无需 keepalive（S3 超时仅作用于非默认会话）
    _log("---- Step 1: 读取 SN（22 F18C，默认会话）----")
    try:
        rx = uds_req(bus_id, SID_RDBI,
                     [(DID_SERIAL_NUMBER >> 8) & 0xFF, DID_SERIAL_NUMBER & 0xFF])
    except UdsNrcError as e:
        if e.nrc == NRC_REQUEST_OUT_OF_RANGE:
            _log("[判定结论] SN 未写入：NRC 0x31 requestOutOfRange——Device Info 块无效"
                 "（magic/version/CRC32 校验失败，device_info.c:57-79），"
                 "fill_did_payload 返回 -1（can_protocol.c:687-690）→ 22 F18C 回 7F 22 31（:994）")
            raise RuntimeError("SN 未写入（NRC 0x31）：Device Info 块无效/不存在。"
                               "请先运行 zcanpro_sn_write.py 写入 SN 后再用本脚本验证")
        _log("[判定结论] SN 读取失败：NRC 0x%02X（%s）"
             % (e.nrc, NRC_DESC.get(e.nrc, "未知 NRC，详见固件 can_protocol.h NRC 宏")))
        raise
    if len(rx) < 3 + 32:
        raise RuntimeError("SN 响应过短: %d 字节（期望 62 F1 8C + 32 字节，≥35）" % len(rx))
    if rx[1] != 0xF1 or rx[2] != 0x8C:
        raise RuntimeError("响应 DID 不符: %s（期望 62 F1 8C ...）" % _hex(rx[:3]))

    payload = [int(x) & 0xFF for x in rx[3:35]]
    _log("SN hex(32B): %s" % _hex(payload))

    # 有效长度 = 去掉尾部 0x20 空格填充后的字节数（固件写入按 32 字节空格
    # 填充，zcanpro_sn_write.py / can_protocol.c:1090-1115 写侧语义）
    eff_len = len(payload)
    while eff_len > 0 and payload[eff_len - 1] == 0x20:
        eff_len -= 1

    if all(b == 0xFF for b in payload):
        _log("SN ASCII: （空）")
        _log("有效长度: 0")
        _log("[判定结论] SN 未写入：正响应但 32 字节全 0xFF——Device Info 块存在而 "
             "SN 字段未编程（flash 擦除态；例：仅写过 pubkey 时 "
             "device_info_write_pubkey 初始化保留 sn=0xFF，device_info.c:150-151）")
        raise RuntimeError("SN 未写入（32B 全 0xFF）：Device Info 块存在但 SN 字段未编程。"
                           "请先运行 zcanpro_sn_write.py 写入 SN 后再用本脚本验证")

    sn_ascii = "".join(chr(b) if 0x20 <= b < 0x7F else "." for b in payload[:eff_len])
    _log("SN ASCII: %s" % sn_ascii)
    _log("有效长度: %d" % eff_len)

    bad = [i for i in range(eff_len) if not (0x20 <= payload[i] < 0x7F)]
    if bad:
        _log("警告: SN 含 %d 个非可打印字节（位置 %s），写入数据可能损坏"
             % (len(bad), ",".join(str(i) for i in bad[:8])))
    if eff_len == 0:
        _log("[判定结论] 警告：SN 有效长度 0（32 字节全空格）——SN 曾被写入但内容为空，请核查写入流程")
    elif bad:
        _log("[判定结论] SN 读取完成（含非可打印字节，见上方警告）")
    else:
        _log("[判定结论] SN 读取成功")


# ======== 入口 ========

def z_main():
    global stopTask
    stopTask = False
    _log("======== Qi Charger SN 读取工具 ========")
    _log("DID: 0x%04X (DID_SERIAL_NUMBER)；默认会话读，无需解锁" % DID_SERIAL_NUMBER)
    buses = zcanpro.get_buses()
    if not buses:
        _log("请先打开 CAN 通道 250kbps 扩展帧")
        return
    try:
        run_sn_read(buses[0]["busID"])
    except Exception as e:
        _log("SN 读取失败: " + str(e))
    finally:
        try:
            zcanpro.uds_deinit()
        except Exception:
            pass
