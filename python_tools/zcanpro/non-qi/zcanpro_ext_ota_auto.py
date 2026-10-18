# -*- coding: utf-8 -*-
"""
ZCANPRO 扩展脚本 — Qi 无线充 CAN-UDS OTA（单 App 架构，OTA-ARCH-0920）

升级流程（Boot + App + Backup 区，A/B 槽已移除）：
  ① 设备烧录 boot+App 完整固件作为基线（产线 merge_prod_bin.py）；
  ② 用户侧 Keil 改 SW_VERSION_STR → 目标版本；
  ③ Rebuild qi_wireless_code_app 工程（禁 Incremental Build）；
  ④ pack_image.py 打包（app bin/app_image.bin；Keil 新 bin 亦可，
     脚本现场打包）；
  ⑤ 运行本脚本：10 02→27→31 擦 Backup 区→34/36 写入 0x08010000→
     37 验签提交（设备自复位）→ BOOT 搬运 Backup→App（擦 App 区→
     复制→复核→清标志→跳转）→ 复位后版本核验。
EXPECTED_SW_VERSION 固化 ""（通用模式）：不做版本闸门，自动选 mtime
最新的载荷升级；升级后从被刷镜像 strings 提取版本做判定闭环。
命令行 --firmware <path> 可指定发送 bin（跳过自动选择）。
判定闭环：①复位后 APP 应答 ②0xF195==被刷镜像 strings 版本
（0x2113 在单 App 架构恒为 0x00，仅信息展示）。

导入: 高级功能 -> 扩展脚本 -> 打开本文件
运行前: 先打开 CAN 通道 (250 kbps, Classical CAN, 扩展帧)
需要: Python 3.8 32 位（ZCANPRO 扩展脚本要求）
"""

import argparse
import os
import sys
import time
import struct
import hashlib
import binascii
import zlib

try:
    import zcanpro
except ImportError:
    zcanpro = None

# ======== 用户配置 ========
_TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))

# EXPECTED_SW_VERSION：升级目标版本（判定闭环第②条件）。
#   通用模式=""（默认）：不校验版本，自动选 mtime 最新载荷；升级后
#   从被刷镜像 strings 提取版本作为判定基准。
#   指定版本="QC_JYF_MCU1_FW_x.y.z"：仅选版本匹配的候选，不符→拒闪。
EXPECTED_SW_VERSION = ""

# BASELINE_SW_VERSION：升级前基线版本（用户五步流程第①步烧录的 1.1.1
# 基线）。仅用于升级前基线确认打印（异常时醒目提醒，不拦截流程），
# 与判定闭环三条件无关。
BASELINE_SW_VERSION = "QC_JYF_MCU1_FW_1.1.1"

# FIRMWARE_OVERRIDE：命令行 --firmware 指定的 OTA 载荷路径（None=自动选择）。
#   仅 standalone 命令行运行且传入 --firmware 时由 __main__ 入口赋值；
#   ZCANPRO 宿主加载（无 CLI 参数）时保持 None → _pick_firmware() 自动选择。
#   指定路径不豁免版本闸门：strings 校验+拒闪 fail-closed 与自动路径同一套。
FIRMWARE_OVERRIDE = None


def _find_repo_root(start):
    """逐级向上探测含 docs/keys 的仓库根（qi-can-uds-ota-scripts §6：
    禁止固定层级 dirname 拼接——复制粘贴错层曾致密钥路径拼错）。
    探测失败回退脚本所在目录。"""
    d = os.path.abspath(start)
    for _i in range(6):
        if os.path.isdir(os.path.join(d, "docs", "keys")):
            return d
        parent = os.path.dirname(d)
        if parent == d:
            break
        d = parent
    return os.path.abspath(start)


REPO_ROOT = _find_repo_root(_TOOLS_DIR)
FIRMWARE_DIR = os.path.join(_TOOLS_DIR, "app bin")
PRIVATE_KEY_PATH = os.path.join(REPO_ROOT, "docs", "keys", "private.pem")
PUBLIC_KEY_PATH = os.path.join(REPO_ROOT, "docs", "keys", "public.pem")
KEIL_APP_BIN = os.path.join(REPO_ROOT, "qi_wireless_code_app", "mdk_project", "Objects", "qi_wireless_code_app.bin")

def _scan_firmware():
    """Scan app bin/ for firmware candidates (single-App arch: every
    image must be App-window linked; returns list of paths)."""
    result = []
    if not os.path.isdir(FIRMWARE_DIR):
        raise RuntimeError("firmware dir not found: " + FIRMWARE_DIR)
    for name in sorted(os.listdir(FIRMWARE_DIR)):
        if not name.endswith(".bin"):
            continue
        path = os.path.join(FIRMWARE_DIR, name)
        try:
            data = open(path, "rb").read()
        except Exception:
            continue
        if len(data) < IMAGE_HEADER_SIZE + 8:
            continue
        result.append(path)
    return result


def _extract_sw_version(blob):
    """从 payload 里抠 QC_JYF_MCU1_FW_...（编译进 APP 的 SW_VERSION_STR）。"""
    marker = b"QC_JYF_MCU1_FW_"
    i = blob.find(marker)
    if i < 0:
        return None
    s = blob[i:i + 32].split(b"\x00")[0]
    try:
        return s.decode("ascii").strip()
    except Exception:
        return None


def _pick_firmware():
    """Payload selection (single-App arch): pick newest by mtime.
    Candidates = Keil app build + every .bin under app bin/
    (packed or raw, all must be App-window linked; validate_image
    enforces the reset-vector window). EXPECTED_SW_VERSION=""（通用模式）:
    no version gate, newest mtime wins; post-upgrade judgment extracts
    version from the flashed image strings."""
    candidates = []
    seen = set()

    def _add(path, tag):
        ap = os.path.abspath(path)
        if ap in seen or not os.path.isfile(path):
            return
        try:
            if os.path.getsize(path) < IMAGE_HEADER_SIZE + 8:
                return
        except Exception:
            return
        seen.add(ap)
        candidates.append((path, tag))

    _add(KEIL_APP_BIN, "Keil app bin")
    if os.path.isdir(FIRMWARE_DIR):
        for _name in sorted(os.listdir(FIRMWARE_DIR)):
            if _name.endswith(".bin"):
                _add(os.path.join(FIRMWARE_DIR, _name), "app bin/" + _name)
    if not candidates:
        raise RuntimeError("firmware not found; build chain: SW_VERSION_STR -> "
                           "Keil Rebuild qi_wireless_code_app -> pack_image.py")
    candidates.sort(key=lambda t: os.path.getmtime(t[0]), reverse=True)
    path, tag = candidates[0]
    _log("载荷选择（mtime 最新）：%s（%s）" % (path, tag))
    return path





FIRMWARE_PATH = ""
DOWNLOAD_ADDR = 0x08010000  # 0x34 target = Backup region (single-App arch)
TRANSFER_BLOCK_DATA = 128

UDS_REQ_ID = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D
SID_DSC, SID_RDBI, SID_SA = 0x10, 0x22, 0x27
SID_WDBI, SID_RC, SID_RD, SID_TD, SID_RTE = 0x2E, 0x31, 0x34, 0x36, 0x37
SID_TP = 0x3E
SID_NRC, SID_PR = 0x7F, 0x40
NRC_RCRRP = 0x78
SA_SIG_CHUNK = 4  # 27 03 单帧：SID+03+seq+4B = 7，避开 ISO-TP 多帧
IMAGE_MAGIC = 0x4F544158
IMAGE_HEADER_SIZE = 256
# 0x4C 起 16B：原 image_header_t.version 字段已从结构定义删除（2026-09-18），
# 改为保留占位，打包固定填 0x00；偏移锁定不可回收（头总长 256B、其余字段
# 偏移逐字节不变）。
HDR_RESERVED_VER_OFF = 0x4C   # 原 version 字段起始偏移（保留占位区）
HDR_RESERVED_VER_LEN = 16     # 保留占位区长度（=原 version 字段字节数）
HDR_BUILD_TS_OFF     = 0x5C   # build_timestamp 偏移（紧随保留区之后，未变）
# ---- Single-App partition constants (OTA-ARCH-0920; same-source with
# ---- boot_metadata.h / ota_trigger.h) ----
APP_BASE = 0x08003000
APP_SIZE = 0xD000
APP_ENTRY = 0x08003100
BACKUP_BASE = 0x08010000
BACKUP_SIZE = 0xD000
DL_TARGET_BACKUP = 0x02
# deprecated aliases kept so legacy helpers still compile
SLOT_A, SLOT_B = 0, 1
SLOT_A_BASE = APP_BASE
SLOT_B_BASE = BACKUP_BASE
SLOT_SIZE = APP_SIZE
MAX_TD_DATA = 254

# SIT1145 Standby 唤醒标识帧：固件唤醒后约 100ms（CAN_LP_ANNOUNCE_DELAY_MS）
# 在 0x18FF260D 主动发 01 41 57 4B cnt src secL secH（can_protocol.c can_lp_send_ident_bus），
# 与上电 BOOTUP 01 41 00 ... 区分。收到它 = 唤醒收敛的正证据。
LIFE_ANNOUNCE_ID = 0x18FF260D
LIFE_ANNOUNCE_MAGIC = (0x01, 0x41, 0x57, 0x4B)  # 01 'A' 'W' 'K'
LIFE_BOOTUP_MAGIC = (0x01, 0x41, 0x00)  # 01 'A' 00 — 上电/Boot 跳转标识帧
PROBE_ROUNDS = 3

# ---- Boot safe mode 诊断帧（与 qi_wireless_bootloader/mdk_app/Src/
# ---- boot_safe_mode.c 头部注释严格一致，两侧勿改其一）----
# 探测请求: CAN ID 0x18DA0D03 (UDS_REQ_ID)，数据 22 21 13
#           固件 RX 为自实现兼容解析（不依赖 ISO-TP 协议栈），兼容
#           ISO-TP SF（03 22 21 13 ...）与裸 UDS（22 21 13）两种写法
# Boot 应答（现行固件 boot_safe_mode.c safe_send_sf ISO-TP SF 组帧）:
#   22 2113 → CAN ID 0x18DA030D (UDS_RESP_ID) ISO-TP 单帧，DLC=8:
#               05 62 21 13 FE <fail_step> CC CC（尾部 0xCC 填充）
#   3E（sub bit7 suppress 位为 0）→ 同 ID ISO-TP 单帧
#               02 7E <子功能低 7 位>；suppress 位为 1 不应答
#   其他 22 DID → NRC 7F 22 11（ISO-TP 单帧）
# 心跳: 每 500ms 在 0x18FF260D (LIFE_ANNOUNCE_ID) 发
#           01 41 42 54 cause fail_step A5 00（'ABT' 标记帧），并同周期
#           重切 SIT1145 收发器 Normal（boot_safe_mode.c enter_safe_mode）
# 主机侧解析: 本文件 _safe_mode_step —— 双格式兼容：历史裸帧
#           62 21 13 FE <fail_step> + 现行 ISO-TP SF
#           05 62 21 13 FE <fail_step>
# fail_step 语义提取自 boot_verify.c g_verify_fail_step（1~6 为镜像校验
# 步骤，0 为 select_boot_slot 无有效槽/未执行校验）。
SAFE_MODE_RESP_ID = UDS_RESP_ID          # 0x18DA030D
SAFE_MODE_MARKER = (0x62, 0x21, 0x13, 0xFE)  # UDS 载荷标记（不含 ISO-TP PCI）
FAIL_STEP_DESC = {
    0: "未执行镜像校验（无待搬运固件且 App 区校验未跑）",
    1: "镜像 magic 校验失败",
    2: "image_length 为 0 或超出槽范围",
    3: "镜像 CRC32 校验失败",
    4: "Reset handler 不在槽内（跨槽链接镜像）",
    5: "ECDSA 公钥缺失/无效（Device Info 与内置公钥均不可用）",
    6: "ECDSA P-256 验签失败",
}

# secp256r1 / prime256v1. n 必须与 bootloader uECC.c 的 N[] 一致。
_P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
_N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
_A = _P - 3
_GX = 0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296
_GY = 0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5

stopTask = False


class UdsNrcError(RuntimeError):
    def __init__(self, sid, nrc):
        RuntimeError.__init__(self, "NRC SID=0x%02X NRC=0x%02X" % (sid, nrc))
        self.sid = sid
        self.nrc = nrc


def _safe_mode_step(dat):
    """识别 Boot safe mode：裸 62 21 13 FE step，或 ISO-TP 05 62 21 13 FE step。"""
    if (len(dat) >= 6 and dat[0] == 0x05 and dat[1] == 0x62 and dat[2] == 0x21
            and dat[3] == 0x13 and dat[4] == 0xFE):
        return int(dat[5])
    if (len(dat) >= 5 and dat[0] == 0x62 and dat[1] == 0x21
            and dat[2] == 0x13 and dat[3] == 0xFE):
        return int(dat[4])
    return None


def _safe_mode_msg(step):
    desc = FAIL_STEP_DESC.get(step, "未知 fail_step（Boot 固件可能早于本标记帧版本）")
    return ("设备处于 Boot safe mode，fail_step=%d（%s）→ merge_prod_bin 烧录器重刷"
            % (step, desc))


class SafeModeError(RuntimeError):
    """Boot safe mode 标记帧命中：设备停在 Boot，需 merge_prod_bin 重刷。"""
    def __init__(self, step):
        RuntimeError.__init__(self, _safe_mode_msg(step))
        self.step = step


def z_notify(type, obj):
    _log("Notify " + str(type) + " " + str(obj))
    if type == "stop":
        global stopTask
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


def _uds_init_cfg():
    return {
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
    }


def uds_init():
    zcanpro.uds_init(_uds_init_cfg())
    _log("UDS 就绪 0x18DA0D03 / 0x18DA030D 扩展帧")


def uds_req(bus_id, sid, payload, suppress=0, wait_pending_s=0):
    """UDS 请求：单帧请求用 ZCANPRO uds_request，多帧请求用 uds_req_mf。

    ZCANPRO uds_request 能正确接收多帧应答（ECU→host），但不能发送
    多帧请求（host→ECU）。因此：
    - 单帧请求（≤7B）：用 uds_request（它处理多帧应答组装）
    - 多帧请求（>7B，如 0x34/0x36）：用 uds_req_mf 手动 ISO-TP
    """
    if stopTask:
        raise RuntimeError("用户停止脚本")
    uds = [sid] + list(payload)
    if len(uds) <= 7:
        req = {
            "src_addr": UDS_REQ_ID,
            "dst_addr": UDS_RESP_ID,
            "suppress_response": 1 if suppress else 0,
            "sid": sid,
            "data": list(payload),
        }
        t_end = time.time() + float(wait_pending_s)
        while True:
            if stopTask:
                raise RuntimeError("用户停止脚本")
            resp = zcanpro.uds_request(bus_id, req)
            if suppress:
                return None
            data = list((resp or {}).get("data") or [])
            if data:
                sm_step = _safe_mode_step(data)
                if sm_step is not None:
                    raise SafeModeError(sm_step)
            if len(data) >= 3 and data[0] == SID_NRC:
                # NRC 也必须对上 SID：7F 27 78 等串台帧不能当本服务应答
                if len(data) >= 2 and data[1] != sid:
                    _log("[Rx] NRC SID=0x%02X≠0x%02X（串台），跳过" % (data[1], sid))
                    continue
                if data[2] == NRC_RCRRP:
                    if wait_pending_s <= 0 or time.time() >= t_end:
                        raise RuntimeError("SID=0x%02X 只收到 NRC 0x78" % sid)
                    time.sleep(1.0)
                    continue
                raise UdsNrcError(data[1], data[2])
            if not data:
                raise RuntimeError("无应答 SID=0x%02X" % sid)
            if data[0] != (sid + SID_PR):
                # 生命周期帧落在 UDS 应答 ID：03 01 41 00 / 07 01 41 57 4B
                _log("[Rx] 非本服务应答 %s（期望 SID=0x%02X），跳过"
                     % (_hex(data[:6]), sid))
                continue
            return data
    else:
        return uds_req_mf(bus_id, sid, payload)


def uds_try(bus_id, sid, payload, suppress=0):
    try:
        return uds_req(bus_id, sid, payload, suppress=suppress)
    except Exception as e:
        _log("可忽略: " + str(e))
        return None


def read_did_u8(bus_id, did):
    rx = uds_req(bus_id, SID_RDBI, [(did >> 8) & 0xFF, did & 0xFF])
    if len(rx) < 4:
        raise RuntimeError("DID 0x%04X 响应过短" % did)
    return rx[3]


def send_security_key(bus_id, sig, seed=None, priv=None):
    """SecurityAccess 解锁：27 03 分片 + 27 02 验签。

    27 03 每帧 6B，走 uds_req_mf（ZCANPRO uds_request 在 WSL shim 下
    不可靠）。不做超时重试——重试产生残留应答导致响应错位。
    """
    sig = _to_bytes(sig)
    if len(sig) != 64:
        raise RuntimeError("ECDSA 签名须 64 字节, 实际 %d" % len(sig))
    seq = 1
    off = 0
    while off < 64:
        piece = sig[off:off + SA_SIG_CHUNK]
        # 每 4 帧发 3E 80 保活（suppress，无应答帧），防分片期间 S3 超时
        if seq in (5, 9, 13):
            try:
                uds_req(bus_id, SID_TP, [0x80], suppress=1)
            except Exception:
                pass
        for _try in range(3):
            try:
                uds_req_mf(bus_id, SID_SA, [0x03, seq] + _to_list(piece),
                           expect_sub=0x03, expect_sub2=seq)
                break
            except RuntimeError:
                if _try == 2:
                    raise
                _log("27 03 %02X 超时，重试 %d/2" % (seq, _try + 1))
                time.sleep(0.2)
        off += len(piece)
        seq += 1
    _log("27 03 已送 64 字节 / %d 帧" % (seq - 1))
    time.sleep(0.15)
    return uds_req_mf(bus_id, SID_SA, [0x02], expect_sub=0x02)


def _lifecycle_check(cid, dat):
    """判断 0x18FF260D 上的帧类型：awk/bootup/shutdown/None。"""
    if cid != LIFE_ANNOUNCE_ID or len(dat) < 3:
        return None
    if len(dat) >= 4 and tuple(dat[:4]) == LIFE_ANNOUNCE_MAGIC:
        return "awk"          # 01 41 57 4B — 从 Standby 唤醒
    if dat[0] == 0x01 and dat[1] == 0x41 and dat[2] == 0x00:
        return "bootup"       # 01 41 00 — 上电/Boot 跳转
    if dat[0] == 0x06 and dat[1] == 0x41:
        return "shutdown"     # 06 41 53 42 — 进入 Standby
    return None


def _listen_lifecycle(bus_id, listen_s=1.0):
    """释放 UDS 通道后 raw 收帧，监听 0x18FF260D 生命周期帧。
    返回 [(类型, data), ...]；finally 恢复 UDS 通道。"""
    found = []
    try:
        zcanpro.uds_deinit()
    except Exception as e:
        _log("UDS 通道释放失败（继续监听）: " + str(e))
    try:
        t_end = time.time() + float(listen_s)
        while time.time() < t_end:
            if stopTask:
                raise RuntimeError("用户停止脚本")
            for cid, dat in _recv_frames(bus_id):
                lt = _lifecycle_check(cid, dat)
                if lt is not None:
                    _log("生命周期帧 [%s] 0x%08X %s" % (lt.upper(), cid, _hex(dat[:8])))
                    found.append((lt, dat))
            time.sleep(0.02)
    finally:
        uds_init()
    return found


ERASE_REQ = [0x01, 0xFF, 0x00]
ERASE_WAIT_PENDING_S = 90
ERASE_ENHANCED_CFG_S = 120  # _uds_init_cfg() enhanced_timeout_ms=120000（配置值）


def _sniff_erase_late_response(bus_id, sniff_s=3.0):
    """0x31 擦除 uds_request 失败后的原始通道取证。

    本机 ZCANPRO 库内部消化 0x78（脚本在 uds_request 路径下看不到泵帧），
    且实测失败耗时 55s 既不等于配置的 enhanced_timeout 120s 也不等于
    response_timeout 3s，库内部另有 RCRRP/无响应预算，语义不可从 WSL 取证。
    失败后释放 UDS 通道 raw 收帧，抓迟到的最终响应：
      ("positive", 0)  → MCU 擦除实际完成，0x71 迟到被库放弃
      ("nrc", code)    → MCU 回了最终 NRC（设备存活，固件拒绝）
      None             → 全静默（MCU 挂死或 CAN TX 黑洞）
    顺带统计嗅探窗口内的 7F 31 78 泵帧数，日志可区分「MCU 一直在泵 78
    但库提前放弃」与「MCU 一个帧都没发出」。finally 恢复 UDS 通道。"""
    pend = 0
    result = None
    try:
        zcanpro.uds_deinit()
    except Exception as e:
        _log("UDS 通道释放失败（继续嗅探）: " + str(e))
    try:
        t_end = time.time() + float(sniff_s)
        while time.time() < t_end:
            if stopTask:
                raise RuntimeError("用户停止脚本")
            for cid, dat in _recv_frames(bus_id):
                if (cid != UDS_RESP_ID) or (len(dat) < 2):
                    continue
                if dat[0] == SID_NRC and len(dat) >= 4 and dat[2] == SID_RC:
                    if dat[3] == NRC_RCRRP:
                        pend += 1
                        continue
                    _log("原始通道捕获擦除最终 NRC: " + _hex(dat[:4]))
                    result = ("nrc", dat[3])
                elif dat[0] == 0x71 and len(dat) >= 3 and dat[1] == 0x01:
                    _log("原始通道捕获擦除迟到正响应: " + _hex(dat[:8]))
                    result = ("positive", 0)
            time.sleep(0.02)
    finally:
        uds_init()
    _log("擦除失败取证：嗅探 %.1fs，捕获 0x78 泵帧 %d 个，最终响应 %s"
         % (sniff_s, pend, ("有" if result else "无")))
    return result


def _erase_with_retry(bus_id):
    """0x31 擦除带韧性：失败后取证 + 自动重试一次 + 耗时定性报错。

    固件侧擦除幂等且有活跃槽防护（g_base==ota_running_slot_base() 回 NRC 22），
    重试安全：MCU 若只是慢/瞬时 bus-off，二次 31 01 FF 00 可直接成功；
    MCU 若已实际擦完（0x71 迟到被库放弃），取证命中正响应则跳过重试续跑。"""
    t0 = time.time()
    try:
        uds_req(bus_id, SID_RC, ERASE_REQ, wait_pending_s=ERASE_WAIT_PENDING_S)
        return
    except UdsNrcError as e:
        _log("0x31 擦除耗时 %.1fs，MCU 回 NRC 0x%02X（设备存活，固件拒绝，重试无意义）"
             % (time.time() - t0, e.nrc))
        raise
    except RuntimeError as e:
        elapsed = time.time() - t0
        _log("0x31 擦除第 1 次失败：耗时 %.1fs「%s」（wait_pending 上限 %ds，"
             "库 enhanced_timeout 配置 %ds；0x78 被库内部消化，脚本侧全程不可见）"
             % (elapsed, e, ERASE_WAIT_PENDING_S, ERASE_ENHANCED_CFG_S))
        sniff = _sniff_erase_late_response(bus_id)
        if sniff and sniff[0] == "positive":
            _log("MCU 擦除实际已完成（0x71 迟到被库放弃），跳过重试直接续跑")
            return
        if sniff and sniff[0] == "nrc":
            raise RuntimeError("0x31 擦除被 MCU 拒绝：NRC 0x%02X（第 1 次耗时 %.1fs，"
                               "原始通道捕获最终 NRC，设备存活）"
                               % (sniff[1], elapsed))
        _log("原始通道无最终响应，自动重试一次 31 01 FF 00（固件擦除幂等+活跃槽防护，安全）")
        t1 = time.time()
        try:
            uds_req(bus_id, SID_RC, ERASE_REQ, wait_pending_s=ERASE_WAIT_PENDING_S)
            _log("0x31 擦除第 2 次成功（耗时 %.1fs）" % (time.time() - t1))
            return
        except Exception as e2:
            elapsed2 = time.time() - t1
            total = time.time() - t0
            probe = uds_try(bus_id, SID_RDBI, [0x21, 0x13])
            if probe:
                alive = "应答正常（%s），设备存活" % _hex(probe[:8])
            else:
                sm_step = _raw_probe_safe_mode(bus_id)
                if sm_step is not None:
                    alive = _safe_mode_msg(sm_step)
                else:
                    alive = "无应答（MCU 疑似挂死或 CAN TX 黑洞）"
            raise RuntimeError(
                "0x31 擦除两次失败：第1次 %.1fs「%s」，第2次 %.1fs「%s」，总计 %.1fs；"
                "wait_pending 上限 %ds，库 enhanced_timeout 配置 %ds"
                "（库内部消化 0x78 且另有上限，实测 55s 量级）。失败后 22 2113 探测：%s。"
                "两次耗时都贴近库内部上限→MCU 擦除慢于库预算；"
                "第2次远小于上限且探测无应答→MCU 擦除路径挂死/TX 黑洞，需固件修复后重编译烧录。"
                % (elapsed, e, elapsed2, e2, total,
                   ERASE_WAIT_PENDING_S, ERASE_ENHANCED_CFG_S, alive))


def confirm_app_after_reset(bus_id):
    """复位后等待 APP 起来。Boot 验签 ECDSA 需数秒，回退路径更久。

    三阶段：前 3 次盲探 22 2113 → 失败后 wake_bus + 监听生命周期帧 →
    继续探测并间歇监听。窗口 45s。

    返回 (slot, sw_ver, ota_status)：slot=复位后 0x2113 应答槽字节，
    sw_ver=0xF195 应答 ASCII rstrip（读失败=None），
    ota_status=0x2112 应答字节（0x05=Confirmed 搬运成功，读失败=None）——
    三者供 run_ota 判定闭环使用，本函数内仅记录不断言。

    三态诊断：
    a) UDS 响应 = 成功；
    b) 生命周期帧但无 UDS = APP 已启动但链路/会话异常；
    c) 全静默 = 可能停在 Boot safe mode 或镜像问题：raw 探测 22 2113 识别
       safe mode 应答（_safe_mode_step 双格式识别，命中→SafeModeError
       精确报错），未命中提示 merge_prod_bin。应答帧格式（现行 ISO-TP SF
       05 62 21 13 FE <fail_step>）与 fail_step 语义见文件头 SAFE_MODE
       注释（与 boot_safe_mode.c 两侧一致）。
    """
    WINDOW_S = 45.0
    BLIND_PROBES = 3
    rx = None
    last_err = None
    t0 = time.time()
    probe_count = 0
    woken = False
    lifecycle_seen = []

    _log("等待 APP 起来（Boot 验签+可能回退，窗口 %.0fs）" % WINDOW_S)

    while time.time() - t0 < WINDOW_S:
        if stopTask:
            raise RuntimeError("用户停止脚本")

        # Phase 1: blind probe (no wake frames yet)
        if probe_count < BLIND_PROBES:
            probe_count += 1
            try:
                rx = uds_req(bus_id, SID_RDBI, [0x21, 0x13])
                last_err = None
                break
            except SafeModeError:
                raise
            except Exception as e:
                last_err = e
                _log("复位后 22 2113 盲探 %d/%d: %s" % (probe_count, BLIND_PROBES, e))
                sm_step = _raw_probe_safe_mode(bus_id)
                if sm_step is not None:
                    raise SafeModeError(sm_step)
                time.sleep(0.5)
                continue

        # Phase 2: wake bus + listen for lifecycle frames (once)
        if not woken:
            _log("盲探 %d 次无应答，唤醒总线并监听生命周期帧..." % BLIND_PROBES)
            woken = True
            try:
                wake_ok = wake_bus(bus_id, listen_s=2.0)
                _log("wake_bus: %s" % ("收到 AWK，已从 Standby 唤醒" if wake_ok
                                       else "未收到 AWK，继续探测"))
            except Exception as e:
                _log("wake_bus 异常: %s" % e)
            lifecycle_seen.extend(_listen_lifecycle(bus_id, listen_s=1.0))
            sm_step = _raw_probe_safe_mode(bus_id)
            if sm_step is not None:
                raise SafeModeError(sm_step)

        # Phase 3: probe + brief lifecycle listen between attempts
        try:
            rx = uds_req(bus_id, SID_RDBI, [0x21, 0x13])
            last_err = None
            break
        except SafeModeError:
            raise
        except Exception as e:
            last_err = e
            _log("复位后 22 2113 等待: %s" % e)
            lifecycle_seen.extend(_listen_lifecycle(bus_id, listen_s=0.5))
            sm_step = _raw_probe_safe_mode(bus_id)
            if sm_step is not None:
                raise SafeModeError(sm_step)
            time.sleep(0.5)

    if last_err is not None:
        if lifecycle_seen:
            details = "; ".join("%s %s" % (lt.upper(), _hex(dat[:8]))
                                for lt, dat in lifecycle_seen[:4])
            raise RuntimeError(
                "复位后 APP 已启动（生命周期帧: %s）但 UDS 22 2113 无应答"
                "——链路/会话异常，请检查 CAN 配置或会话状态: %s"
                % (details, last_err))
        else:
            sm_step = _raw_probe_safe_mode(bus_id)
            if sm_step is not None:
                raise SafeModeError(sm_step)
            raise RuntimeError(
                "复位后无 UDS 且无生命周期帧（%.0fs 全静默）"
                "——可能停在 Boot（验签失败进 safe mode / 镜像问题），"
                "safe-mode 标记帧也未捕获（旧 Boot 固件无此应答或 CAN 未起）；"
                "请用 merge_prod_bin.py 重刷排查: %s" % (WINDOW_S, last_err))

    slot = None
    if rx is not None and len(rx) >= 4:
        slot = rx[3]
    _log("复位后 DID 0x2113 slot=" + _hex((rx or [])[3:4]))
    try:
        fw = uds_req(bus_id, SID_RDBI, [0x20, 0x10])
        _log("DID 0x2010 fw_type=" + _hex(fw[3:4]))
    except UdsNrcError as e:
        _log("DID 0x2010 NRC 0x%02X（Boot 无此 DID）" % e.nrc)
    # 运行版本采集：DID 0xF195 应答 = APP 编译时常量 SW_VERSION_STR
    # （can_protocol.c 唯一真相源），不读 OTA metadata / XATO 镜像头；
    # 解析方式与 zcanpro_read_app_version.py 一致（rx[3:35] 32B ASCII rstrip）。
    # 采集值供判定闭环第③条件使用，读失败计入 FAIL 差异明细。
    sw_ver = None
    try:
        ver = uds_req(bus_id, SID_RDBI, [0xF1, 0x95])
        if len(ver) >= 35 and ver[0] == (SID_RDBI + SID_PR):
            sw_ver = "".join(chr(b) if 0x20 <= b < 0x7F else "?" for b in ver[3:35]).rstrip()
            _log("DID 0xF195 APP编译版本=" + sw_ver + "（来源=固件 SW_VERSION_STR 编译常量）")
        else:
            _log("DID 0xF195 响应异常: " + _hex(ver[:8]))
    except UdsNrcError as e:
        _log("DID 0xF195 NRC 0x%02X（计入判定差异明细）" % e.nrc)
    except Exception as e:
        _log("DID 0xF195 读取失败（计入判定差异明细）: %s" % e)
    ota_status = None
    try:
        st = uds_req(bus_id, SID_RDBI, [0x21, 0x12])
        if len(st) >= 4 and st[0] == (SID_RDBI + SID_PR):
            ota_status = st[3]
            _log("DID 0x2112 OTA状态=0x%02X（0x05=Confirmed 搬运成功/"
                 "0x06=Rolled Back 回滚/0x07=Failed 失败）" % ota_status)
        else:
            _log("DID 0x2112 响应异常: " + _hex(st[:8]))
    except UdsNrcError as e:
        _log("DID 0x2112 NRC 0x%02X（计入判定差异明细）" % e.nrc)
    except Exception as e:
        _log("DID 0x2112 读取失败（计入判定差异明细）: %s" % e)
    try:
        uds_req(bus_id, SID_RD, [0x00])
        _log("复位后 0x34 正响应，已在 APP")
    except UdsNrcError as e:
        _log("复位后 0x34 NRC 0x%02X，已在 APP（默认会话下正常）" % e.nrc)
    except RuntimeError as e:
        raise RuntimeError("复位后 0x34 无应答（跳转失败或 APP CAN 未起来）: " + str(e))
    return slot, sw_ver, ota_status


def _read_did_u8_safe(bus_id, did):
    """判定差异明细用只读采集：任何异常返回 None，不中断判定流程。"""
    try:
        return read_did_u8(bus_id, did)
    except Exception as e:
        _log("DID 0x%04X 读失败（差异明细采集）: %s" % (did, e))
        return None


def _fmt_slot(v):
    return slot_name(v) if v is not None else "读失败"


def _raise_ota_fail(bus_id, from_slot, dest, to_slot, got_ver, expect_ver,
                    problems, otx_anomaly):
    """OTA 判定 FAIL：差异明细 + 判别矩阵指引 + 明确 FAIL 判定。

    判据缺口背景：旧脚本"OTA 成功"仅要求复位后有 APP 应答，不校验
    切槽/版本（取证报告 /mnt/k/slot_switch_forensics_0919.md §3 M5
    确证缺陷）——"脚本说完成+版本仍旧"是该缺口的直接预期形态。"""
    pend = _read_did_u8_safe(bus_id, 0x2114)
    reason = _read_did_u8_safe(bus_id, 0x2115)
    rollback = _read_did_u8_safe(bus_id, 0x2116)
    if pend == 0xFE:
        pend_str = "NONE(无待定)"
    else:
        pend_str = _fmt_slot(pend)
    lines = []
    lines.append("OTA 判定 FAIL（判定闭环三条件未全部满足，禁止假成功）")
    for p in problems:
        lines.append("  · " + p)
    lines.append("差异明细：0x2113 当前槽=%s（升级前=%s 目标槽=%s）；"
                 "0xF195 当前版本=%s（预期=%s）；0x2114 pending=%s；"
                 "0x2116 rollback_count=%s；0x2115 last_boot_reason=%s"
                 % (_fmt_slot(to_slot), slot_name(from_slot), slot_name(dest),
                    got_ver or "未读到", expect_ver or "不可用",
                    pend_str,
                    ("%d" % rollback) if rollback is not None else "读失败",
                    ("0x%02X" % reason) if reason is not None else "读失败"))
    if otx_anomaly:
        lines.append("附加事实：0x37 TransferExit 应答链异常（五次无最终应答或"
                     "重试落在已自复位重启的设备上），详见上方日志")
    lines.append("判别矩阵指引（0x2115 语义：00=POR 01=SW 02=WDG 03=OTA激活 04=回滚）：")
    lines.append("  · 2113=目标槽但版本旧 → 镜像内容问题：SW_VERSION_STR 构建链/"
                 "pack 输入/app bin 残留旧镜像（查日志「固件」「选中 bin 内 "
                 "SW_VERSION_STR」行，并在实际被刷 bin 内 strings 自检）")
    lines.append("  · 2113=升级前槽 且 2115=0x04 或 2116≥1 → Boot 回滚：新槽验签失败"
                 "（重定位/签名/Device Info 公钥配对；safe-mode fail_step 语义见"
                 "脚本头注释）或试运行确认失败")
    lines.append("  · 2113=升级前槽 且 2114=目标槽 → metadata 已提交但自复位"
                 "未生效（0x37 收尾自动复位失败/未执行，无外部请求依赖）"
                 "→ 请断电重启后重跑（断电重启即触发 BOOT 搬运）")
    lines.append("  · 2113=升级前槽 且 2114=0xFE → 无切槽证据链：0x37 commit 未发生"
                 "或 metadata 被重置（挂死后重烧/多次异常掉电）→ 查 0x37 段日志"
                 "与设备恢复方式")
    lines.append("完整判别矩阵：/mnt/k/slot_switch_forensics_0919.md §5")
    raise RuntimeError("\n".join(lines))


def _as_int(x):
    try:
        return int(x)
    except Exception:
        return None


def _parse_can_frame(f):
    """dict / list / tuple / object → (can_id_29bit, data list) or None。
    解析方式与 zcanpro_read_app_version.py 实测一致。"""
    if f is None:
        return None
    if isinstance(f, dict):
        cid = None
        for k in ("can_id", "id", "CANID", "canid"):
            if k in f:
                cid = _as_int(f[k])
                break
        dat = f.get("data")
    elif isinstance(f, (list, tuple)) and len(f) >= 2 and _as_int(f[0]) is not None:
        cid = _as_int(f[0])
        dat = f[1]
    else:
        cid = _as_int(getattr(f, "can_id", getattr(f, "id", None)))
        dat = getattr(f, "data", None)
    if cid is None:
        return None
    if not isinstance(dat, (list, tuple, bytes, bytearray)):
        dat = []
    return (cid & 0x1FFFFFFF, [int(x) & 0xFF for x in list(dat)])


def _unwrap_receive(raw):
    """与 zcanpro_read_app_version.py 一致：receive 常见 (status, [frames])。"""
    if raw is None:
        return []
    if isinstance(raw, tuple) or (isinstance(raw, list) and len(raw) == 2
                                  and not isinstance(raw[0], dict)
                                  and isinstance(raw[1], (list, tuple))):
        a, b = raw[0], raw[1]
        if isinstance(b, (list, tuple)):
            return list(b)
        if isinstance(a, (list, tuple)):
            return list(a)
    if isinstance(raw, dict) or (not isinstance(raw, (list, tuple))):
        return [raw]
    return list(raw)


def _recv_frames(bus_id):
    try:
        raw = zcanpro.receive(bus_id)
    except TypeError:
        try:
            raw = zcanpro.receive()
        except Exception:
            return []
    except Exception:
        return []
    out = []
    for f in _unwrap_receive(raw):
        p = _parse_can_frame(f)
        if p is not None:
            out.append(p)
    return out


def _raw_send(bus_id, can_id, data):
    """扩展帧 raw 发送（ZLG bit31=1，参考 zcanpro_read_app_version.py
    实测模式）。仅用于 safe-mode 取证探测，不进 UDS 请求主路径
    （qi-can-uds-ota-scripts §3：raw 模式仅限取证/唤醒监听）。"""
    cid = (int(can_id) & 0x1FFFFFFF) | 0x80000000
    frame = {
        "can_id": cid,
        "id": cid,
        "is_canfd": 0,
        "canfd_brs": 0,
        "is_extend": 1,
        "is_extended": 1,
        "extend": 1,
        "extern_flag": 1,
        "is_extern": 1,
        "eff": 1,
        "id_type": 1,
        "data": list(data) + [0xCC] * (8 - len(data)),
    }
    for name in ("transmit", "send"):
        fn = getattr(zcanpro, name, None)
        if fn is None:
            continue
        try:
            fn(bus_id, [frame])
            return True
        except Exception:
            try:
                fn(bus_id, frame)
                return True
            except Exception:
                continue
    return False


def uds_req_mf(bus_id, sid, payload, timeout_s=3.0, expect_sub=None, expect_sub2=None,
               quiet=False):
    """手动 ISO-TP 多帧发送 + 单帧/多帧接收。

    ZCANPRO uds_request 不支持 host→ECU 多帧发送（>7B UDS 请求必超时），
    此函数绕过 ZCANPRO 的 ISO-TP 栈，用 _raw_send 手动组 FF/FC/CF。

    expect_sub: 期望应答 sub-function（data[1]），不匹配则跳过（防延迟应答污染）。
    expect_sub2: 期望应答 data[2]（如 27 03 的 blockSeq），不匹配则跳过。
    quiet: True 时不打逐帧 CF/raw 日志（TransferData 高频路径），只打一行 TX/RX。
    """
    uds = [sid] + list(payload)
    total = len(uds)
    if total == 0:
        raise RuntimeError("UDS payload 为空")

    # 单帧：≤7B，直接 SF 发送
    if total <= 7:
        sf = [total & 0x0F] + uds + [0xCC] * (7 - total)
        _raw_send(bus_id, UDS_REQ_ID, sf)
        _log("[Tx] %02X %s" % (sid, _hex(payload[:16]) + (" ..." if len(payload) > 16 else "")))
    else:
        # FF：短超时重发（ECU 邮箱忙时首帧 FC 会丢）；收到 FC 后不再发 FF
        fc = None
        ff_hdr = [0x10 | ((total >> 8) & 0x0F), total & 0xFF]
        for ff_try in range(3):
            ff = ff_hdr + uds[:6]
            ff += [0xCC] * (8 - len(ff))
            _raw_send(bus_id, UDS_REQ_ID, ff)
            if not quiet:
                _log("[Tx FF] try%d %s" % (ff_try + 1, _hex(ff_hdr + uds[:6])))
            fc = _wait_isotp_fc(bus_id, timeout_s, quiet=quiet)
            if fc is not None:
                break
        if fc is None:
            raise RuntimeError("ISO-TP FC 超时（ECU 未回 Flow Control）")
        bs = fc[1] if len(fc) > 1 else 0
        stmin_raw = fc[2] if len(fc) > 2 else 0
        stmin = 0.001 if stmin_raw == 0 else (stmin_raw / 1000.0 if stmin_raw <= 0x7F else 0.001)

        # CF 循环
        offset = 6
        sn = 1
        cf_in_block = 0
        cf_count = 0
        cf_fail = 0
        while offset < total:
            if bs != 0 and cf_in_block >= bs:
                fc = _wait_isotp_fc(bus_id, timeout_s, quiet=quiet)
                if fc is None:
                    raise RuntimeError("ISO-TP FC 超时（BS 等待）")
                bs = fc[1] if len(fc) > 1 else 0
                stmin_raw = fc[2] if len(fc) > 2 else 0
                stmin = 0.001 if stmin_raw == 0 else (stmin_raw / 1000.0 if stmin_raw <= 0x7F else 0.001)
                cf_in_block = 0
            if stmin > 0:
                time.sleep(stmin)
            chunk = uds[offset:offset + 7]
            cf = [0x20 | (sn & 0x0F)] + chunk + [0xCC] * (8 - 1 - len(chunk))
            cf_ok = _raw_send(bus_id, UDS_REQ_ID, cf)
            if not cf_ok:
                cf_fail += 1
            if not quiet:
                _log("[Tx CF] sn=%d %s" % (sn, "OK" if cf_ok else "FAIL"))
            # 末帧重发一次：首帧丢则 SN=1 重发仍能完成组包
            if offset + len(chunk) >= total:
                time.sleep(0.01)
                _raw_send(bus_id, UDS_REQ_ID, cf)
                if not quiet:
                    _log("[Tx CF-retry] sn=%d" % sn)
            offset += len(chunk)
            sn = (sn + 1) & 0x0F
            cf_in_block += 1
            cf_count += 1
        if quiet:
            _log("[TX] %02X %02X len=%d CF=%d%s"
                 % (sid, payload[0] if payload else 0, total, cf_count,
                    (" FAIL=%d" % cf_fail) if cf_fail else ""))
        else:
            _log("[Tx CF] %d 帧" % cf_count)

    # 接收应答（单帧或多帧）
    t_end = time.time() + 45.0
    while True:
        data = _recv_uds_response(bus_id, sid, timeout_s, expect_sub=expect_sub,
                                  expect_sub2=expect_sub2, quiet=quiet)
        if data and len(data) >= 3 and data[0] == SID_NRC:
            if data[2] == NRC_RCRRP:
                if time.time() >= t_end:
                    raise RuntimeError("SID=0x%02X 只收到 NRC 0x78（等待 45s）" % sid)
                _log("[Rx] NRC 0x78 ResponsePending，等待 ECU 验签…")
                time.sleep(1.0)
                continue
            raise UdsNrcError(data[1], data[2])
        if quiet:
            _log("[RX] %s" % _hex((data or [])[:8]))
        return data


def _wait_isotp_fc(bus_id, timeout_s=2.0, quiet=False):
    """等待 ISO-TP Flow Control 帧（PCI=0x3n）。返回 FC data 或 None。"""
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if stopTask:
            raise RuntimeError("用户停止脚本")
        for cid, dat in _recv_frames(bus_id):
            cid29 = cid & 0x1FFFFFFF
            if cid29 != (UDS_RESP_ID & 0x1FFFFFFF):
                continue
            if len(dat) >= 1:
                if not quiet:
                    _log("[FC wait] %s" % _hex(list(dat[:8])))
                if (dat[0] & 0xF0) == 0x30:
                    return list(dat)
        time.sleep(0.002)
    return None


def _recv_uds_response(bus_id, sid, timeout_s=3.0, expect_sub=None, expect_sub2=None,
                       quiet=False):
    """接收 UDS 应答（单帧 SF 或多帧 FF+CF）。多帧自动回 FC 并组帧。

    SID 校验：data[0] 必须等于 sid+0x40（正响应），否则跳过。
    expect_sub: 若非 None，还须 data[1]==expect_sub（SA 服务防延迟应答污染）。
    expect_sub2: 若非 None，还须 data[2]==expect_sub2（如 27 03 blockSeq）。
    quiet: True 时不打 [Rx raw] 逐帧日志。
    """
    t0 = time.time()
    ff_buf = None
    ff_total = 0
    cf_buf = b""
    expected_sn = 1
    expect_pr = sid + SID_PR

    def _match(data):
        if not data:
            return False
        # NRC (7F <sid> <nrc>) 须对上 SID，否则是串台（7F 27 78 等）
        if data[0] == SID_NRC:
            return len(data) >= 2 and data[1] == sid
        if data[0] != expect_pr:
            return False
        if expect_sub is not None and (len(data) < 2 or data[1] != expect_sub):
            return False
        if expect_sub2 is not None and (len(data) < 3 or data[2] != expect_sub2):
            return False
        return True

    while time.time() - t0 < timeout_s:
        if stopTask:
            raise RuntimeError("用户停止脚本")
        for cid, dat in _recv_frames(bus_id):
            cid29 = cid & 0x1FFFFFFF
            if cid29 != (UDS_RESP_ID & 0x1FFFFFFF):
                continue
            if len(dat) < 1:
                continue
            pci = dat[0] & 0xF0
            if not quiet:
                _log("[Rx raw] pci=%02X %s" % (pci, _hex(list(dat[:8]))))
            if pci == 0x00:
                n = dat[0] & 0x0F
                data = list(dat[1:1 + n])
                if not _match(data):
                    _log("[Rx] 应答不匹配 %s（期望 SID=0x%02X sub=%s），跳过"
                         % (_hex(data[:6]), expect_pr,
                            ("%02X" % expect_sub) if expect_sub is not None else "-"))
                    continue
                return data
            elif pci == 0x10:
                if ff_buf is not None:
                    continue
                ff_total = ((dat[0] & 0x0F) << 8) | dat[1]
                ff_buf = bytes(dat[2:8])
                # 先校验 FF 中的 SID/sub，不匹配则丢弃整个多帧
                ff_data = list(ff_buf) + [0] * max(0, ff_total - len(ff_buf))
                if not _match(ff_data[:max(2, ff_total)]):
                    _log("[Rx FF] 多帧 SID/sub 不匹配，丢弃 total=%d" % ff_total)
                    ff_buf = None
                    ff_total = 0
                    continue
                _log("[Rx FF] total=%d, 发 FC" % ff_total)
                ok = _raw_send(bus_id, UDS_REQ_ID, [0x30, 0x00, 0x01, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC])
                _log("[Tx FC] %s" % ("OK" if ok else "FAIL"))
            elif pci == 0x20 and ff_buf is not None:
                sn = dat[0] & 0x0F
                if sn == expected_sn:
                    cf_buf += bytes(dat[1:8])
                    expected_sn = (expected_sn + 1) & 0x0F
                    _log("[Rx CF] sn=%d got=%d/%d" % (sn, len(ff_buf) + len(cf_buf), ff_total))
                    if len(ff_buf) + len(cf_buf) >= ff_total:
                        full = ff_buf + cf_buf
                        return list(full[:ff_total])
                else:
                    _log("[Rx CF] sn=%d 期望 %d，跳过" % (sn, expected_sn))
        time.sleep(0.001)
    raise RuntimeError("UDS 应答超时 SID=0x%02X (got %d/%d)" % (sid, len(ff_buf) + len(cf_buf) if ff_buf else 0, ff_total if ff_buf else 0))


def _raw_probe_safe_mode(bus_id, sniff_s=2.0):
    """失败路径取证：raw 发 22 2113 探测帧（ISO-TP SF）后监听 Boot safe
    mode 应答：现行固件为 ISO-TP 单帧 05 62 21 13 FE <fail_step>
    （DLC=8，0xCC 填充）；_safe_mode_step 双格式兼容历史裸帧
    62 21 13 FE <fail_step>。返回 fail_step（int）或 None；finally 恢复
    UDS 通道。应答仅在设备收到探测帧时发一次，库路径可能吞掉，故须 raw
    重探。"""
    step = None
    try:
        zcanpro.uds_deinit()
    except Exception as e:
        _log("UDS 通道释放失败（继续 safe-mode 取证）: " + str(e))
    try:
        if _raw_send(bus_id, UDS_REQ_ID, [0x03, 0x22, 0x21, 0x13,
                                          0xCC, 0xCC, 0xCC, 0xCC]):
            _log("[Tx-raw] 0x%08X 03 22 21 13 (safe-mode 探测)" % UDS_REQ_ID)
        t_end = time.time() + float(sniff_s)
        while time.time() < t_end:
            if stopTask:
                raise RuntimeError("用户停止脚本")
            for cid, dat in _recv_frames(bus_id):
                if (cid & 0x1FFFFFFF) == SAFE_MODE_RESP_ID:
                    step = _safe_mode_step(dat)
                    if step is not None:
                        _log("[Rx-raw] 0x%08X %s → Boot safe mode fail_step=%d"
                             % (cid, _hex(dat[:8]), step))
                        return step
            time.sleep(0.02)
    finally:
        uds_init()
    return step


def wake_bus(bus_id, listen_s=2.0):
    """SIT1145 空闲 180s 进 Standby：首帧只当 WUP，MCU 收不到内容，
    所以唤醒帧必须连发、且发完要等固件收敛再探测。

    步骤：
    1. 连发 3 帧 3E 00（间隔 200ms）打破静默；
    2. 释放 UDS 通道后原始收帧监听 1~2s（zcanpro_read_app_version.py
       验证过：UDS 占用时 raw receive 不可靠，先 uds_deinit）；
    3. 收到 0x18FF260D 上的 01 41 57 4B 唤醒标识帧 = 唤醒成功，返回 True；
       超时未见标识帧返回 False（不能当作已唤醒）。"""
    if stopTask:
        raise RuntimeError("用户停止脚本")
    for _ in range(3):
        uds_try(bus_id, SID_TP, [0x00], suppress=1)
        time.sleep(0.2)
    try:
        zcanpro.uds_deinit()
    except Exception as e:
        _log("UDS 通道释放失败（继续监听）: " + str(e))
    try:
        t_end = time.time() + float(listen_s)
        while time.time() < t_end:
            if stopTask:
                raise RuntimeError("用户停止脚本")
            for cid, dat in _recv_frames(bus_id):
                if cid == LIFE_ANNOUNCE_ID and tuple(dat[:4]) == LIFE_ANNOUNCE_MAGIC:
                    _log("收到唤醒标识帧 0x%08X %s，总线已唤醒" % (cid, _hex(dat[:8])))
                    return True
            time.sleep(0.02)
        _log("监听 %.1fs 未见 0x%08X 唤醒标识帧" % (listen_s, LIFE_ANNOUNCE_ID))
        return False
    finally:
        uds_init()  # 恢复 UDS 通道，供后续探测/升级使用


def probe_in_app(bus_id, retries=PROBE_ROUNDS):
    """Boot 无 UDS（safe mode 除外，见下）。APP 在线则 22 2113 有应答
    （正响应或 NRC）。

    不要用 0x34 探测：APP 已实现下载，默认会话回 0x22；且 Standby 下首帧
    只当 WUP，3s 超时后 ident 早已发出，再去听 0x18FF260D 会漏。
    无应答时：① raw 取证探测 Boot safe mode 标记帧（命中→SafeModeError，
    报错带 fail_step 语义，不再笼统"无应答"）；② 连发 3E 00 再立刻重试
    2113（MCU 此时应已 Normal）；③ 全部轮次失败后 wake_bus + 生命周期帧
    监听再判一次（禁止纯裸探循环），唤醒后再探。"""
    for attempt in range(1, retries + 1):
        try:
            rx = uds_req(bus_id, SID_RDBI, [0x21, 0x13])
            _log("探测 22 2113 成功 slot=%s，当前在 APP" % _hex((rx or [])[3:4]))
            return True
        except SafeModeError:
            raise
        except UdsNrcError as e:
            _log("探测 22 2113 NRC 0x%02X，当前在 APP" % e.nrc)
            return True
        except RuntimeError as e:
            _log("探测 22 2113 无应答（第 %d/%d 轮）: %s" % (attempt, retries, e))
            sm_step = _raw_probe_safe_mode(bus_id)
            if sm_step is not None:
                raise SafeModeError(sm_step)
            if attempt < retries:
                for _ in range(3):
                    uds_try(bus_id, SID_TP, [0x00], suppress=1)
                    time.sleep(0.15)
                time.sleep(0.4)
    # 全部轮次无应答：唤醒 + 生命周期帧监听后再判（Standby 首帧只当 WUP）
    try:
        wake_ok = wake_bus(bus_id, listen_s=2.0)
        life = _listen_lifecycle(bus_id, listen_s=1.0)
        _log("wake_bus=%s，生命周期帧 %d 个" % (wake_ok, len(life)))
        if wake_ok or life:
            try:
                rx = uds_req(bus_id, SID_RDBI, [0x21, 0x13])
                _log("唤醒后探测 22 2113 成功 slot=%s，当前在 APP"
                     % _hex((rx or [])[3:4]))
                return True
            except SafeModeError:
                raise
            except Exception as e:
                _log("唤醒后探测 22 2113 仍无应答: %s" % e)
    except SafeModeError:
        raise
    except Exception as e:
        _log("唤醒/监听异常: %s" % e)
    sm_step = _raw_probe_safe_mode(bus_id)
    if sm_step is not None:
        raise SafeModeError(sm_step)
    return False


def run_ota(bus_id):
    global FIRMWARE_PATH
    if not (1 <= TRANSFER_BLOCK_DATA <= MAX_TD_DATA):
        raise RuntimeError("TRANSFER_BLOCK_DATA 须为 1..%d" % MAX_TD_DATA)
    if not os.path.isfile(PRIVATE_KEY_PATH):
        raise RuntimeError("找不到私钥: " + PRIVATE_KEY_PATH)
    _log("私钥 " + PRIVATE_KEY_PATH)
    priv = load_ec_private_key(PRIVATE_KEY_PATH)
    uds_init()
    try:
        if FIRMWARE_OVERRIDE:
            _log("用户指定固件: %s（跳过自动选择）" % FIRMWARE_OVERRIDE)
            if not os.path.isfile(FIRMWARE_OVERRIDE):
                raise RuntimeError("用户指定固件不存在: %s（不回退自动选择；"
                                   "请检查 --firmware 路径后重跑）"
                                   % FIRMWARE_OVERRIDE)
            FIRMWARE_PATH = os.path.abspath(FIRMWARE_OVERRIDE)
        else:
            FIRMWARE_PATH = _pick_firmware()
        fw_data = open(FIRMWARE_PATH, "rb").read()
        bin_ver = _extract_sw_version(fw_data)
        _log("选中 bin 内 SW_VERSION_STR=%s（strings 扫描 QC_JYF_MCU1_FW_ 前缀）"
             % (bin_ver or "未找到"))
        # 版本闸门（fail-closed）：_pick_firmware() 自动选择与 --firmware
        # 指定两条路径均执行本检查，闸门不因指定文件而放松。
        if EXPECTED_SW_VERSION:
            if bin_ver != EXPECTED_SW_VERSION:
                raise RuntimeError(
                    "拒闪：选中 bin 版本 %s ≠ EXPECTED_SW_VERSION %s（零业务流量"
                    "退出，fail-closed）。构建链检查："
                    "① can_protocol.c SW_VERSION_STR 是否已改为目标版本（唯一真相源）；"
                    "② MDK 是否 Rebuild（禁 Incremental Build）；"
                    "③ pack/OTA 输入是否为本次构建产物；"
                    "④ python_tools/app bin/ 是否残留旧镜像（清理或确保 Keil bin 更新）。"
                    "期望版本切换：改脚本头部 EXPECTED_SW_VERSION 常量即可（留空则缺省"
                    "从镜像 strings 提取）；载荷前提=用户侧 Keil SW_VERSION_STR="
                    "QC_JYF_MCU1_FW_1.1.2 后 Rebuild APP→pack（仓库固件保持 1.1.1 零触碰）"
                    % (bin_ver or "未找到", EXPECTED_SW_VERSION))
            _log("版本感知：bin 版本与 EXPECTED_SW_VERSION=%s 一致，允许刷写"
                 % EXPECTED_SW_VERSION)
        image = pack_image_if_needed(FIRMWARE_PATH, priv)
        validate_image(image)
        want_ver = bin_ver or _extract_sw_version(image)
        expect_ver = EXPECTED_SW_VERSION or want_ver
        _log("镜像内 SW_VERSION_STR=%s；判定预期版本=%s（%s）" % (
            want_ver or "未找到", expect_ver or "不可用",
            "EXPECTED_SW_VERSION 配置" if EXPECTED_SW_VERSION else "缺省取镜像 strings"))
        if not probe_in_app(bus_id):
            raise RuntimeError("UDS 无应答（已重试 %d 轮）。"
                               "跳转后试运行确认会擦 metadata，CAN 可能 bus-off；"
                               "请烧录含 bus-off 恢复的 APP 后再连升。"
                               "空片用 merge_prod_bin.py。" % PROBE_ROUNDS)
        _log("[人话] 设备应答正常，CAN 探测通过")
        from_slot = read_did_u8(bus_id, 0x2113)
        _log("升级前 DID 0x2113=%s（0x00=App 区运行）" % _fmt_slot(from_slot))
        # ---- pre-upgrade baseline check (single-App arch; warn-only) ----
        base_ver = None
        try:
            rxv = uds_req(bus_id, SID_RDBI, [0xF1, 0x95])
            vb = _to_bytes(rxv[3:]) if len(rxv) > 3 else b""
            base_ver = vb.split(b"\x00")[0].decode("ascii", "ignore").strip() or None
        except Exception as e:
            _log("[基线] 0xF195 读取失败（不拦截流程）: %s" % e)
        if base_ver == BASELINE_SW_VERSION:
            _log("[基线] 设备基线：App 区 / %s，起点正确，开始升级" % base_ver)
        else:
            _log("[基线] ！！！！！ 基线异常提醒 ！！！！！ 设备当前版本=%s"
                 % (base_ver or "未读到"))
            _log("[基线] 预期起点=App 区 / %s（①烧录 boot+App 完整固件基线后"
                 "再升级）；基线不符不拦截执行，请自行确认烧录基线"
                 % BASELINE_SW_VERSION)
        _log("单 App 升级流：31/34/36 写 Backup 区 0x08010000 → 0x37 验签提交后"
             "设备自复位 → BOOT 搬运 Backup→App（擦 App 区→复制→复核→清标志→跳转）")
        _log("---- Programming ----")
        last_err = None
        for attempt in range(1, 9):
            try:
                uds_req(bus_id, SID_DSC, [0x02])
                last_err = None
                break
            except Exception as e:
                last_err = e
                _log("Programming 10 02 第 %d/8 次失败: %s" % (attempt, e))
                if attempt < 8:
                    time.sleep(0.5)
        if last_err is not None:
            raise last_err
        _log("---- SecurityAccess ----")
        rx = None
        last_sa = None
        for sa_try in range(1, 4):
            try:
                rx = uds_req(bus_id, SID_SA, [0x01])
                last_sa = None
                break
            except RuntimeError as e:
                last_sa = e
                _log("27 01 第 %d/3 次无应答: %s" % (sa_try, e))
                time.sleep(0.4)
        if last_sa is not None:
            raise last_sa
        # 清空残留应答（27 01 重试产生的延迟应答会污染 27 03 序列）
        for _ in range(5):
            try:
                if not _recv_frames(bus_id):
                    break
            except Exception:
                break
            time.sleep(0.05)
        if len(rx) < 6:
            raise RuntimeError("seed 响应过短")
        if len(rx) >= 34:
            seed = _to_bytes(rx[2:34])
            unlocked = (seed == b"\x00" * 32)
        else:
            seed = _to_bytes(rx[2:6])
            unlocked = (seed == b"\x00\x00\x00\x00")
        if unlocked:
            _log("已解锁 (ISO 14229 seed=0)，跳过 SendKey")
        else:
            if len(rx) < 34:
                raise RuntimeError("seed 须 32 字节, 实际 %d" % (len(rx) - 2))
            _log("seed " + _hex(rx[2:34]))
            sig = ecdsa_sign_msg(priv, seed)
            _log("SendKey 签名 %d 字节" % len(sig))
            send_security_key(bus_id, sig, seed=seed, priv=priv)
        _log("---- DID 0x2010 APP ----")
        uds_req(bus_id, SID_WDBI, [0x20, 0x10, 0x01])
        _log("---- 擦除 Backup 区（0x08010000~0x0801BFFF）----")
        _erase_with_retry(bus_id)
        try:
            did_dest = read_did_u8(bus_id, 0x2114)
            _log("DID 0x2114=0x%02X（0x02=Backup 区标记）" % did_dest)
            if did_dest != DL_TARGET_BACKUP:
                _log("警告: DID 0x2114=0x%02X ≠ 0x02（Backup 区标记），继续下载"
                     % did_dest)
        except Exception as e:
            _log("DID 0x2114 读失败（%s），继续下载" % e)
        size = len(image)
        addr_val = BACKUP_BASE
        _log("[人话] 固件写入 Backup 区 0x08010000；0x37 提交后 BOOT 搬运到 App 区")
        sz = [(size >> 24) & 0xFF, (size >> 16) & 0xFF, (size >> 8) & 0xFF, size & 0xFF]
        addr = [(addr_val >> 24) & 0xFF, (addr_val >> 16) & 0xFF,
                (addr_val >> 8) & 0xFF, addr_val & 0xFF]
        _log("---- RequestDownload %d @ 0x%08X ----" % (size, addr_val))
        # 清空残留应答（27 03 重试产生的延迟应答会干扰 FC 等待）
        for _ in range(5):
            try:
                if not _recv_frames(bus_id):
                    break
            except Exception:
                break
            time.sleep(0.05)
        # TesterPresent 确认 ECU SF 路径活着
        try:
            tp = uds_req_mf(bus_id, 0x3E, [0x00], timeout_s=2.0)
            _log("[TP] 3E 00 → %s" % _hex(tp))
        except Exception as e:
            _log("[TP] 3E 00 失败: %s" % e)
        # 0x34 重试：CF 可能因 CAN 间歇丢帧丢失，ECU ISO-TP RX 超时后不回
        rd_ok = False
        for rd_try in range(3):
            try:
                uds_req_mf(bus_id, SID_RD, [0x00, 0x44] + addr + sz, timeout_s=5.0)
                rd_ok = True
                break
            except RuntimeError as e:
                _log("0x34 第 %d/3 次失败: %s" % (rd_try + 1, e))
                time.sleep(1.0)
        if not rd_ok:
            raise RuntimeError("0x34 RequestDownload 三次均失败")
        _log("---- TransferData ----")
        seq = 1
        off = 0
        nblocks = (size + TRANSFER_BLOCK_DATA - 1) // TRANSFER_BLOCK_DATA
        blk = 0
        while off < size:
            if stopTask:
                raise RuntimeError("用户停止")
            chunk = image[off:off + TRANSFER_BLOCK_DATA]
            uds_req_mf(bus_id, SID_TD, [seq] + _to_list(chunk), quiet=True)
            off += len(chunk)
            blk += 1
            seq = (seq + 1) & 0xFF
            if off == size or (blk % 16 == 0):
                _log("[进度] %d/%d 块  %d/%d 字节 (%d%%)"
                     % (blk, nblocks, off, size, (off * 100) // size))
        _log("---- TransferExit ----")
        last_err = None
        for attempt in range(1, 6):
            try:
                uds_req(bus_id, SID_RTE, [], wait_pending_s=45)
                last_err = None
                break
            except SafeModeError:
                raise
            except Exception as e:
                last_err = e
                _log("0x37 第 %d/5 次: %s" % (attempt, e))
                if attempt < 5:
                    time.sleep(0.8)
        # 0x37 应答链异常分流（判别闭环原则：最终结论由复位后三条件给出，
        # 此处只拦"固件确定性拒绝"）：
        #   最终 NRC ≠ 0x22 → verify/字节数/commit_trial 被固件拒绝
        #     （ota_download.c ota_dl_poll 回 NRC 0x72 等），metadata 未提交，
        #     切槽必败 → 立即报错，不让判定闭环输出误导性"复位未生效"；
        #   NRC 0x22 → 可能是会话丢失，也可能是新固件 0x37 成功即自复位后
        #     重试帧落在重启后的新 APP（默认会话回 0x22）→ 快速取证0x2113：
        #     已是目标槽则升级实际已完成，交由三条件判定；
        #   纯超时 → 新固件 77 后自复位来不及应答/帧丢失，不判死，
        #     交给确认窗口判定。
        otx_anomaly = False
        if last_err is not None:
            if isinstance(last_err, UdsNrcError):
                if last_err.nrc == 0x22:
                    # post-commit self-reset makes retries land on the new APP
                    # (default session -> NRC 0x22): transport anomaly only;
                    # final verdict belongs to the post-reset window
                    otx_anomaly = True
                    _log("0x37 重试收 NRC 0x22（会话丢失或设备已自复位）——"
                         "交给复位后版本核验判定")
                else:
                    raise RuntimeError(
                        "0x37 TransferExit 被固件拒绝：%s。NRC 语义："
                        "0x72=verify/commit_backup 失败（Backup 区校验/提交环节，"
                        "flag 未落盘，搬运不会发生，见 ota_download.c ota_dl_poll）；"
                        "0x22=条件不正确（会话丢失/设备已重启）；0x24=请求序错误；"
                        "0x13=消息长度错；0x33=安全未解锁；0x71=传输中止/字节不符"
                        % last_err)
            else:
                otx_anomaly = True
                _log("0x37 五次均无最终应答（%s）。设备 0x37 收尾 77 后即自复位，"
                     "应答丢失属可能形态；复位后版本核验将给出最终判定" % last_err)
        if last_err is None:
            _log("[人话] 固件已写入 Backup 区并校验提交（0x37 已确认），"
                 "设备自复位后 BOOT 开始搬运")
        else:
            _log("[人话] 数据传输结束但 0x37 应答异常（详见上方技术日志）；"
                 "是否提交成功以复位后版本核验为准")
        _log("---- 等待设备自复位 ----")
        _log("[人话] 等待设备可能的自复位（0x37 提交结果见上；无需主机"
             "请求）；BOOT 搬运 Backup→App 约需数秒，等待设备回报…")
        t0 = time.time()
        while time.time() - t0 < 2.0:
            if stopTask:
                raise RuntimeError("用户停止脚本")
            time.sleep(0.05)
        to_slot, got_ver, ota_status = confirm_app_after_reset(bus_id)

        _log("[人话] 重启后设备回报：0x2113=%s（0x00=App 区运行），版本=%s；"
             "开始最终判定…" % (_fmt_slot(to_slot), got_ver or "未读到"))
        # ---- verdict closed loop (single-App arch) ----
        # 1) APP responds after reset (confirm_app_after_reset did not raise)
        # 2) 0xF195 == EXPECTED_SW_VERSION (backup->App copy + jump happened)
        # 3) 0x2113 informational: single-App architecture answers 0x00
        problems = []
        if not expect_ver:
            problems.append("预期版本不可用：EXPECTED_SW_VERSION 未设置且镜像内未找到"
                            " QC_JYF_MCU1_FW_ 版本串——请在脚本 EXPECTED_SW_VERSION 配置"
                            "目标版本后重跑")
        elif got_ver != expect_ver:
            problems.append("版本不符：0xF195=%s，预期=%s（Backup→App 搬运未生效"
                            "或载荷错误——查看 Boot 诊断帧 M1~M4 与 copy_retry）"
                            % (got_ver or "未读到", expect_ver))
        if to_slot is not None and to_slot != 0:
            problems.append("异常：0x2113=0x%02X（单 App 架构应恒为 0x00=App 区）"
                            % (to_slot & 0xFF))
        if problems:
            _raise_ota_fail(bus_id, 0, DL_TARGET_BACKUP, to_slot, got_ver,
                            expect_ver, problems, otx_anomaly)
        _log("判定闭环满足：① 复位后 APP 应答；② 0xF195=%s==预期版本 %s；"
             "0x2113=%s（0x00=App 区）" % (got_ver, expect_ver, _fmt_slot(to_slot)))
        _log("[人话] 升级成功：设备已运行 App 区 / %s（Boot 搬运完成）" % got_ver)
        _log("======== OTA 成功 ========")
    finally:
        zcanpro.uds_deinit()


def z_main():
    global stopTask
    stopTask = False
    _log("======== Qi CAN-UDS OTA (APP 写对面槽) ========")
    buses = zcanpro.get_buses()
    _log("总线 " + str(buses))
    if not buses:
        _log("请先打开 CAN 通道 250kbps 扩展帧")
        return
    try:
        run_ota(buses[0]["busID"])
    except Exception as e:
        _log("OTA 失败: " + str(e))


def _parse_cli_args():
    """命令行参数解析（standalone 运行路径专用）。

    ZCANPRO 宿主加载本脚本时不经过本函数（宿主直接调用 z_main()，
    FIRMWARE_OVERRIDE 保持 None → 自动选择路径）。用 parse_known_args：
    宿主/包装器 argv 中的未知参数不拦截脚本执行。"""
    parser = argparse.ArgumentParser(
        description="Qi CAN-UDS OTA 扩展脚本（单 App 架构）命令行参数")
    parser.add_argument("--firmware", default=None,
                        help="指定 OTA 发送的 bin 文件路径（跳过自动选择，仍执行版本闸门检查）")
    args, _unknown = parser.parse_known_args()
    return args


if __name__ == "__main__":
    _cli = _parse_cli_args()
    FIRMWARE_OVERRIDE = _cli.firmware
    if zcanpro is None:
        _log("zcanpro 模块不可用：本脚本需在 ZCANPRO 扩展脚本环境"
             "（Python 3.8 32 位 + zcanpro 模块）中运行；"
             "--firmware 参数解析正常（%s）"
             % (FIRMWARE_OVERRIDE or "未指定，自动选择"))
        sys.exit(1)
    _argv0 = os.path.basename((sys.argv[0] if sys.argv else "") or "")
    if _argv0.endswith(".py"):
        # 命令行直跑本脚本（argv[0]=脚本路径）→ 参数解析后启动 OTA；
        # ZCANPRO 宿主若以 __main__ 形态加载（argv[0]=宿主可执行体）不自跑，
        # 由宿主调用 z_main()，避免双重执行。
        z_main()
