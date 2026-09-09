# -*- coding: utf-8 -*-
"""
ZCANPRO 扩展脚本 — SN 序列号写入

通过 CAN-UDS 将 SN 写入 MCU Device Info（DID 0xF18C）。
APP 与 Boot Safe Mode 均可写。

流程：
  1. 识别 APP / Boot
  2. 编程会话 0x10 02
  3. 安全解锁 0x27 01 → 0x27 03 分片 → 0x27 02
  4. 写入 0x2E F18C（32 字节，空格补齐）
  5. 读回 0x22 F18C（35 字节多帧：自组 ISO-TP，不走 uds_request）

导入: ZCANPRO → 高级功能 → 扩展脚本 → 打开本文件
运行前: 打开 CAN 通道（250 kbps, Classical CAN, 29-bit 扩展帧）
Python: 3.8 32 位
"""

import os
import sys
import time
import binascii

try:
    import zcanpro
except ImportError:
    zcanpro = None

# ======== 用户配置 ========
_TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(_TOOLS_DIR)
PRIVATE_KEY_PATH = os.path.join(REPO_ROOT, "docs", "keys", "private.pem")

SN_CODE = "LSCH42JY012606020001"
SA_SIG_CHUNK = 4  # 27 03 每帧 4 字节，整帧恰好 7 字节单帧

# ======== UDS ========
UDS_REQ_ID = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

SID_DSC = 0x10
SID_SA = 0x27
SID_RDBI = 0x22
SID_WDBI = 0x2E
SID_RD = 0x34
SID_TP = 0x3E
SID_NRC = 0x7F
SID_PR = 0x40
NRC_SNS = 0x11
NRC_CNC = 0x22
NRC_RCRRP = 0x78
DID_SN = 0xF18C
FILL = 0xCC

# secp256r1
_P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
_N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
_A = _P - 3
_GX = 0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296
_GY = 0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5

stopTask = False
_tx_style = 0
_rx_dump = 0


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


def _hex(data, n=None):
    if data is None:
        return ""
    seq = list(data) if n is None else list(data)[:n]
    return " ".join("%02X" % (int(b) & 0xFF) for b in seq)


def _to_list(b):
    if sys.version_info[0] >= 3:
        return list(b)
    return [ord(c) for c in b]


def _to_bytes(seq):
    if isinstance(seq, bytes):
        return seq
    if sys.version_info[0] >= 3:
        return bytes(seq)
    return "".join(chr(int(x) & 0xFF) for x in seq)


def _int_be(b):
    if sys.version_info[0] >= 3:
        return int.from_bytes(b, "big")
    return int(binascii.hexlify(b), 16)


def _inv(x, m):
    x %= m
    if sys.version_info[0] >= 3:
        return pow(x, -1, m)
    return pow(x, m - 2, m)


def _jp_double(x, y, z):
    if z == 0 or y == 0:
        return 0, 0, 0
    ysq = (y * y) % _P
    s = (4 * x * ysq) % _P
    m = (3 * x * x + _A * ((z * z) % _P) * ((z * z) % _P)) % _P
    nx = (m * m - 2 * s) % _P
    ny = (m * (s - nx) - 8 * ysq * ysq) % _P
    nz = (2 * y * z) % _P
    return nx, ny, nz


def _jp_add(x1, y1, z1, x2, y2, z2):
    if z1 == 0:
        return x2, y2, z2
    if z2 == 0:
        return x1, y1, z1
    z1z1 = (z1 * z1) % _P
    z2z2 = (z2 * z2) % _P
    u1 = (x1 * z2z2) % _P
    u2 = (x2 * z1z1) % _P
    s1 = (y1 * z2 * z2z2) % _P
    s2 = (y2 * z1 * z1z1) % _P
    if u1 == u2:
        if (s1 + s2) % _P == 0:
            return 0, 0, 0
        return _jp_double(x1, y1, z1)
    h = (u2 - u1) % _P
    r = (s2 - s1) % _P
    h2 = (h * h) % _P
    h3 = (h * h2) % _P
    nx = (r * r - h3 - 2 * u1 * h2) % _P
    ny = (r * (u1 * h2 - nx) - s1 * h3) % _P
    nz = (h * z1 * z2) % _P
    return nx, ny, nz


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
    z2 = (zinv * zinv) % _P
    return (rx * z2) % _P, (ry * z2 * zinv) % _P


def _i2b32(v):
    if v < 0 or v >= (1 << 256):
        raise ValueError("ECDSA 整数超出 32 字节: bit_length=%d" % v.bit_length())
    if sys.version_info[0] >= 3:
        return v.to_bytes(32, "big")
    return binascii.unhexlify("%064x" % v)


def ecdsa_sign_msg(priv, msg):
    """ECDSA P-256 (IEEE P1363: R‖S)"""
    import hashlib
    h = hashlib.sha256(msg).digest()
    z = _int_be(h) % _N
    while True:
        k = _int_be(os.urandom(32)) % _N
        if k == 0:
            continue
        x, _y = _jp_mul(k, _GX, _GY)
        r = x % _N
        if r == 0:
            continue
        s = (_inv(k, _N) * (z + r * priv)) % _N
        if s == 0:
            continue
        return _i2b32(r) + _i2b32(s)


def _collect_octet32(buf, out, start, end):
    i = start
    while i < end:
        tag = buf[i]
        i += 1
        try:
            ln, i = _der_len(buf, i, end)
        except Exception:
            break
        if i + ln > end:
            break
        if tag == 0x04:
            if ln == 32:
                out.append(buf[i:i + 32])
            else:
                _collect_octet32(buf, out, i, i + ln)
        elif tag in (0x30, 0x31, 0xA0, 0xA1):
            _collect_octet32(buf, out, i, i + ln)
        i += ln


def _der_len(buf, i, end):
    if i >= end:
        raise ValueError("DER truncated")
    first = buf[i]
    i += 1
    if first < 0x80:
        return first, i
    n = first & 0x7F
    if n == 0 or n > 4 or i + n > end:
        raise ValueError("DER length")
    ln = 0
    for _k in range(n):
        ln = (ln << 8) | buf[i]
        i += 1
    return ln, i


def load_ec_private_key(path):
    raw = open(path, "rb").read()
    if len(raw) == 32:
        priv = _int_be(raw)
        if 0 < priv < _N:
            return priv
    text = raw.decode("ascii", "ignore") if sys.version_info[0] >= 3 else raw
    if "BEGIN" in text:
        lines = []
        take = False
        for line in text.splitlines():
            s = line.strip()
            if "BEGIN" in s:
                take = True
                continue
            if "END" in s:
                break
            if take:
                lines.append(s)
        import base64
        der = base64.b64decode("".join(lines))
    else:
        der = raw
    cands = []
    _collect_octet32(der, cands, 0, len(der))
    for key in cands:
        if len(key) != 32:
            continue
        priv = _int_be(key)
        if 0 < priv < _N:
            return priv
    raise ValueError("无法解析私钥: " + path)


# ======== UDS (zcanpro.uds_request) ========

def uds_init():
    zcanpro.uds_init({
        "response_timeout_ms": 3000,
        "use_canfd": 0,
        "canfd_brs": 0,
        "trans_ver": 0,
        "fill_byte": FILL,
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
            extra = " (suppress)" if suppress else ""
            _log("[Tx] %02X %s%s" % (
                sid, _hex(payload, 16) + (" ..." if len(payload) > 16 else ""), extra))
            logged_tx = True
        resp = zcanpro.uds_request(bus_id, req)
        if suppress:
            return None
        data = list((resp or {}).get("data") or [])
        if data:
            _log("[Rx] " + _hex(data, 24))
        if len(data) >= 3 and data[0] == SID_NRC:
            if data[2] == NRC_RCRRP:
                if wait_pending_s <= 0 or time.time() >= t_end:
                    raise RuntimeError("SID=0x%02X 只收到 NRC 0x78，超时" % sid)
                _log("SID=0x%02X NRC 0x78，MCU 忙，继续等待" % sid)
                time.sleep(1.0)
                continue
            raise RuntimeError("NRC SID=0x%02X NRC=0x%02X" % (data[1], data[2]))
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


def send_security_key(bus_id, sig):
    sig = _to_bytes(sig)
    if len(sig) != 64:
        raise RuntimeError("ECDSA 签名须 64 字节, 实际 %d" % len(sig))
    seq = 1
    off = 0
    while off < 64:
        piece = sig[off:off + SA_SIG_CHUNK]
        uds_req(bus_id, SID_SA, [0x03, seq] + _to_list(piece))
        off += len(piece)
        seq += 1
    _log("27 03 已送 64 字节 / %d 帧" % (seq - 1))
    time.sleep(0.15)
    last = None
    for i in range(5):
        if stopTask:
            raise RuntimeError("用户停止脚本")
        try:
            return uds_req(bus_id, SID_SA, [0x02], wait_pending_s=45)
        except Exception as e:
            last = e
            _log("27 02 第 %d/5 次: %s" % (i + 1, e))
            time.sleep(0.5)
            rx = uds_try(bus_id, SID_SA, [0x01])
            if rx is not None and len(rx) >= 6 and list(rx[2:6]) == [0, 0, 0, 0]:
                _log("27 01 seed=0，已解锁")
                return rx
    raise last


# ======== 原始 CAN / ISO-TP（仅用于 22 F18C 多帧读回）========
# zcanpro.receive(bus_id) 必须 1 个参数。
# uds_request 发多帧没问题，但组不齐 MCU 回的 35 字节（FF+CF）。
# 自组前必须 uds_deinit，否则 UDS 栈截走 18DA030D。

def _pad8(data):
    d = [int(x) & 0xFF for x in data]
    while len(d) < 8:
        d.append(FILL)
    return d[:8]


def _frame_id(fr):
    if isinstance(fr, dict):
        inner = fr.get("frame")
        if isinstance(inner, dict):
            for k in ("can_id", "id", "canID"):
                if inner.get(k) is not None:
                    return int(inner[k]) & 0x1FFFFFFF
        for k in ("can_id", "id", "canID"):
            if fr.get(k) is not None:
                return int(fr[k]) & 0x1FFFFFFF
        return 0
    return int(getattr(fr, "can_id", getattr(fr, "id", 0)) or 0) & 0x1FFFFFFF


def _frame_data(fr):
    d = None
    if isinstance(fr, dict):
        inner = fr.get("frame")
        if isinstance(inner, dict):
            d = inner.get("data")
        if d is None:
            d = fr.get("data")
    else:
        d = getattr(fr, "data", None)
    if d is None:
        return []
    if isinstance(d, (bytes, bytearray)):
        return [int(x) & 0xFF for x in d]
    return [int(x) & 0xFF for x in list(d)]


def _tx_payload(can_id, data, style):
    d = _pad8(data)
    cid = int(can_id)
    # 0: bit31=EFF + frame_type 扩展（ZLG 常见）
    # 1: 仅 frame_type
    # 2: 同 0，但 transmit 传入 [msg]
    if style == 1:
        msg = {"can_id": cid, "is_canfd": 0, "canfd_brs": 0, "data": d, "frame_type": 1}
        return msg, False
    msg = {
        "can_id": cid | 0x80000000,
        "is_canfd": 0,
        "canfd_brs": 0,
        "data": d,
        "frame_type": 1,
    }
    return msg, (style == 2)


def can_send(bus_id, can_id, data):
    msg, as_list = _tx_payload(can_id, data, _tx_style)
    ret = zcanpro.transmit(bus_id, [msg] if as_list else msg)
    _log("[Tx CAN] %08X %s" % (can_id, _hex(_pad8(data))))
    return ret


def can_recv(bus_id):
    global _rx_dump
    try:
        msgs = zcanpro.receive(bus_id)
    except Exception as e:
        if _rx_dump < 3:
            _rx_dump += 1
            _log("receive 异常: " + str(e))
        return []
    if not msgs:
        return []
    if isinstance(msgs, dict):
        msgs = [msgs]
    else:
        try:
            msgs = list(msgs)
        except TypeError:
            return []
    if msgs and _rx_dump < 6:
        _rx_dump += 1
        fr = msgs[0]
        keys = list(fr.keys()) if isinstance(fr, dict) else type(fr).__name__
        _log("receive 样例 n=%d keys=%s id=%08X %s" % (
            len(msgs), keys, _frame_id(fr), _hex(_frame_data(fr), 8)))
    return msgs


def can_flush(bus_id):
    try:
        can_recv(bus_id)
    except Exception:
        pass


def _poll_resp(bus_id, timeout_s):
    """yield (id, data) of MCU UDS frames; log others once."""
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if stopTask:
            raise RuntimeError("用户停止脚本")
        frames = can_recv(bus_id)
        if not frames:
            time.sleep(0.01)
            continue
        for fr in frames:
            d = _frame_data(fr)
            if not d:
                continue
            fid = _frame_id(fr)
            if fid == UDS_RESP_ID:
                yield fid, d
            elif fid == UDS_REQ_ID:
                pass
            else:
                _log("[Rx 其它] id=%08X %s" % (fid, _hex(d, 8)))
        time.sleep(0.01)


def isotp_request(bus_id, uds_payload, timeout_s=2.5):
    """发 UDS 载荷，收完整 ISO-TP 响应。"""
    uds_payload = [int(x) & 0xFF for x in uds_payload]
    n = len(uds_payload)
    can_flush(bus_id)

    if n <= 7:
        can_send(bus_id, UDS_REQ_ID, [n] + uds_payload)
    else:
        can_send(bus_id, UDS_REQ_ID, [0x10 | ((n >> 8) & 0x0F), n & 0xFF] + uds_payload[:6])
        got_fc = False
        for _fid, d in _poll_resp(bus_id, timeout_s):
            if (d[0] & 0xF0) == 0x30:
                _log("[Rx FC] " + _hex(d, 8))
                got_fc = True
                break
        if not got_fc:
            raise RuntimeError("ISO-TP 未收到 MCU 流控")
        off, sn = 6, 1
        while off < n:
            chunk = uds_payload[off:off + 7]
            can_send(bus_id, UDS_REQ_ID, [0x20 | (sn & 0x0F)] + chunk)
            off += len(chunk)
            sn = (sn + 1) & 0x0F
            time.sleep(0.001)

    buf = []
    total = 0
    expect_sn = 1
    saw = 0
    for _fid, d in _poll_resp(bus_id, timeout_s):
        saw += 1
        pci = d[0] & 0xF0
        if pci == 0x00:
            ln = d[0] & 0x0F
            return d[1:1 + ln]
        if pci == 0x10:
            total = ((d[0] & 0x0F) << 8) | d[1]
            buf = list(d[2:])
            _log("[Rx FF] len=%d %s" % (total, _hex(d, 8)))
            can_send(bus_id, UDS_REQ_ID, [0x30, 0x00, 0x01])
            expect_sn = 1
            continue
        if pci == 0x20:
            sn = d[0] & 0x0F
            if sn != expect_sn:
                raise RuntimeError("ISO-TP SN 错误 expect=%d got=%d" % (expect_sn, sn))
            buf.extend(d[1:])
            expect_sn = (expect_sn + 1) & 0x0F
            if total and len(buf) >= total:
                return buf[:total]
    raise RuntimeError("ISO-TP 接收超时 (已收 %d/%d, 见过 %d 帧)" % (len(buf), total, saw))


def _probe_tx(bus_id):
    """3E 00 单帧探测 transmit 格式，MCU 回 7E 即锁定。最多 3 种。"""
    global _tx_style
    for style in (0, 1, 2):
        if stopTask:
            raise RuntimeError("用户停止脚本")
        _tx_style = style
        can_flush(bus_id)
        msg, as_list = _tx_payload(UDS_REQ_ID, [0x02, SID_TP, 0x00], style)
        try:
            ret = zcanpro.transmit(bus_id, [msg] if as_list else msg)
        except Exception as e:
            _log("TX 格式 %d 异常: %s" % (style, e))
            continue
        _log("[Tx probe] style=%d list=%d ret=%s" % (style, int(as_list), str(ret)))
        t0 = time.time()
        while time.time() - t0 < 0.4:
            for fr in can_recv(bus_id):
                fid = _frame_id(fr)
                d = _frame_data(fr)
                if fid == UDS_RESP_ID and d:
                    _log("[Rx probe] %s" % _hex(d, 8))
                    _log("锁定 TX 格式 %d" % style)
                    return
            time.sleep(0.02)
    _tx_style = 0
    _log("3E 探测无应答，沿用 TX 格式 0")


def read_sn(bus_id):
    uds = isotp_request(bus_id, [SID_RDBI, 0xF1, 0x8C], timeout_s=2.5)
    _log("[Rx ISO-TP] " + _hex(uds, 40))
    if not uds:
        raise RuntimeError("DID 0xF18C 空响应")
    if uds[0] == SID_NRC:
        raise RuntimeError("NRC SID=0x%02X NRC=0x%02X" % (
            uds[1], uds[2] if len(uds) > 2 else 0))
    if uds[0] != (SID_RDBI + SID_PR) or len(uds) < 3:
        raise RuntimeError("非正响应 22 F18C %s" % _hex(uds))
    return uds[3:]


def _sn32(sn_code):
    if sys.version_info[0] >= 3:
        raw = sn_code.encode("ascii")
    else:
        raw = str(sn_code)
    if len(raw) > 32:
        raise RuntimeError("SN 超过32字节: %d" % len(raw))
    return _to_list(raw) + [0x20] * (32 - len(raw))


def _sn_text(data):
    return "".join(chr(int(b) & 0xFF) for b in data[:32]).rstrip(" ")


# ======== 流程 ========

def _probe_side(bus_id):
    """0x34: APP → NRC 0x11；Boot 默认会话 → NRC 0x22。两边都可写 SN。"""
    try:
        uds_req(bus_id, SID_RD, [0x00])
        _log("当前在 Boot Programming，继续写 SN")
        return "BOOT"
    except RuntimeError as e:
        msg = str(e)
        if "NRC=0x11" in msg:
            _log("当前在 APP")
            return "APP"
        if "NRC=0x22" in msg:
            _log("当前在 Bootloader Safe Mode")
            return "BOOT"
        raise


def run_sn_write(bus_id, sn_code):
    global _tx_style, _rx_dump
    _tx_style = 0
    _rx_dump = 0

    _log("======== SN 写入流程 ========")
    _log("SN: %s (%d字节)" % (sn_code, len(sn_code)))
    _log("私钥: " + PRIVATE_KEY_PATH)
    if not os.path.isfile(PRIVATE_KEY_PATH):
        raise RuntimeError("找不到私钥: " + PRIVATE_KEY_PATH)
    priv = load_ec_private_key(PRIVATE_KEY_PATH)

    uds_init()

    _log("---- Step 0: 识别 APP / Boot ----")
    _probe_side(bus_id)

    _log("---- Step 1: 进入编程会话 ----")
    uds_req(bus_id, SID_DSC, [0x02])

    _log("---- Step 2: 安全解锁 ----")
    rx = uds_req(bus_id, SID_SA, [0x01])
    if len(rx) < 6:
        raise RuntimeError("seed 响应过短")
    seed = rx[2:6]
    _log("seed " + _hex(seed))
    if list(seed) == [0, 0, 0, 0]:
        _log("已解锁 (seed=0)，跳过签名")
    else:
        _log("ECDSA P-256 签名中...")
        sig = ecdsa_sign_msg(priv, _to_bytes(seed))
        _log("签名完成，发送分片...")
        send_security_key(bus_id, sig)
    _log("安全解锁成功")
    uds_try(bus_id, SID_TP, [0x00])

    _log("---- Step 3: 写入 SN ----")
    sn32 = _sn32(sn_code)
    _log("写入数据: " + _hex(sn32))
    uds_req(bus_id, SID_WDBI, [0xF1, 0x8C] + sn32, wait_pending_s=10)

    time.sleep(0.3)
    uds_try(bus_id, SID_TP, [0x00])
    _log("---- Step 4: 读回验证（自组 ISO-TP）----")
    try:
        zcanpro.uds_deinit()
        _log("已 uds_deinit，避免 UDS 栈截走多帧")
    except Exception as e:
        _log("uds_deinit: " + str(e))
    _probe_tx(bus_id)

    last = None
    sn_data = None
    for i in range(3):
        if stopTask:
            raise RuntimeError("用户停止脚本")
        try:
            sn_data = read_sn(bus_id)
            last = None
            break
        except Exception as e:
            last = e
            _log("22 F18C 第 %d/3 次: %s" % (i + 1, e))
            try:
                isotp_request(bus_id, [SID_TP, 0x00], timeout_s=1.0)
            except Exception:
                pass
            time.sleep(0.2)
    if last is not None:
        raise last

    sn_read = _sn_text(sn_data)
    _log("读回 SN: [%s]" % sn_read)
    if sn_read != sn_code:
        raise RuntimeError("读回 SN 与写入不一致! 写入=[%s] 读回=[%s]" % (sn_code, sn_read))
    _log("======== SN 写入成功 ========")


def z_main():
    global stopTask, _tx_style, _rx_dump
    stopTask = False
    _tx_style = 0
    _rx_dump = 0
    _log("======== Qi Charger SN 写入工具 ========")
    _log("SN: " + SN_CODE)
    buses = zcanpro.get_buses()
    if not buses:
        _log("请先打开 CAN 通道 250kbps 扩展帧")
        return
    try:
        run_sn_write(buses[0]["busID"], SN_CODE)
    except Exception as e:
        _log("SN 写入失败: " + str(e))
    finally:
        try:
            zcanpro.uds_deinit()
        except Exception:
            pass


if __name__ == "__main__":
    _log("此脚本需要在 ZCANPRO 扩展脚本环境中运行")
