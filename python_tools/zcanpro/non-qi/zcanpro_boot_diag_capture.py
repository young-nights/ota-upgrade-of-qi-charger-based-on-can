# -*- coding: utf-8 -*-
"""
ZCANPRO 扩展脚本 — 抓 Boot 诊断标记 M1~M4（CAN ID 0x18FF480D）

用法：
  1. ZCANPRO 打开通道：250 kbps、Classical CAN、扩展帧、正常模式（要 ACK，不要只听）
  2. 高级功能 → 扩展脚本 → 打开本文件 → 运行
  3. 日志出现「请给设备上电」之后再给 MCU 上电或按复位
  4. 窗口默认 30 秒

M1~M4 只在 Boot 里发，正常启动大约几十毫秒就跳进 App。
先上电再开脚本，这几帧已经过了。OTA 只更新 App，Boot 要 SWD/ISP 整片烧。
"""

import time

try:
    import zcanpro
except Exception:
    zcanpro = None

BOOT_DIAG_ID = 0x18FF480D
LIFE_ID = 0x18FF260D
CAPTURE_WINDOW_S = 30.0

stopTask = False


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
    return " ".join("%02X" % (int(b) & 0xFF) for b in data)


def _as_int(x):
    try:
        return int(x)
    except Exception:
        return None


def _parse_one_frame(f):
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
    raw = None
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
        parsed = _parse_one_frame(f)
        if parsed is not None:
            out.append(parsed)
    return out


def _decode_m(dat):
    if not dat:
        return "?"
    mk = dat[0]
    if mk == 0xA1:
        src = {0: "主区", 1: "备区恢复", 2: "默认重建"}.get(dat[1] if len(dat) > 1 else 0xFF, "未知")
        return "M1 metadata  src=%s magic=%d ver=%d crc=%d" % (
            src,
            dat[2] if len(dat) > 2 else -1,
            dat[3] if len(dat) > 3 else -1,
            dat[4] if len(dat) > 4 else -1)
    if mk == 0xA2:
        result = dat[1] if len(dat) > 1 else 0xFF
        detail = dat[2] if len(dat) > 2 else 0xFF
        if result == 0 and detail == 0xFF:
            meaning = "无 pending"
        elif result == 0 and detail == 0:
            meaning = "拷贝开始"
        elif result == 1:
            meaning = "拷贝提交"
        elif result == 0xFF:
            meaning = "拷贝失败 step=0x%02X" % detail
        else:
            meaning = "result=0x%02X detail=0x%02X" % (result, detail)
        return "M2 拷贝  " + meaning
    if mk == 0xA3:
        tgt = "Backup" if (len(dat) > 3 and dat[3] == 0) else "App"
        pass_b = dat[1] if len(dat) > 1 else 0
        step = dat[2] if len(dat) > 2 else 0
        if pass_b == 0 and step == 0xFF:
            meaning = "开始校验 %s" % tgt
        elif pass_b == 1:
            meaning = "%s 通过" % tgt
        else:
            meaning = "%s 失败 step=%d" % (tgt, step)
        return "M3 校验  " + meaning
    if mk == 0xA4:
        addr = 0
        if len(dat) >= 5:
            addr = dat[1] | (dat[2] << 8) | (dat[3] << 16) | (dat[4] << 24)
        return "M4 跳转  0x%08X" % addr
    return "未知标记 0x%02X" % mk


def _decode_life(dat):
    if len(dat) >= 4 and tuple(dat[:4]) == (0x01, 0x41, 0x57, 0x4B):
        return "App 唤醒 ident"
    if len(dat) >= 8 and tuple(dat[:4]) == (0x01, 0x41, 0x42, 0x54):
        return "Safe mode 心跳 cause=0x%02X step=%d" % (dat[4], dat[5])
    if len(dat) >= 3 and dat[0] == 0x01 and dat[1] == 0x41:
        return "App 生命周期"
    return "生命周期"


def run(bus_id):
    # 打开适配器 RX。UDS 栈不收 0x18FF480D，raw receive 能拿到标记帧。
    zcanpro.uds_init({
        "response_timeout_ms": 2000, "use_canfd": 0, "canfd_brs": 0,
        "trans_ver": 0, "fill_byte": 0xCC, "frame_type": 1,
        "src_addr": 0x03, "dst_addr": 0x0D,
    })

    _log("======== Boot 诊断抓帧 0x18FF480D ========")
    _log("通道须 250kbps / 扩展帧 / 正常模式（要对 MCU 帧 ACK）")
    _log("现在请给设备上电或按复位，等待 %.0f 秒…" % CAPTURE_WINDOW_S)

    t_end = time.time() + CAPTURE_WINDOW_S
    marks = []
    life_n = 0
    total = 0

    while time.time() < t_end:
        if stopTask:
            _log("用户停止")
            break
        for cid, dat in can_recv(bus_id):
            total += 1
            if cid == BOOT_DIAG_ID:
                text = _decode_m(dat)
                _log("[M] %s  %s" % (_hex(dat[:8]), text))
                marks.append(dat[:8] if len(dat) >= 8 else dat)
            elif cid == LIFE_ID:
                life_n += 1
                if life_n <= 6:
                    _log("[L] %s  %s" % (_hex(dat[:8]), _decode_life(dat)))
        time.sleep(0.01)

    _log("")
    _log("---- 汇总：标记 %d 帧，生命周期 %d 帧，总线共 %d 帧 ----" % (
        len(marks), life_n, total))
    tags = [d[0] for d in marks if d]
    if 0xA1 in tags and 0xA2 in tags and 0xA4 in tags:
        _log("正常启动链 M1→M2→M4 已收到" + (
            "（含 M3，走了拷贝）" if 0xA3 in tags else "（无 pending，无 M3）"))
    elif marks:
        _log("只收到部分标记：%s" % " ".join("A%d" % (t & 0x0F) for t in tags))
    elif life_n > 0:
        _log("没有 M1~M4，已经看到 App/Safe mode 生命周期。")
        _log("先运行本脚本再上电；OTA 不更新 Boot，需 SWD 烧当前 bootloader.bin")
    elif total == 0:
        _log("总线上 0 帧。确认：通道已开、250kbps 扩展帧、正常模式非只听、")
        _log("先跑脚本再上电、烧录的是带 M1~M4 的 Boot（merge_prod_bin 整片）。")
    else:
        _log("有其它 CAN 帧，没有 0x18FF480D。ZCANPRO 接收过滤不要只留 UDS ID。")

    try:
        zcanpro.uds_deinit()
    except Exception:
        pass


def z_main():
    global stopTask
    stopTask = False
    buses = zcanpro.get_buses()
    if not buses:
        _log("请先打开 CAN 通道（250kbps，扩展帧）")
        return
    try:
        run(buses[0]["busID"])
    except Exception as e:
        _log("失败: " + str(e))
