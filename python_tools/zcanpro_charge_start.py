# -*- coding: utf-8 -*-
"""
ZCANPRO 扩展脚本 — 启动 Qi 无线充电

流程：
  1. 进入扩展会话 (0x10 0x03)
  2. 安全解锁 (0x27 01 → 0x27 03 分片 → 0x27 02)
  3. 设置功率 (0x2E 21 0D，默认15W)
  4. 使能充电 (0x2E 21 01 01)
  5. 轮询充电状态 (0x22 21 02) 等待 CHARGING

导入: ZCANPRO → 高级功能 → 扩展脚本 → 打开本文件
运行前: 先打开 CAN 通道 (250 kbps, Classical CAN, 扩展帧)
"""

import os
import sys
import time

try:
    import zcanpro
except ImportError:
    zcanpro = None

# ======== 用户配置 ========
_TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(_TOOLS_DIR)
PRIVATE_KEY_PATH = os.path.join(REPO_ROOT, "docs", "keys", "private.pem")

# 功率档位: 0x01=5W, 0x02=10W, 0x03=15W
POWER_LEVEL = 0x03

# 充电状态轮询间隔（秒）
POLL_INTERVAL = 1.0

# 等待 CHARGING 超时（秒）
CHARGE_TIMEOUT = 30

SA_SIG_CHUNK = 4

# ======== UDS 常量 ========
UDS_REQ_ID = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

SID_DSC = 0x10
SID_SA  = 0x27
SID_WDBI = 0x2E
SID_RDBI = 0x22
SID_NRC  = 0x7F
SID_PR   = 0x40

NRC_RCRRP = 0x78

DID_CHARGER_ENABLE = 0x2101
DID_CHARGE_STATE   = 0x2102
DID_POWER_LIMIT    = 0x210D

# 充电状态值
CHARGE_DISABLED = 0x00
CHARGE_STANDBY  = 0x01
CHARGE_CHARGING = 0x04

# 功率档位映射
POWER_NAME = {0x01: "5W", 0x02: "10W", 0x03: "15W"}

# secp256r1
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
        raise ValueError("ECDSA 整数超出 32 字节")
    if sys.version_info[0] >= 3:
        return v.to_bytes(32, "big")
    return binascii.unhexlify("%064x" % v)


def ecdsa_sign_msg(priv, msg):
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


def read_did(bus_id, did):
    rx = uds_req(bus_id, SID_RDBI, [(did >> 8) & 0xFF, did & 0xFF])
    if len(rx) < 4:
        raise RuntimeError("DID 0x%04X 响应过短" % did)
    return rx[3:]


def send_security_key(bus_id, sig):
    sig = _to_bytes(sig)
    if len(sig) != 64:
        raise RuntimeError("ECDSA 签名须 64 字节")
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


def charge_state_name(state):
    return {
        0x00: "DISABLED",
        0x01: "STANDBY",
        0x02: "DEVICE_DETECTED",
        0x03: "NEGOTIATING",
        0x04: "CHARGING",
        0x05: "CHARGE_COMPLETE",
        0x06: "SUSPENDED_THERMAL",
        0x07: "SUSPENDED_FOD",
        0x08: "FAULT",
    }.get(state, "未知(0x%02X)" % state)


# ======== 主流程 ========

def run_charge_start(bus_id):
    _log("======== 启动 Qi 充电 ========")
    _log("功率: %s" % POWER_NAME.get(POWER_LEVEL, "未知"))
    _log("私钥: " + PRIVATE_KEY_PATH)

    if not os.path.isfile(PRIVATE_KEY_PATH):
        raise RuntimeError("找不到私钥: " + PRIVATE_KEY_PATH)
    priv = load_ec_private_key(PRIVATE_KEY_PATH)

    # Step 1: 进入扩展会话
    _log("---- Step 1: 进入扩展会话 ----")
    uds_req(bus_id, SID_DSC, [0x03])

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

    # Step 3: 设置功率
    _log("---- Step 3: 设置功率 %s ----" % POWER_NAME.get(POWER_LEVEL, "?"))
    uds_req(bus_id, SID_WDBI, [0x21, 0x0D, POWER_LEVEL])

    # Step 4: 使能充电
    _log("---- Step 4: 使能充电 ----")
    uds_req(bus_id, SID_WDBI, [0x21, 0x01, 0x01])
    _log("充电器已使能（g_qi_charger_enable=1）")
    _log("等待 PA0 霍尔检测到手机后 PB2 拉高...")

    # Step 5: 轮询充电状态
    _log("---- Step 5: 轮询充电状态 ----")
    t0 = time.time()
    while time.time() - t0 < CHARGE_TIMEOUT:
        if stopTask:
            raise RuntimeError("用户停止脚本")
        try:
            rx = read_did(bus_id, DID_CHARGE_STATE)
            state = rx[0]
            name = charge_state_name(state)
            _log("充电状态: 0x%02X (%s)" % (state, name))
            if state == CHARGE_CHARGING:
                _log("======== 充电已启动 ========")
                return
            if state in (0x06, 0x07, 0x08):
                _log("警告: 检测到故障状态 %s" % name)
        except Exception as e:
            _log("读取失败: %s" % e)
        time.sleep(POLL_INTERVAL)

    _log("超时: %d 秒内未进入 CHARGING 状态" % CHARGE_TIMEOUT)
    _log("请检查: 1) 手机是否已放上充电板 2) PA0 霍尔是否检测到手机")


def z_main():
    global stopTask
    stopTask = False
    _log("======== Qi 充电启动工具 ========")
    _log("功率: %s" % POWER_NAME.get(POWER_LEVEL, "?"))
    buses = zcanpro.get_buses()
    if not buses:
        _log("请先打开 CAN 通道 250kbps 扩展帧")
        return
    try:
        run_charge_start(buses[0]["busID"])
    except Exception as e:
        _log("启动充电失败: " + str(e))
    finally:
        try:
            zcanpro.uds_deinit()
        except Exception:
            pass
