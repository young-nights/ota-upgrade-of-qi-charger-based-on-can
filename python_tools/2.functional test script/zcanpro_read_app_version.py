# -*- coding: utf-8 -*-
"""
ZCANPRO 脚本 — 读取 APP 侧版本号 DID 0xF195 / 0xF180 / 0xF193

版本数据源（2026-09-18 改造）：
  DID 0xF195 应答 = APP 固件编译时常量 SW_VERSION_STR（can_protocol.c），
  不读 OTA metadata / XATO 镜像头（镜像头 0x4C 区为保留占位，原 version
  字段已从结构定义删除，打包固定填 0x00）。应答格式不变：32 字节 ASCII 右补空格。
  本脚本期望值必须与固件编译常量一致。

流程：
  1. SIT1145 Standby 唤醒（2026-10-01）：raw 连发 3 帧 02 3E 80
     （suppress，200ms 间隔）打破 Standby → _uds_init() → 嗅探
     0x18FF260D 上的 AWK 帧(01 41 57 4B)作唤醒证据（1.5s，超时不拦截）
     → TesterPresent 0x3E 00 最多连发 8 次（覆盖 Boot→App CAN 黑窗）
  2. 原始 CAN 发 03 22 F1xx，收 ISO-TP 多帧并按 SN 组包
  3. 失败再试 zcanpro.uds_request

架构（OTA-ARCH-0920）：Boot(16KB) + App(48KB) + Backup(48KB)，单 App 无 A/B 槽位。

2026-09-21 P0-P2 修复（OTA-ARCH-0920-D3 审计）：
  - P0: wake_mcu 重试 2→8 次、间隔 0.15→0.5s，覆盖最坏 ~5s Boot→App CAN 黑窗
  - P1: raw 嗅探路径改为嗅探前重新 _uds_init()（deinit 后 receive 恒空，嗅探失效）
  - P2: 旧架构文案（旧槽位/旧 Boot 尺寸表述）更新为当前单 App 架构口径

2026-10-01 修复（Windows ZCANPRO 实测 0 帧）：
  - TX 静默失败：transmit/send 用返回值报错而非抛异常，can_send 现检查
    返回值（1/True=成功，0/False/负=失败；None/未知类型放行），失败落
    具体错误日志并换下一种发送形式重试；每次发送打 发送方式+返回值 日志。
  - Standby 唤醒不足：wake_mcu 先 raw 连发 3 帧 3E 80 打破 SIT1145
    Standby（首帧被 WUP 吃掉后 UDS 层不重发的问题），再 init + 3E 00×8。
  - 预期版本：QC_JYF_FW_1.1.1 → QC_JYF_FW_1.1.2（固件已升 1.1.2）。

receive() 实际返回 (status, [frames])，不是帧字典列表。
"""

import time

try:
    import zcanpro
except Exception:
    zcanpro = None

UDS_REQ_ID  = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

SID_RDBI = 0x22
SID_TP   = 0x3E
SID_NRC  = 0x7F
SID_PR   = 0x40

# 期望值 = 固件编译常量：SW_VERSION_STR / BOOTLOADER_VER_STR / HW_VERSION_STR
# （can_protocol.c）。镜像头不携带版本号，版本号唯一定义在固件编译常量。
DID_LIST = [
    (0xF195, "APP 软件版本", "QC_JYF_FW_1.1.2"),
    (0xF180, "Bootloader 版本", "QC_JYF_BL_1.0.0"),
    (0xF193, "硬件版本",     "QC_JYF_HW_1.1.5"),
]

stopTask = False
_tx_mode = None
_rx_logged = 0


def z_notify(type, obj):
    global stopTask
    if type == "stop":
        stopTask = True


def _log(msg):
    if zcanpro is not None:
        try:
            zcanpro.write_log(str(msg))
        except Exception:
            pass


def _hex(data):
    if data is None:
        return ""
    return " ".join("%02X" % (int(b) & 0xFF) for b in data)


def _pad8(data):
    d = [int(x) & 0xFF for x in list(data)]
    while len(d) < 8:
        d.append(0xCC)
    return d[:8]


def _uds_init():
    # 与 V1.0.0 一致：不靠 suppress；STmin=10 避免 CF 乱序
    zcanpro.uds_init({
        "response_timeout_ms": 2000, "use_canfd": 0, "canfd_brs": 0,
        "trans_ver": 0, "fill_byte": 0xCC, "frame_type": 1,
        "trans_stmin_valid": 1, "trans_stmin": 10, "enhanced_timeout_ms": 8000,
    })


def _uds_deinit():
    try:
        zcanpro.uds_deinit()
    except Exception:
        pass


def _make_frame(can_id, data):
    # ZLG：bit31=1 表示扩展帧。上次 raw 发出的是 18da0d03（无 x）标准帧，MCU 滤掉。
    cid29 = int(can_id) & 0x1FFFFFFF
    cid = cid29 | 0x80000000
    return {
        "can_id": cid,
        "id": cid,
        "is_canfd": 0,
        "canfd_brs": 0,
        "is_extend": 1,
        "is_extended": 1,
        "extend": 1,
        "extern_flag": 1,
        "is_extern": 1,
        "eff": 1,
        "id_type": 1,
        "data": _pad8(data),
    }


def _tx_ret_ok(ret):
    """transmit/send 返回值判定（宽松）。
    ZLG 惯例：1/True=成功，0/False/负数=明显失败。
    None 或未知类型（dict/str 等）不可判，放行（return None），只记日志，
    避免误杀——是否上线由日志里的 返回值 肉眼可判。"""
    if ret is None:
        return None
    if isinstance(ret, bool):
        return ret
    if isinstance(ret, int):
        return ret > 0
    return None


def can_send(bus_id, can_id, data):
    """发送一帧。ZCANPRO API 用返回值报错（不抛异常），必须检查返回值：
    通道未开/帧格式错时旧代码照样"成功"，帧根本没上线。
    每次发送日志记录 [发送方式 + 返回值]，供判断帧是否真的发出。"""
    global _tx_mode
    frame = _make_frame(can_id, data)
    attempts = [
        ("transmit(list)", "transmit", (bus_id, [frame])),
        ("transmit(dict)", "transmit", (bus_id, frame)),
        ("send(list)", "send", (bus_id, [frame])),
        ("send(dict)", "send", (bus_id, frame)),
    ]
    if _tx_mode:
        attempts = ([a for a in attempts if a[0] == _tx_mode]
                    + [a for a in attempts if a[0] != _tx_mode])
    last = None
    for label, name, args in attempts:
        fn = getattr(zcanpro, name, None)
        if fn is None:
            continue
        try:
            ret = fn(*args)
        except Exception as e:
            last = "%s 抛异常: %s" % (label, e)
            _log("CAN TX %s 失败: %s" % (label, e))
            continue
        if _tx_ret_ok(ret) is False:
            last = "%s 返回失败值 %r" % (label, ret)
            _log("CAN TX %s 返回失败值: %r（换下一种发送形式重试）" % (label, ret))
            continue
        if _tx_mode != label:
            _tx_mode = label
            _log("CAN TX 使用 " + label)
        _log("CAN TX [%s] %s ret=%r" % (label, _hex(_pad8(data)), ret))
        return
    raise RuntimeError("无法发送 CAN: %s" % (last or "无可用发送 API"))


def _as_int(x):
    try:
        return int(x)
    except Exception:
        return None


def _parse_one_frame(f):
    """dict / list / tuple / object → (can_id, data_list) or None."""
    if f is None:
        return None
    if isinstance(f, dict):
        cid = None
        for k in ("can_id", "id", "CANID", "canid"):
            if k in f:
                cid = _as_int(f[k])
                break
        dat = f.get("data")
        if cid is None:
            return None
        return (cid & 0x1FFFFFFF, [int(x) & 0xFF for x in list(dat or [])])
    if isinstance(f, (list, tuple)):
        if len(f) >= 2 and _as_int(f[0]) is not None:
            cid = _as_int(f[0]) & 0x1FFFFFFF
            dat = f[1]
            if isinstance(dat, (list, tuple, bytes, bytearray)):
                return (cid, [int(x) & 0xFF for x in list(dat)])
        return None
    cid = _as_int(getattr(f, "can_id", getattr(f, "id", None)))
    dat = getattr(f, "data", None)
    if cid is None:
        return None
    return (cid & 0x1FFFFFFF, [int(x) & 0xFF for x in list(dat or [])])


def _unwrap_receive(raw):
    """ZCANPRO receive 常见返回：(status, [frames]) 或 [frames] 或 单帧。"""
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


def can_recv(bus_id):
    global _rx_logged
    raw = None
    try:
        raw = zcanpro.receive(bus_id)
    except TypeError:
        try:
            raw = zcanpro.receive()
        except Exception as e:
            _log("receive() 失败: " + str(e))
            return []
    except Exception as e:
        _log("receive(bus_id) 失败: " + str(e))
        return []

    if _rx_logged < 3:
        _rx_logged += 1
        _log("receive 原始: %s" % (repr(raw)[:240],))

    items = _unwrap_receive(raw)
    out = []
    for f in items:
        parsed = _parse_one_frame(f)
        if parsed is None:
            continue
        out.append(parsed)
    return out


def _assemble(head, total, cfs):
    buf = list(head)
    sn = 1
    while len(buf) < total:
        if sn not in cfs:
            return None
        buf.extend(cfs[sn])
        sn = (sn + 1) & 0x0F
    return buf[:total]


def isotp_raw_request(bus_id, sid, payload, timeout_s=2.5):
    req = [sid] + list(payload)
    pci = [len(req)] + req
    can_send(bus_id, UDS_REQ_ID, pci)
    _log("[Tx raw] %s" % _hex(_pad8(pci)))

    t0 = time.time()
    total = None
    head = None
    cfs = {}
    fc_sent = False
    saw = 0

    while (time.time() - t0) < timeout_s:
        if stopTask:
            raise RuntimeError("用户停止")
        for cid, dat in can_recv(bus_id):
            saw += 1
            if saw <= 12:
                _log("[raw RX] id=0x%08X %s" % (cid, _hex(dat[:8])))
            if cid != (UDS_RESP_ID & 0x1FFFFFFF):
                continue
            if not dat:
                continue
            pci_t = dat[0] & 0xF0
            if pci_t == 0x00:
                n = dat[0] & 0x0F
                return dat[1:1 + n]
            if pci_t == 0x10:
                total = ((dat[0] & 0x0F) << 8) | dat[1]
                head = list(dat[2:8])
                if not fc_sent:
                    can_send(bus_id, UDS_REQ_ID, [0x30, 0x00, 0x0A])
                    fc_sent = True
                    _log("[Tx raw] FC 30 00 0A")
                t0 = time.time()
            elif pci_t == 0x20:
                cfs[dat[0] & 0x0F] = list(dat[1:8])
                t0 = time.time()
            got = _assemble(head, total, cfs) if (head is not None and total is not None) else None
            if got is not None:
                return got
        time.sleep(0.01)
    raise RuntimeError("ISO-TP 组帧超时 (raw RX=%d 帧, CF SN=%s)" % (
        saw, ",".join("%d" % k for k in sorted(cfs.keys())) or "无"))


def uds_req(bus_id, sid, payload, timeout_note=""):
    req = {
        "src_addr": UDS_REQ_ID, "dst_addr": UDS_RESP_ID,
        "suppress_response": 0, "sid": sid, "data": list(payload),
    }
    _log("[Tx uds] %02X %s" % (sid, _hex(payload)))
    resp = zcanpro.uds_request(bus_id, req)
    data = list((resp or {}).get("data") or [])
    if data:
        _log("[Rx uds] %s" % _hex(data[:40]))
    if not resp or not resp.get("result"):
        raise RuntimeError("uds 无应答%s: %s" % (timeout_note, (resp or {}).get("result_msg", "")))
    return data


def parse_did_string(did, rx):
    if len(rx) >= 3 and rx[0] == SID_NRC:
        raise RuntimeError("NRC 0x%02X" % rx[2])
    if len(rx) < 1 or rx[0] != (SID_RDBI + SID_PR):
        raise RuntimeError("非正响应: %s" % _hex(rx))
    if len(rx) < 35:
        raise RuntimeError("DID 0x%04X 过短 %dB: %s" % (did, len(rx), _hex(rx)))
    raw = rx[3:35]
    return "".join(chr(b) if 0x20 <= b < 0x7F else "?" for b in raw).rstrip()


def _sniff(bus_id, seconds):
    """不经过 UDS 库，把总线上的帧都打出来。
    前提：UDS 处于 init 状态——ZLG deinit 后 receive 恒返回 (1,[])，嗅探必为 0 帧。"""
    t_end = time.time() + float(seconds)
    n = 0
    saw_boot = None
    saw_app = False
    saw_life = False
    while time.time() < t_end:
        if stopTask:
            raise RuntimeError("用户停止")
        for cid, dat in can_recv(bus_id):
            n += 1
            _log("[sniff] 0x%08X %s" % (cid, _hex(dat[:8])))
            if cid == (UDS_RESP_ID & 0x1FFFFFFF) and len(dat) >= 5:
                if dat[0] == 0x05 and dat[1] == 0x62 and dat[4] == 0xFE:
                    saw_boot = dat[5]
                elif dat[0] == 0x62 and dat[3] == 0xFE:
                    saw_boot = dat[4]
                elif dat[0] in (0x02, 0x03, 0x04, 0x05) and dat[1] == 0x7E:
                    saw_app = True
                elif dat[0] == 0x04 and dat[1] == 0x62 and dat[2] == 0x21:
                    saw_app = True
            if cid == 0x18FF260D:
                saw_life = True
                if len(dat) >= 4 and dat[2] == 0x42 and dat[3] == 0x54:
                    saw_boot = dat[5] if len(dat) > 5 else 0
        time.sleep(0.02)
    _log("监听 %.1fs 共 %d 帧" % (seconds, n))
    return saw_boot, saw_app, saw_life, n


def _sniff_wk_evidence(bus_id, seconds=1.5):
    """唤醒证据嗅探（可选，不拦截主流程）：0x18FF260D 上的
    01 41 57 4B（AWK 唤醒标识帧）= 唤醒成功。超时/异常只记日志。
    前提：已 _uds_init()（deinit 后 receive 恒空，见 P1）。"""
    try:
        t_end = time.time() + float(seconds)
        while time.time() < t_end:
            if stopTask:
                break
            for cid, dat in can_recv(bus_id):
                if cid == 0x18FF260D and len(dat) >= 4 and list(dat[:4]) == [0x01, 0x41, 0x57, 0x4B]:
                    _log("[唤醒证据] 收到 AWK 帧 0x%08X %s（Standby 已打破）" % (cid, _hex(dat[:8])))
                    return True
            time.sleep(0.02)
        _log("[唤醒证据] %.1fs 内未见 AWK 帧（不拦截，继续 3E 00 探测）" % seconds)
    except Exception as e:
        _log("[唤醒证据] sniff 异常（忽略）: %s" % e)
    return False


def wake_mcu(bus_id):
    """唤醒链（2026-10-01）：
    1. raw 连发 3 帧 02 3E 80（suppress，200ms 间隔）打破 SIT1145
       Standby——旧实现只靠 uds 3E 00，首帧被 WUP 吃掉后 UDS 层不重发，
       ECU 醒了也没人再问（参考 zcanpro_ext_ota_auto.wake_bus）；
    2. _uds_init() 开接收通路，嗅探 AWK 帧作唤醒证据（1.5s，不拦截）；
    3. uds 3E 00 等 7E，最多重试 8 次（OTA-ARCH-0920-D3/P0，覆盖
       Boot→App CAN 黑窗最坏 ~5s；每次 UDS 超时 2s + 间隔 0.5s ≈ 20s）。"""
    # 1) raw 3E 80 burst 打破 Standby
    for i in range(1, 4):
        if stopTask:
            raise RuntimeError("用户停止")
        try:
            can_send(bus_id, UDS_REQ_ID, [0x02, 0x3E, 0x80])
            _log("[Tx raw] 02 3E 80 (%d/3, 打破 Standby)" % i)
        except Exception as e:
            _log("Standby 唤醒帧 %d/3 发送失败: %s" % (i, e))
        time.sleep(0.2)

    # 2) 开接收 + AWK 证据嗅探（init 同时保证下面 uds 3E 00 可用）
    _uds_init()
    _sniff_wk_evidence(bus_id, 1.5)

    # 3) uds 3E 00 等 7E
    ok = False
    for i in range(1, 9):
        if stopTask:
            raise RuntimeError("用户停止")
        try:
            rx = uds_req(bus_id, SID_TP, [0x00], timeout_note=" (唤醒第%d次)" % i)
            if rx and rx[0] == (SID_TP + SID_PR):
                _log("MCU 已在线 (7E)")
                ok = True
                break
        except Exception as e:
            _log("唤醒 %d/8: %s" % (i, e))
            time.sleep(0.5)
    return ok


def read_did_string(bus_id, did):
    payload = [(did >> 8) & 0xFF, did & 0xFF]
    rx = isotp_raw_request(bus_id, SID_RDBI, payload)
    if rx and len(rx) >= 3 and rx[0] == SID_NRC:
        raise RuntimeError("NRC 0x%02X（Boot 无此 DID）" % rx[2])
    if rx and len(rx) >= 5 and rx[0] == 0x62 and rx[1] == 0x21 and rx[3] == 0xFE:
        raise RuntimeError("Boot safe mode fail_step=%d" % rx[4])
    return parse_did_string(did, rx)


def run(bus_id):
    _log("======== 读取 APP 侧版本号 ========")
    _log("CAN ID: Tx 0x%08X  Rx 0x%08X" % (UDS_REQ_ID, UDS_RESP_ID))
    names = [a for a in dir(zcanpro) if not a.startswith("_")]
    _log("zcanpro API: " + ", ".join(names))
    _log("")

    # 唤醒链：raw 3E 80 burst → _uds_init() → AWK 嗅探 → 3E 00×8
    # （init 移入 wake_mcu；V1.0.0：必须 uds_init，ZLG 才会开接收）
    online = wake_mcu(bus_id)
    if online:
        _log("UDS 已通，直接 uds_request 读 DID")
        results = []
        for did, name, expected in DID_LIST:
            try:
                payload = [(did >> 8) & 0xFF, did & 0xFF]
                rx = uds_req(bus_id, SID_RDBI, payload)
                ver = parse_did_string(did, rx)
                match = "[OK]" if ver == expected else "[不匹配 期望:%s]" % expected
                _log("DID 0x%04X [%s]: %s %s" % (did, name, ver, match))
                results.append((name, ver, expected, None))
            except Exception as e:
                _log("DID 0x%04X [%s]: 读取失败 - %s" % (did, name, e))
                results.append((name, None, expected, str(e)))
            time.sleep(0.05)
        _log("")
        _log("---- 汇总 ----")
        for name, ver, expected, err in results:
            if ver is not None:
                match = "✓" if ver == expected else "✗ 期望:%s" % expected
                _log("  %s = %s %s" % (name, ver, match))
            else:
                _log("  %s = [失败] %s" % (name, err))
        return

    # OTA-ARCH-0920-D3/P1：ZLG 库 deinit 后 receive 恒返回 (1,[])，嗅探拿不到任何帧；
    # 必须重新 _uds_init() 打开接收通路，raw 嗅探才有信息量。
    _log("UDS 无 7E，重新 _uds_init() 后 raw 嗅探（deinit 后 receive 恒空）")
    time.sleep(0.05)
    _uds_init()

    can_send(bus_id, UDS_REQ_ID, [0x02, 0x3E, 0x00])
    _log("[Tx raw] 02 3E 00")
    time.sleep(0.2)
    can_send(bus_id, UDS_REQ_ID, [0x03, 0x22, 0x21, 0x13])
    _log("[Tx raw] 03 22 21 13")

    saw_boot, saw_app, saw_life, n = _sniff(bus_id, 1.5)
    if saw_boot is not None:
        _log("设备在 Boot safe mode, fail_step=%d。请 merge_prod_bin 整片烧写 Boot(16KB)+App(48KB)+Backup(48KB)。" % saw_boot)
        return
    if n == 0:
        _log("总线上 0 帧 MCU 回复。请确认：1) 通道 250kbps 扩展帧已打开；"
             "2) 已用当前 main 烧写完整镜像 Boot(16KB)+App(48KB)+Backup(48KB)"
             "（OTA-ARCH-0920 单 App 架构，无 A/B 槽位；勿与 V1.0.0 旧版 Boot 混用）；"
             "3) 断电重启后再跑。")
        return

    results = []
    for did, name, expected in DID_LIST:
        try:
            ver = read_did_string(bus_id, did)
            match = "[OK]" if ver == expected else "[不匹配 期望:%s]" % expected
            _log("DID 0x%04X [%s]: %s %s" % (did, name, ver, match))
            results.append((name, ver, expected, None))
        except Exception as e:
            _log("DID 0x%04X [%s]: 读取失败 - %s" % (did, name, e))
            results.append((name, None, expected, str(e)))
        time.sleep(0.05)

    _log("")
    _log("---- 汇总 ----")
    for name, ver, expected, err in results:
        if ver is not None:
            match = "✓" if ver == expected else "✗ 期望:%s" % expected
            _log("  %s = %s %s" % (name, ver, match))
        else:
            _log("  %s = [失败] %s" % (name, err))


def z_main():
    global stopTask
    stopTask = False
    _log("======== APP 版本读取工具 ========")
    _log("预期版本: SW=QC_JYF_FW_1.1.2 / BL=QC_JYF_BL_1.0.0 / HW=QC_JYF_HW_1.1.5")
    _log("DID: 0xF195(SW) / 0xF180(BL) / 0xF193(HW)")
    _log("")
    buses = zcanpro.get_buses()
    if not buses:
        _log("请先打开 CAN 通道 (250kbps, 扩展帧)")
        return
    _log("bus = " + str(buses[0]))
    try:
        run(buses[0]["busID"])
    except Exception as e:
        _log("失败: " + str(e))
    finally:
        _uds_deinit()
