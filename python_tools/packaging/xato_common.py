#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""XATO 镜像公共工具（packaging/ 自包含）。

原 zcanpro_ext_ota_auto.py 的 XATO 打包/校验/ECDSA 子集，2026-10-28 随
zcanpro/ 目录删除收编至此，供 pack_image.py 等打包脚本引用。
版本号唯一定义在固件 SW_VERSION_STR（can_protocol.c），镜像头不携带版本。
"""
from __future__ import print_function

import binascii
import hashlib
import os
import struct
import sys
import time
import zlib


def _find_repo_root(start):
    """逐级向上探测含 docs/keys 的仓库根（禁止固定层级 dirname 拼接）。"""
    d = os.path.abspath(start)
    for _i in range(8):
        if os.path.isdir(os.path.join(d, "docs", "keys")):
            return d
        parent = os.path.dirname(d)
        if parent == d:
            break
        d = parent
    return os.path.abspath(start)


REPO_ROOT = _find_repo_root(os.path.dirname(os.path.abspath(__file__)))
PUBLIC_KEY_PATH = os.path.join(REPO_ROOT, "docs", "keys", "public.pem")

# ---- XATO 镜像头常量（与 boot_verify.c / image_header_t 同源）----
IMAGE_MAGIC = 0x4F544158
IMAGE_HEADER_SIZE = 256
# 0x4C 起 16B：原 image_header_t.version 字段已删除，保留占位固定填 0x00
HDR_RESERVED_VER_OFF = 0x4C
HDR_RESERVED_VER_LEN = 16
HDR_BUILD_TS_OFF = 0x5C
# ---- Single-App 分区常量（OTA-ARCH-0920；与 boot_metadata.h 同源）----
APP_BASE = 0x08003000
APP_SIZE = 0xD000
APP_ENTRY = 0x08003100
SLOT_SIZE = APP_SIZE

# secp256r1 / prime256v1. n 必须与 bootloader uECC.c 的 N[] 一致。
_P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
_N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
_A = _P - 3
_GX = 0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296
_GY = 0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5

def _log(msg):
    sys.stdout.write(str(msg) + "\n")
    sys.stdout.flush()


def _u8(buf, i):
    v = buf[i]
    return v if isinstance(v, int) else ord(v)


def _int_be(b):
    if sys.version_info[0] >= 3:
        return int.from_bytes(b, "big")
    return int(binascii.hexlify(b), 16)


def _inv(x, m):
    x %= m
    # pow(x, -1, m) 需 Python 3.8+；3.0–3.7 会抛
    # "pow() 2nd argument cannot be negative when 3rd argument specified"
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
    if v < 0 or v >= (1 << 256):
        raise ValueError("ECDSA 整数超出 32 字节: bit_length=%d" % v.bit_length())
    if sys.version_info[0] >= 3:
        return v.to_bytes(32, "big")
    return binascii.unhexlify("%064x" % v)


def ecdsa_sign_msg(priv, msg):
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


# secp256r1 曲线常数 b（验签专用；_A/_P 同上）。
_P256_B = 0x5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B


def _load_ec_public_key(path):
    """解析 docs/keys/public.pem（SPKI PEM/DER）→ (x, y) 仿射坐标。

    仅做字节解析，不触碰任何签名路径数学。解析按 ASN.1 结构走：
    在 DER 中检索 BIT STRING（tag 0x03），内容须为
    [unused_bits=0x00, 点标记=0x04, X(32B), Y(32B)]。
    标准 P-256 SPKI 该 BIT STRING 长度字节=0x42（=1+65）；此前按
    0x44 硬匹配在本仓库 public.pem 上必失败（2026-09-19 冒烟
    S1-S5/S7 全红根因），已改为按 DER 结构解析。"""
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
    n = len(der)
    i = 0
    while i < n:
        if _u8(der, i) == 0x03:                 # BIT STRING
            j = i + 1
            if j >= n:
                break
            ln = _u8(der, j)
            j += 1
            if ln & 0x80:                       # 长形式（P-256 SPKI 不会走到）
                k = ln & 0x7F
                if k == 0 or k > 4 or (j + k) > n:
                    i += 1
                    continue
                ln = 0
                for _m in range(k):
                    ln = (ln << 8) | _u8(der, j)
                    j += 1
            # 标准非压缩 P-256 点：1 unused-bits + 1 点标记 + 32 X + 32 Y = 66
            if (ln == 66 and (j + 66) <= n and
                    _u8(der, j) == 0x00 and _u8(der, j + 1) == 0x04):
                pt = der[j + 2:j + 66]
                x = _int_be(pt[:32])
                y = _int_be(pt[32:64])
                if 0 < x < _P and 0 < y < _P:
                    return x, y
            i += 1
            continue
        i += 1
    raise ValueError("无法从公钥解析 P-256 非压缩点（期望 DER 内 BIT STRING "
                     "03 42 00 04 <X 32B> <Y 32B>）: " + path)


def _aff_add(p1, p2):
    """仿射点加（验签专用独立实现；None=无穷远点）。

    与 ecdsa_sign_msg 的 _jp_* 雅可比实现刻意不同源：签名与自检验签
    共用同一套点运算时，同一失效模式会一起错（c81bb18 _jp_add 损坏
    实机 0x35 教训；SA 签名自检纪律同源要求）。"""
    if p1 is None:
        return p2
    if p2 is None:
        return p1
    x1, y1 = p1
    x2, y2 = p2
    if x1 == x2:
        if (y1 + y2) % _P == 0:
            return None
        lam = ((3 * x1 * x1 + _A) * _inv(2 * y1, _P)) % _P
    else:
        lam = ((y2 - y1) * _inv(x2 - x1, _P)) % _P
    x3 = (lam * lam - x1 - x2) % _P
    y3 = (lam * (x1 - x3) - y1) % _P
    return x3, y3


def _aff_mul(k, p):
    r = None
    while k > 0:
        if k & 1:
            r = _aff_add(r, p)
        p = _aff_add(p, p)
        k >>= 1
    return r


def _ecdsa_verify_affine(pub_xy, msg, sig):
    """独立仿射 ECDSA 验签（u1*G + u2*Q 仿射坐标，不复用 _jp_* 雅可比）。"""
    if pub_xy is None or sig is None or len(sig) != 64:
        return False
    r = _int_be(sig[:32])
    s = _int_be(sig[32:])
    if not (1 <= r < _N and 1 <= s < _N):
        return False
    qx, qy = pub_xy
    if (qy * qy - (qx * qx * qx + _A * qx + _P256_B)) % _P != 0:
        return False
    z = _int_be(hashlib.sha256(msg).digest()) % _N
    w = _inv(s, _N)
    u1 = (z * w) % _N
    u2 = (r * w) % _N
    pt = _aff_add(_aff_mul(u1, (_GX, _GY)), _aff_mul(u2, (qx, qy)))
    if pt is None:
        return False
    return (pt[0] % _N) == r


def _der_len(buf, i, end):
    if i >= end:
        raise ValueError("DER truncated")
    first = _u8(buf, i)
    i += 1
    if first < 0x80:
        return first, i
    n = first & 0x7F
    if n == 0 or n > 4 or i + n > end:
        raise ValueError("DER length")
    ln = 0
    for _k in range(n):
        ln = (ln << 8) | _u8(buf, i)
        i += 1
    return ln, i


def _collect_octet32(buf, out, start, end):
    i = start
    while i < end:
        tag = _u8(buf, i)
        i += 1
        try:
            ln, i = _der_len(buf, i, end)
        except ValueError:
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
        der = binascii.a2b_base64("".join(lines))
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


def slot_name(slot):
    """Diagnostic label (single-App arch): 0=App region, 1=Backup region."""
    v = int(slot) & 0xFF if slot is not None else 0xFF
    return {0: "App区", 1: "备份区"}.get(v, "未知(0x%02X)" % v)


def slot_base(slot):
    """Deprecated compat: single-App arch always runs from APP_BASE."""
    return APP_BASE


def validate_image(image):
    if len(image) < IMAGE_HEADER_SIZE + 8:
        raise RuntimeError("镜像太短: %d" % len(image))
    if struct.unpack_from("<I", image, 0)[0] != IMAGE_MAGIC:
        raise RuntimeError("镜像缺少 XATO 头")
    payload_len = struct.unpack_from("<I", image, 4)[0]
    expected = IMAGE_HEADER_SIZE + payload_len
    if expected != len(image):
        raise RuntimeError("镜像头 length=%d 与文件总长 %d 不一致" % (payload_len, len(image)))
    if len(image) > APP_SIZE:
        raise RuntimeError("镜像 %d 超过 App 区大小 %d (0x%X)" % (len(image), APP_SIZE, APP_SIZE))
    reset = struct.unpack_from("<I", image, IMAGE_HEADER_SIZE + 4)[0] & 0xFFFFFFFE
    lo = APP_ENTRY
    hi = APP_BASE + APP_SIZE
    if not (lo <= reset < hi):
        raise RuntimeError("Reset Handler 0x%08X 不在 App 窗口 [0x08003100, 0x08010000)，"
                           "请改 Target IROM1=0x08003100" % reset)
    _log("镜像校验通过：App 窗口链接，总长 %d（Backup 区暂存→BOOT 搬运）" % len(image))
    return 0

def _selfcheck_image(image, dest, what):
    """Packed-image host self-check (run before entering 0x34):

    ① Reset handler inside the App run window [APP_BASE+256,
       APP_BASE+APP_SIZE) — same criterion as Boot boot_verify_image
       reset-window check (fail_step=4 rejects and BOOT skips copy);
    ② payload CRC matches header crc32 — same as Boot fail_step=3;
    ③ independent affine ECDSA verify with docs/keys/public.pem —
       same as Boot fail_step=6; deliberately does NOT reuse the
       ecdsa_sign_msg point arithmetic (signing and self-check must
       not share a failure mode).

    Any FAIL -> raise, refusing to enter 0x34; log includes failures."""
    base = slot_base(dest)
    lo = base + IMAGE_HEADER_SIZE
    hi = base + SLOT_SIZE
    reset = struct.unpack_from("<I", image, IMAGE_HEADER_SIZE + 4)[0]
    reset_t = reset & 0xFFFFFFFE
    payload = image[IMAGE_HEADER_SIZE:]
    hdr_crc = struct.unpack_from("<I", image, 8)[0]
    calc_crc = zlib.crc32(payload) & 0xFFFFFFFF
    sig = image[0x0C:0x0C + 64]
    fails = []
    if not (lo <= reset_t < hi):
        fails.append("① Reset handler 0x%08X 不在 Slot %s 窗口 [0x%08X, 0x%08X)"
                     "（Boot fail_step 4 会拒绝并回旧槽）"
                     % (reset, slot_name(dest), lo, hi))
    if calc_crc != hdr_crc:
        fails.append("② CRC 不一致：头=0x%08X 实算=0x%08X（Boot fail_step 3 会拒绝）"
                     % (hdr_crc, calc_crc))
    if not os.path.isfile(PUBLIC_KEY_PATH):
        fails.append("③ 找不到公钥 %s，无法独立验签" % PUBLIC_KEY_PATH)
    else:
        pub = None
        try:
            pub = _load_ec_public_key(PUBLIC_KEY_PATH)
        except Exception as e:
            fails.append("③ 公钥解析失败: %s" % e)
        if pub is not None:
            if _ecdsa_verify_affine(pub, payload, sig):
                _log("[自检] 签名本地验证 PASS（public.pem 独立仿射验签，%s payload）" % what)
            else:
                fails.append("③ 独立验签 FAIL：public.pem 对 %s 头签名验证不通过"
                             "（Boot fail_step 6 会拒绝）" % what)
    if fails:
        raise RuntimeError("%s：%s" % (what, "；".join(fails)))
    _log("[自检] %s PASS：Reset=0x%08X ∈ Slot %s 窗口，CRC=0x%08X 与头一致，"
         "签名独立验签通过" % (what, reset, slot_name(dest), hdr_crc))


def pack_image_if_needed(fw_path, priv):
    """必要时为裸 bin 补 XATO 头。0x4C 起 16B 为保留占位（原 version 字段，
    已从 image_header_t 定义删除），打包固定填 0x00，偏移锁定不可回收；
    版本号唯一定义在固件 SW_VERSION_STR（can_protocol.c），发版只改固件
    常量 + 文档。CRC32/ECDSA 签名只覆盖头后 payload，保留占位区不参与校验。"""
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
    # 0x4C 起 16B 保留占位（原 version 字段已删除）：固定填 0x00，偏移锁定不可回收
    hdr_reserved_ver = b"\x00" * HDR_RESERVED_VER_LEN
    header = struct.pack("<III", IMAGE_MAGIC, len(data), crc) + sig + hdr_reserved_ver + struct.pack("<I", int(time.time()) & 0xFFFFFFFF)
    header += b"\x00" * (IMAGE_HEADER_SIZE - len(header))
    _log("打包完成 crc=0x%08X（版本号不在镜像头，见固件 SW_VERSION_STR）" % crc)
    _selfcheck_image(header + data, 0, "pack_image 打包自检")
    return header + data

