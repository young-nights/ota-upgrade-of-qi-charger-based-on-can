#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SN 产线批量写入工具（Windows 独立脚本，扫码枪版）

用法（cmd / PowerShell，仓库根目录）：
  python python_tools/sn_write_prod.py --port COM7
  python python_tools/sn_write_prod.py --port COM9

流程（循环）：
  1. 提示 "扫描 SN:" → 光标等待扫码枪输入（HID 键盘模拟，自动回车）
  2. 校验 SN 格式（ASCII 字母数字，1~32 字节）
  3. 探测运行侧 → 编程会话 → 安全解锁 (ECDSA P-256)
  4. 写入 SN (0x2E F18C)
  5. 读回验证 (0x22 F18C) 比对
  6. PASS / FAIL + 计数 → 自动提示下一台

退出: Ctrl+C
依赖: pip install pyserial
"""
from __future__ import print_function

import argparse
import os
import sys
import time
import binascii

HERE = os.path.dirname(os.path.abspath(__file__))
WSL_DIR = os.path.join(HERE, "wsl")
REPO_ROOT = os.path.dirname(HERE)
sys.path.insert(0, WSL_DIR)

from zqwl_can_listen import Zqwl  # noqa: E402

PRIVATE_KEY_PATH = os.path.join(REPO_ROOT, "docs", "keys", "private.pem")

# ======== SN 校验 ========
SN_MAX_LEN = 32
SN_ALLOWED_CHARS = set("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_")

# ======== UDS ========
UDS_REQ_ID = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D
SID_DSC = 0x10
SID_SA  = 0x27
SID_WDBI = 0x2E
SID_RDBI = 0x22
SID_NRC  = 0x7F
NRC_RCRRP = 0x78
NRC_EXCEEDED = 0x36
NRC_TIME_DELAY = 0x37
NRC_COND = 0x22
NRC_DENIED = 0x33

SA_SIG_CHUNK = 4
FC_FRAME = [0x30, 0x00, 0x00, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC]


# ======== 日志 ========
def log(msg):
    print(msg)
    sys.stdout.flush()


def _hex(data):
    return " ".join("%02X" % (int(b) & 0xFF) for b in (data or []))


# ======== ECDSA P-256 ========
_P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
_N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
_A = _P - 3
_GX = 0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296
_GY = 0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5


def _inv(x, m):
    x %= m
    return pow(x, -1, m) if sys.version_info[0] >= 3 else pow(x, m - 2, m)


def _jp_double(x, y, z):
    if z == 0 or y == 0:
        return 0, 0, 0
    ysq = (y * y) % _P
    m = (3 * x * x + _A * ((z * z) % _P) ** 2) % _P
    nx = (m * m - 2 * x * ysq) % _P
    ny = (m * (x * ysq - nx) - 8 * ysq * ysq) % _P
    return nx, ny, (2 * y * z) % _P


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
        return _jp_double(x1, y1, z1) if s1 == s2 else (0, 0, 0)
    h = (u2 - u1) % _P
    r = (s2 - s1) % _P
    h2 = (h * h) % _P
    h3 = (h * h2) % _P
    u1h2 = (u1 * h2) % _P
    nx = (r * r - h3 - 2 * u1h2) % _P
    ny = (r * (u1h2 - nx) - s1 * h3) % _P
    return nx, ny, (h * z1 * z2) % _P


def _jp_mul(k, px, py):
    k %= _N
    if k == 0:
        return 0, 0, 0
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


def load_ec_private_key(path):
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
    i = 0
    while i < len(der) - 2:
        if der[i] == 0x04 and der[i + 1] == 0x20:
            return int.from_bytes(der[i + 2:i + 34], "big")
        i += 1
    raise RuntimeError("无法解析 PEM 私钥")


def ecdsa_sign(priv, msg_hash):
    z = int.from_bytes(msg_hash, "big") % _N
    k = (int.from_bytes(msg_hash, "big") ^ priv) % (_N - 1) + 1
    for _ in range(32):
        kx, ky = _jp_mul(k, _GX, _GY)
        r = kx % _N
        if r == 0:
            k = (k * 2 + 1) % (_N - 1) + 1
            continue
        s = (_inv(k, _N) * (z + r * priv)) % _N
        if s == 0:
            k = (k * 2 + 1) % (_N - 1) + 1
            continue
        return r.to_bytes(32, "big") + s.to_bytes(32, "big")
    raise RuntimeError("ECDSA 签名失败")


# ======== ISO-TP over ZQWL ========
def _tp_tx(z, can_id, data, fill=0xCC):
    """ISO-TP 发送（SF/FF+CF），自动等 FC。"""
    d = list(data)
    n = len(d)
    if n <= 7:
        pkt = [n] + d + [fill] * (7 - n)
        z.send_can(can_id, pkt)
        return
    # FF
    ff_len = 0x1000 | n
    ff = [(ff_len >> 8) & 0xFF, ff_len & 0xFF] + d[:6]
    z.send_can(can_id, ff)
    # 等 FC
    t0 = time.time()
    fc = None
    while time.time() - t0 < 2.0:
        frames = z.pump()
        for cid, payload in frames:
            if cid == UDS_RESP_ID and payload and (payload[0] & 0xF0) == 0x30:
                fc = payload
                break
        if fc:
            break
    if fc is None:
        raise RuntimeError("ISO-TP FC 超时")
    stmin = fc[2] if len(fc) > 2 else 0
    if stmin > 0x7F:
        stmin = 0x7F
    stmin_s = stmin / 1000.0 if stmin <= 0x7F else 0.1
    # CF
    seq = 1
    off = 6
    while off < n:
        chunk = d[off:off + 7]
        pkt = [0x10 | (seq & 0x0F)] + chunk + [fill] * (7 - len(chunk))
        z.send_can(can_id, pkt)
        seq = (seq + 1) & 0x0F
        off += 7
        if stmin_s > 0:
            time.sleep(stmin_s)


def _tp_rx(z, timeout=2.0):
    """ISO-TP 接收，自动回 FC，组装完整 UDS payload。"""
    t0 = time.time()
    buf = b""
    total = 0
    while time.time() - t0 < timeout:
        frames = z.pump()
        for cid, payload in frames:
            if cid != UDS_RESP_ID or not payload:
                continue
            pci = payload[0]
            ftype = pci & 0xF0
            if ftype == 0x00:  # SF
                ln = pci & 0x0F
                return list(payload[1:1 + ln])
            elif ftype == 0x10:  # FF
                total = ((pci & 0x0F) << 8) | payload[1]
                buf = bytes(payload[2:])
                z.send_can(UDS_REQ_ID, FC_FRAME)
            elif ftype == 0x20:  # CF
                buf += bytes(payload[1:])
                if len(buf) >= total:
                    return list(buf[:total])
    return list(buf) if buf else []


def uds(z, sid, data, wait=3.0, suppress=0):
    """发送 UDS 请求，返回完整响应 payload。"""
    payload = [sid] + list(data)
    _tp_tx(z, UDS_REQ_ID, payload)
    if suppress:
        return []
    t_end = time.time() + wait
    while time.time() < t_end:
        resp = _tp_rx(z, timeout=min(1.0, t_end - time.time()))
        if not resp:
            continue
        if resp[0] == 0x7F and len(resp) >= 3:
            if resp[2] == NRC_RCRRP:
                time.sleep(0.3)
                continue
            raise RuntimeError("NRC SID=0x%02X NRC=0x%02X" % (sid, resp[2]))
        return resp
    return []


def keepalive(z):
    _tp_tx(z, UDS_REQ_ID, [0x3E, 0x80])


# ======== SN 校验 ========
def validate_sn(sn):
    if not sn:
        return False, "SN 为空"
    if len(sn) > SN_MAX_LEN:
        return False, "SN 超过 %d 字节 (%d)" % (SN_MAX_LEN, len(sn))
    bad = set(sn) - SN_ALLOWED_CHARS
    if bad:
        return False, "含非法字符: %s" % "".join(sorted(bad))
    return True, ""


# ======== 单台写入 ========
def write_one(z, priv, sn):
    # 1. 探测 APP
    keepalive(z)
    time.sleep(0.1)
    keepalive(z)
    time.sleep(0.3)
    try:
        rx = uds(z, SID_RDBI, [0x21, 0x13])
        if not rx or rx[0] != 0x62:
            log("  [FAIL] 设备无应答")
            return False
    except Exception as e:
        log("  [FAIL] 探测失败: %s" % e)
        return False

    # 2. 编程会话
    log("  10 02 编程会话...")
    uds(z, SID_DSC, [0x02])

    # 3. 安全解锁
    log("  27 解锁...")
    rx = uds(z, SID_SA, [0x01])
    if len(rx) < 34:
        log("  [FAIL] seed 响应过短")
        return False
    seed = bytes(rx[2:34])
    if seed == b"\x00" * 32:
        log("  已解锁 (seed=0)")
    else:
        sig = ecdsa_sign(priv, seed)
        for i in range(0, 64, SA_SIG_CHUNK):
            seq = (i // SA_SIG_CHUNK) + 1
            keepalive(z)
            uds(z, SID_SA, [0x03, seq] + list(sig[i:i + SA_SIG_CHUNK]), suppress=1)
        time.sleep(0.1)
        keepalive(z)
        rx = uds(z, SID_SA, [0x02], wait=15)
        if not rx or rx[0] != 0x67:
            log("  [FAIL] 解锁失败: %s" % _hex(rx))
            return False
        log("  解锁成功")

    # 4. 写入 SN
    sn_bytes = sn.encode("ascii")
    sn32 = list(sn_bytes) + [0x20] * (32 - len(sn_bytes))
    log("  2E F18C 写入 %s" % _hex(sn32))
    for attempt in range(2):
        try:
            keepalive(z)
            rx = uds(z, SID_WDBI, [0xF1, 0x8C] + sn32, wait=10)
            break
        except RuntimeError as e:
            if "NRC=0x22" in str(e) or "NRC=0x33" in str(e):
                if attempt == 0:
                    log("  NRC 重试：重新会话+解锁")
                    uds(z, SID_DSC, [0x02])
                    rx2 = uds(z, SID_SA, [0x01])
                    if len(rx2) >= 34 and bytes(rx2[2:34]) != b"\x00" * 32:
                        sig = ecdsa_sign(priv, bytes(rx2[2:34]))
                        for i in range(0, 64, SA_SIG_CHUNK):
                            seq = (i // SA_SIG_CHUNK) + 1
                            uds(z, SID_SA, [0x03, seq] + list(sig[i:i + SA_SIG_CHUNK]), suppress=1)
                        uds(z, SID_SA, [0x02], wait=15)
                    continue
            raise

    # 5. 读回验证
    log("  22 F18C 读回验证...")
    keepalive(z)
    rx = uds(z, SID_RDBI, [0xF1, 0x8C])
    if len(rx) < 5:
        log("  [FAIL] 读回失败")
        return False
    actual = bytes(rx[3:]).rstrip(b" ").decode("ascii", "replace")
    if actual.strip() == sn:
        log("  读回匹配: %s" % actual.strip())
        return True
    else:
        log("  读回不匹配: 期望[%s] 实际[%s]" % (sn, actual.strip()))
        return False


# ======== 主循环 ========
def main():
    ap = argparse.ArgumentParser(description="SN 产线批量写入")
    ap.add_argument("--port", required=True, help="串口 (COM7 / COM9 / /dev/ttyACM0)")
    ap.add_argument("--key", default=PRIVATE_KEY_PATH, help="ECDSA 私钥路径")
    args = ap.parse_args()

    log("╔══════════════════════════════════════════╗")
    log("║     Qi Charger SN 产线批量写入工具       ║")
    log("╚══════════════════════════════════════════╝")

    if not os.path.isfile(args.key):
        log("[FATAL] 找不到私钥: %s" % args.key)
        return 1

    log("打开串口 %s ..." % args.port)
    z = Zqwl(port=args.port)
    try:
        z.read_device()
        z.open_can0_250k()
        log("CAN0 250kbps 已打开")
    except Exception as e:
        log("[FATAL] 串口/CAN 初始化失败: %s" % e)
        return 1

    priv = load_ec_private_key(args.key)
    log("私钥已加载")

    pass_count = 0
    fail_count = 0
    total = 0

    log("")
    log("======== 等待扫码（扫码枪对准标签扫码即可）========")
    log("")

    try:
        while True:
            try:
                sys.stdout.write(">>> 扫描 SN #%d: " % (total + 1))
                sys.stdout.flush()
                sn = input().strip()
            except (EOFError, KeyboardInterrupt):
                log("\n用户中断")
                break

            if not sn:
                continue

            ok, err = validate_sn(sn)
            if not ok:
                log("[FAIL] SN 格式错误: %s" % err)
                fail_count += 1
                total += 1
                continue

            total += 1
            log("")
            log("======== 写入 SN #%d: %s ========" % (total, sn))

            try:
                success = write_one(z, priv, sn)
            except Exception as e:
                log("[FAIL] 异常: %s" % e)
                success = False

            if success:
                pass_count += 1
                log("  ✅ PASS  SN: %s" % sn)
            else:
                fail_count += 1
                log("  ❌ FAIL  SN: %s" % sn)

            log("  --- 累计: %d 台 (PASS %d / FAIL %d) ---" % (total, pass_count, fail_count))
            log("")

    finally:
        z.close()
        log("")
        log("======== 产线写入结束 ========")
        log("总产量: %d 台 | PASS: %d | FAIL: %d" % (total, pass_count, fail_count))

    return 0


if __name__ == "__main__":
    sys.exit(main())
