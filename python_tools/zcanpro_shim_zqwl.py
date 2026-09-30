#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""zcanpro 模块兼容 shim —— 让 zcanpro_ext_ota_auto.py 在 WSL2 零改动运行。

底层复用 zqwl_can_listen.Zqwl（ZQWL-USBCANFD /dev/ttyACM0，250kbps 扩展帧，
49 3B/5A/A5 私有协议）。实现面 = zcanpro_ext_ota_auto.py 的实际使用面：
write_log / get_buses / uds_init / uds_request / uds_deinit / receive / transmit(=send)。

UDS 语义对齐 ZCANPRO 宿主实测行为（与脚本 uds_req() 注释一致）：
  · 请求 >7B：ISO-TP FF → 等 ECU FC → 按 STmin（取 FC 值与 cfg trans_stmin
    较大者）发 CF；≤7B 单帧 SF（fill_byte 0xCC 填充至 8B）
  · 响应 >7B：自动回 FC(30 00 00 CC×5) 并跨帧组装完整 UDS payload
  · NRC 0x78 内部消化、不上抛：首帧等待 = response_timeout_ms；一旦见过 78，
    后续一直等到正响应/终态 NRC 或 enhanced_timeout_ms 绝对上限
  · suppress_response=1：发完即返回，不等应答
  · uds_deinit() 只是标记，不关串口（脚本 deinit 后还要 raw 收发取证、
    生命周期监听、wake_bus）
  · UDS 等待期间非目标 ID 帧（0x18FF260D 生命周期、0x18FF480D Boot 诊断等）
    缓存进队列供 receive() 取走；UDS 空闲时目标 ID 帧也进队列（raw 取证窗口
    如 _sniff_erase_late_response 需要看到 0x18DA030D 迟到响应）

串口生命周期：uds_init() 首次打开（幂等不重开）；进程退出 atexit 关闭。
同一 /dev/ttyACM0 同一时刻只允许本进程打开。
"""
from __future__ import print_function

import atexit
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from zqwl_can_listen import Zqwl  # noqa: E402

UDS_REQ_ID = 0x18DA0D03   # 主机 TX（脚本 src_addr）
UDS_RESP_ID = 0x18DA030D  # ECU TX / 主机 RX（脚本 dst_addr）
DEFAULT_PORT = os.environ.get("ZQWL_PORT", "/dev/ttyACM0")

# ISO-TP 流控 CTS / BS=0 / STmin=0 + CC 填充（与 zqwl_can_send.py 实测一致）
FC_FRAME = [0x30, 0x00, 0x00, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC]

# 0xFE/0xFF 为 ISO 15765-2 保留值，按惯例取 125ms
_STMIN_SPECIAL = {0xFA: 0.010, 0xFB: 0.020, 0xFC: 0.050,
                  0xFD: 0.100, 0xFE: 0.125, 0xFF: 0.125}

_dev = None
_cfg = {}
_rxq = []   # receive() 待取帧队列


# ---------------------------------------------------------------- 公共 API

def write_log(text):
    """ZCANPRO 宿主日志：stdout + flush。"""
    sys.stdout.write(str(text) + "\n")
    sys.stdout.flush()


def get_buses():
    return [{"busID": 0, "channel": 0, "name": "ZQWL-CAN0-250k-ext"}]


def uds_init(cfg=None):
    """打开 Zqwl 通道（幂等：已打开不重开）；cfg 每次刷新（脚本每次同值）。"""
    global _cfg
    if cfg:
        _cfg = dict(cfg)
    _ensure_dev()
    return True


def uds_deinit():
    """只停 UDS 语义，不关串口——脚本 deinit 后还要 raw send/receive 取证。"""
    return True


def uds_request(bus_id, req):
    """UDS 请求主入口。req={src_addr,dst_addr,suppress_response,sid,data}。

    返回 {result, data, result_msg}：data 为组装好的完整 UDS payload
    （正响应/终态 NRC；NRC 0x78 不上抛）；失败 result=False + result_msg。
    """
    dev = _ensure_dev()
    req = req or {}
    src = int(req.get("src_addr", UDS_REQ_ID) or UDS_REQ_ID) & 0x1FFFFFFF
    dst = int(req.get("dst_addr", UDS_RESP_ID) or UDS_RESP_ID) & 0x1FFFFFFF
    suppress = int(req.get("suppress_response", 0) or 0) != 0
    sid = int(req.get("sid", 0)) & 0xFF
    payload = [sid] + [int(x) & 0xFF for x in (req.get("data") or [])]

    resp_to = max(0.05, float(_cfg.get("response_timeout_ms", 3000)) / 1000.0)
    enh_to = max(resp_to, float(_cfg.get("enhanced_timeout_ms", 120000)) / 1000.0)
    fill = int(_cfg.get("fill_byte", 0xCC)) & 0xFF
    deadline = time.time() + enh_to

    # 丢弃上一请求迟到的目标 ID 应答帧，避免串台
    _drain(dst)

    # ---- TX ----
    try:
        if len(payload) <= 7:
            _send_frame(dev, src, _pad([len(payload)] + payload, fill))
        else:
            _tx_isotp(dev, src, dst, payload, fill, resp_to, deadline)
    except RuntimeError as e:
        return {"result": False, "data": [], "result_msg": str(e)}

    if suppress:
        return {"result": True, "data": [],
                "result_msg": "suppress_response=1 已发送"}

    # ---- RX ----
    got, msg = _rx_wait(dev, src, dst, resp_to, deadline)
    if got is None:
        return {"result": False, "data": [], "result_msg": msg}
    return {"result": True, "data": got, "result_msg": "OK"}


def receive(bus_id=None):
    """泵出已捕获帧 → (status, [frame_dict, ...])，形状可被 _parse_can_frame 解析。"""
    _ensure_dev()
    _pump_to_queue()
    out = _rxq[:]
    del _rxq[:]
    return (0, out)


def transmit(bus_id, frames, *args, **kwargs):
    """raw 发送：frames 为 dict 或 dict 列表（兼容 ZLG bit31=1 的 can_id）。"""
    dev = _ensure_dev()
    if isinstance(frames, dict):
        frames = [frames]
    if not frames:
        return True
    for f in frames:
        cid, data = _split_frame(f)
        if cid is None:
            continue
        dev.send_can(cid, bytes(int(b) & 0xFF for b in (data or [])), ext=True)
    return True


send = transmit  # 脚本 _raw_send 依次尝试 transmit / send


def close():
    """关闭串口（进程退出 atexit 自动调用）。"""
    global _dev
    if _dev is not None:
        _dev.close()
        _dev = None


atexit.register(close)


# ---------------------------------------------------------------- 内部实现

def _log(msg):
    write_log("[zcanpro-shim] " + str(msg))


def _ensure_dev():
    global _dev
    if _dev is None:
        _dev = Zqwl(DEFAULT_PORT)
        try:
            info = _dev.read_device()
        except Exception:
            info = b""
        _dev.open_can0_250k()
        _log("串口 %s 已开，CAN0 250kbps 扩展帧（device_info %d B）"
             % (DEFAULT_PORT, len(info)))
    return _dev


def _as_int(x):
    try:
        return int(x)
    except Exception:
        return None


def _split_frame(f):
    """frame → (can_id_29bit, data list)；剥掉 ZLG bit31 发送标志。"""
    if isinstance(f, dict):
        cid = None
        for k in ("can_id", "id", "CANID", "canid"):
            if k in f:
                cid = _as_int(f[k])
                break
        data = f.get("data")
    elif isinstance(f, (list, tuple)) and len(f) >= 2 and _as_int(f[0]) is not None:
        cid, data = _as_int(f[0]), f[1]
    else:
        cid = _as_int(getattr(f, "can_id", getattr(f, "id", None)))
        data = getattr(f, "data", None)
    if cid is None:
        return None, None
    if not isinstance(data, (list, tuple, bytes, bytearray)):
        data = []
    return cid & 0x1FFFFFFF, [int(x) & 0xFF for x in list(data)]


def _to_frame(cid, data):
    """泵出的原始 CAN 帧 → _parse_can_frame 可解析的 dict。"""
    data = [int(x) & 0xFF for x in data]
    return {
        "can_id": cid & 0x1FFFFFFF,
        "id": cid & 0x1FFFFFFF,
        "is_canfd": 0,
        "canfd_brs": 0,
        "is_extend": 1,
        "is_extended": 1,
        "extend": 1,
        "extern_flag": 1,
        "is_extern": 1,
        "eff": 1,
        "id_type": 1,
        "dlc": len(data),
        "data": data,
    }


def _pad(data, fill):
    if len(data) >= 8:
        return data[:8]
    return data + [fill] * (8 - len(data))


DEBUG = bool(os.environ.get("ZQWL_SHIM_DEBUG"))


def _send_frame(dev, cid, data):
    if DEBUG:
        _log("TX %08X %s" % (cid, " ".join("%02X" % (int(b) & 0xFF) for b in data)))
    dev.send_can(cid, bytes(int(b) & 0xFF for b in data), ext=True)


def _pump_to_queue():
    """泵串口，非过滤场景：所有 CAN 帧进 receive() 队列。"""
    if _dev is None:
        return
    for item in _dev.pump():
        if item[0] == "can":
            _rxq.append(_to_frame(item[1], item[3]))


def _drain(dst):
    """清掉队列里残留的目标 ID 帧；泵一次，目标 ID 帧丢弃、其余进队列。"""
    for i in range(len(_rxq) - 1, -1, -1):
        if _rxq[i]["can_id"] == dst:
            del _rxq[i]
    if _dev is None:
        return
    for item in _dev.pump():
        if item[0] != "can":
            continue
        if (item[1] & 0x1FFFFFFF) != dst:
            _rxq.append(_to_frame(item[1], item[3]))


def _stmin_s(b, floor_s):
    if b <= 0x7F:
        s = b / 1000.0
    elif 0xF1 <= b <= 0xF9:
        s = (b - 0xF0) * 0.0001
    else:
        s = _STMIN_SPECIAL.get(b, 0.125)
    return max(s, floor_s)


def _tx_isotp(dev, src, dst, payload, fill, resp_to, deadline):
    """ISO-TP 多帧发送：FF → 等 FC → 按 STmin 发 CF（支持 BS 分块）。"""
    n = len(payload)
    if n > 4095:
        raise RuntimeError("UDS payload %d 字节超 ISO-TP 4095 上限" % n)
    floor = 0.0
    if int(_cfg.get("trans_stmin_valid", 0) or 0):
        floor = max(0.0, float(_cfg.get("trans_stmin", 0) or 0) / 1000.0)
    _send_frame(dev, src, _pad([0x10 | ((n >> 8) & 0x0F), n & 0xFF]
                               + payload[:6], fill))
    bs, stmin = _wait_fc(dev, dst, resp_to, deadline)
    idx = 6
    seq = 1
    in_block = 0
    while idx < n:
        _send_frame(dev, src, _pad([0x20 | (seq & 0x0F)]
                                   + payload[idx:idx + 7], fill))
        idx += 7
        seq = (seq + 1) & 0x0F
        in_block += 1
        time.sleep(stmin)
        if idx < n and bs and in_block >= bs:
            bs, stmin = _wait_fc(dev, dst, resp_to, deadline)
            in_block = 0


def _wait_fc(dev, dst, resp_to, deadline):
    """等 ECU 流控帧（PCI=3）。CTS→(BS, STmin)；WAIT→继续等；溢出→报错。"""
    t_out = time.time() + resp_to
    while True:
        now = time.time()
        if now >= t_out or now >= deadline:
            raise RuntimeError("ISO-TP TX 等 FC 超时（%.1fs）" % (now - (t_out - resp_to)))
        for item in dev.pump():
            if item[0] != "can":
                continue
            cid = item[1] & 0x1FFFFFFF
            data = list(item[3])
            if cid != dst:
                _rxq.append(_to_frame(cid, data))
                continue
            if not data or (data[0] >> 4) != 0x3:
                continue  # 迟到的应答帧，FC 等待期丢弃
            fs = data[0] & 0x0F
            if fs == 0:
                bs = data[1] if len(data) > 1 else 0
                stmin = data[2] if len(data) > 2 else 0
                return (bs & 0xFF), _stmin_s(stmin & 0xFF, _stmin_floor())
            if fs == 1:
                t_out = time.time() + resp_to  # WAIT：再给一个响应窗口
                continue
            raise RuntimeError("ISO-TP FC 溢出（FS=%d）" % fs)
        time.sleep(0.002)


def _stmin_floor():
    if int(_cfg.get("trans_stmin_valid", 0) or 0):
        return max(0.0, float(_cfg.get("trans_stmin", 0) or 0) / 1000.0)
    return 0.0


def _rx_wait(dev, src, dst, resp_to, deadline):
    """等并组装 UDS 响应。返回 ([payload] | None, msg)。

    NRC 0x78：组装出 7F xx 78 后内部继续等（78 不上抛）；见过 78 后
    后续帧间超时放宽到 enhanced 绝对上限（擦除等长任务 78 挂很久）。
    """
    buf = bytearray()
    expected = None
    seen_78 = 0
    t_start = time.time()
    next_to = time.time() + resp_to

    def _final(payload):
        return [int(b) & 0xFF for b in payload], "OK"

    while True:
        now = time.time()
        if now >= deadline:
            if seen_78:
                return None, ("NRC 0x78 收到 %d 次但 %.0fs（enhanced_timeout_ms）"
                              "内无正响应/终态 NRC" % (seen_78, now - t_start))
            return None, ("%.0fs 内无任何响应（response_timeout=%.0fs 起算，"
                          "enhanced_timeout_ms 上限 %.0fs）"
                          % (now - t_start, resp_to, deadline - t_start))
        if now >= next_to:
            return None, "等待最终响应超时（%.1fs，NRC78=%d 次，已等 %.1fs）" % (
                resp_to, seen_78, now - t_start)

        items = dev.pump()
        if not items:
            time.sleep(0.002)
            continue
        for item in items:
            if item[0] != "can":
                if DEBUG:
                    _log("pump non-can item=%s" % item[0])
                continue
            cid = item[1] & 0x1FFFFFFF
            data = list(item[3])
            if DEBUG:
                _log("RX %08X %s" % (cid, " ".join("%02X" % b for b in data)))
            if cid != dst:
                _rxq.append(_to_frame(cid, data))  # 生命周期/诊断帧留给 receive()
                continue

            # 目标 ID 帧：ISO-TP 组装（容忍裸 UDS payload 帧兜底）
            pci = (data[0] >> 4) if data else -1
            done = None
            if pci == 0:                      # SF
                ln = data[0] & 0x0F
                if ln:
                    done = data[1:1 + ln]
                else:
                    done = data[1:]           # CAN FD SF 变体
            elif pci == 1:                    # FF：回 FC 后等 CF
                expected = ((data[0] & 0x0F) << 8) | (data[1] if len(data) > 1 else 0)
                buf = bytearray(data[2:])
                _send_frame(dev, src, _pad(FC_FRAME, 0xCC))
                if expected and len(buf) >= expected:
                    done = list(buf[:expected])
            elif pci == 2:                    # CF
                if expected is None:
                    continue
                buf.extend(data[1:])
                if len(buf) >= expected:
                    done = list(buf[:expected])
                    expected = None
                else:
                    next_to = time.time() + resp_to
                    continue              # 未组装完，继续等下一帧
            elif pci == 3:                    # ECU 侧 FC，RX 阶段忽略
                continue
            else:                             # 裸 UDS payload 兜底
                done = data

            if done is None:
                continue
            payload = [int(b) & 0xFF for b in done]
            if (len(payload) >= 3 and payload[0] == 0x7F
                    and payload[2] == 0x78):
                seen_78 += 1
                # 语义对齐 ZCANPRO：78 不上抛，一直等到终态或绝对上限
                next_to = deadline
                continue
            return _final(payload)

        # 收到目标帧后刷新帧间超时（78 场景已在上面放宽到 deadline）
        if seen_78:
            next_to = deadline
