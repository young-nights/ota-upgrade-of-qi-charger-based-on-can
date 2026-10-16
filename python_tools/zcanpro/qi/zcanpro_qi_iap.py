# -*- coding: utf-8 -*-
"""
ZCANPRO 扩展脚本 — Qi 芯片 IAP 固件升级

流程（UDS OTA 路径，AGENTS.md Qi IAP 路径）：
  ① 探测 APP 在线（22 2113）
  ② 10 02 编程会话 + 27 01/02 安全解锁（ECDSA 27 03 分片）
  ③ 2E 20 10 04 选 MCU2 目标
  ④ 31 01 FF 00 擦除（MCU2 直通，无本机 Flash 擦除）
  ⑤ 34 00 44 RequestDownload（启动 Qi IAP prepare，延迟应答等 Qi ACK）
  ⑥ 36 × N TransferData（每包 ≤22B，经 UART 转发给 Qi 芯片）
  ⑦ 37 TransferExit（完成 IAP）
  ⑧ 22 2132 查 IAP 状态 + 22 2133 查 Qi 固件版本

固件格式：Qi 芯片裸 bin（无 XATO 头），最大 64KB（16-bit 地址空间）。
BSC 从 0x01 起递增，0xFF 回卷到 0x00（ISO 14229-1）。

用法: ZCANPRO → 高级功能 → 扩展脚本 → 打开本文件
      或命令行: python3 zcanpro_qi_iap.py --firmware <path>
运行前: 先打开 CAN 通道 (250 kbps, Classical CAN, 扩展帧)
"""

import argparse
import os
import sys
import time

try:
    import zcanpro
except ImportError:
    zcanpro = None

# ======== 常量 ========
TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(TOOLS_DIR)))

UDS_REQ_ID  = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

SID_DSC  = 0x10
SID_RDBI = 0x22
SID_SA   = 0x27
SID_WDBI = 0x2E
SID_RC   = 0x31
SID_RD   = 0x34
SID_TD   = 0x36
SID_RTE  = 0x37
SID_TP   = 0x3E
SID_NRC  = 0x7F
SID_PR   = 0x40

NRC_RCRRP = 0x78

DID_FW_TYPE       = 0x2010
DID_QI_IAP_STATUS = 0x2132
DID_QI_FW_VERSION = 0x2133

FW_TYPE_MCU2      = 0x04
QI_IAP_MAX_CHUNK  = 22     # 每包最大固件字节数（qi_protocol.h）
MAX_FW_SIZE       = 0xFFFF # Qi IAP 16-bit 地址空间

PRIVATE_KEY_PATH = os.path.join(REPO_ROOT, "docs", "keys", "private.pem")

# 固件搜索目录
QI_FW_DIRS = [
    os.path.join(REPO_ROOT, "python_tools", "iap bin"),
    os.path.join(REPO_ROOT, "qi_wireless_code_app", "mdk_project", "Objects"),
    os.path.join(TOOLS_DIR, "..", "app bin"),
]

FIRMWARE_OVERRIDE = None
stopTask = False


# ======== 工具函数 ========

def z_notify(type, obj):
    global stopTask
    if type == "stop":
        stopTask = True


def _log(msg):
    text = str(msg)
    if zcanpro is not None and hasattr(zcanpro, "write_log"):
        zcanpro.write_log(text)
    else:
        sys.stdout.write(text + "\n")
        sys.stdout.flush()


def _hex(data):
    if data is None:
        return ""
    return " ".join("%02X" % (int(b) & 0xFF) for b in data)


def _to_bytes(seq):
    if isinstance(seq, bytes):
        return seq
    return bytes(int(x) & 0xFF for x in seq)


def _to_list(b):
    return [int(x) & 0xFF for x in b]


# ======== ECDSA 签名（同 zcanpro_ext_ota_auto） ========

_P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
_N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
_A = _P - 3
_GX = 0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296
_GY = 0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5


def _inv(x, m):
    x %= m
    if sys.version_info >= (3, 8):
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
    return v.to_bytes(32, "big") if sys.version_info[0] >= 3 else bytes(bytearray(v.to_bytes(32, "big")))


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


def load_ec_private_key(path):
    import binascii
    raw = open(path, "rb").read()
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
        der = binascii.a2b_base64("".join(lines))
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


def _collect_octet32(buf, out, start, end):
    i = start
    while i < end:
        tag = buf[i]
        i += 1
        if i >= end:
            break
        ln = buf[i]
        i += 1
        if ln & 0x80:
            k = ln & 0x7F
            ln = 0
            for _m in range(k):
                if i >= end:
                    return
                ln = (ln << 8) | buf[i]
                i += 1
        if i + ln > end:
            break
        if tag == 0x04 and ln == 32:
            out.append(buf[i:i + 32])
        elif tag in (0x30, 0x31, 0xA0, 0xA1):
            _collect_octet32(buf, out, i, i + ln)
        i += ln


# ======== UDS 通信 ========

def uds_init():
    zcanpro.uds_init({
        "src_addr": UDS_REQ_ID,
        "dst_addr": UDS_RESP_ID,
        "response_timeout_ms": 3000,
        "use_canfd": 0,
        "canfd_brs": 0,
        "trans_ver": 0,
        "fill_byte": 0xCC,
        "frame_type": 1,
        "trans_stmin_valid": 1,
        "trans_stmin": 1,
        "enhanced_timeout_ms": 120000,
    })
    _log("UDS 就绪 0x%08X / 0x%08X 扩展帧" % (UDS_REQ_ID, UDS_RESP_ID))


def uds_req(bus_id, sid, payload, suppress=0, wait_pending_s=0):
    if stopTask:
        raise RuntimeError("用户停止")
    req = {
        "src_addr": UDS_REQ_ID, "dst_addr": UDS_RESP_ID,
        "suppress_response": 1 if suppress else 0,
        "sid": sid, "data": list(payload),
    }
    t_end = time.time() + float(wait_pending_s)
    while True:
        if stopTask:
            raise RuntimeError("用户停止")
        _log("[Tx] %02X %s%s" % (sid, _hex(payload[:16]) + (" ..." if len(payload) > 16 else ""),
                                 " (suppress)" if suppress else ""))
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
            raise RuntimeError("NRC SID=0x%02X NRC=0x%02X" % (data[1], data[2]))
        if not resp or not resp.get("result") or not data:
            raise RuntimeError("无应答 SID=0x%02X %s" % (sid, (resp or {}).get("result_msg", "")))
        if data[0] != (sid + SID_PR):
            raise RuntimeError("非正响应 SID=0x%02X %s" % (sid, _hex(data)))
        return data


def uds_try(bus_id, sid, payload, suppress=0):
    try:
        return uds_req(bus_id, sid, payload, suppress=suppress)
    except Exception as e:
        _log("可忽略: " + str(e))
        return None


# ======== 安全解锁 ========

def send_security_key(bus_id, sig, seed=None, priv=None):
    sig = _to_bytes(sig)
    if len(sig) != 64:
        raise RuntimeError("ECDSA 签名须 64 字节, 实际 %d" % len(sig))
    time.sleep(0.08)
    try:
        _log("SendKey 27 02 + 64 字节")
        return uds_req(bus_id, SID_SA, [0x02] + _to_list(sig), wait_pending_s=45)
    except RuntimeError as e:
        if "NRC=0x24" in str(e) and seed is not None and priv is not None:
            _log("27 02 NRC 0x24，重新 27 01 再送 27 02")
            time.sleep(0.1)
            rx = uds_req(bus_id, SID_SA, [0x01])
            if len(rx) >= 34:
                seed2 = _to_bytes(rx[2:34])
                if seed2 != b"\x00" * 32:
                    sig = ecdsa_sign_msg(priv, seed2)
            time.sleep(0.08)
            return uds_req(bus_id, SID_SA, [0x02] + _to_list(sig), wait_pending_s=45)
        if "NRC=0x12" not in str(e) and "NRC=0x13" not in str(e):
            raise
        _log("27 02 整包 NRC，改 27 03 分片")
    seq = 1
    off = 0
    while off < 64:
        piece = sig[off:off + 4]
        uds_req(bus_id, SID_SA, [0x03, seq] + _to_list(piece))
        off += len(piece)
        seq += 1
    _log("27 03 已送 64 字节 / %d 帧" % (seq - 1))
    time.sleep(0.15)
    return uds_req(bus_id, SID_SA, [0x02], wait_pending_s=45)


# ======== 固件选择 ========

def _pick_firmware():
    if FIRMWARE_OVERRIDE:
        if not os.path.isfile(FIRMWARE_OVERRIDE):
            raise RuntimeError("指定固件不存在: %s" % FIRMWARE_OVERRIDE)
        return os.path.abspath(FIRMWARE_OVERRIDE)
    candidates = []
    for d in QI_FW_DIRS:
        if not os.path.isdir(d):
            continue
        for name in os.listdir(d):
            if not name.endswith(".bin"):
                continue
            low = name.lower()
            # Qi 固件命名模式：qi_fw / mcu2 / qi_app / QI_IAP_Ver / qi_iap
            if any(k in low for k in ("qi_fw", "mcu2", "qi_app", "qi_iap", "iap_ver")):
                path = os.path.join(d, name)
                candidates.append((os.path.getmtime(path), path))
    if not candidates:
        raise RuntimeError("未找到 Qi 固件（qi_fw*/mcu2*/qi_app*.bin），"
                           "请用 --firmware 指定路径")
    candidates.sort(reverse=True)
    path = candidates[0][1]
    _log("载荷选择（mtime 最新）：%s" % path)
    return path


# ======== 主流程 ========

def run_qi_iap(bus_id):
    if not os.path.isfile(PRIVATE_KEY_PATH):
        raise RuntimeError("找不到私钥: " + PRIVATE_KEY_PATH)
    _log("私钥 " + PRIVATE_KEY_PATH)
    priv = load_ec_private_key(PRIVATE_KEY_PATH)

    # 选固件
    fw_path = _pick_firmware()
    fw_data = open(fw_path, "rb").read()
    fw_size = len(fw_data)
    _log("固件: %s（%d 字节）" % (fw_path, fw_size))
    if fw_size == 0 or fw_size > MAX_FW_SIZE:
        raise RuntimeError("固件大小 %d 超出范围 (1..%d)" % (fw_size, MAX_FW_SIZE))

    uds_init()

    # ---- Step 0: 探测 APP ----
    _log("---- Step 0: 探测 APP ----")
    rx = uds_req(bus_id, SID_RDBI, [0x21, 0x13])
    _log("22 2113 slot=%s" % _hex(rx[3:4]))

    # ---- Step 1: 编程会话 ----
    _log("---- Step 1: 编程会话 ----")
    uds_req(bus_id, SID_DSC, [0x02])
    _log("10 02 OK")

    # ---- Step 2: 安全解锁 ----
    _log("---- Step 2: 安全解锁 ----")
    rx = uds_req(bus_id, SID_SA, [0x01])
    seed = _to_bytes(rx[2:34])
    if seed == b"\x00" * 32:
        _log("已解锁 (seed=0)")
    else:
        sig = ecdsa_sign_msg(priv, seed)
        send_security_key(bus_id, sig, seed=seed, priv=priv)
        _log("SA 解锁 OK")

    # ---- Step 3: 选 MCU2 ----
    _log("---- Step 3: 选 MCU2 (2E 20 10 04) ----")
    uds_req(bus_id, SID_WDBI, [0x20, 0x10, FW_TYPE_MCU2])
    _log("FW_TYPE = MCU2 (0x04)")

    # ---- Step 4: 擦除（MCU2 直通） ----
    _log("---- Step 4: 擦除 (31 01 FF 00) ----")
    uds_req(bus_id, SID_RC, [0x01, 0xFF, 0x00], wait_pending_s=30)
    _log("擦除 OK（MCU2 直通）")

    # ---- Step 5: RequestDownload ----
    _log("---- Step 5: RequestDownload (34) ----")
    sz = [(fw_size >> 24) & 0xFF, (fw_size >> 16) & 0xFF,
          (fw_size >> 8) & 0xFF, fw_size & 0xFF]
    addr = [0x00, 0x00, 0x00, 0x00]  # MCU2 不关心地址
    rx = uds_req(bus_id, SID_RD, [0x00, 0x44] + addr + sz, wait_pending_s=15)
    max_block = (rx[2] << 8) | rx[3] if len(rx) >= 4 else QI_IAP_MAX_CHUNK
    _log("maxBlockLen=%d" % max_block)

    # ---- Step 6: TransferData ----
    _log("---- Step 6: TransferData (36) %d 字节 ----" % fw_size)
    chunk_size = min(max_block, QI_IAP_MAX_CHUNK)
    seq = 1
    off = 0
    total = fw_size
    t0 = time.time()
    while off < total:
        if stopTask:
            raise RuntimeError("用户停止")
        chunk = fw_data[off:off + chunk_size]
        uds_req(bus_id, SID_TD, [seq] + _to_list(chunk), wait_pending_s=10)
        off += len(chunk)
        seq = (seq + 1) & 0xFF
        pct = off * 100 // total
        if off == total or (off % (chunk_size * 20) == 0):
            elapsed = time.time() - t0
            speed = off / elapsed / 1024 if elapsed > 0 else 0
            _log("  %d/%d (%d%%) %.1fs %.1fKB/s" % (off, total, pct, elapsed, speed))
    _log("传输完成 %d/%d" % (off, total))

    # ---- Step 7: TransferExit ----
    _log("---- Step 7: TransferExit (37) ----")
    uds_req(bus_id, SID_RTE, [], wait_pending_s=15)
    _log("37 OK，Qi IAP 完成")

    # ---- Step 8: 验证 ----
    _log("---- Step 8: 验证 ----")
    time.sleep(1.0)
    try:
        rx = uds_req(bus_id, SID_RDBI, [0x21, 0x32])
        state = rx[3] if len(rx) > 3 else 0xFF
        prog = rx[4] if len(rx) > 4 else 0
        vmaj = rx[5] if len(rx) > 5 else 0
        vmin = rx[6] if len(rx) > 6 else 0
        vpat = rx[7] if len(rx) > 7 else 0
        state_cn = {0x00: "Idle", 0x01: "In Progress", 0x02: "Success",
                    0x03: "Failed", 0x04: "Wait ACK"}.get(state, "0x%02X" % state)
        _log("IAP 状态: %s (progress=%d%%) Qi版本=%d.%d.%d" % (state_cn, prog, vmaj, vmin, vpat))
    except Exception as e:
        _log("IAP 状态读取失败: %s" % e)

    try:
        rx = uds_req(bus_id, SID_RDBI, [0x21, 0x33])
        vmaj = rx[3] if len(rx) > 3 else 0
        vmin = rx[4] if len(rx) > 4 else 0
        vpat = rx[5] if len(rx) > 5 else 0
        _log("Qi 固件版本: %d.%d.%d" % (vmaj, vmin, vpat))
    except Exception as e:
        _log("Qi 版本读取失败: %s" % e)

    _log("======== Qi IAP 升级完成 ========")


def z_main():
    global stopTask
    stopTask = False
    _log("======== Qi IAP 固件升级 ========")
    buses = zcanpro.get_buses()
    _log("总线 " + str(buses))
    if not buses:
        _log("请先打开 CAN 通道 250kbps 扩展帧")
        return
    try:
        run_qi_iap(buses[0]["busID"])
    except Exception as e:
        _log("Qi IAP 失败: " + str(e))
    finally:
        try:
            zcanpro.uds_deinit()
        except Exception:
            pass


def _parse_cli_args():
    parser = argparse.ArgumentParser(description="Qi IAP 固件升级")
    parser.add_argument("--firmware", default=None,
                        help="Qi 固件 bin 路径（默认自动搜索 qi_fw*/mcu2*/qi_app*.bin）")
    args, _ = parser.parse_known_args()
    return args


if __name__ == "__main__":
    _cli = _parse_cli_args()
    FIRMWARE_OVERRIDE = _cli.firmware
    if zcanpro is None:
        _log("zcanpro 不可用：需在 ZCANPRO 扩展脚本环境运行")
        sys.exit(1)
    if (sys.argv and sys.argv[0].endswith(".py")):
        z_main()
