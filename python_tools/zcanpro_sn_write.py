# -*- coding: utf-8 -*-
"""
ZCANPRO 扩展脚本 — SN 序列号写入

通过 CAN-UDS 将 SN 写入 MCU 的 Device Info 区（DID 0xF18C）。

流程：
  1. 进入编程会话 (0x10 0x02)
  2. 安全解锁 (0x27 01 → 0x27 03 分片 → 0x27 02)
  3. 写入 SN (0x2E F1 8C [32字节])

导入: ZCANPRO → 高级功能 → 扩展脚本 → 打开本文件
运行前: 先打开 CAN 通道 (250 kbps, Classical CAN, 扩展帧)
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

# 要写入的 SN（最多32字节ASCII，不足补空格）
SN_CODE = "LSCH42JY012606020001"

SA_SIG_CHUNK = 4  # 27 03 每帧签名数据长度

# ======== UDS 常量 ========
UDS_REQ_ID = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

SID_DSC = 0x10
SID_SA  = 0x27
SID_WDBI = 0x2E
SID_RDBI = 0x22
SID_RD   = 0x34
SID_TP   = 0x3E
SID_NRC  = 0x7F
SID_PR   = 0x40
NRC_SNS  = 0x11

NRC_RCRRP = 0x78

DID_SN = 0xF18C

# secp256r1 / prime256v1
_P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
_N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
_A = _P - 3
_GX = 0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296
_GY = 0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5

stopTask = False


def z_notify(type, obj):
    if type == "stop":
        global stopTask
        stopTask = True


# ======== 工具函数 ========

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
    """ECDSA P-256 签名 (IEEE P1363: R‖S, 各32字节)"""
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
    """加载 EC 私钥 (PEM/DER/裸32字节)"""
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
    """发送 UDS 请求，处理 NRC 0x78 等待"""
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
            _log("[Tx] %02X %s%s" % (sid, _hex(payload[:16]) + (" ..." if len(payload) > 16 else ""),
                                     " (suppress)" if suppress else ""))
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


# ZCANPRO uds_request() 组不好 35 字节 ISO-TP 响应。用 transmit/receive 自组。
# 上一版失败点：uds_deinit 后 TX 未标扩展帧 → MCU 滤掉；或 UDS 栈截走 18DA030D。
_FILL = 0xCC
_ISOTP_LOGGED_API = False
_RX_DUMP = 0
_TX_STYLE = None  # (builder_index, wrap_list)
_UDS_RELEASED = False


def _can_pad8(data):
    d = [int(x) & 0xFF for x in data]
    while len(d) < 8:
        d.append(_FILL)
    return d[:8]


def _as_int(v):
    if v is None:
        return 0
    if isinstance(v, bool):
        return int(v)
    if isinstance(v, int):
        return int(v)
    s = str(v).strip()
    if not s:
        return 0
    try:
        if s.lower().startswith("0x"):
            return int(s, 16)
        return int(s, 10)
    except Exception:
        return 0


def _id_norm(fid):
    return _as_int(fid) & 0x1FFFFFFF


def _frame_id(fr):
    if isinstance(fr, dict):
        inner = fr.get("frame")
        if isinstance(inner, dict):
            for k in ("can_id", "canID", "id", "canId", "ID"):
                if k in inner and inner[k] is not None:
                    return _id_norm(inner[k])
        for k in ("can_id", "canID", "id", "canId", "ID"):
            if k in fr and fr[k] is not None:
                return _id_norm(fr[k])
        return 0
    raw = getattr(fr, "can_id", None)
    if raw is None:
        raw = getattr(fr, "canID", getattr(fr, "id", 0))
    return _id_norm(raw)


def _bytes_from(d):
    if d is None:
        return []
    if isinstance(d, (bytes, bytearray)):
        return [int(x) & 0xFF for x in d]
    if isinstance(d, str):
        s = d.replace(",", " ").replace("0x", " ").replace("0X", " ")
        out = []
        for tok in s.split():
            try:
                out.append(int(tok, 16) & 0xFF)
            except Exception:
                pass
        return out
    try:
        seq = list(d)
    except TypeError:
        return [_as_int(d) & 0xFF]
    out = []
    for x in seq:
        if isinstance(x, (list, tuple)):
            break
        out.append(_as_int(x) & 0xFF)
    return out


def _frame_data(fr):
    if isinstance(fr, dict):
        inner = fr.get("frame")
        if isinstance(inner, dict):
            d = inner.get("data") or inner.get("Data") or inner.get("payload")
            got = _bytes_from(d)
            if got:
                return got
        d = fr.get("data") or fr.get("Data") or fr.get("payload")
        return _bytes_from(d)
    return _bytes_from(getattr(fr, "data", None))


def _log_zcan_apis():
    global _ISOTP_LOGGED_API
    if _ISOTP_LOGGED_API or zcanpro is None:
        return
    _ISOTP_LOGGED_API = True
    names = [n for n in dir(zcanpro) if not n.startswith("_")]
    _log("zcanpro 原始 CAN API: " + ", ".join(names))
    for fn in ("transmit", "receive"):
        obj = getattr(zcanpro, fn, None)
        _log("%s doc=%s" % (fn, str(getattr(obj, "__doc__", None))))
        try:
            import inspect
            _log("%s spec=%s" % (fn, inspect.getfullargspec(obj)))
        except Exception:
            pass


def _tx_builders():
    def b0(cid, d):
        return {"can_id": cid | 0x80000000, "is_canfd": 0, "canfd_brs": 0,
                "data": d, "frame_type": 1}
    def b1(cid, d):
        return {"can_id": cid, "is_canfd": 0, "canfd_brs": 0,
                "data": d, "frame_type": 1}
    def b2(cid, d):
        return {"can_id": cid | 0x80000000, "is_canfd": 0, "canfd_brs": 0, "data": d}
    def b3(cid, d):
        return {"id": cid, "is_canfd": 0, "data": d, "frame_type": 1, "extend": 1}
    def b4(cid, d):
        return {"can_id": cid, "is_canfd": 0, "canfd_brs": 0, "data": d,
                "extend": 1, "is_extend": 1, "eff": 1}
    return (b0, b1, b2, b3, b4)


def _transmit_one(bus_id, msg, wrap_list):
    payload = [msg] if wrap_list else msg
    return zcanpro.transmit(bus_id, payload)


def can_send(bus_id, can_id, data):
    """扩展帧。优先用探测到的 TX 格式。"""
    payload = _can_pad8(data)
    builders = _tx_builders()
    if _TX_STYLE is None:
        bi, wrap = 0, True
    else:
        bi, wrap = _TX_STYLE
    msg = builders[bi](int(can_id), payload)
    ret = _transmit_one(bus_id, msg, wrap)
    _log("[Tx CAN] %08X %s style=%d list=%d ret=%s" % (
        can_id, _hex(payload), bi, int(wrap), str(ret)))
    return ret


def _normalize_msgs(msgs):
    if msgs is None:
        return []
    if isinstance(msgs, dict):
        inner = msgs.get("data")
        if isinstance(inner, list) and inner and isinstance(inner[0], dict) and (
                msgs.get("can_id") is None and msgs.get("id") is None):
            return inner
        return [msgs]
    if isinstance(msgs, (bytes, bytearray, str)):
        return []
    try:
        seq = list(msgs)
    except TypeError:
        return [msgs]
    if seq and not isinstance(seq[0], (dict, list, tuple)) and not hasattr(seq[0], "data"):
        return []
    return seq


def can_recv(bus_id):
    global _RX_DUMP
    collected = []
    try:
        raw = zcanpro.receive(bus_id)
    except Exception as e:
        if _RX_DUMP < 4:
            _log("receive 异常: %s" % e)
        return []
    collected = _normalize_msgs(raw)
    if collected and _RX_DUMP < 12:
        _RX_DUMP += 1
        fr0 = collected[0]
        keys = str(list(fr0.keys()) if isinstance(fr0, dict) else dir(fr0)[:16])
        _log("receive 样例 type=%s n=%d keys=%s id=%08X data=%s" % (
            type(fr0).__name__, len(collected), keys,
            _frame_id(fr0), _hex(_frame_data(fr0)[:8])))
    return collected


def can_flush(bus_id):
    try:
        can_recv(bus_id)
    except Exception:
        pass


def release_uds_stack():
    """UDS 栈会截走 18DA030D，自组多帧前必须释放。"""
    global _UDS_RELEASED
    if _UDS_RELEASED:
        return
    try:
        zcanpro.uds_deinit()
        _UDS_RELEASED = True
        _log("已 uds_deinit，后续走原始 CAN")
    except Exception as e:
        _log("uds_deinit: " + str(e))


def _discover_tx(bus_id):
    """用 3E 00 单帧探测哪种 transmit 字典 MCU 能回 7E。"""
    global _TX_STYLE
    if _TX_STYLE is not None:
        return
    builders = _tx_builders()
    payload = _can_pad8([0x02, SID_TP, 0x00])
    echo_style = None
    for wrap in (True, False):
        for bi, builder in enumerate(builders):
            if stopTask:
                raise RuntimeError("用户停止脚本")
            can_flush(bus_id)
            msg = builder(UDS_REQ_ID, payload)
            try:
                ret = _transmit_one(bus_id, msg, wrap)
            except Exception as e:
                _log("transmit probe style=%d list=%d 异常: %s" % (bi, int(wrap), e))
                continue
            _log("[Tx probe] style=%d list=%d ret=%s %s" % (
                bi, int(wrap), str(ret), _hex(payload)))
            t0 = time.time()
            while time.time() - t0 < 0.35:
                for fr in can_recv(bus_id):
                    fid = _frame_id(fr)
                    d = _frame_data(fr)
                    _log("[Rx probe] id=%08X %s" % (fid, _hex(d[:8])))
                    if fid == UDS_RESP_ID and d:
                        _TX_STYLE = (bi, wrap)
                        _log("锁定 TX 格式 style=%d list=%d (MCU 应答)" % (bi, int(wrap)))
                        return
                    if fid == UDS_REQ_ID:
                        echo_style = (bi, wrap)
                time.sleep(0.02)
    if echo_style is not None:
        _TX_STYLE = echo_style
        _log("仅见 TX 回显，锁定 style=%d list=%d" % (echo_style[0], int(echo_style[1])))
        return
    _TX_STYLE = (0, True)
    _log("未探测到 MCU/回显，默认 style=0 list=1")


def isotp_request(bus_id, uds_payload, timeout_s=3.0):
    """发 UDS 载荷（含 SID），收完整 ISO-TP 响应。不走 uds_request。"""
    _log_zcan_apis()
    uds_payload = [int(x) & 0xFF for x in uds_payload]
    n = len(uds_payload)
    can_flush(bus_id)

    if n <= 7:
        can_send(bus_id, UDS_REQ_ID, [n] + uds_payload)
    else:
        ff = [0x10 | ((n >> 8) & 0x0F), n & 0xFF] + uds_payload[:6]
        can_send(bus_id, UDS_REQ_ID, ff)
        t0 = time.time()
        got_fc = False
        while time.time() - t0 < timeout_s:
            if stopTask:
                raise RuntimeError("用户停止脚本")
            for fr in can_recv(bus_id):
                d = _frame_data(fr)
                fid = _frame_id(fr)
                if fid != UDS_RESP_ID:
                    if d:
                        _log("[Rx 其它] id=%08X %s" % (fid, _hex(d[:8])))
                    continue
                if d and (d[0] & 0xF0) == 0x30:
                    _log("[Rx CAN FC] " + _hex(d[:8]))
                    got_fc = True
                    break
            if got_fc:
                break
            time.sleep(0.01)
        if not got_fc:
            raise RuntimeError("ISO-TP 未收到 MCU 流控")
        off = 6
        sn = 1
        while off < n:
            chunk = uds_payload[off:off + 7]
            can_send(bus_id, UDS_REQ_ID, [0x20 | (sn & 0x0F)] + chunk)
            off += len(chunk)
            sn = (sn + 1) & 0x0F
            time.sleep(0.001)

    buf = []
    total = 0
    expect_sn = 1
    saw_any = 0
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if stopTask:
            raise RuntimeError("用户停止脚本")
        frames = can_recv(bus_id)
        if not frames:
            time.sleep(0.01)
            continue
        for fr in frames:
            fid = _frame_id(fr)
            d = _frame_data(fr)
            if not d:
                continue
            saw_any += 1
            if fid != UDS_RESP_ID:
                _log("[Rx 其它] id=%08X %s" % (fid, _hex(d[:8])))
                continue
            pci = d[0] & 0xF0
            if pci == 0x00:
                ln = d[0] & 0x0F
                return d[1:1 + ln]
            if pci == 0x10:
                total = ((d[0] & 0x0F) << 8) | d[1]
                buf = list(d[2:])
                _log("[Rx CAN FF] len=%d %s" % (total, _hex(d[:8])))
                can_send(bus_id, UDS_REQ_ID, [0x30, 0x00, 0x01])
                expect_sn = 1
                t0 = time.time()
            elif pci == 0x20:
                sn = d[0] & 0x0F
                if sn != expect_sn:
                    raise RuntimeError("ISO-TP SN 错误 expect=%d got=%d" % (expect_sn, sn))
                buf.extend(d[1:])
                expect_sn = (expect_sn + 1) & 0x0F
                if total != 0 and len(buf) >= total:
                    return buf[:total]
        time.sleep(0.01)
    raise RuntimeError("ISO-TP 接收超时 (已收 %d/%d, 见过 %d 帧)" % (len(buf), total, saw_any))


def read_did(bus_id, did):
    """读取 DID。32 字节响应走自组 ISO-TP，避开 uds_request 多帧 bug。"""
    uds = isotp_request(
        bus_id,
        [SID_RDBI, (did >> 8) & 0xFF, did & 0xFF],
        timeout_s=3.0,
    )
    _log("[Rx ISO-TP] " + _hex(uds[:40]))
    if len(uds) < 1:
        raise RuntimeError("DID 0x%04X 空响应" % did)
    if uds[0] == SID_NRC:
        raise RuntimeError("NRC SID=0x%02X NRC=0x%02X" % (uds[1], uds[2] if len(uds) > 2 else 0))
    if uds[0] != (SID_RDBI + SID_PR):
        raise RuntimeError("非正响应 SID=0x%02X %s" % (SID_RDBI, _hex(uds)))
    if len(uds) < 3:
        raise RuntimeError("DID 0x%04X 响应过短" % did)
    return uds[3:]


def send_security_key(bus_id, sig):
    """发送64字节 ECDSA 签名: 0x27 0x03 分片(每帧4字节) + 0x27 0x02 验签"""
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


# ======== SN 写入流程 ========

def _probe_side(bus_id):
    """0x34: APP → NRC 0x11；Boot 默认会话 → NRC 0x22。两边都可以写 SN。"""
    try:
        uds_req(bus_id, SID_RD, [0x00])
        _log("0x34 正响应，按 Boot Programming 继续写 SN")
        return "BOOT"
    except RuntimeError as e:
        msg = str(e)
        if "NRC=0x11" in msg:
            _log("当前在 APP（0x34 NRC 0x11）")
            return "APP"
        if "NRC=0x22" in msg:
            _log("当前在 Bootloader（0x34 NRC 0x22），Safe Mode 写 SN")
            return "BOOT"
        raise


def run_sn_write(bus_id, sn_code):
    """完整 SN 写入流程（APP 或 Boot Safe Mode）"""
    _log("======== SN 写入流程 ========")
    _log("SN: %s (%d字节)" % (sn_code, len(sn_code)))
    _log("私钥: " + PRIVATE_KEY_PATH)

    if not os.path.isfile(PRIVATE_KEY_PATH):
        raise RuntimeError("找不到私钥: " + PRIVATE_KEY_PATH)
    priv = load_ec_private_key(PRIVATE_KEY_PATH)

    uds_init()

    # Step 0: 识别 APP / Boot（均可写 F18C）
    _log("---- Step 0: 识别 APP / Boot ----")
    _probe_side(bus_id)

    # Step 1: 进入编程会话（APP 内 10 02 不会复位；Boot 本身就是 Safe Mode）
    _log("---- Step 1: 进入编程会话 ----")
    uds_req(bus_id, SID_DSC, [0x02])

    # Step 2: 安全解锁
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

    # Step 3: 写入 SN（SID+DID+32B = 35B，ISO-TP 多帧；Flash 擦写可能 >P2）
    _log("---- Step 3: 写入 SN ----")
    if sys.version_info[0] >= 3:
        sn_bytes = sn_code.encode("ascii")
    else:
        sn_bytes = str(sn_code)
    if len(sn_bytes) > 32:
        raise RuntimeError("SN 超过32字节: %d" % len(sn_bytes))
    sn32 = _to_list(sn_bytes) + [0x20] * (32 - len(sn_bytes))
    _log("写入数据: " + _hex(sn32))
    uds_req(bus_id, SID_WDBI, [0xF1, 0x8C] + sn32, wait_pending_s=10)

    time.sleep(0.3)
    # 先用 uds_request 发 3E 刷新 S3（单帧，API 没问题），再释放 UDS 栈
    uds_try(bus_id, SID_TP, [0x00])
    _log("---- Step 4: 读回验证（自组 ISO-TP）----")
    release_uds_stack()
    _discover_tx(bus_id)
    rx = None
    last = None
    for i in range(4):
        if stopTask:
            raise RuntimeError("用户停止脚本")
        try:
            rx = read_did(bus_id, DID_SN)
            last = None
            break
        except Exception as e:
            last = e
            _log("22 F18C 第 %d/4 次: %s" % (i + 1, e))
            try:
                isotp_request(bus_id, [SID_TP, 0x00], timeout_s=1.0)
            except Exception:
                pass
            time.sleep(0.2)
    if last is not None:
        raise last
    sn_read = "".join(chr(int(b) & 0xFF) for b in rx[:32]).rstrip(" ")
    _log("读回 SN: [%s]" % sn_read)

    if sn_read == sn_code:
        _log("======== SN 写入成功 ========")
    else:
        raise RuntimeError("读回 SN 与写入不一致! 写入=[%s] 读回=[%s]" % (sn_code, sn_read))


# ======== 入口 ========

def z_main():
    global stopTask, _TX_STYLE, _UDS_RELEASED, _RX_DUMP, _ISOTP_LOGGED_API
    stopTask = False
    _TX_STYLE = None
    _UDS_RELEASED = False
    _RX_DUMP = 0
    _ISOTP_LOGGED_API = False
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
    # 独立运行模式（不依赖 zcanpro）
    _log("此脚本需要在 ZCANPRO 扩展脚本环境中运行")
    _log("或通过 zcanpro 模块调用")
