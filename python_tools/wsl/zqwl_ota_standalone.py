#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ZQWL-CANFD 独立 OTA 升级脚本（不依赖 ZCANPRO）。

全 raw ISO-TP：SF/FF/FC/CF 手动组帧，UDS 10/27/31/34/36/37 全链路。
ECDSA secp256r1 签名内嵌（与 zcanpro_ext_ota_auto.py 同源）。

用法（仓库根目录）:
  python3 python_tools/wsl/zqwl_ota_standalone.py --firmware "python_tools/app bin/app_image_v1_1_1.bin"
  python3 python_tools/wsl/zqwl_ota_standalone.py   # 自动选 app bin/ 下最新
"""
from __future__ import print_function
import argparse, binascii, hashlib, os, struct, sys, time, zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from zqwl_can_listen import Zqwl

# ============================================================
# 常量
# ============================================================
UDS_REQ_ID = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

SID_DSC, SID_RDBI, SID_SA = 0x10, 0x22, 0x27
SID_WDBI, SID_RC, SID_RD, SID_TD, SID_RTE = 0x2E, 0x31, 0x34, 0x36, 0x37
SID_NRC, SID_PR = 0x7F, 0x40
NRC_RCRRP = 0x78

IMAGE_MAGIC = 0x4F544158
IMAGE_HEADER_SIZE = 256
HDR_RESERVED_VER_OFF = 0x4C
HDR_RESERVED_VER_LEN = 16
APP_SIZE = 0xD000
BACKUP_BASE = 0x08010000
SLOT_SIZE = APP_SIZE
TRANSFER_BLOCK_DATA = 128
SA_SIG_CHUNK = 4
MAX_TD_DATA = 254

FC_RESPONSE = bytes([0x30, 0x00, 0x01, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC])

# secp256r1
_P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
_N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
_A = _P - 3
_GX = 0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296
_GY = 0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5

_REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PRIVATE_KEY_PATH = os.path.join(_REPO, "docs", "keys", "private.pem")


# ============================================================
# 日志
# ============================================================
def _log(msg):
    print(msg, flush=True)


def _hex(data, limit=24):
    d = list(data)[:limit]
    s = " ".join("%02X" % b for b in d)
    if len(data) > limit:
        s += " ..."
    return s


def _to_bytes(seq):
    if isinstance(seq, (bytes, bytearray)):
        return bytes(seq)
    return bytes(seq)


# ============================================================
# ECDSA secp256r1（与 zcanpro_ext_ota_auto.py 同源）
# ============================================================
def _int_be(b):
    return int.from_bytes(b, "big")


def _i2b32(v):
    return v.to_bytes(32, "big")


def _inv(x, m):
    return pow(x, m - 2, m)


def _jp_add(p1, p2):
    if p1 is None:
        return p2
    if p2 is None:
        return p1
    x1, y1, z1 = p1
    x2, y2, z2 = p2
    z1z1 = (z1 * z1) % _P
    z2z2 = (z2 * z2) % _P
    u1 = (x1 * z2z2) % _P
    u2 = (x2 * z1z1) % _P
    s1 = (y1 * z2 * z2z2) % _P
    s2 = (y2 * z1 * z1z1) % _P
    if u1 == u2:
        if s1 != s2:
            return None
        return _jp_dbl(p1)
    h = (u2 - u1) % _P
    i = ((2 * h) ** 2) % _P
    j = (h * i) % _P
    r = (2 * (s2 - s1)) % _P
    v = (u1 * i) % _P
    x3 = (r * r - j - 2 * v) % _P
    y3 = (r * (v - x3) - 2 * s1 * j) % _P
    z3 = (((z1 + z2) ** 2 - z1z1 - z2z2) * h) % _P
    return (x3, y3, z3)


def _jp_dbl(p):
    x, y, z = p
    ysq = (y * y) % _P
    s = (4 * x * ysq) % _P
    m = (3 * x * x + _A * ((z * z) % _P) * ((z * z) % _P)) % _P
    nx = (m * m - 2 * s) % _P
    ny = (m * (s - nx) - 8 * ysq * ysq) % _P
    nz = (2 * y * z) % _P
    return (nx, ny, nz)


def _jp_mul(k, x, y):
    r = None
    p = (x, y, 1)
    while k > 0:
        if k & 1:
            r = _jp_add(r, p)
        p = _jp_dbl(p)
        k >>= 1
    return r


def ecdsa_sign_msg(priv, msg):
    h = hashlib.sha256(msg).digest()
    z = _int_be(h) % _N
    while True:
        k = _int_be(os.urandom(32)) % _N
        if k == 0:
            continue
        jp = _jp_mul(k, _GX, _GY)
        x = (jp[0] * _inv(jp[2] * jp[2] % _P, _P)) % _P
        r = x % _N
        if r == 0:
            continue
        s = (_inv(k, _N) * (z + r * priv)) % _N
        if s == 0:
            continue
        return _i2b32(r) + _i2b32(s)


def load_ec_private_key(path):
    raw = open(path, "rb").read()
    text = raw.decode("ascii", "ignore")
    if "BEGIN" not in text:
        return _int_be(raw[:32]) if len(raw) >= 32 else _int_be(raw)
    import base64
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
    der = base64.b64decode("".join(lines))
    # SEC1 ECPrivateKey: find OCTET STRING 04 20 <32B>
    i = 0
    while i < len(der) - 34:
        if der[i] == 0x04 and der[i + 1] == 0x20:
            return _int_be(der[i + 2:i + 34])
        i += 1
    raise ValueError("无法从 PEM 解析 EC 私钥: " + path)


# ============================================================
# 镜像打包
# ============================================================
def _extract_sw_version(blob):
    marker = b"QC_JYF_MCU1_FW_"
    i = blob.find(marker)
    if i < 0:
        return None
    s = blob[i:i + 32].split(b"\x00")[0]
    try:
        return s.decode("ascii").strip()
    except Exception:
        return None


def pack_image_if_needed(fw_path, priv):
    data = open(fw_path, "rb").read()
    if len(data) >= IMAGE_HEADER_SIZE and struct.unpack_from("<I", data, 0)[0] == IMAGE_MAGIC:
        _log("固件已带 XATO 头, 总长 %d" % len(data))
        return data
    packed_len = IMAGE_HEADER_SIZE + len(data)
    if packed_len > SLOT_SIZE:
        raise RuntimeError("裸 bin %d + 头 256 = %d，超过槽大小 %d" % (len(data), packed_len, SLOT_SIZE))
    _log("固件无头，现场打包 %d 字节" % len(data))
    crc = zlib.crc32(data) & 0xFFFFFFFF
    sig = ecdsa_sign_msg(priv, data)
    hdr = struct.pack("<III", IMAGE_MAGIC, len(data), crc) + sig
    hdr += b"\x00" * HDR_RESERVED_VER_LEN
    hdr += struct.pack("<I", int(time.time()) & 0xFFFFFFFF)
    hdr += b"\x00" * (IMAGE_HEADER_SIZE - len(hdr))
    _log("打包完成 crc=0x%08X" % crc)
    return hdr + data


# ============================================================
# ISO-TP over ZQWL raw CAN
# ============================================================
class IsoTp:
    def __init__(self, dev):
        self.dev = dev
        self.rx_buf = bytearray()

    def _send_frame(self, data):
        self.dev.send_can(UDS_REQ_ID, data, ext=True)

    def _recv_frames(self, timeout=0.0):
        """收一批 CAN 帧，返回 [(can_id, data), ...]"""
        t0 = time.time()
        frames = []
        while True:
            for item in self.dev.pump():
                if item[0] == "can":
                    _, cid, ext, dat = item
                    frames.append((cid, bytes(dat)))
            if frames or (time.time() - t0) >= timeout:
                break
            time.sleep(0.001)
        return frames

    def send_sf(self, uds_bytes):
        """单帧发送（≤7B UDS）"""
        n = len(uds_bytes)
        assert n <= 7, "SF payload must be <=7, got %d" % n
        frame = bytes([n]) + uds_bytes + b"\xCC" * (7 - n)
        self._send_frame(frame)

    def send_mf(self, uds_bytes):
        """多帧发送（>7B UDS）：FF → 等 FC → CF..."""
        total = len(uds_bytes)
        assert total > 7
        # FF
        ff = bytes([0x10 | ((total >> 8) & 0x0F), total & 0xFF]) + uds_bytes[:6]
        ff += b"\xCC" * (8 - len(ff))
        self._send_frame(ff)
        _log("[Tx FF] %s" % _hex(ff, 8))
        # 等 FC（擦除后 FC 可能延迟，放宽到 5s）
        fc = self._wait_fc(timeout=5.0)
        if fc is None:
            raise RuntimeError("ISO-TP FC 超时")
        _log("[Rx FC] %s" % _hex(fc, 8))
        bs = fc[1] if len(fc) > 1 else 0
        stmin_raw = fc[2] if len(fc) > 2 else 0
        stmin = 0.001 if stmin_raw == 0 else (stmin_raw / 1000.0 if stmin_raw <= 0x7F else 0.001)
        # CF：FC 后稍等，给 ECU 留出进入 RX_IN_PROGRESS 的时间；SN=1 重发一次防丢
        offset = 6
        sn = 1
        cf_in_block = 0
        first_cf = True
        while offset < total:
            if bs != 0 and cf_in_block >= bs:
                fc = self._wait_fc(timeout=2.0)
                if fc is None:
                    raise RuntimeError("ISO-TP FC 超时 (BS)")
                bs = fc[1] if len(fc) > 1 else 0
                stmin_raw = fc[2] if len(fc) > 2 else 0
                stmin = 0.001 if stmin_raw == 0 else (stmin_raw / 1000.0 if stmin_raw <= 0x7F else 0.001)
                cf_in_block = 0
            if stmin > 0:
                time.sleep(stmin)
            chunk = uds_bytes[offset:offset + 7]
            cf = bytes([0x20 | (sn & 0x0F)]) + chunk + b"\xCC" * (8 - 1 - len(chunk))
            self._send_frame(cf)
            _log("[Tx CF] sn=%d %s" % (sn, _hex(cf, 8)))
            if first_cf:
                time.sleep(0.01)
                self._send_frame(cf)
                _log("[Tx CF-retry] sn=%d（防首帧丢失）" % sn)
                first_cf = False
            offset += len(chunk)
            sn = (sn + 1) & 0x0F
            cf_in_block += 1

    def _wait_fc(self, timeout=2.0):
        t0 = time.time()
        while time.time() - t0 < timeout:
            for cid, dat in self._recv_frames():
                if (cid & 0x1FFFFFFF) == (UDS_RESP_ID & 0x1FFFFFFF) and len(dat) >= 1:
                    _log("[FC wait] %s" % _hex(dat, 8))
                    if (dat[0] & 0xF0) == 0x30:
                        return dat
        return None

    def recv_response(self, timeout=3.0, expect_sid=None, expect_sub=None):
        """接收 UDS 应答（SF 或 MF），自动回 FC 组多帧。返回 UDS payload bytes。

        expect_sub: 子功能过滤（27 02 的正响应是 67 02，须与 27 03 的
        67 03 <seq> 区分）。NRC 也必须匹配 expect_sid，否则生命周期帧
        03 01 41 00 / 7F xx 78 会串台。
        """
        t0 = time.time()
        ff_buf = None
        ff_total = 0
        cf_buf = b""
        expect_sn = 1
        while time.time() - t0 < timeout:
            for cid, dat in self._recv_frames():
                if (cid & 0x1FFFFFFF) != (UDS_RESP_ID & 0x1FFFFFFF):
                    continue
                if len(dat) < 1:
                    continue
                pci = dat[0] & 0xF0
                _log("[Rx raw] pci=%02X %s" % (pci, _hex(dat, 8)))
                if pci == 0x00:  # SF
                    n = dat[0] & 0x0F
                    if n == 0 or n > 7:
                        continue
                    data = dat[1:1 + n]
                    if expect_sid is not None and len(data) >= 1:
                        if data[0] == SID_NRC:
                            # NRC 必须对上 SID，否则是别的服务/生命周期残留
                            if len(data) < 3 or data[1] != expect_sid:
                                continue
                        elif data[0] != (expect_sid + SID_PR):
                            continue
                        elif expect_sub is not None and (len(data) < 2 or data[1] != expect_sub):
                            continue
                    return bytes(data)
                elif pci == 0x10:  # FF
                    if ff_buf is not None:
                        continue
                    ff_total = ((dat[0] & 0x0F) << 8) | dat[1]
                    ff_buf = dat[2:8]
                    self._send_frame(FC_RESPONSE)
                elif pci == 0x20 and ff_buf is not None:  # CF
                    sn = dat[0] & 0x0F
                    if sn == expect_sn:
                        cf_buf += dat[1:8]
                        expect_sn = (expect_sn + 1) & 0x0F
                        if len(ff_buf) + len(cf_buf) >= ff_total:
                            return bytes((ff_buf + cf_buf)[:ff_total])
            time.sleep(0.001)
        raise RuntimeError("UDS 应答超时 (got %d/%d)" % (len(ff_buf) + len(cf_buf) if ff_buf else 0, ff_total))

    def drain(self, duration=0.1):
        """排空残留帧"""
        t0 = time.time()
        while time.time() - t0 < duration:
            self._recv_frames()
            time.sleep(0.01)


# ============================================================
# UDS 服务
# ============================================================
class Uds:
    def __init__(self, tp):
        self.tp = tp

    def request(self, sid, payload, expect_sid=True, timeout=3.0, wait_pending_s=45.0,
                expect_sub=None):
        """发送 UDS 请求并等应答。自动处理 NRC 0x78。"""
        uds = bytes([sid]) + bytes(payload)
        if len(uds) <= 7:
            self.tp.send_sf(uds)
        else:
            self.tp.send_mf(uds)
        t_end = time.time() + wait_pending_s
        while True:
            data = self.tp.recv_response(
                timeout=timeout, expect_sid=sid if expect_sid else None,
                expect_sub=expect_sub)
            if len(data) >= 3 and data[0] == SID_NRC:
                if data[2] == NRC_RCRRP:
                    if time.time() >= t_end:
                        raise RuntimeError("SID=0x%02X 只收到 NRC 0x78" % sid)
                    _log("  NRC 0x78 ResponsePending…")
                    time.sleep(1.0)
                    continue
                raise RuntimeError("NRC SID=0x%02X NRC=0x%02X" % (data[1], data[2]))
            return data


# ============================================================
# OTA 流程
# ============================================================
def run_ota(dev, fw_path):
    tp = IsoTp(dev)
    uds = Uds(tp)

    # 加载私钥 + 打包镜像
    if not os.path.isfile(PRIVATE_KEY_PATH):
        raise RuntimeError("找不到私钥: " + PRIVATE_KEY_PATH)
    _log("私钥 " + PRIVATE_KEY_PATH)
    priv = load_ec_private_key(PRIVATE_KEY_PATH)
    image = pack_image_if_needed(fw_path, priv)
    ver = _extract_sw_version(image)
    _log("镜像内 SW_VERSION_STR=%s" % (ver or "未找到"))

    # 探测
    _log("---- 探测 ----")
    tp.drain(0.2)
    rx = uds.request(SID_RDBI, [0x21, 0x13])
    _log("[Rx] %s" % _hex(rx))
    slot = rx[3] if len(rx) >= 4 else 0xFF
    _log("DID 0x2113=0x%02X（0x00=App 区）" % slot)

    # 读版本（可选）
    try:
        rx = uds.request(SID_RDBI, [0xF1, 0x95], timeout=5.0)
        vb = rx[3:].split(b"\x00")[0].decode("ascii", "ignore").strip() if len(rx) > 3 else ""
        _log("设备版本=%s" % vb)
    except Exception as e:
        _log("版本读取失败（不拦截）: %s" % e)

    # 10 02 编程会话
    _log("---- Programming (10 02) ----")
    for i in range(3):
        try:
            rx = uds.request(SID_DSC, [0x02])
            _log("[Rx] %s" % _hex(rx))
            break
        except Exception as e:
            _log("10 02 第 %d/3 次失败: %s" % (i + 1, e))
            time.sleep(0.5)
    else:
        raise RuntimeError("10 02 失败")

    # 27 01 获取 seed
    _log("---- SecurityAccess (27) ----")
    for i in range(3):
        try:
            rx = uds.request(SID_SA, [0x01], timeout=5.0)
            break
        except Exception as e:
            _log("27 01 第 %d/3 次失败: %s" % (i + 1, e))
            time.sleep(0.5)
    else:
        raise RuntimeError("27 01 失败")

    if len(rx) >= 34:
        seed = rx[2:34]
    elif len(rx) >= 6:
        seed = rx[2:6]
    else:
        raise RuntimeError("seed 响应过短")
    _log("seed %s" % _hex(seed, 32))

    if seed == b"\x00" * len(seed):
        _log("已解锁（seed=0），跳过 SendKey")
    else:
        sig = ecdsa_sign_msg(priv, seed)
        _log("SendKey 签名 %d 字节" % len(sig))
        # 27 03 分片发送（每帧 4B，共 16 帧）
        seq = 1
        off = 0
        while off < 64:
            piece = sig[off:off + SA_SIG_CHUNK]
            payload = [0x03, seq] + list(piece)
            for attempt in range(3):
                try:
                    rx = uds.request(SID_SA, payload, timeout=2.0)
                    break
                except Exception as e:
                    if attempt == 2:
                        raise
                    _log("27 03 %02X 超时，重试 %d/2" % (seq, attempt + 1))
                    time.sleep(0.2)
            off += len(piece)
            seq += 1
        _log("27 03 已送 64 字节 / %d 帧" % (seq - 1))
        time.sleep(0.15)
        # 27 02 触发验签
        for attempt in range(3):
            try:
                rx = uds.request(SID_SA, [0x02], timeout=8.0, wait_pending_s=90.0,
                                 expect_sub=0x02)
                _log("[Rx] %s" % _hex(rx))
                break
            except Exception as e:
                if attempt == 2:
                    raise
                _log("27 02 第 %d/3 次失败: %s" % (attempt + 1, e))
                time.sleep(1.0)
        _log("SecurityAccess 解锁成功")

    # 2E 20 10 01 选择 MCU1
    _log("---- DID 0x2010 MCU1 ----")
    rx = uds.request(SID_WDBI, [0x20, 0x10, 0x01])
    _log("[Rx] %s" % _hex(rx))

    # 31 01 FF 00 擦除
    _log("---- 擦除 Backup 区 ----")
    for i in range(3):
        try:
            rx = uds.request(SID_RC, [0x01, 0xFF, 0x00], timeout=10.0, wait_pending_s=60.0)
            _log("[Rx] %s" % _hex(rx))
            break
        except Exception as e:
            _log("31 擦除第 %d/3 次失败: %s" % (i + 1, e))
            time.sleep(1.0)
    else:
        raise RuntimeError("31 擦除失败")

    # 22 21 14 确认
    try:
        rx = uds.request(SID_RDBI, [0x21, 0x14])
        _log("DID 0x2114=0x%02X" % (rx[3] if len(rx) >= 4 else 0xFF))
    except Exception as e:
        _log("DID 0x2114 读失败（继续）: %s" % e)

    # 34 RequestDownload
    size = len(image)
    _log("---- RequestDownload %d @ 0x%08X ----" % (size, BACKUP_BASE))
    sz = [(size >> 24) & 0xFF, (size >> 16) & 0xFF, (size >> 8) & 0xFF, size & 0xFF]
    addr = [(BACKUP_BASE >> 24) & 0xFF, (BACKUP_BASE >> 16) & 0xFF,
            (BACKUP_BASE >> 8) & 0xFF, BACKUP_BASE & 0xFF]
    tp.drain(0.1)
    for i in range(3):
        try:
            rx = uds.request(SID_RD, [0x00, 0x44] + addr + sz, timeout=5.0)
            _log("[Rx] %s" % _hex(rx))
            break
        except Exception as e:
            _log("0x34 第 %d/3 次失败: %s" % (i + 1, e))
            # 确认 ECU 是否还活着
            try:
                tp.send_sf(bytes([0x3E, 0x00]))
                rx2 = tp.recv_response(timeout=2.0)
                _log("  [TP] 3E 00 → %s（ECU 活着）" % _hex(rx2))
            except Exception as e2:
                _log("  [TP] 3E 00 也失败: %s（ECU 可能挂了）" % e2)
            time.sleep(1.0)
    else:
        raise RuntimeError("0x34 失败")

    # 36 TransferData
    _log("---- TransferData ----")
    seq = 1
    off = 0
    fail_count = 0
    while off < size:
        chunk = image[off:off + TRANSFER_BLOCK_DATA]
        payload = [seq] + list(chunk)
        for attempt in range(3):
            try:
                rx = uds.request(SID_TD, payload, timeout=3.0)
                fail_count = 0
                break
            except Exception as e:
                if attempt == 2:
                    fail_count += 1
                    if fail_count > 10:
                        raise RuntimeError("36 连续失败过多")
                    _log("36 seq=0x%02X 第 %d/3 次失败: %s（跳过）" % (seq, attempt + 1, e))
                    break
                time.sleep(0.1)
        off += len(chunk)
        seq = (seq + 1) & 0xFF
        if off == size or (off % (TRANSFER_BLOCK_DATA * 32) == 0):
            _log("  %d/%d" % (off, size))
    _log("传输完成 %d 字节" % off)

    # 37 TransferExit
    _log("---- TransferExit (0x37) ----")
    for i in range(5):
        try:
            rx = uds.request(SID_RTE, [], timeout=5.0, wait_pending_s=60.0)
            _log("[Rx] %s" % _hex(rx))
            _log("[人话] 0x37 确认，设备将自复位并搬运")
            break
        except Exception as e:
            _log("0x37 第 %d/5 次: %s" % (i + 1, e))
            time.sleep(1.0)
    else:
        _log("0x37 无最终应答（设备可能已自复位）")

    # 等待复位
    _log("---- 等待设备自复位 ----")
    time.sleep(3.0)

    # 复位后确认
    _log("---- 复位后确认 ----")
    for i in range(10):
        try:
            tp.drain(0.2)
            rx = uds.request(SID_RDBI, [0x21, 0x13], timeout=3.0)
            slot = rx[3] if len(rx) >= 4 else 0xFF
            _log("DID 0x2113=0x%02X" % slot)
            try:
                rx = uds.request(SID_RDBI, [0xF1, 0x95], timeout=5.0)
                vb = rx[3:].split(b"\x00")[0].decode("ascii", "ignore").strip() if len(rx) > 3 else ""
                _log("设备版本=%s" % vb)
            except Exception:
                vb = None
            if ver and vb:
                if vb == ver:
                    _log("======== OTA 成功：版本 %s = 预期 %s ========" % (vb, ver))
                else:
                    _log("======== OTA 版本不符：设备=%s 预期=%s ========" % (vb, ver))
            return
        except Exception as e:
            _log("复位后第 %d/10 次探测失败: %s" % (i + 1, e))
            time.sleep(1.5)
    _log("======== OTA 结果无法确认 ========")


def _pick_firmware():
    fw_dir = os.path.join(_REPO, "python_tools", "app bin")
    if not os.path.isdir(fw_dir):
        raise RuntimeError("firmware dir not found: " + fw_dir)
    candidates = []
    for name in sorted(os.listdir(fw_dir)):
        if name.endswith(".bin"):
            path = os.path.join(fw_dir, name)
            candidates.append((path, os.path.getmtime(path)))
    if not candidates:
        raise RuntimeError("app bin/ 下无 .bin 文件")
    candidates.sort(key=lambda t: t[1], reverse=True)
    _log("自动选择: %s" % candidates[0][0])
    return candidates[0][0]


def main():
    ap = argparse.ArgumentParser(description="ZQWL-CANFD 独立 OTA（无 ZCANPRO）")
    ap.add_argument("--firmware", default=None, help="固件 bin 路径")
    ap.add_argument("--port", default="/dev/ttyACM0")
    args = ap.parse_args()

    fw = args.firmware or _pick_firmware()
    if not os.path.isfile(fw):
        raise RuntimeError("固件不存在: " + fw)

    _log("======== Qi 独立 OTA（raw ISO-TP）========")
    _log("固件: %s" % fw)
    dev = Zqwl(args.port)
    try:
        dev.read_device()
        dev.open_can0_250k()
        _log("CAN0 250kbps 扩展帧已打开")
        run_ota(dev, fw)
    finally:
        dev.close()


if __name__ == "__main__":
    main()
