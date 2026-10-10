# -*- coding: utf-8 -*-
"""
ZCANPRO 扩展脚本 — SN 产线批量写入（扫码枪版）

流程（循环）：
  1. 提示 "扫描 SN:" → 光标等待扫码枪输入（HID 键盘模拟，自动回车）
  2. 校验 SN 格式（ASCII 字母数字，1~32 字节）
  3. 探测运行侧 → 编程会话 → 安全解锁
  4. 写入 SN (0x2E F18C)
  5. 读回验证 (0x22 F18C) 比对
  6. PASS / FAIL + 计数 → 自动提示下一台

导入: ZCANPRO → 高级功能 → 扩展脚本 → 打开本文件 → 运行
运行前: 先打开 CAN 通道 (250 kbps, Classical CAN, 扩展帧)
退出:   停止脚本（ZCANPRO 停止按钮）或 Ctrl+C

扫码枪配置: HID 键盘模拟模式，后缀 = 回车(Enter)
"""

import os
import sys
import time
import binascii

try:
    import zcanpro
except ImportError:
    zcanpro = None

# ======== 路径 / 私钥 ========
def _find_repo_root(start):
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

# ======== SN 校验规则 ========
SN_MAX_LEN = 32
# 允许的字符：大写字母 + 数字 + 少量分隔符（按实际标签格式调整）
SN_ALLOWED_CHARS = set("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_")

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

NRC_RCRRP = 0x78
NRC_EXCEEDED_ATTEMPTS   = 0x36
NRC_REQUIRED_TIME_DELAY = 0x37
NRC_CONDITIONS_NOT_CORRECT = 0x22
NRC_SECURITY_ACCESS_DENIED = 0x33

SA_SIG_CHUNK = 4
S3_KEEPALIVE_INTERVAL_S = 3.0
SA_LOCKOUT_WAIT_S = 31.0

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


# ======== ECDSA P-256 签名（SecurityAccess） ========
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
    m = (3 * x * x + _A * ((z * z) % _P) * ((z * z) % _P)) % _P
    nx = (m * m - 2 * x * ysq) % _P
    ny = (m * (x * ysq - nx) - 8 * ysq * ysq) % _P
    nz = (2 * y * z) % _P
    return nx, ny, nz


def _jp_add(x1, y1, z1, x2, y2, z2):
    if z1 == 0:
        return x2, y2, z2
    if z2 == 0:
        return x1, y1, z1
    u1 = (x1 * z2 * z2) % _P
    u2 = (x2 * z1 * z1) % _P
    s1 = (y1 * z2 * z2 * z2) % _P
    s2 = (y2 * z1 * z1 * z1) % _P
    if u1 == u2:
        if s1 != s2:
            return 0, 0, 0
        return _jp_double(x1, y1, z1)
    h = (u2 - u1) % _P
    r = (s2 - s1) % _P
    h2 = (h * h) % _P
    h3 = (h * h2) % _P
    u1h2 = (u1 * h2) % _P
    nx = (r * r - h3 - 2 * u1h2) % _P
    ny = (r * (u1h2 - nx) - s1 * h3) % _P
    nz = (h * z1 * z2) % _P
    return nx, ny, nz


def _jp_mul(k, px, py):
    if k % _N == 0 or (px == 0 and py == 0):
        return 0, 0, 0
    k = k % _N
    rx, ry, rz = 0, 0, 0
    qx, qy, qz = px, py, 1
    while k:
        if k & 1:
            rx, ry, rz = _jp_add(rx, ry, rz, qx, qy, qz)
        qx, qy, qz = _jp_double(qx, qy, qz)
        k >>= 1
    if rz == 0:
        return 0, 0, 0
    invz = _inv(rz, _P)
    invz2 = (invz * invz) % _P
    return (rx * invz2) % _P, (ry * invz2 * invz) % _P


def _to_affine(x, y, z):
    if z == 0:
        return 0, 0
    invz = _inv(z, _P)
    invz2 = (invz * invz) % _P
    return (x * invz2) % _P, (y * invz2 * invz) % _P


def load_ec_private_key(path):
    """读 PEM 私钥，返回整数 d。"""
    with open(path, "rb") as f:
        pem = f.read()
    import base64
    b64 = b""
    in_body = False
    for line in pem.splitlines():
        if b"BEGIN" in line:
            in_body = True
            continue
        if b"END" in line:
            break
        if in_body:
            b64 += line.strip()
    der = base64.b64decode(b64)
    # 简易 ASN.1 解析：找 32 字节 OCTET STRING（私钥标量）
    # ECPrivateKey ::= SEQUENCE { version INTEGER, privateKey OCTET STRING, ... }
    i = 0
    while i < len(der) - 2:
        if der[i] == 0x04 and der[i + 1] == 0x20:  # OCTET STRING 32B
            return int.from_bytes(der[i + 2:i + 34], "big")
        i += 1
    raise RuntimeError("无法从 PEM 解析私钥")


def ecdsa_sign_msg(priv, msg_hash):
    """ECDSA sign 32B hash → (r, s) 32B each. k deterministic (RFC 6979 lite)."""
    z = int.from_bytes(msg_hash, "big") % _N
    k = (int.from_bytes(msg_hash, "big") ^ priv) % (_N - 1) + 1  # simplified k
    for _ in range(16):
        kx, ky = _jp_mul(k, _GX, _GY)
        r = kx % _N
        if r == 0:
            k = (k * 2 + 1) % (_N - 1) + 1
            continue
        s = (_inv(k, _N) * (z + r * priv)) % _N
        if s == 0:
            k = (k * 2 + 1) % (_N - 1) + 1
            continue
        return r.to_bytes(32, "big"), s.to_bytes(32, "big")
    raise RuntimeError("ECDSA sign failed")


# ======== UDS 通信 ========
def uds_init():
    if zcanpro is not None:
        zcanpro.uds_init({
            "response_timeout_ms": 3000,
            "use_canfd": 0,
            "canfd_brs": 0,
            "trans_ver": 0,
            "fill_byte": 0xCC,
            "frame_type": 1,
            "trans_stmin_valid": 1,
            "trans_stmin": 1,
            "enhanced_timeout_ms": 8000,
        })


class UdsNrcError(RuntimeError):
    def __init__(self, sid, nrc):
        RuntimeError.__init__(self, "NRC SID=0x%02X NRC=0x%02X" % (sid, nrc))
        self.sid = sid
        self.nrc = nrc


def uds_req(bus_id, sid, payload, suppress=0, wait_pending_s=0):
    req = {
        "src_addr": UDS_REQ_ID, "dst_addr": UDS_RESP_ID,
        "suppress_response": 1 if suppress else 0,
        "sid": sid, "data": list(payload),
    }
    t_end = time.time() + (wait_pending_s if wait_pending_s > 0 else 8.0)
    while True:
        resp = zcanpro.uds_request(bus_id, req)
        d = list((resp or {}).get("data") or [])
        if suppress:
            return []
        if len(d) >= 3 and d[0] == 0x7F:
            if d[2] == NRC_RCRRP and time.time() < t_end:
                _log("  [NRC 0x78 ResponsePending]")
                _keepalive(bus_id)
                time.sleep(0.3)
                continue
            raise UdsNrcError(sid, d[2])
        return d


def _keepalive(bus_id):
    req = {
        "src_addr": UDS_REQ_ID, "dst_addr": UDS_RESP_ID,
        "suppress_response": 1, "sid": 0x3E, "data": [0x80],
    }
    try:
        zcanpro.uds_request(bus_id, req)
    except Exception:
        pass


def _s3_keepalive_wait(bus_id, total_s):
    t0 = time.time()
    while time.time() - t0 < total_s:
        _keepalive(bus_id)
        time.sleep(S3_KEEPALIVE_INTERVAL_S)


# ======== SecurityAccess 解锁 ========
def send_security_key(bus_id, priv):
    """27 01 → 签名 → 27 03 分片 → 27 02"""
    rx = uds_req(bus_id, SID_SA, [0x01])
    if len(rx) < 34:
        raise RuntimeError("seed 响应过短: %d" % len(rx))
    seed = _to_bytes(rx[2:34])
    if seed == b"\x00" * 32:
        _log("  已解锁 (seed=0)")
        return
    _log("  seed(32B) " + _hex(rx[2:34]))
    r, s = ecdsa_sign_msg(priv, seed)
    sig = r + s  # 64B
    # 27 03 分片发送（每片 4B，共 16 片）
    for i in range(0, 64, SA_SIG_CHUNK):
        seq = (i // SA_SIG_CHUNK) + 1
        chunk = list(sig[i:i + SA_SIG_CHUNK])
        _keepalive(bus_id)
        uds_req(bus_id, SID_SA, [0x03, seq] + chunk, suppress=1)
    time.sleep(0.1)
    _keepalive(bus_id)
    rx = uds_req(bus_id, SID_SA, [0x02], wait_pending_s=15)
    if not rx or rx[0] != 0x67:
        raise RuntimeError("27 02 失败: " + _hex(rx))
    _log("  解锁成功")


def _wdbi_with_s3_guard(bus_id, payload, priv):
    """写 DID，NRC 0x22/0x33 时重新会话+解锁后重试一次"""
    for attempt in range(2):
        try:
            _keepalive(bus_id)
            rx = uds_req(bus_id, SID_WDBI, payload, wait_pending_s=10)
            return rx
        except UdsNrcError as e:
            if e.nrc in (NRC_CONDITIONS_NOT_CORRECT, NRC_SECURITY_ACCESS_DENIED) and attempt == 0:
                _log("  NRC 0x%02X: 重新会话+解锁后重试" % e.nrc)
                uds_req(bus_id, SID_DSC, [0x02])
                send_security_key(bus_id, priv)
                continue
            raise


# ======== SN 校验 ========
def validate_sn(sn):
    """校验 SN 格式。返回 (ok, error_msg)。"""
    if not sn:
        return False, "SN 为空"
    if len(sn) > SN_MAX_LEN:
        return False, "SN 超过 %d 字节 (实际 %d)" % (SN_MAX_LEN, len(sn))
    bad = set(sn) - SN_ALLOWED_CHARS
    if bad:
        return False, "含非法字符: %s" % "".join(sorted(bad))
    return True, ""


# ======== 读回验证 ========
def read_sn_verify(bus_id, expect_sn):
    """读 0x22 F18C 并比对。返回 (ok, actual_sn)。"""
    _keepalive(bus_id)
    rx = uds_req(bus_id, SID_RDBI, [0xF1, 0x8C])
    if len(rx) < 5:
        return False, "<读取失败>"
    raw = _to_bytes(rx[3:])  # 62 F1 8C + 32B
    actual = raw.rstrip(b" ").decode("ascii", "replace")
    expect_padded = expect_sn.ljust(32)
    actual_padded = raw.decode("ascii", "replace")
    return actual_padded.rstrip() == expect_sn, actual.strip()


# ======== 探测运行侧 ========
def probe_location(bus_id):
    """唤醒 + 确认 APP 在跑。"""
    # burst 3E 80 唤醒
    for _ in range(5):
        _keepalive(bus_id)
        time.sleep(0.1)
    time.sleep(0.3)
    # 22 2113 探测
    try:
        rx = uds_req(bus_id, SID_RDBI, [0x21, 0x13])
        if len(rx) >= 4 and rx[0] == 0x62:
            return "APP"
    except Exception:
        pass
    return "UNKNOWN"


# ======== 单台写入 ========
def write_one_sn(bus_id, sn_code, priv):
    """完整单台 SN 写入流程，返回 True/False。"""
    _log("---- Step 1: 探测运行侧 ----")
    verdict = probe_location(bus_id)
    if verdict != "APP":
        _log("  [FAIL] 设备无应答或不在 APP")
        return False

    _log("---- Step 2: 编程会话 + 解锁 ----")
    uds_req(bus_id, SID_DSC, [0x02])
    send_security_key(bus_id, priv)

    _log("---- Step 3: 写入 SN ----")
    sn_bytes = sn_code.encode("ascii")
    sn32 = _to_list(sn_bytes) + [0x20] * (32 - len(sn_bytes))
    _log("  数据: " + _hex(sn32))
    _wdbi_with_s3_guard(bus_id, [0xF1, 0x8C] + sn32, priv)

    _log("---- Step 4: 读回验证 ----")
    ok, actual = read_sn_verify(bus_id, sn_code)
    if ok:
        return True
    else:
        _log("  读回不匹配: 期望[%s] 实际[%s]" % (sn_code, actual))
        return False


# ======== 产线主循环 ========
def z_main():
    global stopTask
    stopTask = False

    _log("╔══════════════════════════════════════════╗")
    _log("║     Qi Charger SN 产线批量写入工具       ║")
    _log("╚══════════════════════════════════════════╝")

    if not os.path.isfile(PRIVATE_KEY_PATH):
        _log("[FATAL] 找不到私钥: " + PRIVATE_KEY_PATH)
        return

    buses = zcanpro.get_buses()
    if not buses:
        _log("[FATAL] 请先打开 CAN 通道 250kbps 扩展帧")
        return
    bus_id = buses[0]["busID"]
    _log("CAN 通道已就绪, bus=%d" % bus_id)
    _log("私钥: " + PRIVATE_KEY_PATH)

    try:
        priv = load_ec_private_key(PRIVATE_KEY_PATH)
    except Exception as e:
        _log("[FATAL] 私钥加载失败: " + str(e))
        return

    uds_init()

    pass_count = 0
    fail_count = 0
    total = 0

    _log("")
    _log("======== 等待扫码（扫码枪对准标签扫码即可）========")
    _log("")

    while not stopTask:
        try:
            # 产线输入：扫码枪 HID 模拟键盘输入 + 回车
            if zcanpro is not None:
                # ZCANPRO 环境：用 write_log 提示 + input() 阻塞
                _log(">>> 扫描 SN #%d:" % (total + 1))
            else:
                sys.stdout.write(">>> 扫描 SN #%d: " % (total + 1))
                sys.stdout.flush()

            sn = input().strip()
            if not sn:
                continue

            # 校验
            ok, err = validate_sn(sn)
            if not ok:
                _log("[FAIL] SN 格式错误: %s" % err)
                fail_count += 1
                total += 1
                continue

            total += 1
            _log("")
            _log("======== 写入 SN #%d: %s ========" % (total, sn))

            try:
                success = write_one_sn(bus_id, sn, priv)
            except Exception as e:
                _log("[FAIL] 写入异常: " + str(e))
                success = False

            if success:
                pass_count += 1
                _log("")
                _log("  ✅ PASS  SN: %s" % sn)
            else:
                fail_count += 1
                _log("")
                _log("  ❌ FAIL  SN: %s" % sn)

            _log("  --- 累计: %d 台 (PASS %d / FAIL %d) ---" % (total, pass_count, fail_count))
            _log("")

        except (EOFError, KeyboardInterrupt):
            _log("")
            _log("用户中断，退出产线模式")
            break
        except Exception as e:
            _log("[ERROR] " + str(e))
            fail_count += 1
            total += 1

    _log("")
    _log("======== 产线写入结束 ========")
    _log("总产量: %d 台 | PASS: %d | FAIL: %d" % (total, pass_count, fail_count))

    try:
        zcanpro.uds_deinit()
    except Exception:
        pass
