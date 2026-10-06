# -*- coding: utf-8 -*-
"""
ZCANPRO 脚本 — 设置 Qi 芯片功率

写 DID 0x210D（uint16 LE, 单位 mW）：
  0x01 = 5W (500mW), 0x02 = 10W (1000mW), 0x03 = 15W (1500mW)

需要：扩展会话（10 03）+ SecurityAccess
固件门禁（can_protocol.c handle_write_data_by_id 的 DID_POWER_LIMIT case）：
写 0x210D DID_POWER_LIMIT 要求 current_session==SESSION_EXTENDED 且
security_unlocked，否则 NRC 0x22/0x33

运行前先探测运行侧（唤醒收敛 + 正证据判定）：设备在 Boot safe mode 或
无应答（已排除 Standby）时直接退出，绝不猜测继续。

用法: ZCANPRO → 高级功能 → 扩展脚本 → 打开本文件
      修改下方 POWER_MW 值后运行
"""

import os
import sys
import time

try:
    import zcanpro
except ImportError:
    zcanpro = None

# ======== 用户配置 ========
def _find_repo_root(start):
    """向上探测仓库根目录（含 docs/keys 的祖先目录），不写死目录层级假设。"""
    d = os.path.abspath(start)
    while True:
        if os.path.isdir(os.path.join(d, "docs", "keys")):
            return d
        parent = os.path.dirname(d)
        if parent == d:
            return os.path.abspath(start)
        d = parent


_TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = _find_repo_root(_TOOLS_DIR)
PRIVATE_KEY_PATH = os.path.join(REPO_ROOT, "docs", "keys", "private.pem")

# 功率档位 (uint16 LE, mW)
POWER_MW = 1500  # 500=5W, 1000=10W, 1500=15W

# ======== UDS 常量 ========
UDS_REQ_ID  = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

SID_DSC  = 0x10
SID_SA   = 0x27
SID_WDBI = 0x2E
SID_RDBI = 0x22   # 运行侧探测：22 2113
SID_RD   = 0x34   # 运行侧探测：仅 APP 实现，Boot 不应答
SID_TP   = 0x3E   # TesterPresent：wake_bus 唤醒 burst（3E 80 suppress）；对齐 charge_start.py:64
SID_NRC  = 0x7F
SID_PR   = 0x40

NRC_RCRRP = 0x78
NRC_EXCEEDED_ATTEMPTS   = 0x36  # 27 02 验签失败次数超限当次应答（fail_count≥3，固件随即锁定约30s）
NRC_REQUIRED_TIME_DELAY = 0x37  # 锁定期内 27 01 的应答（requiredTimeDelay）；锁定期内
                                # 27 02 因固件已清 seed/签名缓冲回 0x24，非 0x37
NRC_CONDITIONS_NOT_CORRECT = 0x22  # 固件写门禁：会话不满足（S3 超时后会话回 default 的典型表现）
NRC_SECURITY_ACCESS_DENIED = 0x33  # 固件写门禁：security_unlocked=0（S3 超时/会话切换被清）

# ======== S3 会话超时防护参数（三脚本同构）========
# 固件：SESSION_TIMEOUT_MS=5000（can_protocol.h SESSION_TIMEOUT_MS）；UDS 交换间隙>5s 时
# isotp_message_received / can_protocol_poll 会把会话回 default+清 security
# （can_protocol.c isotp_message_received / can_protocol_poll 会话超时回落），后续 2E 写撞 NRC 0x22/0x33。
# 固件验证结论：3E 80（suppress）与 3E 00 均刷新 S3 计时——uds_process_message
# 派发前对任何诊断请求统一刷新 last_tester_present_tick（can_protocol.c uds_process_message），
# handle_tester_present 对 suppress 帧同样刷新计时且不回响应（can_protocol.c handle_tester_present）→
# keepalive 优先 3E 80（无响应帧干扰，ZCANPRO suppress 请求立即返回）。
S3_KEEPALIVE_INTERVAL_S = 3.0      # keepalive 周期：3s < 5s 超时窗，留 2s 余量
S3_SIGN_GAP_GUARD_S     = 3.0      # 签名耗时超过该值：先发 3E 再进 27 03 分片
SA_LOCKOUT_WAIT_S       = 31.0     # SA 锁定等待总时长（与原 sleep(31) 一致）


class UdsNrcError(RuntimeError):
    """带 NRC 码的 UDS 异常；SecurityAccess 重试分支按 e.nrc 判别设备锁定等场景。"""

    def __init__(self, sid, nrc):
        RuntimeError.__init__(self, "NRC SID=0x%02X NRC=0x%02X" % (sid, nrc))
        self.sid = sid
        self.nrc = nrc


DID_POWER_LIMIT = 0x210D
SA_SIG_CHUNK = 4

stopTask = False


def z_notify(type, obj):
    global stopTask
    if type == "stop":
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


def _to_list(b):
    return list(b) if isinstance(b, (list, bytes)) else [ord(c) for c in b]


def _to_bytes(seq):
    if isinstance(seq, bytes):
        return seq
    return bytes(seq) if sys.version_info[0] >= 3 else "".join(chr(x & 0xFF) for x in seq)


# ======== ECDSA 签名 (P-256) ========
_P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
_N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
_GX = 0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296
_GY = 0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5


def _inv(x, m):
    return pow(x % m, -1, m) if sys.version_info[0] >= 3 else pow(x % m, m - 2, m)


def _jp_double(x, y, z):
    if z == 0 or y == 0:
        return 0, 0, 0
    ysq = (y * y) % _P
    s = (4 * x * ysq) % _P
    m = (3 * x * x + (_P - 3) * pow(z, 4, _P)) % _P
    return (m * m - 2 * s) % _P, (m * (s - (m * m - 2 * s) % _P) - 8 * pow(ysq, 2, _P)) % _P, (2 * y * z) % _P


def _jp_add(x1, y1, z1, x2, y2, z2):
    if z1 == 0:
        return x2, y2, z2
    if z2 == 0:
        return x1, y1, z1
    z1z1, z2z2 = pow(z1, 2, _P), pow(z2, 2, _P)
    u1, u2 = x1 * z2z2 % _P, x2 * z1z1 % _P
    s1, s2 = y1 * z2 % _P * z2z2 % _P, y2 * z1 % _P * z1z1 % _P
    if u1 == u2:
        return _jp_double(x1, y1, z1) if (s1 + s2) % _P != 0 else (0, 0, 0)
    h = (u2 - u1) % _P
    r = (s2 - s1) % _P
    h2 = h * h % _P
    h3 = h * h2 % _P
    nx = (r * r - h3 - 2 * u1 * h2) % _P
    ny = (r * (u1 * h2 - nx) - s1 * h3) % _P
    return nx, ny, h * z1 % _P * z2 % _P


def _jp_mul(k, x, y):
    rx, ry, rz = 0, 0, 0
    sx, sy, sz = x, y, 1
    while k > 0:
        if k & 1:
            rx, ry, rz = _jp_add(rx, ry, rz, sx, sy, sz)
        sx, sy, sz = _jp_double(sx, sy, sz)
        k >>= 1
    if rz == 0:
        return 0, 0
    zinv = _inv(rz, _P)
    z2 = zinv * zinv % _P
    return rx * z2 % _P, ry * z2 % _P * zinv % _P


def ecdsa_sign_msg(priv, msg):
    import hashlib
    h = hashlib.sha256(msg).digest()
    z = int.from_bytes(h, "big") % _N
    while True:
        k = int.from_bytes(os.urandom(32), "big") % _N
        if k == 0:
            continue
        x, _ = _jp_mul(k, _GX, _GY)
        r = x % _N
        if r == 0:
            continue
        s = _inv(k, _N) * (z + r * priv) % _N
        if s == 0:
            continue
        return r.to_bytes(32, "big") + s.to_bytes(32, "big")


def _collect_octet32(buf, out, start, end):
    i = start
    while i < end:
        tag = buf[i]
        i += 1
        if i >= end:
            break
        first = buf[i]
        i += 1
        ln = first if first < 0x80 else 0
        if first >= 0x80:
            n = first & 0x7F
            if 0 < n <= 4 and i + n <= end:
                ln = 0
                for _ in range(n):
                    ln = (ln << 8) | buf[i]
                    i += 1
            else:
                break
        if i + ln > end:
            break
        if tag == 0x04 and ln == 32:
            out.append(buf[i:i + 32])
        elif tag in (0x30, 0x31, 0xA0, 0xA1):
            _collect_octet32(buf, out, i, i + ln)
        i += ln


def load_ec_private_key(path):
    raw = open(path, "rb").read()
    if len(raw) == 32:
        priv = int.from_bytes(raw, "big")
        if 0 < priv < _N:
            return priv
    text = raw.decode("ascii", "ignore")
    if "BEGIN" in text:
        lines = [l.strip() for l in text.splitlines() if l.strip() and "BEGIN" not in l and "END" not in l]
        import base64
        der = base64.b64decode("".join(lines))
    else:
        der = raw
    cands = []
    _collect_octet32(der, cands, 0, len(der))
    for key in cands:
        if len(key) == 32:
            priv = int.from_bytes(key, "big")
            if 0 < priv < _N:
                return priv
    raise ValueError("无法解析私钥: " + path)


# ======== UDS 通信 ========

def uds_init():
    zcanpro.uds_init({
        "response_timeout_ms": 3000, "use_canfd": 0, "canfd_brs": 0,
        "trans_ver": 0, "fill_byte": 0xCC, "frame_type": 1,
        "trans_stmin_valid": 1, "trans_stmin": 1, "enhanced_timeout_ms": 30000,
    })


def uds_req(bus_id, sid, payload, suppress=0, wait_pending_s=0):
    if stopTask:
        raise RuntimeError("用户停止")
    req = {
        "src_addr": UDS_REQ_ID, "dst_addr": UDS_RESP_ID,
        "suppress_response": 1 if suppress else 0, "sid": sid, "data": list(payload),
    }
    t_end = time.time() + float(wait_pending_s)
    logged = False
    while True:
        if stopTask:
            raise RuntimeError("用户停止")
        if not logged:
            _log("[Tx] %02X %s" % (sid, _hex(payload[:20])))
            logged = True
        resp = zcanpro.uds_request(bus_id, req)
        if suppress:
            return None
        data = list((resp or {}).get("data") or [])
        if data:
            _log("[Rx] " + _hex(data[:24]))
        if len(data) >= 3 and data[0] == SID_NRC:
            if data[2] == NRC_RCRRP:
                if wait_pending_s <= 0 or time.time() >= t_end:
                    raise RuntimeError("NRC 0x78 超时")
                _log("NRC 0x78，等待中...")
                time.sleep(1.0)
                continue
            raise UdsNrcError(data[1], data[2])
        if not resp or not resp.get("result"):
            raise RuntimeError("无应答")
        if data[0] != (sid + SID_PR):
            raise RuntimeError("非正响应: " + _hex(data))
        return data


def _sa_fetch_seed(bus_id):
    """27 01 取 seed。固件自 d64e8c2 起 seed 为 32 字节（67 01 + 32B）。
    固件每次 27 01 都刷新 seed 并清空签名缓冲。"""
    rx = uds_req(bus_id, SID_SA, [0x01])
    if len(rx) < 34:
        raise RuntimeError("seed 响应过短: %d 字节, 期望 67 01 + 32 字节 seed（≥34）" % len(rx))
    return rx


def _sa_send_sig(bus_id, sig):
    """27 03 分片发送 64 字节签名：4 字节/帧 × 16 帧，blockSeq 0x01 起递增。"""
    sig = _to_list(sig)
    if len(sig) != 64:
        raise RuntimeError("ECDSA 签名须 64 字节")
    seq, off = 1, 0
    while off < 64:
        piece = sig[off:off + SA_SIG_CHUNK]
        uds_req(bus_id, SID_SA, [0x03, seq] + piece)
        off += len(piece)
        seq += 1
    _log("27 03 已送 64 字节 / %d 帧" % (seq - 1))
    time.sleep(0.15)


def send_security_key(bus_id, priv):
    """SecurityAccess 解锁：每次尝试都是完整流程——
    27 01 取 32 字节 seed（全 0 = 已解锁，直接返回正响应）→
    ecdsa_sign_msg(priv, seed) 重签 → 重发 16 帧 27 03 分片 → 27 02 验签。

    固件每次 27 01 都刷新 seed 并清空签名缓冲，27 02 失败后只重发 27 02
    或沿用旧 seed 签名必失败，故重试必须完整重做。最多 5 次完整尝试；
    NRC 0x36/0x37 = 设备 SecurityAccess 锁定（fail_count≥3，约 30s）：
    0x36 是 27 02 验签失败超限当次的应答；0x37 是锁定期内 27 01 的应答
    （requiredTimeDelay）。锁定期内的 27 02 固件因验签失败已清
    g_seed_generated，先撞序检查回 NRC 0x24（can_protocol.c handle_security_access
    的 g_seed_generated 序检查），不是 0x37——脚本每轮先发 27 01，锁定仍由 0x37 捕获，重试逻辑
    不变：两者都日志明确提示并经 _s3_keepalive_wait（每 3s 发 3E 80 suppress
    刷新 S3 计时，总等待 31s，防等待期会话超时）后继续完整流程。
    """
    last = None
    for attempt in range(1, 6):
        if stopTask:
            raise RuntimeError("用户停止")
        try:
            _log("SecurityAccess 第 %d/5 次：27 01 → 重签 → 27 03 → 27 02" % attempt)
            rx = _sa_fetch_seed(bus_id)
            seed = _to_bytes(rx[2:34])
            if seed == b"\x00" * 32:
                _log("27 01 seed=0（32 字节全 0），已解锁，直接返回正响应")
                return rx
            _log("seed(32B) " + _hex(rx[2:34]))
            # S3 防护：ecdsa_sign_msg 为 Python 纯软件实现，在 ZCANPRO 解释器
            # 可能耗时秒级；签名期间无总线流量，距上次 UDS 交换（27 01 应答）
            # 超过阈值则先发 3E 80 刷新 S3 计时再进 27 03 分片（>5s 时固件
            # poll 已将会话回 default，3E 救不回，最终由写步 NRC 0x22/0x33
            # 兜底 _wdbi_with_s3_guard 恢复）
            t_sign = time.time()
            sig = ecdsa_sign_msg(priv, seed)
            sign_gap = time.time() - t_sign
            if sign_gap > S3_SIGN_GAP_GUARD_S:
                _log("ECDSA 签名耗时 %.1fs（>%.1fs）：先发 3E 80 刷新 S3 计时再进 27 03 分片"
                     % (sign_gap, S3_SIGN_GAP_GUARD_S))
                uds_try(bus_id, SID_TP, [0x80], suppress=1)
            _sa_send_sig(bus_id, sig)
            return uds_req(bus_id, SID_SA, [0x02], wait_pending_s=45)
        except UdsNrcError as e:
            last = e
            if e.nrc in (NRC_EXCEEDED_ATTEMPTS, NRC_REQUIRED_TIME_DELAY):
                _log("NRC 0x%02X：设备 SecurityAccess 锁定（fail_count≥3，固件锁定约30s），S3 keepalive 等待 %.0fs 后完整重试" % (e.nrc, SA_LOCKOUT_WAIT_S))
                _s3_keepalive_wait(bus_id)
                continue
            _log("SecurityAccess 第 %d/5 次失败: %s（重试将重新取 seed 重签重发分片）" % (attempt, e))
            time.sleep(0.5)
        except RuntimeError as e:
            last = e
            _log("SecurityAccess 第 %d/5 次失败: %s" % (attempt, e))
            time.sleep(0.5)
    raise last


def uds_try(bus_id, sid, payload, suppress=0):
    try:
        return uds_req(bus_id, sid, payload, suppress=suppress)
    except Exception as e:
        _log("可忽略: " + str(e))
        return None


def _s3_keepalive_wait(bus_id, total_s=SA_LOCKOUT_WAIT_S, interval_s=S3_KEEPALIVE_INTERVAL_S):
    """S3 会话超时防护：SA 锁定等待期间周期发送 3E 80 keepalive。

    固件验证（can_protocol.c）：uds_process_message 对任何诊断请求（含
    3E suppress 帧）在派发前刷新 last_tester_present_tick（can_protocol.c uds_process_message）；
    handle_tester_present 对 suppress 帧同样刷新计时且不回响应（can_protocol.c handle_tester_present）
    → 3E 80 与 3E 00 同样续期 S3，优先 3E 80（无响应帧干扰，库立即返回）。
    周期 3s < SESSION_TIMEOUT_MS 5s（can_protocol.h SESSION_TIMEOUT_MS）；总等待时长与原
    sleep(31) 一致。走既有 uds_try 通道，不碰探测/raw 路径；keepalive
    失败只记录，不中断等待。
    """
    t_end = time.time() + float(total_s)
    sent = 0
    _log("S3 keepalive 等待 %.0fs（每 %.0fs 发 3E 80 suppress，防会话超时回 default+清 security）"
         % (total_s, interval_s))
    while True:
        remain = t_end - time.time()
        if remain <= 0:
            break
        time.sleep(interval_s if remain > interval_s else remain)
        if stopTask:
            raise RuntimeError("用户停止")
        uds_try(bus_id, SID_TP, [0x80], suppress=1)
        sent += 1
    _log("S3 keepalive 等待结束：%.0fs 内共发 %d 次 3E 80" % (total_s, sent))


def _wdbi_with_s3_guard(bus_id, payload, priv, wait_pending_s=0):
    """2E 写步 S3 超时兜底（三脚本同构）：写收到 NRC 0x22/0x33 →
    重发本脚本会话控制 10 03 + 既有 send_security_key 完整重解锁 + 重试写一次。

    固件门禁（can_protocol.c handle_write_data_by_id 的 DID_POWER_LIMIT case）：写 0x210D 要求
    SESSION_EXTENDED+security_unlocked——NRC 0x22=会话不满足（S3 超时回
    default 的典型表现），0x33=安全态被清。0x210D 值域（非 500/1000/1500）回 0x31、
    长度不足回 0x13（同 case 内校验），不会误入本兜底。会话字节与本脚本业务链
    一致（10 03 扩展会话，blocking#2 修复后）。日志注明「S3 超时恢复」。
    """
    try:
        uds_req(bus_id, SID_WDBI, payload, wait_pending_s=wait_pending_s)
        return
    except UdsNrcError as e:
        if e.nrc not in (NRC_CONDITIONS_NOT_CORRECT, NRC_SECURITY_ACCESS_DENIED):
            raise
        _log("2E 写 NRC 0x%02X：疑似 S3 会话超时（会话回 default / 安全态被清）——"
             "S3 超时恢复：重发 10 03 扩展会话 + 完整重解锁 + 重试写一次" % e.nrc)
    uds_req(bus_id, SID_DSC, [0x03])
    send_security_key(bus_id, priv)
    uds_req(bus_id, SID_WDBI, payload, wait_pending_s=wait_pending_s)
    _log("S3 超时恢复：会话+安全态重建后重试写成功")


# ======== 运行侧探测 / Standby 唤醒收敛（判定只按正证据）========
# 与 zcanpro_charge_start.py probe_location 同构（不在本文件展开固件依据，
# 文件:行号取证见提交报告）：绝不按 NRC 值分叉定位——0x34/22 2113 的任何
# 应答（正响应或任意 NRC，且非 0xFE 标记）= 在 APP；22 2113 应答中
# 62 21 13 后字节为 0xFE = Boot safe mode（APP 的 DID 0x2113 也正响应，
# slot 字节单 App 架构恒 0x00，永不 0xFE）；无应答 ≠ 不在 APP，先 Standby 唤醒
# 收敛（3E 80 burst）+ 释放 UDS 通道监听 0x18FF260D 生命周期帧再判。

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
    5: "已废弃（ECDSA 验签已于 2026-09-24 移除，boot_verify 不再产生 5/6）",
    6: "已废弃（ECDSA 验签已于 2026-09-24 移除，boot_verify 不再产生 5/6）",
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
                raise RuntimeError("用户停止")
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
        raise RuntimeError("用户停止")
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
        raise RuntimeError("用户停止")
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
            raise RuntimeError("用户停止")
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


# ======== 主流程 ========

def run(bus_id):
    # stopTask 在本函数只读不写，无需 global 声明（pyflakes 清零；预存在死声明）
    power_name = {500: "5W", 1000: "10W", 1500: "15W"}
    if POWER_MW not in power_name:
        raise RuntimeError("无效功率: %d mW (可选 500/1000/1500)" % POWER_MW)

    _log("======== 设置 Qi 功率: %s ========" % power_name[POWER_MW])

    if not os.path.isfile(PRIVATE_KEY_PATH):
        raise RuntimeError("找不到私钥: " + PRIVATE_KEY_PATH)
    priv = load_ec_private_key(PRIVATE_KEY_PATH)

    uds_init()

    # Step 0: 运行侧探测（唤醒收敛 + 正证据判定；与 zcanpro_charge_start.py 同构）
    _log("---- Step 0: 探测运行侧（唤醒收敛 + 正证据判定）----")
    verdict, detail = probe_location(bus_id)
    _log("[判定结论] %s（依据: %s）" % (VERDICT_CN[verdict], detail))
    if verdict == "BOOT_SM":
        raise RuntimeError("设备在 Boot safe mode，功能测试需 APP——%s。"
                           "请先用 merge_prod_bin 烧录器重刷或确认 APP 槽有效后再试" % detail)
    if verdict == "UNKNOWN":
        raise RuntimeError("设备无应答（已排除 Standby），无法确定运行侧。"
                           "取证见上方日志（burst 轮次 / 监听时长 / 收帧明细）")

    # 1. 扩展会话（blocking#2 修复）：固件门禁 handle_write_data_by_id 的
    #    DID_POWER_LIMIT case——写 0x210D 要求 SESSION_EXTENDED+security_unlocked——
    #    10 02 编程会话写此 DID 必撞 NRC 0x22；10 02 另有副作用
    #    board_5v_set(0)（can_protocol.c handle_diag_session_ctrl，编程会话关 5V）。顺序保持
    #    会话→SA→写：session_switch（can_protocol.c session_switch）默认→非默认不清 security，
    #    重复发相同非默认会话也不清，SA 在会话切换之后不受影响。
    _log("---- 进入扩展会话（10 03）----")
    uds_req(bus_id, SID_DSC, [0x03])

    # 2. 安全解锁（seed 32 字节，d64e8c2 起；失败重试为完整重签重发流程）
    _log("---- 安全解锁 ----")
    unlocked = False
    try:
        rx = uds_req(bus_id, SID_SA, [0x01])
        if len(rx) < 34:
            raise RuntimeError("seed 响应过短: %d 字节, 期望 67 01 + 32 字节 seed（≥34）" % len(rx))
        seed = _to_bytes(rx[2:34])
        _log("seed(32B) " + _hex(rx[2:34]))
        if seed == b"\x00" * 32:
            unlocked = True
            _log("已解锁 (seed=0，32 字节全 0)")
    except UdsNrcError as e:
        if e.nrc not in (NRC_EXCEEDED_ATTEMPTS, NRC_REQUIRED_TIME_DELAY):
            raise
        _log("27 01 NRC 0x%02X：设备 SecurityAccess 锁定（fail_count≥3，约30s），S3 keepalive 等待 %.0fs 后完整解锁" % (e.nrc, SA_LOCKOUT_WAIT_S))
        _s3_keepalive_wait(bus_id)
    if not unlocked:
        _log("SecurityAccess 解锁中（每次尝试完整重做：27 01 → 重签 → 27 03 → 27 02）...")
        send_security_key(bus_id, priv)
    _log("安全解锁成功")

    # 3. 写功率 (DID 0x210D, uint16 LE mW)——经 S3 兜底包装：写撞 NRC 0x22/0x33
    #    （会话回 default/安全态被清）时重发 10 03 + 完整重解锁 + 重试一次
    _log("---- 写入功率 %d mW ----" % POWER_MW)
    _wdbi_with_s3_guard(bus_id, [0x21, 0x0D, POWER_MW & 0xFF, (POWER_MW >> 8) & 0xFF], priv)
    _log("功率已设置: %s" % power_name[POWER_MW])


def z_main():
    global stopTask
    stopTask = False
    _log("======== Qi 功率设置工具 ========")
    _log("目标功率: %d mW" % POWER_MW)
    buses = zcanpro.get_buses()
    if not buses:
        _log("请先打开 CAN 通道 (250kbps, 扩展帧)")
        return
    try:
        run(buses[0]["busID"])
    except Exception as e:
        _log("失败: " + str(e))
    finally:
        try:
            zcanpro.uds_deinit()
        except Exception:
            pass
