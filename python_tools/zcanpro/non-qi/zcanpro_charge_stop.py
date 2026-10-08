# -*- coding: utf-8 -*-
"""
ZCANPRO 扩展脚本 — 停止 Qi 无线充电

与 zcanpro_charge_start.py 配对使用（start 写 0x2101=0x01 使能，本脚本写 0x00 停止）。

流程：
  0. 运行侧探测（22 2113 / 0x34 正证据判定 + Standby 唤醒收敛，仅 APP 可控充电）
  1. 进入扩展会话 (0x10 0x03)
  2. 安全解锁 (0x27 01 → 0x27 03 分片 → 0x27 02；含签名本地自检)
  3. 信息读当前充电状态 (0x22 21 02) + 停止充电 (0x2E 21 01 00)
  4. 读回确认 (0x22 21 02)：状态非 CHARGING(0x04) → PASS
  5. [判定结论] 充电停止 PASS/FAIL（证据：6E 21 01 + 状态读回值）

固件事实（当前树 bf41077 复核，改动前逐项核实）：
  - DID 0x2101 = CHARGER_ENABLE，写门禁 = SESSION_EXTENDED + security_unlocked
    （can_protocol.c:1035-1045）；写处理 g_qi_charger_enable=val +
    board_charge_set_enable(val)（can_protocol.c:1252-1253）；写专用不可读
    （fill_did_payload 无 case → 默认 NRC 0x31，can_protocol.c:850-851）。
  - 业务门禁：充电故障态（g_qi_fault_code!=0）时仅拒绝**使能值 0x01**
    （can_protocol.c:1247-1251，条件为 val==0x01U）——停止值 0x00 不受该
    故障门禁拦截，故障态下停止照常放行（写步若回 NRC 0x22 = 会话门禁
    （S3 超时回 default），不是故障门禁，判读见 Step 3 日志）。
  - 0x2101 值域：0x00/0x01（val>0x01 → NRC 0x31，can_protocol.c:1240-1243）。
  - DID 0x2102 = 充电状态，读无会话/安全门禁（handle_read_data_by_id 仅长度
    检查 can_protocol.c:972-1007，派发 :1785-1786）；应答为 PB2 实时电平 +
    使能标志的合成值（can_protocol.c:775-782）：PB2 高→0x04 CHARGING；
    PB2 低且 enable!=0→0x01 STANDBY；PB2 低且 enable=0→0x00 DISABLED。
  - 状态枚举（can_protocol.h:185-195）：0x00 DISABLED / 0x01 STANDBY /
    0x02 DEVICE_DETECTED / 0x03 NEGOTIATING / 0x04 CHARGING /
    0x05 CHARGE_COMPLETE / 0x06 SUSPENDED_THERMAL / 0x07 SUSPENDED_FOD /
    0x08 FAULT / 0x09 SERVICE_MODE / 0x0A LOW_POWER。
  - 停止生效路径：2E 21 01 00 → board_charge_set_enable(0) → board_5v_set(0)
    拉低 PB2（board_gpio.c:99-105）；board_charge_poll 只在 g_charge_enabled
    !=0 且霍尔检测到手机时才拉高 PB2（board_gpio.c:86-97）→ 使能清零后
    读回预期 0x00 DISABLED（手机仍在充电板上不影响该判定）。

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
def _find_repo_root(start):
    """向上探测仓库根目录（含 docs/keys 的祖先目录），不写死目录层级假设。"""
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
PUBLIC_KEY_PATH = os.path.join(REPO_ROOT, "docs", "keys", "public.pem")  # 签名本地自检用

SA_SIG_CHUNK = 4

# ======== UDS 常量 ========
UDS_REQ_ID = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

SID_DSC = 0x10
SID_SA  = 0x27
SID_WDBI = 0x2E
SID_RDBI = 0x22
SID_RD  = 0x34
SID_TP  = 0x3E
SID_NRC  = 0x7F
SID_PR   = 0x40

NRC_RCRRP = 0x78
NRC_EXCEEDED_ATTEMPTS   = 0x36  # 27 02 验签失败次数超限当次应答（fail_count≥3，固件随即锁定约30s）
NRC_REQUIRED_TIME_DELAY = 0x37  # 锁定期内 27 01 的应答（requiredTimeDelay）；锁定期内
                                # 27 02 因固件已清 seed/签名缓冲回 0x24，非 0x37
NRC_CONDITIONS_NOT_CORRECT = 0x22  # 固件写门禁：会话不满足（S3 超时后会话回 default 的典型表现）
NRC_SECURITY_ACCESS_DENIED = 0x33  # 固件写门禁：security_unlocked=0（S3 超时/会话切换被清）

# ======== S3 会话超时防护参数（与 charge_start/set_power/sn_write 同构）========
# 固件：SESSION_TIMEOUT_MS=5000（can_protocol.h:167）；UDS 交换间隙>5s 时
# isotp_message_received / can_protocol_poll 会把会话回 default+清 security
# （can_protocol.c:1844-1853 / 2094-2096），后续 2E 写撞 NRC 0x22/0x33。
# 固件验证结论：3E 80（suppress）与 3E 00 均刷新 S3 计时——uds_process_message
# 派发前对任何诊断请求统一刷新 last_tester_present_tick（can_protocol.c:1770），
# handle_tester_present 对 suppress 帧同样刷新计时且不回响应（:1509）→
# keepalive 优先 3E 80（无响应帧干扰，ZCANPRO suppress 请求立即返回）。
S3_KEEPALIVE_INTERVAL_S = 3.0      # keepalive 周期：3s < 5s 超时窗，留 2s 余量
S3_SIGN_GAP_GUARD_S     = 3.0      # 签名+自检耗时超过该值：先发 3E 再进 27 03 分片
SA_LOCKOUT_WAIT_S       = 31.0     # SA 锁定等待总时长（与 charge_start 一致）


class UdsNrcError(RuntimeError):
    """带 NRC 码的 UDS 异常；SecurityAccess 重试分支按 e.nrc 判别设备锁定等场景。"""

    def __init__(self, sid, nrc):
        RuntimeError.__init__(self, "NRC SID=0x%02X NRC=0x%02X" % (sid, nrc))
        self.sid = sid
        self.nrc = nrc

DID_CHARGER_ENABLE = 0x2101
DID_CHARGE_STATE   = 0x2102

# 充电状态值（can_protocol.h:185-195 完整枚举）
CHARGE_DISABLED = 0x00
CHARGE_STANDBY  = 0x01
CHARGE_CHARGING = 0x04

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


# ======== 宿主侧签名自检（与 zcanpro_sa_lockout_test.py 同构，已实机验证）========
# 背景（skill §7.13/§8）：mock 只断言"签名非全 0+分片完整"，无密码学校验，
# 拦不住 EC 数学/编码类缺陷（c81bb18 实机 0x35 实证）→ 签名后、发送 27 03
# 前宿主侧验签自检。验签实现 = 独立仿射 EC 数学（不复用签名路径 _jp_*
# 雅可比代码，自检不与签名共享失效模式）；零新增依赖。

def load_ec_public_key(path):
    """解析 public.pem（SPKI PEM/DER 或 65 字节裸未压缩点），
    返回 (x, y) 仿射坐标 int 对；未找到 0x04||X||Y 即报错。"""
    raw = open(path, "rb").read()
    if sys.version_info[0] >= 3:
        text = raw.decode("ascii", "ignore")
    else:
        text = raw
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
    pt = None
    if len(der) == 65 and _hex(der[:1]) == "04":
        pt = der[1:65]
    else:
        # SPKI：递归 TLV 遍历（顶层 SEQUENCE(0x30) 内嵌套 BIT STRING(0x03)
        # = 0x00 未用位 + 0x04||X||Y；按 int 比较 tag）
        def _walk_spki(buf, start, end):
            i = start
            while i + 2 <= end:
                tag = buf[i]
                try:
                    ln, j = _der_len(buf, i + 1, end)
                except Exception:
                    return None
                if j + ln > end:
                    return None
                body = buf[j:j + ln]
                if tag == 0x03 and ln >= 66 and _hex(body[:2]) == "00 04":
                    return body[2:66]
                if tag == 0x04 and ln == 64:
                    return body
                if tag in (0x30, 0x31):  # 构造类型：递归进入
                    found = _walk_spki(buf, j, j + ln)
                    if found is not None:
                        return found
                i = j + ln
            return None
        pt = _walk_spki(der, 0, len(der))
    if pt is None:
        raise ValueError("无法解析公钥（未找到未压缩点 0x04||X||Y）: " + path)
    px = _int_be(pt[:32])
    py = _int_be(pt[32:64])
    if not (0 < px < _P and 0 < py < _P):
        raise ValueError("公钥坐标超出曲线域: " + path)
    return px, py


def _ec_aff_double(pt):
    """仿射倍点（None=无穷远点）。仅用于宿主侧验签自检。"""
    if pt is None:
        return None
    x, y = pt
    if y % _P == 0:
        return None
    m = ((3 * x * x + _A) * _inv((2 * y) % _P, _P)) % _P
    x3 = (m * m - 2 * x) % _P
    y3 = (m * (x - x3) - y) % _P
    return x3, y3


def _ec_aff_add(p1, p2):
    """仿射点加（None=无穷远点）。仅用于宿主侧验签自检。"""
    if p1 is None:
        return p2
    if p2 is None:
        return p1
    x1, y1 = p1
    x2, y2 = p2
    if x1 == x2:
        if (y1 + y2) % _P == 0:
            return None
        return _ec_aff_double(p1)
    m = ((y2 - y1) * _inv((x2 - x1) % _P, _P)) % _P
    x3 = (m * m - x1 - x2) % _P
    y3 = (m * (x1 - x3) - y1) % _P
    return x3, y3


def _ec_aff_mul(k, pt):
    """仿射标量乘 double-and-add。仅用于宿主侧验签自检。"""
    k %= _N
    result = None
    addend = pt
    while k > 0:
        if k & 1:
            result = _ec_aff_add(result, addend)
        addend = _ec_aff_double(addend)
        k >>= 1
    return result


def ecdsa_verify_msg(pub_xy, msg, sig):
    """ECDSA 验签（secp256r1+SHA-256），验签对象=SHA-256(msg)，
    sig=r||s 各 32 字节大端——与固件 sha256_hash(g_seed,32U)+uECC_verify
    语义一致。返回 True/False；供签名自检与 mock 冒烟宿主验签复用。"""
    import hashlib
    if pub_xy is None:
        return False
    px, py = pub_xy
    if not (0 < px < _P and 0 < py < _P):
        return False
    sig = _to_bytes(sig)
    if len(sig) != 64:
        return False
    r = _int_be(sig[:32])
    s = _int_be(sig[32:])
    if not (1 <= r < _N and 1 <= s < _N):
        return False
    z = _int_be(hashlib.sha256(msg).digest()) % _N
    w = _inv(s, _N)
    u1 = (z * w) % _N
    u2 = (r * w) % _N
    pt = _ec_aff_add(_ec_aff_mul(u1, (_GX, _GY)), _ec_aff_mul(u2, (px, py)))
    if pt is None:
        return False
    return (pt[0] % _N) == r


def _sig_self_check(sig, seed):
    """签名本地自检：public.pem 对 SHA-256(seed) 验签本地签名。
    日志"[自检] 签名本地验证 PASS/FAIL"；FAIL 时调用方不发送 27 03。"""
    if not os.path.isfile(PUBLIC_KEY_PATH):
        _log("  [自检] 签名本地验证 FAIL——找不到公钥 %s（自检无法执行）"
             % PUBLIC_KEY_PATH)
        return False
    try:
        pub = load_ec_public_key(PUBLIC_KEY_PATH)
        ok = ecdsa_verify_msg(pub, seed, sig)
    except Exception as e:
        _log("  [自检] 签名本地验证 FAIL——验签执行异常: %s" % e)
        return False
    if ok:
        _log("  [自检] 签名本地验证 PASS（public.pem 对 SHA-256(seed) 验签通过）")
        return True
    _log("  [自检] 签名本地验证 FAIL（public.pem 验签失败，签名数据差分）")
    return False


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
            raise UdsNrcError(data[1], data[2])
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


def _s3_keepalive_wait(bus_id, total_s=SA_LOCKOUT_WAIT_S, interval_s=S3_KEEPALIVE_INTERVAL_S):
    """S3 会话超时防护：SA 锁定等待期间周期发送 3E 80 keepalive。

    固件验证（can_protocol.c）：uds_process_message 对任何诊断请求（含
    3E suppress 帧）在派发前刷新 last_tester_present_tick（:1770）；
    handle_tester_present 对 suppress 帧同样刷新计时且不回响应（:1509）
    → 3E 80 与 3E 00 同样续期 S3，优先 3E 80（无响应帧干扰，库立即返回）。
    周期 3s < SESSION_TIMEOUT_MS 5s（can_protocol.h:167）；总等待时长与
    charge_start 一致。走既有 uds_try 通道，不碰探测/raw 路径；
    keepalive 失败只记录，不中断等待。
    """
    t_end = time.time() + float(total_s)
    sent = 0
    _log("S3 keepalive 等待 %.0fs（每 %.0fs 发 3E 80 suppress，防会话超时回 default+清 security）"
         % (total_s, interval_s))
    while True:
        remain = t_end - time.time()
        if remain <= 0:
            break
        time.sleep(interval_s if remain > interval_s else remain)
        if stopTask:
            raise RuntimeError("用户停止脚本")
        uds_try(bus_id, SID_TP, [0x80], suppress=1)
        sent += 1
    _log("S3 keepalive 等待结束：%.0fs 内共发 %d 次 3E 80" % (total_s, sent))


def _wdbi_with_s3_guard(bus_id, payload, priv):
    """2E 写步 S3 超时兜底（与 charge_start/set_power/sn_write 同构）：
    写收到 NRC 0x22/0x33 → 重发本脚本会话控制 10 03 + send_security_key
    完整重解锁 + 重试写一次。

    固件门禁（can_protocol.c:1035-1045）：写 0x2101 DID_CHARGER_ENABLE
    要求 SESSION_EXTENDED+security_unlocked——NRC 0x22=会话不满足
    （S3 超时回 default 的典型表现），0x33=安全态被清。
    停止值判读要点（can_protocol.c:1247-1251）：充电故障门禁条件为
    val==0x01U——仅拦使能写；本脚本写停止值 0x00 不受故障门禁拦截，
    写步 0x22 一律按会话门禁判读。兜底重试后若仍 0x22 将以 NRC 抛出
    （失败响亮、无误写面）。会话字节与本脚本业务链一致（10 03 扩展会话）。
    日志注明「S3 超时恢复」。
    """
    try:
        uds_req(bus_id, SID_WDBI, payload)
        return
    except UdsNrcError as e:
        if e.nrc not in (NRC_CONDITIONS_NOT_CORRECT, NRC_SECURITY_ACCESS_DENIED):
            raise
        _log("2E 写 NRC 0x%02X：疑似 S3 会话超时（会话回 default / 安全态被清）——"
             "S3 超时恢复：重发 10 03 扩展会话 + 完整重解锁 + 重试写一次" % e.nrc)
    uds_req(bus_id, SID_DSC, [0x03])
    send_security_key(bus_id, priv)
    uds_req(bus_id, SID_WDBI, payload)
    _log("S3 超时恢复：会话+安全态重建后重试写成功")


def read_did(bus_id, did):
    rx = uds_req(bus_id, SID_RDBI, [(did >> 8) & 0xFF, did & 0xFF])
    if len(rx) < 4:
        raise RuntimeError("DID 0x%04X 响应过短" % did)
    return rx[3:]


def _sa_fetch_seed(bus_id):
    """27 01 取 seed。固件自 d64e8c2 起 seed 为 32 字节（67 01 + 32B）。
    固件每次 27 01 都刷新 seed 并清空签名缓冲。"""
    rx = uds_req(bus_id, SID_SA, [0x01])
    if len(rx) < 34:
        raise RuntimeError("seed 响应过短: %d 字节, 期望 67 01 + 32 字节 seed（≥34）" % len(rx))
    return rx


def _sa_send_sig(bus_id, sig):
    """27 03 分片发送 64 字节签名：4 字节/帧 × 16 帧，blockSeq 0x01 起递增。"""
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


def send_security_key(bus_id, priv):
    """SecurityAccess 解锁：每次尝试都是完整流程——
    27 01 取 32 字节 seed（全 0 = 已解锁，直接返回正响应）→
    ecdsa_sign_msg(priv, seed) 重签 → 签名本地自检（[自检]，FAIL 不发送
    27 03）→ 重发 16 帧 27 03 分片 → 27 02 验签。

    固件每次 27 01 都刷新 seed 并清空签名缓冲，27 02 失败后只重发 27 02
    或沿用旧 seed 签名必失败，故重试必须完整重做。最多 5 次完整尝试；
    NRC 0x36/0x37 = 设备 SecurityAccess 锁定（fail_count≥3，约 30s）：
    0x36 是 27 02 验签失败超限当次的应答；0x37 是锁定期内 27 01 的应答
    （requiredTimeDelay）。锁定期内的 27 02 固件因验签失败已清
    g_seed_generated，先撞序检查回 NRC 0x24（can_protocol.c:1430-1433,
    1469），不是 0x37——脚本每轮先发 27 01，锁定仍由 0x37 捕获，重试逻辑
    不变：两者都日志明确提示并经 _s3_keepalive_wait（每 3s 发 3E 80 suppress
    刷新 S3 计时，总等待 31s，防等待期会话超时）后继续完整流程。
    """
    last = None
    for attempt in range(1, 6):
        if stopTask:
            raise RuntimeError("用户停止脚本")
        try:
            _log("SecurityAccess 第 %d/5 次：27 01 → 重签 → 自检 → 27 03 → 27 02" % attempt)
            rx = _sa_fetch_seed(bus_id)
            seed = _to_bytes(rx[2:34])
            if seed == b"\x00" * 32:
                _log("27 01 seed=0（32 字节全 0），已解锁，直接返回正响应")
                return rx
            _log("seed(32B) " + _hex(rx[2:34]))
            # S3 防护：签名+自检为 Python 纯软件实现，在 ZCANPRO 解释器
            # 可能耗时秒级；计算期间无总线流量，距上次 UDS 交换（27 01 应答）
            # 超过阈值则先发 3E 80 刷新 S3 计时再进 27 03 分片（>5s 时固件
            # poll 已将会话回 default，3E 救不回，最终由写步 NRC 0x22/0x33
            # 兜底 _wdbi_with_s3_guard 恢复）
            t_sign = time.time()
            sig = ecdsa_sign_msg(priv, seed)
            # 宿主侧密码学自检（skill §7.13）：签名完成后、发送 27 03 前执行；
            # FAIL 直接报错，不发送任何 27 03 分片（设备侧未受影响）
            if not _sig_self_check(sig, seed):
                raise RuntimeError(
                    "[自检] 签名本地验证 FAIL：本地 ECDSA 签名无法通过 public.pem "
                    "验签（签名数据差分），不发送 27 03；请检查公钥文件 "
                    + PUBLIC_KEY_PATH)
            sign_gap = time.time() - t_sign
            if sign_gap > S3_SIGN_GAP_GUARD_S:
                _log("ECDSA 签名+自检耗时 %.1fs（>%.1fs）：先发 3E 80 刷新 S3 计时再进 27 03 分片"
                     % (sign_gap, S3_SIGN_GAP_GUARD_S))
                uds_try(bus_id, SID_TP, [0x80], suppress=1)
            _sa_send_sig(bus_id, sig)
            return uds_req(bus_id, SID_SA, [0x02], wait_pending_s=45)
        except UdsNrcError as e:
            last = e
            if e.nrc in (NRC_EXCEEDED_ATTEMPTS, NRC_REQUIRED_TIME_DELAY):
                _log("NRC 0x%02X：设备 SecurityAccess 锁定（fail_count≥3，固件锁定约30s），S3 keepalive 等待 %.0fs 后完整重试" % (e.nrc, SA_LOCKOUT_WAIT_S))
                _s3_keepalive_wait(bus_id)
                continue
            _log("SecurityAccess 第 %d/5 次失败: %s（重试将重新取 seed 重签重发分片）" % (attempt, e))
            time.sleep(0.5)
        except RuntimeError as e:
            last = e
            _log("SecurityAccess 第 %d/5 次失败: %s" % (attempt, e))
            time.sleep(0.5)
    raise last


def charge_state_name(state):
    """充电状态值→名称（can_protocol.h:185-195 完整枚举）。"""
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
        0x09: "SERVICE_MODE",
        0x0A: "LOW_POWER",
    }.get(state, "未知(0x%02X)" % state)


# ======== 运行侧探测 / Standby 唤醒收敛（判定只按正证据）========
# 绝不按 NRC 值分叉定位。固件事实（改动前已核实，文件:行号见提交报告）：
#   - APP 实现全部 UDS（含 0x34）：ota_download.c ota_dl_handle_request_download
#     首个门禁=会话检查，默认会话对 0x34 回 NRC 0x22，该路径无 0x11 分支
#     （应答集合：正响应/0x22/0x33/0x24/0x13/0x31）→ 对 0x34 的任何应答=在 APP；
#   - Boot 无完整 UDS：仅 Boot safe mode 应答 22 2113 与非 suppress 的 3E，
#     0x34 无分支静默（boot_safe_mode.c）→ 0x34 无应答不能反推“不在 APP”；
#   - APP 的 DID 0x2113（运行区标识）同样正响应：62 21 13 <slot>，单 App
#     架构 slot 恒 0x00=App 区运行（ota_running_slot() 为 deprecated
#     shim 恒 0，can_protocol.c fill_did_payload case DID_ACTIVE_SLOT），
#     永不 0xFE → Boot safe mode 判据=应答中
#     62 21 13 后字节为 0xFE（boot_safe_mode.c:155），而非“正响应”本身；
#   - 无应答 ≠ 不在 APP：SIT1145 空闲 180s（CAN_LP_IDLE_TIMEOUT_MS）进
#     Standby，首帧只当 ISO 11898-2 WUP 被消耗，MCU 收不到内容。先唤醒
#     burst（3E 80 suppress ×3 + 0.2s）×3，再释放 UDS 通道 raw 监听生命
#     周期帧（0x18FF260D）：AWK 01 41 57 4B / BOOTUP 01 41 00 = APP 已启动
#     正证据；ABT 01 41 42 54 cause step A5 00（500ms）= Boot safe mode。
#   实现与 python_tools/zcanpro_ext_ota_auto.py wake_bus/probe_in_app 同构。

LIFE_ANNOUNCE_ID    = 0x18FF260D
LIFE_ANNOUNCE_MAGIC = (0x01, 0x41, 0x57, 0x4B)  # AWK：Standby 唤醒标识
LIFE_BOOTUP_MAGIC   = (0x01, 0x41, 0x00)        # BOOTUP：上电/复位/Boot 跳转标识
LIFE_ABT_MAGIC      = (0x01, 0x41, 0x42, 0x54)  # ABT：Boot safe mode 心跳
SAFE_MODE_RESP_ID   = UDS_RESP_ID               # 0x18DA030D
SAFE_MODE_MARKER    = (0x62, 0x21, 0x13, 0xFE)  # Boot safe mode 应答标记（不含 ISO-TP PCI）
DID_ACTIVE_SLOT     = 0x2113
PROBE_ROUNDS        = 3
WAKE_BURST_ROUNDS   = 3      # （3E 80 ×3 + 0.2s）×3
WAKE_BURST_COUNT    = 3
WAKE_BURST_GAP_S    = 0.2
LIFE_LISTEN_S       = 2.0
VERDICT_CN = {"APP": "APP", "BOOT_SM": "Boot safe mode", "UNKNOWN": "UNKNOWN"}

FAIL_STEP_DESC = {
    0: "未执行镜像校验 / metadata 无有效状态（单 App 架构无槽选择）",
    1: "镜像 magic 校验失败",
    2: "image_length 为 0 或超出槽范围",
    3: "镜像 CRC32 校验失败",
    4: "Reset handler 不在槽内（跨槽链接镜像）",
    5: "ECDSA 公钥缺失/无效（Device Info 与内置公钥均不可用）",
    6: "ECDSA P-256 验签失败",
}


def _safe_mode_step(dat):
    """识别 Boot safe mode 应答：ISO-TP SF 05 62 21 13 FE <step> 或历史裸帧
    62 21 13 FE <step>（双格式兼容，与 zcanpro_ext_ota_auto.py 同构）。"""
    if (len(dat) >= 6 and dat[0] == 0x05 and dat[1] == 0x62 and dat[2] == 0x21
            and dat[3] == 0x13 and dat[4] == 0xFE):
        return int(dat[5])
    if (len(dat) >= 5 and dat[0] == 0x62 and dat[1] == 0x21
            and dat[2] == 0x13 and dat[3] == 0xFE):
        return int(dat[4])
    return None


def _safe_mode_detail(step):
    desc = FAIL_STEP_DESC.get(step, "未知 fail_step（Boot 固件可能早于标记帧版本）")
    return "22 2113 应答 0xFE 标记 = Boot safe mode，fail_step=%d（%s）" % (step, desc)


def _parse_can_frame(f):
    """与 zcanpro_ext_ota_auto.py 同构：兼容 dict / (id,data) 元组 / 属性对象；
    CAN ID 取 & 0x1FFFFFFF（bit31 为 ZLG 扩展帧标志）。"""
    cid = None
    dat = None
    if isinstance(f, dict):
        cid = f.get("can_id", f.get("id", f.get("ID")))
        dat = f.get("data", f.get("Data"))
    elif isinstance(f, (tuple, list)) and len(f) >= 2:
        cid, dat = f[0], f[1]
    else:
        cid = getattr(f, "can_id", getattr(f, "id", None))
        dat = getattr(f, "data", None)
    if cid is None:
        return None
    if not isinstance(dat, (list, tuple, bytes, bytearray)):
        dat = []
    try:
        return (int(cid) & 0x1FFFFFFF, [int(x) & 0xFF for x in list(dat)])
    except (TypeError, ValueError):
        return None


def _unwrap_receive(raw):
    """与 zcanpro_ext_ota_auto.py / zcanpro_read_app_version.py 同构：
    receive 常见返回 (status, [frames])。"""
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


def _lifecycle_check(cid, dat):
    """0x18FF260D 生命周期帧分类：awk / bootup / abt / shutdown / None。
    ABT（01 41 42 54）只有 Boot safe mode 发；AWK/BOOTUP 只有 APP 发
    （can_protocol.c can_lp_send_ident_bus、lifecycle.c lifecycle_send）。"""
    if cid != LIFE_ANNOUNCE_ID or len(dat) < 3:
        return None
    if len(dat) >= 4 and tuple(dat[:4]) == LIFE_ANNOUNCE_MAGIC:
        return "awk"
    if len(dat) >= 4 and tuple(dat[:4]) == LIFE_ABT_MAGIC:
        return "abt"
    if dat[0] == 0x01 and dat[1] == 0x41 and dat[2] == 0x00:
        return "bootup"
    if dat[0] == 0x06 and dat[1] == 0x41:
        return "shutdown"
    return None


def _listen_lifecycle(bus_id, listen_s=LIFE_LISTEN_S):
    """释放 UDS 通道后 raw 收帧监听生命周期帧（UDS 占用通道时 raw receive
    不可靠，必须先 uds_deinit，finally 恢复）。
    返回 ([(类型,data)...], [(cid,data)...全部收帧])。"""
    found = []
    all_frames = []
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
                all_frames.append((cid, dat))
                lt = _lifecycle_check(cid, dat)
                if lt is not None:
                    _log("生命周期帧 [%s] 0x%08X %s" % (lt.upper(), cid, _hex(dat[:8])))
                    found.append((lt, dat))
            time.sleep(0.02)
    finally:
        try:
            uds_init()
        except Exception as e:
            _log("UDS 通道恢复失败: " + str(e))
    return found, all_frames


def wake_bus(bus_id, listen_s=LIFE_LISTEN_S):
    """Standby 唤醒收敛：（3E 80 suppress ×3 + 0.2s）×3 后释放 UDS 通道
    监听生命周期帧。返回 (证据列表, 全部收帧)。证据 (类型,data)：
    AWK/BOOTUP=APP 启动正证据；ABT=Boot safe mode 正证据（500ms 心跳，
    无需再发 UDS 探测）。"""
    if stopTask:
        raise RuntimeError("用户停止脚本")
    for _b in range(WAKE_BURST_ROUNDS):
        for _i in range(WAKE_BURST_COUNT):
            uds_try(bus_id, SID_TP, [0x80], suppress=1)
            time.sleep(WAKE_BURST_GAP_S)
    return _listen_lifecycle(bus_id, listen_s=listen_s)


def _probe_once(bus_id, sid, payload):
    """单次探测：直接调库并按【应答字节】分类，绝不按 NRC 值推断运行侧。
    返回 (tag, detail)：
      ("silent",  None)  无应答——不能据此定位（Standby/Boot 静默/链路问题）
      ("boot_sm", str)   22 2113 应答带 0xFE 标记 = Boot safe mode
      ("app",     str)   有应答且非 Boot safe mode 标记 = 在 APP（含任意 NRC）
      ("other",   data)  未识别应答字节，仅取证，不参与定位
    """
    if stopTask:
        raise RuntimeError("用户停止脚本")
    req = {"src_addr": UDS_REQ_ID, "dst_addr": UDS_RESP_ID,
           "suppress_response": 0, "sid": sid, "data": list(payload)}
    _log("[Tx-probe] %02X %s" % (sid, _hex(payload[:16])))
    try:
        resp = zcanpro.uds_request(bus_id, req)
    except Exception as e:
        _log("[Rx-probe] 探测异常（按无应答处理，不据此定位）: %s" % e)
        return ("silent", None)
    data = list((resp or {}).get("data") or [])
    if data:
        _log("[Rx-probe] " + _hex(data[:24]))
    else:
        _log("[Rx-probe] 无应答 (result=%s)" % ((resp or {}).get("result"),))
    if not data:
        return ("silent", None)
    step = _safe_mode_step(data)
    if step is not None:
        return ("boot_sm", _safe_mode_detail(step))
    if len(data) >= 3 and data[0] == SID_NRC and data[1] == sid:
        # 任意 NRC = 对端 UDS 栈应答了该服务 → APP。Boot safe mode 对 22 2113
        # 只回正响应+0xFE 标记，对 0x34 不应答；仅对非 2113 的 22 DID 才回
        # 7F 22 11，而本探测的 22 恒为 DID 2113，不会命中该分支。
        return ("app", "NRC 0x%02X（对 %02X 的应答 = APP 实现该服务）" % (data[2], sid))
    if data[0] == (sid + SID_PR):
        if sid == SID_RDBI:
            return ("app", "22 2113 正响应 slot=%s（APP DID_ACTIVE_SLOT，记录字节非 0xFE）"
                    % _hex(data[3:4]))
        return ("app", "0x34 正响应 %s（APP 实现下载服务）" % _hex(data[:8]))
    # 未识别字节：UDS 响应 ID 上可能出现生命周期双发帧（can_lp_send_ident：
    # 07/03 01 41 57 4B …，APP 侧发出）→ 也是 APP 正证据
    if len(data) >= 3 and data[0] in (0x03, 0x07) and data[1] == 0x01 and data[2] == 0x41:
        return ("app", "生命周期双发帧（UDS 响应 ID）%s = APP 正证据" % _hex(data[:8]))
    return ("other", data)


def probe_location(bus_id):
    """运行侧探测：只按正证据判定，绝不按 NRC 值分叉。
    返回 (verdict, detail)，verdict ∈ ("APP", "BOOT_SM", "UNKNOWN")：
      22 2113 应答带 0xFE 标记 → BOOT_SM；22 2113 / 0x34 任何其他应答 → APP；
      无应答 → wake_bus 唤醒收敛 + 生命周期帧监听（ABT→BOOT_SM，
      AWK/BOOTUP→APP 正证据）后重试，≤3 轮；全部无证据 → UNKNOWN +
      完整取证日志（burst 轮次 / 监听时长 / 收到的所有帧）。"""
    life_evidence = []
    forensics = []
    for attempt in range(1, PROBE_ROUNDS + 1):
        if stopTask:
            raise RuntimeError("用户停止脚本")
        _log("---- 探测运行侧 第 %d/%d 轮 ----" % (attempt, PROBE_ROUNDS))
        tag, detail = _probe_once(bus_id, SID_RDBI,
                                  [(DID_ACTIVE_SLOT >> 8) & 0xFF, DID_ACTIVE_SLOT & 0xFF])
        if tag == "boot_sm":
            return ("BOOT_SM", detail)
        if tag == "app":
            return ("APP", detail)
        if tag == "other":
            _log("22 2113 未识别应答（仅取证，不参与定位）: %s" % _hex(detail))
            forensics.append(("round%d 22 2113" % attempt, detail))
        tag, detail = _probe_once(bus_id, SID_RD, [0x00])
        if tag == "boot_sm":
            return ("BOOT_SM", detail)
        if tag == "app":
            return ("APP", detail)
        if tag == "other":
            _log("0x34 未识别应答（仅取证，不参与定位）: %s" % _hex(detail))
            forensics.append(("round%d 0x34" % attempt, detail))
        # 本轮两探皆无应答 → 唤醒收敛 + 生命周期监听（Standby 首帧只当 WUP）
        _log("两探无应答 → 唤醒 burst（3E 80 ×%d + %.1fs）×%d + 生命周期监听 %.1fs"
             % (WAKE_BURST_COUNT, WAKE_BURST_GAP_S, WAKE_BURST_ROUNDS, LIFE_LISTEN_S))
        found, all_frames = wake_bus(bus_id)
        forensics.append(("round%d listen %.1fs" % (attempt, LIFE_LISTEN_S), all_frames))
        for lt, dat in found:
            life_evidence.append((lt, dat))
            if lt == "abt":
                step = dat[5] if len(dat) > 5 else None
                desc = FAIL_STEP_DESC.get(step, "未知 fail_step") if step is not None else "无 step 字节"
                return ("BOOT_SM", "生命周期 ABT 心跳 %s = Boot safe mode，fail_step=%s（%s）"
                        % (_hex(dat[:8]), step, desc))
        if found:
            _log("生命周期帧证据 %d 个（%s）→ 重试 UDS 探测"
                 % (len(found), ",".join(lt for lt, _d in found)))
        else:
            _log("监听 %.1fs 未见 0x%08X 生命周期帧" % (LIFE_LISTEN_S, LIFE_ANNOUNCE_ID))
    # 全部轮次 UDS 探测无应答
    awk_or_bootup = [e for e in life_evidence if e[0] in ("awk", "bootup")]
    if awk_or_bootup:
        lt, dat = awk_or_bootup[0]
        return ("APP", "生命周期帧 [%s] %s 正证据：APP 已启动"
                "（UDS 探测无应答，链路/会话异常；后续 UDS 步骤若失败请检查链路）"
                % (lt.upper(), _hex(dat[:8])))
    _log("探测取证：共 %d 轮，每轮唤醒 burst（3E 80 ×%d + %.1fs）×%d + 监听 %.1fs；"
         "UDS 探测与生命周期帧均无证据"
         % (PROBE_ROUNDS, WAKE_BURST_COUNT, WAKE_BURST_GAP_S,
            WAKE_BURST_ROUNDS, LIFE_LISTEN_S))
    for tag, frames in forensics:
        if isinstance(frames, list):
            _log("取证[%s] 收帧 %d 个:" % (tag, len(frames)))
            for cid, dat in frames[:40]:
                _log("  0x%08X %s" % (cid, _hex(dat[:8])))
        else:
            _log("取证[%s] %s" % (tag, _hex(frames)))
    return ("UNKNOWN", "设备无应答（已排除 Standby），无法确定运行侧")


# ======== 主流程 ========


def _read_charge_state(bus_id):
    """读充电状态 DID 0x2102（RDBI 无会话/安全门禁，can_protocol.c:972-1007）。
    返回状态值 int；读失败抛异常由调用方处置。"""
    rx = read_did(bus_id, DID_CHARGE_STATE)
    return rx[0]


def run_charge_stop(bus_id):
    _log("======== 停止 Qi 充电 ========")
    _log("私钥: " + PRIVATE_KEY_PATH)

    if not os.path.isfile(PRIVATE_KEY_PATH):
        raise RuntimeError("找不到私钥: " + PRIVATE_KEY_PATH)
    if not os.path.isfile(PUBLIC_KEY_PATH):
        raise RuntimeError("找不到公钥（签名本地自检需要）: " + PUBLIC_KEY_PATH)
    priv = load_ec_private_key(PRIVATE_KEY_PATH)

    uds_init()

    # Step 0: 运行侧探测（仅 APP 可控充电；BOOT_SM/UNKNOWN 直接退出）
    _log("---- Step 0: 探测运行侧（唤醒收敛 + 正证据判定）----")
    verdict, detail = probe_location(bus_id)
    _log("[判定结论] 运行侧 %s（依据: %s）" % (VERDICT_CN[verdict], detail))
    if verdict == "BOOT_SM":
        _log("[判定结论] 充电停止未执行——设备在 Boot safe mode，充电控制仅 APP 可用")
        raise RuntimeError("设备在 Boot safe mode，停止充电需 APP——%s。"
                           "请先用 merge_prod_bin 烧录器重刷或确认 APP 槽有效后再试" % detail)
    if verdict == "UNKNOWN":
        _log("[判定结论] 充电停止未执行——设备无应答（已排除 Standby），无法确定运行侧")
        raise RuntimeError("设备无应答（已排除 Standby），无法确定运行侧。"
                           "取证见上方日志（burst 轮次 / 监听时长 / 收帧明细）")

    # Step 1: 进入扩展会话（0x2101 写门禁=SESSION_EXTENDED+SA，
    # can_protocol.c:1035-1045）
    _log("---- Step 1: 进入扩展会话 ----")
    uds_req(bus_id, SID_DSC, [0x03])   # 预期 50 03 00 32 01 F4
    _log("[PASS] Step 1 扩展会话已建立（10 03 → 50 03）")

    # Step 2: 安全解锁（seed 32 字节，d64e8c2 起；签名本地自检后才发 27 03）
    _log("---- Step 2: 安全解锁 ----")
    unlocked = False
    try:
        rx = uds_req(bus_id, SID_SA, [0x01])
        if len(rx) < 34:
            raise RuntimeError("seed 响应过短: %d 字节, 期望 67 01 + 32 字节 seed（≥34）" % len(rx))
        seed = _to_bytes(rx[2:34])
        _log("seed(32B) " + _hex(rx[2:34]))
        if seed == b"\x00" * 32:
            unlocked = True
            _log("已解锁 (seed=0，32 字节全 0)，跳过签名")
    except UdsNrcError as e:
        if e.nrc not in (NRC_EXCEEDED_ATTEMPTS, NRC_REQUIRED_TIME_DELAY):
            raise
        _log("27 01 NRC 0x%02X：设备 SecurityAccess 锁定（fail_count≥3，约30s），S3 keepalive 等待 %.0fs 后完整解锁" % (e.nrc, SA_LOCKOUT_WAIT_S))
        _s3_keepalive_wait(bus_id)
    if not unlocked:
        _log("SecurityAccess 解锁中（每次尝试完整重做：27 01 → 重签 → 自检 → 27 03 → 27 02）...")
        send_security_key(bus_id, priv)
    _log("[PASS] Step 2 安全解锁成功")

    # Step 3: 停止充电。先信息读当前状态（RDBI 无门禁），再写停止值。
    # 写步经 S3 兜底包装：NRC 0x22/0x33 → 重发 10 03 + 重解锁 + 重试一次。
    _log("---- Step 3: 停止充电（2E 21 01 00）----")
    pre_state = None
    try:
        pre_state = _read_charge_state(bus_id)
        _log("当前充电状态: 0x%02X (%s)" % (pre_state, charge_state_name(pre_state)))
        if pre_state != CHARGE_CHARGING:
            _log("提示：设备当前已处于非充电态（0x%02X %s），写 0x00 为幂等停止操作"
                 % (pre_state, charge_state_name(pre_state)))
    except Exception as e:
        _log("信息读当前充电状态失败（不阻断停止流程）: %s" % e)
    try:
        _wdbi_with_s3_guard(bus_id, [0x21, 0x01, 0x00], priv)
    except UdsNrcError as e:
        # 写步最终失败：按固件事实解读 NRC（can_protocol.c:1035-1045 / 1247-1253）
        if e.nrc == NRC_CONDITIONS_NOT_CORRECT:
            _log("[FAIL] Step 3 写停止值 NRC 0x22（含 S3 兜底重试后仍 0x22）："
                 "停止值 0x00 不受充电故障门禁拦截（固件门禁仅拦 val==0x01，"
                 "can_protocol.c:1247-1251）→ 0x22 指向会话门禁持续不满足"
                 "（S3 反复超时/会话被切），请检查总线负载或改用更短业务链复测")
        elif e.nrc == NRC_SECURITY_ACCESS_DENIED:
            _log("[FAIL] Step 3 写停止值 NRC 0x33（含 S3 兜底重试后仍 0x33）："
                 "安全态门禁（security_unlocked=0，can_protocol.c:1040-1042），"
                 "解锁态被持续清除——请断电重启后重跑，或检查是否有会话切换干扰")
        else:
            _log("[FAIL] Step 3 写停止值 NRC 0x%02X（SID=0x%02X）" % (e.nrc, e.sid))
        _log("[判定结论] 充电停止 FAIL（证据：2E 21 01 00 未获正响应 6E 21 01，NRC 0x%02X）" % e.nrc)
        raise RuntimeError("停止写失败: %s" % e)
    _log("[PASS] Step 3 停止写入成功：6E 21 01（g_qi_charger_enable=0，"
         "board_charge_set_enable(0) → PB2 拉低，can_protocol.c:1252-1253 / board_gpio.c:99-105）")

    # Step 4: 读回确认（22 21 02）——判定=状态非 CHARGING(0x04) → PASS
    _log("---- Step 4: 读回确认（22 21 02）----")
    post_state = None
    try:
        post_state = _read_charge_state(bus_id)
    except Exception as e:
        _log("[FAIL] Step 4 读回充电状态失败: %s" % e)
        _log("[判定结论] 充电停止 FAIL（证据：6E 21 01 写入成功但 22 21 02 读回失败）")
        raise RuntimeError("读回确认失败: %s" % e)
    _log("读回充电状态: 0x%02X (%s)" % (post_state, charge_state_name(post_state)))
    if post_state == CHARGE_CHARGING:
        _log("[FAIL] Step 4 读回仍为 CHARGING(0x04)：写入已正响应但充电状态未变——"
             "排查方向：1) 硬件使能链（PB2/CCU 通路）2) 负载/手机仍在触发充电判定 "
             "3) 固件 charge_poll 是否被其他路径重新拉高 PB2")
        _log("[判定结论] 充电停止 FAIL（证据：6E 21 01 写入成功，但 22 21 02 读回 0x04 CHARGING）")
        raise RuntimeError("停止后读回仍 CHARGING")
    _log("[PASS] Step 4 读回确认：状态已非 CHARGING")

    # Step 5: 结束判定
    evidence = "2E 21 01 00 → 6E 21 01；22 21 02 读回 0x%02X (%s)" % (
        post_state, charge_state_name(post_state))
    if pre_state is not None and pre_state != CHARGE_CHARGING:
        evidence += "；写前已非充电态（幂等停止）"
    _log("[判定结论] 充电停止 PASS（%s）" % evidence)


def z_main():
    global stopTask
    stopTask = False
    _log("======== Qi 充电停止工具 ========")
    buses = zcanpro.get_buses()
    if not buses:
        _log("请先打开 CAN 通道 250kbps 扩展帧")
        return
    try:
        run_charge_stop(buses[0]["busID"])
    except Exception as e:
        _log("停止充电失败: " + str(e))
    finally:
        try:
            zcanpro.uds_deinit()
        except Exception:
            pass
