# -*- coding: utf-8 -*-
"""
ZCANPRO 扩展脚本 — Qi 芯片 IAP 固件升级

通过 CAN-UDS 将 Qi 无线充芯片固件推送给 MCU，MCU 再通过 UART 转发给 Qi 芯片。

流程：
  1. 进入编程会话 + 安全解锁
  2. DID 0x2130 启动 IAP（传固件大小）
  3. DID 0x2131 分包发送固件数据（22字节/包）
  4. DID 0x2132 轮询升级状态

导入: ZCANPRO → 高级功能 → 扩展脚本 → 打开本文件
运行前: 先打开 CAN 通道 (250 kbps, Classical CAN, 扩展帧)
固件目录: python_tools/iap bin/（13V.BIN / 14V.BIN）
"""

import os
import sys
import time
import struct

try:
    import zcanpro
except ImportError:
    zcanpro = None

# ======== 用户配置 ========
_TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
FIRMWARE_DIR = os.path.join(_TOOLS_DIR, "iap bin")

# 固件文件名，改为你要升级的文件
# 13V.BIN = 13V 版本固件，14V.BIN = 14V 版本固件
FIRMWARE_NAME = "13V.BIN"

# Qi IAP 每包数据长度（协议定义最大 22 字节）
QI_IAP_DATA_LEN = 22

# 升级状态轮询间隔（秒）
STATUS_POLL_INTERVAL = 1.0

# 升级状态超时（秒）
STATUS_TIMEOUT = 120

# ======== UDS 常量 ========
UDS_REQ_ID = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

SID_DSC = 0x10
SID_SA = 0x27
SID_RDBI = 0x22
SID_WDBI = 0x2E
SID_NRC = 0x7F
SID_PR = 0x40

NRC_RCRRP = 0x78

# ======== Qi IAP DID ========
DID_QI_IAP_CONTROL = 0x2130
DID_QI_IAP_DATA = 0x2131
DID_QI_IAP_STATUS = 0x2132

# IAP 状态码
QI_IAP_IDLE = 0x00
QI_IAP_IN_PROGRESS = 0x01
QI_IAP_SUCCESS = 0x02
QI_IAP_FAILED = 0x03

# ======== 私钥路径 ========
PRIVATE_KEY_PATH = os.path.join(REPO_ROOT, "docs", "keys", "private.pem") if 'REPO_ROOT' in dir() else os.path.join(os.path.dirname(_TOOLS_DIR), "docs", "keys", "private.pem")

stopTask = False


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


def z_notify(type, obj):
    _log("Notify " + str(type) + " " + str(obj))
    if type == "stop":
        global stopTask
        stopTask = True


# ======== ECDSA 签名（复用 ota 脚本的逻辑） ========
_P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
_N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
_A = _P - 3
_GX = 0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296
_GY = 0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5


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
    if sys.version_info[0] >= 3:
        return v.to_bytes(32, "big")
    return binascii.unhexlify("%064x" % v)


def ecdsa_sign_msg(priv, msg):
    import hashlib
    h = hashlib.sha256(msg).digest()
    z = int.from_bytes(h, "big") % _N
    while True:
        k = int.from_bytes(os.urandom(32), "big") % _N
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
        tag = buf[i] if isinstance(buf[i], int) else ord(buf[i])
        i += 1
        if i >= end:
            break
        first = buf[i] if isinstance(buf[i], int) else ord(buf[i])
        i += 1
        if first < 0x80:
            ln = first
        else:
            n = first & 0x7F
            if n == 0 or n > 4 or i + n > end:
                break
            ln = 0
            for _k in range(n):
                ln = (ln << 8) | (buf[i] if isinstance(buf[i], int) else ord(buf[i]))
                i += 1
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


def load_ec_private_key(path):
    raw = open(path, "rb").read()
    if len(raw) == 32:
        priv = int.from_bytes(raw, "big")
        if 0 < priv < _N:
            return priv
    text = raw.decode("ascii", "ignore")
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
        priv = int.from_bytes(key, "big")
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
    _log("UDS 就绪 0x18DA0D03 / 0x18DA030D 扩展帧")


def uds_req(bus_id, sid, payload, suppress=0, wait_pending_s=0):
    global stopTask
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
            _log("[Tx] %02X %s" % (sid, _hex(payload[:16]) + (" ..." if len(payload) > 16 else "")))
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
                    raise RuntimeError("SID=0x%02X 只收到 NRC 0x78" % sid)
                _log("SID=0x%02X NRC 0x78，MCU 忙，继续等待" % sid)
                time.sleep(1.0)
                continue
            raise RuntimeError("NRC SID=0x%02X NRC=0x%02X" % (sid, data[2]))
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
    """读取 DID，返回数据字节列表"""
    rx = uds_req(bus_id, SID_RDBI, [(did >> 8) & 0xFF, did & 0xFF])
    # 响应格式: 62 [DID_H] [DID_L] [data...]
    if len(rx) < 4:
        raise RuntimeError("DID 0x%04X 响应过短" % did)
    return rx[3:]


def write_did(bus_id, did, data):
    """写入 DID"""
    payload = [(did >> 8) & 0xFF, did & 0xFF] + list(data)
    return uds_req(bus_id, SID_WDBI, payload)


# ======== Qi IAP 流程 ========

def qi_iap_start(bus_id, fw_size):
    """启动 Qi IAP：通知 MCU 开始升级，传固件大小"""
    _log("---- 启动 Qi IAP (固件大小 %d 字节) ----" % fw_size)
    size_hi = (fw_size >> 8) & 0xFF
    size_lo = fw_size & 0xFF
    write_did(bus_id, DID_QI_IAP_CONTROL, [0x01, size_hi, size_lo])
    _log("Qi IAP 启动成功")


def qi_iap_abort(bus_id):
    """中止 Qi IAP"""
    _log("---- 中止 Qi IAP ----")
    write_did(bus_id, DID_QI_IAP_CONTROL, [0x02])
    _log("Qi IAP 已中止")


def qi_iap_send_data(bus_id, addr, data):
    """发送一包固件数据"""
    addr_hi = (addr >> 8) & 0xFF
    addr_lo = addr & 0xFF
    payload = [addr_hi, addr_lo] + list(data)
    write_did(bus_id, DID_QI_IAP_DATA, payload)


def qi_iap_read_status(bus_id):
    """读取 Qi IAP 状态"""
    data = read_did(bus_id, DID_QI_IAP_STATUS)
    if len(data) < 2:
        raise RuntimeError("Qi IAP 状态响应过短")
    state = data[0]
    progress = data[1]
    return state, progress


def qi_iap_wait_complete(bus_id, timeout_s=120):
    """轮询等待升级完成"""
    _log("---- 等待 Qi IAP 完成 ----")
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if stopTask:
            raise RuntimeError("用户停止脚本")
        state, progress = qi_iap_read_status(bus_id)
        state_str = {0x00: "空闲", 0x01: "升级中", 0x02: "成功", 0x03: "失败"}.get(state, "未知(%02X)" % state)
        _log("  状态: %s, 进度: %d%%" % (state_str, progress))
        if state == QI_IAP_SUCCESS:
            _log("Qi IAP 升级成功!")
            return True
        if state == QI_IAP_FAILED:
            raise RuntimeError("Qi IAP 升级失败!")
        time.sleep(STATUS_POLL_INTERVAL)
    raise RuntimeError("Qi IAP 超时 (%d 秒)" % timeout_s)


# ======== 签名相关（用于 SecurityAccess） ========

def send_security_key(bus_id, sig):
    sig_bytes = _to_list(sig) if not isinstance(sig, list) else sig
    if len(sig_bytes) != 64:
        raise RuntimeError("签名须 64 字节")
    SA_SIG_CHUNK = 4
    seq = 1
    off = 0
    while off < 64:
        piece = sig_bytes[off:off + SA_SIG_CHUNK]
        uds_req(bus_id, SID_SA, [0x03, seq] + piece)
        off += len(piece)
        seq += 1
    _log("27 03 已送 64 字节 / %d 帧" % (seq - 1))
    time.sleep(0.15)
    uds_req(bus_id, SID_SA, [0x02], wait_pending_s=45)


# ======== 主流程 ========

def run_qi_iap(bus_id):
    global stopTask

    # 1. 检查固件文件
    fw_path = os.path.join(FIRMWARE_DIR, FIRMWARE_NAME)
    if not os.path.isfile(fw_path):
        raise RuntimeError("找不到固件: " + fw_path)

    fw_data = open(fw_path, "rb").read()
    fw_size = len(fw_data)
    _log("固件: %s (%d 字节)" % (fw_path, fw_size))

    # 2. 检查私钥
    if not os.path.isfile(PRIVATE_KEY_PATH):
        raise RuntimeError("找不到私钥: " + PRIVATE_KEY_PATH)
    priv = load_ec_private_key(PRIVATE_KEY_PATH)

    uds_init()

    try:
        # 3. 进入编程会话
        _log("---- 编程会话 ----")
        uds_req(bus_id, SID_DSC, [0x02])

        # 4. 安全解锁
        _log("---- SecurityAccess ----")
        rx = uds_req(bus_id, SID_SA, [0x01])
        seed = rx[2:6]
        if seed == [0, 0, 0, 0]:
            _log("已解锁 (seed=0)")
        else:
            _log("seed " + _hex(seed))
            sig = ecdsa_sign_msg(priv, bytes(seed))
            send_security_key(bus_id, sig)

        # 5. 启动 Qi IAP
        qi_iap_start(bus_id, fw_size)

        # 6. 分包发送固件
        _log("---- 发送固件数据 ----")
        addr = 0
        total_packets = (fw_size + QI_IAP_DATA_LEN - 1) // QI_IAP_DATA_LEN
        pkt_idx = 0
        while addr < fw_size:
            if stopTask:
                qi_iap_abort(bus_id)
                raise RuntimeError("用户停止")
            chunk = fw_data[addr:addr + QI_IAP_DATA_LEN]
            qi_iap_send_data(bus_id, addr, chunk)
            pkt_idx += 1
            addr += len(chunk)
            if pkt_idx % 10 == 0 or addr >= fw_size:
                _log("  已发送 %d/%d 包 (%d/%d 字节)" % (pkt_idx, total_packets, addr, fw_size))

        _log("固件发送完成，共 %d 包" % pkt_idx)

        # 7. 等待升级完成
        qi_iap_wait_complete(bus_id, STATUS_TIMEOUT)

        _log("======== Qi IAP 升级完成 ========")

    finally:
        zcanpro.uds_deinit()


def z_main():
    global stopTask
    stopTask = False
    _log("======== Qi 芯片 IAP 升级 ========")
    _log("固件目录: " + FIRMWARE_DIR)
    _log("固件文件: " + FIRMWARE_NAME)

    # 列出可用固件
    if os.path.isdir(FIRMWARE_DIR):
        files = [f for f in os.listdir(FIRMWARE_DIR) if f.endswith(".BIN") or f.endswith(".bin")]
        _log("可用固件: " + str(files))

    buses = zcanpro.get_buses()
    _log("总线 " + str(buses))
    if not buses:
        _log("请先打开 CAN 通道 250kbps 扩展帧")
        return
    try:
        run_qi_iap(buses[0]["busID"])
    except Exception as e:
        _log("Qi IAP 失败: " + str(e))
