# -*- coding: utf-8 -*-
"""
ZCANPRO 扩展脚本 — SA 锁定恢复测试（P1 测试项）

用户明确要求："写一个用于测试故意key错的脚本"——验证 d770e86 S3 keepalive
防护在 SA 锁定等待场景的实机效果。

流程：
  0. 运行侧探测（唤醒收敛 + 正证据判定；仅 APP 可做 SA 锁定测试）
  1. 进入编程会话 10 02（预期 50 02 00 32 01 F4；与 sn_write 一致，后续验证写 F18C 门禁）
  2. 故意错 key ×WRONG_KEY_ATTEMPTS（默认 3）：每次完整 27 01 → 假签名（全 0x00）
     → 27 03 ×16 帧 → 27 02；预期 NRC 0x35/0x35/0x36（第 3 次触发锁定）；
     fail_count 残留（复测前未断电重启）时序列前移（0x36/0x37 提前出现）
     → 打印残留指引（建议断电重启后重跑）并按设计继续后续步骤（2026-09-19）
  3. 锁定确认：27 01 → 预期 NRC 0x37（requiredTimeDelay，锁定期内）
  4. S3 keepalive 等待 31s（每 3s 发 3E 80 suppress，逐次打印序号+累计时间）
  5. 恢复解锁：真 private.pem → 完整 SA（27 01 → seed → ECDSA 真签 →
     [自检] public.pem 本地验签 → 27 03×16 → 27 02）→ 预期 67 02
  6. 验证（S3 防护实机验证点）：
     a. 解锁态确认：27 01 → 预期 67 01 + 32×0x00（已解锁）
     b. 幂等写验证：2E F1 8C + 相同 SN → 预期 6E F1 8C——写步 NRC 判读：
        0x22=疑似 S3 会话失效；0x33 且 Step 5 未解锁=下游失败（0x33≠0x22，
        会话检查已通过=S3 keepalive 有效证据）；0x33 且 Step 5 已解锁=异常
        （安全态被清，非会话失效）

固件事实（2026-09-18 evaluator/主管已验证，file:line）：
  - SA 无会话门禁（handle_security_access，can_protocol.c:1320+，仅长度/锁定/序检查）
  - 锁定机制：验签失败 fail_count++（:1465）；fail_count≥3（SECURITY_MAX_FAILURES=3
    :71）→ 锁定 30s（SECURITY_LOCKOUT_MS=30000U :70）+ 第 3 次失败当次 27 02 回
    NRC 0x36（:1472）；锁定期内 27 01 回 NRC 0x37（:1333-1341）；锁定期过→
    fail_count=0（:1340）
  - 验签失败同时清 g_seed_generated（:1469）→ 裸发 27 02/27 03→NRC 0x24
    （:1429-1433）——脚本每次尝试从 27 01 开局不受影响
  - 已解锁确认：27 01→67 01+32×0x00（:1343-1352）
  - 写门禁 0xF18C：SESSION_PROGRAMMING+security_unlocked，顺序=先会话检查
    （:1055-1057，不满足回 NRC 0x22）后安全检查（:1060-1062，回 0x33）；
    相同值重写=幂等无副作用。诊断判据：写步得 0x33 = 会话检查已通过 =
    keepalive 保住会话的正证据；仅 0x22 才提示疑似 S3 会话失效
  - 验签对象 = SHA-256(g_seed 32 字节)（:1445-1446 sha256_hash+uECC_verify）
  - 签名本地自检（2026-09-18 新增）：签名完成后、发送 27 03 前用
    docs/keys/public.pem 做 ECDSA 验签（独立仿射实现，验签对象=SHA-256(seed
    32B)，与固件一致），日志"[自检] 签名本地验证 PASS/FAIL"；FAIL 不发送、
    直接报错——mock/实机都能拦截签名数据差分类缺陷
  - 2026-09-18 修复记录（c81bb18 首测实机 Step 5 27 02→NRC 0x35）：根因 =
    EC 点运算实现损坏——_jp_add s2 误用 x1（正确式 y2*z1，对照
    zcanpro_sn_write.py 实机成功路径）+ _jp_mul 仿射转换 y 坐标误用 rx，
    标量乘结果错误 → 签名 r 值错误 → 与固件验签对象不一致；已对齐
    sn_write 实现 + 新增签名本地自检
  - 2026-09-19 修复记录（00:03 复测日志）：复测未按指引断电重启 →
    fail_count 残留（=1）→ NRC 序列前移（0x35/0x36/0x37 形态：第2次即
    0x36、第3次 27 01→7F 27 37 锁定期内）→ 异常续行路径把 str 传入
    "%02X" 格式化 → Step 2 后 TypeError 中断（c81bb18 原有缺陷，fcb0782
    未触及）；已改 _fmt_nrc 安全格式化（%X 点实参强制 int/占位符）+
    残留形态检测指引（检测到 0x36/0x37 提前出现即提示断电重启后重跑，
    按设计继续后续步骤：锁定确认/keepalive/恢复解锁对残留场景依然有效）
  - S3：SESSION_TIMEOUT_MS=5000（can_protocol.h:167）；任何诊断请求刷新计时
    （uds_process_message:1770 + handle_tester_present:1509，3E 80 suppress 同样
    刷新——d770e86 evaluator 已双点确证）；锁定等待 31s 若无 keepalive，固件 poll
    （:2094-2098）会复位会话+清安全态→恢复后写步撞 NRC 0x22/0x33
  - NRC 宏：0x35 UDS_NRC_INVALID_KEY（h:99）/0x36 UDS_NRC_EXCEEDED_NUMBER_OF_ATTEMPTS
    （h:100）/0x37 UDS_NRC_REQUIRED_TIME_DELAY（h:101）

安全性（docstring 承诺）：
  - 错 key 阶段假签名零数据（64 字节全 0x00），不消耗设备任何有效密钥/计数资源
    （fail_count 是测试目的本身，30s 后自动归零）
  - 写步仅重写相同 SN 值（幂等写，设备状态无变化）
  - 锁定态测试后自动清除：30s 到期 fail_count 归零 + 成功解锁双保险
  - 建议与其他测试错开执行（避免 fail_count 残留干扰其他 SA 流程）

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
PUBLIC_KEY_PATH  = os.path.join(REPO_ROOT, "docs", "keys", "public.pem")  # 签名本地自检用（2026-09-18）

# 要写入的 SN（与 zcanpro_sn_write.py 一致；幂等写验证用，相同值无副作用）
SN_CODE = "LSCH42JY012606020001"

WRONG_KEY_ATTEMPTS = 3   # 故意错 key 次数（= SECURITY_MAX_FAILURES，触发锁定）
SA_SIG_CHUNK = 4         # 27 03 分片：4 字节/帧

# ======== UDS 常量（d770e86 三功能脚本同款常量块）========
UDS_REQ_ID = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

SID_DSC  = 0x10   # DiagnosticSessionControl
SID_SA   = 0x27   # SecurityAccess
SID_WDBI = 0x2E   # WriteDataByIdentifier
SID_RDBI = 0x22   # ReadDataByIdentifier：运行侧探测 22 2113
SID_RD   = 0x34   # 运行侧探测：仅 APP 实现
SID_TP   = 0x3E   # TesterPresent：keepalive / wake_bus
SID_NRC  = 0x7F
SID_PR   = 0x40

NRC_RCRRP = 0x78
NRC_INVALID_KEY             = 0x35  # 验签失败（fail_count 未达上限）→ UDS_NRC_INVALID_KEY（h:99）
NRC_EXCEEDED_ATTEMPTS       = 0x36  # 验签失败达上限当次 → UDS_NRC_EXCEEDED_NUMBER_OF_ATTEMPTS（h:100）
NRC_REQUIRED_TIME_DELAY     = 0x37  # 锁定期内 27 01 → UDS_NRC_REQUIRED_TIME_DELAY（h:101）
NRC_CONDITIONS_NOT_CORRECT  = 0x22  # 写门禁：会话不满足（S3 超时回 default 的典型表现）
NRC_SECURITY_ACCESS_DENIED  = 0x33  # 写门禁：security_unlocked=0（S3 超时/会话切换被清）

# NRC 描述（判定结论/失败分析用）
NRC_DESC = {
    0x35: "invalidKey——验签失败，fail_count 计数（未达上限）",
    0x36: "exceededNumberOfAttempts——验签失败达上限（fail_count≥3），固件随即锁定约 30s",
    0x37: "requiredTimeDelay——锁定期内 27 01 应答，设备仍在锁定中",
    0x22: "conditionsNotCorrect——写门禁：会话不满足（S3 超时后会话回 default）",
    0x33: "securityAccessDenied——写门禁：security_unlocked=0（S3 清安全态或上游 SA 未解锁；0x33≠0x22=会话存活证据）",
    0x24: "requestSequenceError——固件已清 g_seed_generated，裸发 27 02/27 03 撞序检查",
}

# ======== S3 会话超时防护参数（三脚本同构）========
# 固件：SESSION_TIMEOUT_MS=5000（can_protocol.h:167）；UDS 交换间隙>5s 时
# can_protocol_poll 会把会话回 default + 清 security（:2094-2098）。
# 固件验证结论：3E 80（suppress）与 3E 00 均刷新 S3 计时——uds_process_message
# 派发前对任何诊断请求统一刷新 last_tester_present_tick（can_protocol.c:1770），
# handle_tester_present 对 suppress 帧同样刷新计时且不回响应（:1509）→
# keepalive 优先 3E 80（无响应帧干扰，ZCANPRO suppress 请求立即返回）。
S3_KEEPALIVE_INTERVAL_S = 3.0      # keepalive 周期：3s < 5s 超时窗，留 2s 余量
S3_SIGN_GAP_GUARD_S     = 3.0      # 签名耗时超过该值：先发 3E 再进 27 03 分片
SA_LOCKOUT_WAIT_S       = 31.0     # SA 锁定等待总时长（>30s 锁定期）

KEEPALIVE_GAPS = []    # keepalive 实际发送间隔取证（s），结束判定 S3 联合判定用


def _fmt_nrc(v):
    """NRC 值安全格式化（2026-09-19 %X 格式崩溃修复）：int → 0x%02X；
    非 int（异常占位/None）→ 字符串占位，绝不送入 %X 格式化点。"""
    if isinstance(v, int):
        return "0x%02X" % (v & 0xFF)
    if v is None:
        return "N/A"
    return str(v)


class UdsNrcError(RuntimeError):
    """带 NRC 码的 UDS 异常；SA 锁定测试按 e.nrc 判别预期/非预期 NRC。"""

    def __init__(self, sid, nrc):
        # 2026-09-19：%X 格式崩溃修复——sid/nrc 非 int 时不进 %X 格式化
        self.sid = sid if isinstance(sid, int) else None
        self.nrc = nrc if isinstance(nrc, int) else None
        RuntimeError.__init__(self, "NRC SID=%s NRC=%s"
                              % (_fmt_nrc(self.sid), _fmt_nrc(self.nrc)))


# secp256r1（与 zcanpro_sn_write.py 同款）
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
    # 2026-09-18 修复：S2 = Y2·Z1³（z1z1=Z1²）——c81bb18 误写 y2*x1，
    # 破坏雅可比加法 → 标量乘 x 错误 → 签名 r 错 → 实机 27 02 回 NRC 0x35；
    # 同步恢复 u1==u2（P=Q 倍增 / P=-Q 无穷远）分支，对齐 sn_write 成功路径
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
    # 2026-09-18 修复：仿射转换 Y = ry/Z³——c81bb18 误写 rx（签名路径 y 被
    # 丢弃属死计算，仍对齐 sn_write，防未来验签复用踩坑）
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


# ======== 宿主侧签名自检（2026-09-18 新增）========
# 背景：c81bb18 首测（2026-09-18 23:05 实机）Step 5 恢复解锁 27 02 回
# NRC 0x35 invalidKey。根因 = 本文件 EC 点运算损坏（_jp_add/_jp_mul，已
# 修复）→ 签名数据与固件验签对象（sha256_hash(g_seed,32U)+uECC_verify，
# can_protocol.c:1445-1446）不一致；mock 只断言"签名非全 0+分片完整"，
# 无密码学校验，拦截不了此类缺陷 → 签名后、发送 27 03 前宿主侧验签自检。
# 验签实现 = 独立仿射 EC 数学（不复用签名路径 _jp_* 雅可比代码），
# 自检不与签名共享失效模式；零新增依赖。

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
        # = 0x00 未用位 + 0x04||X||Y；与 _collect_octet32 同款按 int 比较
        # tag，ZCANPRO 生产解释器为 py3）
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
    # 2026-09-18：新增实际发送间隔取证（KEEPALIVE_GAPS），供结束判定 S3
    # 联合判定使用；等待流程逻辑不变
    """S3 会话超时防护：SA 锁定等待期间周期发送 3E 80 keepalive。

    固件验证（can_protocol.c）：uds_process_message 对任何诊断请求（含
    3E suppress 帧）在派发前刷新 last_tester_present_tick（:1770）；
    handle_tester_present 对 suppress 帧同样刷新计时且不回响应（:1509）
    → 3E 80 与 3E 00 同样续期 S3，优先 3E 80（无响应帧干扰，库立即返回）。
    周期 3s < SESSION_TIMEOUT_MS 5s（can_protocol.h:167）；总等待时长 >30s
    锁定期。走既有 uds_try 通道，不碰探测/raw 路径；keepalive 失败只记录，
    不中断等待。

    本测试脚本增强：逐次打印 keepalive 序号+累计时间（核心观察点，
    用户日志可直观看到约 11 次发送）。
    """
    t_start = time.time()
    t_end = t_start + float(total_s)
    t_prev = t_start
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
        t_now = time.time()
        KEEPALIVE_GAPS.append(t_now - t_prev)  # 实际发送间隔取证（仅测量，不改流程）
        t_prev = t_now
        elapsed = t_now - t_start
        _log("  keepalive #%d（累计 %.1fs）3E 80 已发送" % (sent, elapsed))
    total_elapsed = time.time() - t_start
    _log("S3 keepalive 等待结束：%.1fs 内共发 %d 次 3E 80" % (total_elapsed, sent))
    if KEEPALIVE_GAPS:
        _log("S3 keepalive 实际发送间隔：最大 %.1fs（S3 超时窗 5s，须 <5s）"
             % max(KEEPALIVE_GAPS))
    return sent


# ======== SA 辅助函数 ========

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
        raise RuntimeError("ECDSA 签名须 64 字节，实际 %d" % len(sig))
    seq = 1
    off = 0
    while off < 64:
        piece = sig[off:off + SA_SIG_CHUNK]
        uds_req(bus_id, SID_SA, [0x03, seq] + _to_list(piece))
        off += len(piece)
        seq += 1
    _log("  27 03 已送 64 字节 / %d 帧" % (seq - 1))
    time.sleep(0.15)


def _sa_wrong_key_attempt(bus_id, attempt_num):
    """故意错 key 一次完整流程：27 01 → 假签名（全 0x00）→ 27 03×16 → 27 02。

    返回 (expected_nrc, actual_nrc, seed_hex)：
      expected_nrc = 预期 NRC（0x35/0x35/0x36），actual_nrc = 实际 NRC 或 None。
    实际 NRC 与预期不符时由调用方日志警告但继续。
    """
    # 预期 NRC 序列：第 1、2 次 0x35（invalidKey，fail_count 计数），
    # 第 WRONG_KEY_ATTEMPTS 次 0x36（exceededNumberOfAttempts，触发锁定）
    if attempt_num < WRONG_KEY_ATTEMPTS:
        expected_nrc = NRC_INVALID_KEY
    else:
        expected_nrc = NRC_EXCEEDED_ATTEMPTS

    _log("  [错 key 第 %d/%d 次] 27 01 取 seed..." % (attempt_num, WRONG_KEY_ATTEMPTS))
    rx = _sa_fetch_seed(bus_id)
    seed_hex = _hex(rx[2:34])
    _log("  seed(32B) " + seed_hex)

    # 假签名 = 64 字节全 0x00（日志明示）
    fake_sig = b"\x00" * 64
    _log("  故意错误签名（全 0x00）→ 27 03 分片发送")
    _sa_send_sig(bus_id, fake_sig)

    _log("  27 02 验签（假签名）...")
    try:
        rx2 = uds_req(bus_id, SID_SA, [0x02], wait_pending_s=5)
        # 不应到达这里——全 0x00 签名不应验签成功
        _log("  警告：假签名 27 02 返回正响应 %s（固件验签逻辑异常？）" % _hex(rx2[:4]))
        return (expected_nrc, None, seed_hex)
    except UdsNrcError as e:
        return (expected_nrc, e.nrc, seed_hex)


def _sa_real_unlock(bus_id, priv):
    """真 SA 解锁（单次完整流程）：27 01 → seed → ECDSA 真签 → 27 03×16 → 27 02。

    返回 rx（27 02 应答，预期 67 02）。
    已解锁时 27 01 返回 67 01+32×0x00，直接返回正响应。
    """
    _log("  27 01 取 seed...")
    rx = _sa_fetch_seed(bus_id)
    seed = _to_bytes(rx[2:34])
    if seed == b"\x00" * 32:
        _log("  seed=0（32 字节全 0），已解锁，跳过签名")
        return rx
    _log("  seed(32B) " + _hex(rx[2:34]))

    # S3 防护：签名+自检前后计时，距上次 UDS 交换 >3s 则先发 3E 80
    t_sign = time.time()
    sig = ecdsa_sign_msg(priv, seed)
    # 宿主侧密码学自检（2026-09-18）：签名完成后、发送 27 03 前执行；
    # FAIL 不发送、直接报错——mock/实机都能拦截签名数据差分类缺陷
    if not _sig_self_check(sig, seed):
        raise RuntimeError(
            "[自检] 签名本地验证 FAIL：本地 ECDSA 签名无法通过 public.pem "
            "验签（签名数据差分），已中止发送 27 03——请检查 ECDSA 实现与"
            "公钥文件 " + PUBLIC_KEY_PATH)
    sign_gap = time.time() - t_sign
    if sign_gap > S3_SIGN_GAP_GUARD_S:
        _log("  ECDSA 签名+自检耗时 %.1fs（>%.1fs）：先发 3E 80 刷新 S3 计时"
             % (sign_gap, S3_SIGN_GAP_GUARD_S))
        uds_try(bus_id, SID_TP, [0x80], suppress=1)

    _log("  ECDSA 真签名完成（自检 PASS），27 03 分片发送...")
    _sa_send_sig(bus_id, sig)

    _log("  27 02 验签（真签名）...")
    rx2 = uds_req(bus_id, SID_SA, [0x02], wait_pending_s=5)
    return rx2


# ======== 运行侧探测 / Standby 唤醒收敛（判定只按正证据）========
# 与 zcanpro_charge_start.py / zcanpro_sn_write.py probe_location 同构
# （固件依据文件:行号见脚本头部与提交报告）：绝不按 NRC 值分叉定位——
# 0x34/22 2113 的任何应答（正响应或任意 NRC，且非 0xFE 标记）= 在 APP；
# 22 2113 应答中 62 21 13 后字节为 0xFE = Boot safe mode（APP 的
# DID 0x2113 也正响应，slot 字节单 App 架构恒 0x00，永不 0xFE）；无应答 ≠ 不在 APP，
# 先 Standby 唤醒收敛（3E 80 burst）+ 释放 UDS 通道监听 0x18FF260D
# 生命周期帧再判。

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
    ABT（01 41 42 54）只有 Boot safe mode 发；AWK/BOOTUP 只有 APP 发。"""
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
        return ("app", "NRC 0x%02X（对 %02X 的应答 = APP 实现该服务）" % (data[2], sid))
    if data[0] == (sid + SID_PR):
        if sid == SID_RDBI:
            return ("app", "22 2113 正响应 slot=%s（APP DID_ACTIVE_SLOT，记录字节非 0xFE）"
                    % _hex(data[3:4]))
        return ("app", "0x34 正响应 %s（APP 实现下载服务）" % _hex(data[:8]))
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


# ======== SA 锁定恢复测试主流程 ========

def run_sa_lockout_test(bus_id):
    """SA 锁定恢复测试主流程（Step 0~6 + 结束判定）。"""
    _log("======== SA 锁定恢复测试 ========")
    _log("测试目的：故意错 key 触发 SA 锁定 → S3 keepalive 等待 → 恢复解锁 → "
         "验证会话+安全态在 31s 锁定等待期间是否被 keepalive 保住")
    _log("错 key 次数: %d（= SECURITY_MAX_FAILURES，触发锁定）" % WRONG_KEY_ATTEMPTS)
    _log("私钥: " + PRIVATE_KEY_PATH)

    if not os.path.isfile(PRIVATE_KEY_PATH):
        raise RuntimeError("找不到私钥: " + PRIVATE_KEY_PATH)
    priv = load_ec_private_key(PRIVATE_KEY_PATH)

    uds_init()

    # 测试结果收集（每步 pass/fail + 详情）
    results = []  # [(step_name, passed, expected, actual, note)]
    del KEEPALIVE_GAPS[:]  # 间隔取证清零（模块级复用安全）

    def _record(step, passed, expected, actual, note=""):
        results.append((step, passed, expected, actual, note))
        status = "PASS" if passed else "FAIL"
        _log("  [%s] %s | 预期=%s 实际=%s%s"
             % (status, step, expected, actual, (" | " + note) if note else ""))

    # ---- Step 0: 探测运行侧 ----
    _log("---- Step 0: 探测运行侧（唤醒收敛 + 正证据判定）----")
    _log("固件侧解读：SA 锁定测试需正常 APP（Boot safe mode 不实现完整 SA 流程）")
    verdict, detail = probe_location(bus_id)
    _log("[判定结论] %s（依据: %s）" % (VERDICT_CN[verdict], detail))
    if verdict == "BOOT_SM":
        raise RuntimeError("设备在 Boot safe mode，SA 锁定测试需 APP——%s。"
                           "请先用 merge_prod_bin 烧录器重刷或确认 APP 槽有效后再试" % detail)
    if verdict == "UNKNOWN":
        raise RuntimeError("设备无应答（已排除 Standby），无法确定运行侧。"
                           "取证见上方日志（burst 轮次 / 监听时长 / 收帧明细）")
    _record("Step0-探测", True, "APP", verdict, detail[:60])

    # ---- Step 1: 进入编程会话 ----
    _log("---- Step 1: 进入编程会话 10 02 ----")
    _log("固件侧解读：与 zcanpro_sn_write.py 一致（后续 Step 6b 验证写 F18C 需 "
         "SESSION_PROGRAMMING 门禁，can_protocol.c:1051-1062）")
    try:
        rx = uds_req(bus_id, SID_DSC, [0x02])
        dsc_ok = (len(rx) >= 2 and rx[0] == 0x50 and rx[1] == 0x02)
        _record("Step1-10 02", dsc_ok, "50 02 00 32 01 F4", _hex(rx[:6]),
                "" if dsc_ok else "会话切换应答异常")
    except (UdsNrcError, RuntimeError) as e:
        _record("Step1-10 02", False, "50 02", str(e), "会话切换失败，后续步骤门禁将不满足")
        _log("[判定结论] SA 锁定恢复测试 FAIL（Step 1 会话切换失败）")
        return False

    # ---- Step 2: 故意错 key ×WRONG_KEY_ATTEMPTS ----
    _log("---- Step 2: 故意错 key ×%d（假签名=全 0x00）----" % WRONG_KEY_ATTEMPTS)
    _log("固件侧解读：验签失败 fail_count++（can_protocol.c:1465）；fail_count≥3"
         "（SECURITY_MAX_FAILURES=3 :71）→ 锁定 30s + 第 3 次 27 02 回 NRC 0x36（:1472）；"
         "前两次 fail_count<3 → NRC 0x35 invalidKey")
    expected_nrc_seq = []
    actual_nrc_seq = []
    residual_failcount = False  # fail_count 残留形态（2026-09-19，00:03 复测日志）
    for i in range(1, WRONG_KEY_ATTEMPTS + 1):
        try:
            exp_nrc, act_nrc, _seed_h = _sa_wrong_key_attempt(bus_id, i)
        except (UdsNrcError, RuntimeError) as e:
            e_nrc = getattr(e, "nrc", None)
            if isinstance(e_nrc, int):
                # NRC 异常（如锁定期内 27 01→0x37）：保留结构化 int，供
                # 残留检测与汇总打印（2026-09-19 %X 格式崩溃修复核心）
                act_nrc = e_nrc
                _log("  警告：错 key 第 %d 次 NRC 异常: %s（继续后续步骤）" % (i, e))
            else:
                # 非 NRC 异常（无应答/超时等）：占位符 "N/A"，绝不进 %X 格式化
                act_nrc = "N/A"
                _log("  警告：错 key 第 %d 次异常: %s（继续后续步骤）" % (i, e))
            exp_nrc = NRC_INVALID_KEY if i < WRONG_KEY_ATTEMPTS else NRC_EXCEEDED_ATTEMPTS
        expected_nrc_seq.append(exp_nrc)
        actual_nrc_seq.append(act_nrc)
        # fail_count 残留形态检测：锁定类 NRC 提前出现 = 复测前未断电重启，
        # fail_count RAM 残留导致序列前移（00:03 复测实证：残留=1 →
        # 第 2 次即 0x36、第 3 次 27 01→0x37 锁定期内）；检测到即打印
        # 明确指引，后续步骤按设计继续（锁定确认/keepalive/恢复解锁对
        # 残留场景依然有效）
        if isinstance(act_nrc, int) and act_nrc in (
                NRC_EXCEEDED_ATTEMPTS, NRC_REQUIRED_TIME_DELAY) \
                and (i < WRONG_KEY_ATTEMPTS
                     or act_nrc == NRC_REQUIRED_TIME_DELAY):
            if not residual_failcount:
                residual_failcount = True
                _log("  ⚠ 检测到 fail_count 残留（复测前未断电重启）：设备已进入锁定"
                     "（第 %d/%d 次即见 %s，标准序列应第 %d 次 0x36 才触发锁定）——"
                     "建议断电重启后重跑以获得标准序列"
                     % (i, WRONG_KEY_ATTEMPTS, _fmt_nrc(act_nrc), WRONG_KEY_ATTEMPTS))
                _log("  脚本按设计继续后续步骤：Step 3 锁定确认与 Step 4 keepalive "
                     "对残留场景依然有效，Step 5 恢复解锁在锁定期过后仍可验证 "
                     "EC 修复（锁定恢复测试核心验证对象不受残留影响）")
        if act_nrc is None:
            _log("  警告：第 %d 次假签名收到正响应（预期 %s），固件验签逻辑异常？"
                 % (i, _fmt_nrc(exp_nrc)))
        elif isinstance(act_nrc, int) and act_nrc != exp_nrc:
            _log("  警告：第 %d 次 NRC 不符——预期 %s（%s），实际 %s（%s）"
                 % (i, _fmt_nrc(exp_nrc), NRC_DESC.get(exp_nrc, ""),
                    _fmt_nrc(act_nrc), NRC_DESC.get(act_nrc, "未知")))
        elif isinstance(act_nrc, int):
            _log("  第 %d 次 NRC %s 符合预期（%s）"
                 % (i, _fmt_nrc(act_nrc), NRC_DESC.get(act_nrc, "")))
        else:
            # 非 int 占位（防御分支）：%X 格式化点不再接收非 int 实参
            _log("  第 %d 次无 NRC 码（%s），跳过 NRC 比对" % (i, act_nrc))

    exp_str = "/".join(_fmt_nrc(n) for n in expected_nrc_seq)
    act_str = "/".join(_fmt_nrc(n) for n in actual_nrc_seq)
    wrong_key_ok = all(
        isinstance(a, int) and a == e
        for a, e in zip(actual_nrc_seq, expected_nrc_seq))
    if wrong_key_ok:
        step2_note = ""
    elif residual_failcount:
        step2_note = ("fail_count 残留形态（复测前未断电重启）：锁定类 NRC 提前出现，"
                      "非固件逻辑差异——建议断电重启后重跑以获得标准序列；"
                      "后续步骤按设计继续（锁定确认/keepalive/恢复解锁对残留有效）")
    else:
        step2_note = "实际 NRC 与预期不符（日志警告，继续后续步骤）"
    _record("Step2-错key×%d" % WRONG_KEY_ATTEMPTS, wrong_key_ok,
            exp_str, act_str, step2_note)

    # ---- Step 3: 锁定确认 ----
    _log("---- Step 3: 锁定确认（27 01 → 预期 NRC 0x37）----")
    _log("固件侧解读：锁定期内 27 01 回 NRC 0x37 requiredTimeDelay"
         "（can_protocol.c:1333-1341）；设备已进入锁定（fail_count≥3，约 30s）")
    try:
        rx = uds_req(bus_id, SID_SA, [0x01])
        _log("  警告：锁定期内 27 01 返回正响应 %s（预期 NRC 0x37）" % _hex(rx[:4]))
        _record("Step3-锁定确认", False, "NRC 0x37", "正响应 " + _hex(rx[:4]),
                "设备未按预期进入锁定？")
    except UdsNrcError as e:
        if e.nrc == NRC_REQUIRED_TIME_DELAY:
            _log("  设备已进入锁定（fail_count≥3，约 30s）——NRC 0x37 requiredTimeDelay ✓")
            _record("Step3-锁定确认", True, "NRC 0x37", "NRC 0x37",
                    "设备 SecurityAccess 锁定生效")
        else:
            e_nrc = getattr(e, "nrc", None)
            _log("  警告：27 01 NRC %s（预期 0x37），设备锁定状态异常"
                 % _fmt_nrc(e_nrc))
            _record("Step3-锁定确认", False, "NRC 0x37", "NRC " + _fmt_nrc(e_nrc),
                    NRC_DESC.get(e_nrc, "未知 NRC"))
    except RuntimeError as e:
        _log("  警告：27 01 异常 %s（预期 NRC 0x37）" % e)
        _record("Step3-锁定确认", False, "NRC 0x37", str(e), "无应答/通信异常")

    # ---- Step 4: S3 keepalive 等待 ----
    _log("---- Step 4: S3 keepalive 等待 %.0fs（核心观察点）----" % SA_LOCKOUT_WAIT_S)
    _log("固件侧解读：S3=5s（can_protocol.h:167），任何诊断请求刷新计时（:1770+:1509）；"
         "锁定等待 31s 若无 keepalive，固件 poll（:2094-2098）会复位会话+清安全态→"
         "恢复后写步撞 NRC 0x22/0x33——keepalive 正是本测试的核心验证对象")
    keepalive_count = _s3_keepalive_wait(bus_id)
    _log("keepalive 发送 %d 次 / %.0fs" % (keepalive_count, SA_LOCKOUT_WAIT_S))
    keepalive_ok = keepalive_count >= 9  # 31s/3s ≈ 10.3，至少 9 次为合理下限
    ka_max_gap = max(KEEPALIVE_GAPS) if KEEPALIVE_GAPS else None
    ka_interval_ok = (ka_max_gap is None) or (ka_max_gap < 5.0)
    ka_note = ""
    if ka_max_gap is not None:
        ka_note = "实际发送最大间隔 %.1fs（S3 窗 5s，%s）" % (
            ka_max_gap, "<5s 达标" if ka_interval_ok else "≥5s 不达标")
    _record("Step4-keepalive", keepalive_ok,
            "≥9 次（31s/3s≈10-11）", "%d 次" % keepalive_count,
            ka_note if keepalive_ok else
            ("keepalive 次数偏少，S3 防护可能不充分" +
             (("；" + ka_note) if ka_note else "")))

    # ---- Step 5: 恢复解锁（真 private.pem → 完整 SA） ----
    _log("---- Step 5: 恢复解锁（真 private.pem → 完整 SA）----")
    _log("固件侧解读：锁定期过→fail_count=0（can_protocol.c:1340）；"
         "真签名 ECDSA 验签通过 → security_unlocked=1（:1454）；签名发送前"
         "先经 [自检] public.pem 本地验签（2026-09-18）")
    step5_unlock_ok = False
    try:
        rx = _sa_real_unlock(bus_id, priv)
        if len(rx) >= 2 and rx[0] == 0x67 and rx[1] == 0x02:
            step5_unlock_ok = True
            _log("  解锁成功：67 02 ✓")
            _record("Step5-恢复解锁", True, "67 02", "67 02",
                    "ECDSA 真签验签通过（宿主自检 PASS + 固件验签通过）")
        elif len(rx) >= 34 and rx[0] == 0x67 and rx[1] == 0x01 and _to_bytes(rx[2:34]) == b"\x00" * 32:
            step5_unlock_ok = True
            _log("  已解锁（seed 全 0），无需签名")
            _record("Step5-恢复解锁", True, "67 02", "67 01+32×00", "已解锁状态")
        else:
            _log("  恢复解锁异常：应答 %s" % _hex(rx[:6]))
            _record("Step5-恢复解锁", False, "67 02", _hex(rx[:6]),
                    "应答格式不符（设备可能仍在锁定/会话复位）")
    except UdsNrcError as e:
        e_nrc = getattr(e, "nrc", None)
        _log("  恢复解锁失败：NRC %s（%s）" % (_fmt_nrc(e_nrc), NRC_DESC.get(e_nrc, "未知")))
        hint = ""
        if e.nrc == NRC_INVALID_KEY:
            hint = ("验签失败（invalidKey）：签名已经过宿主自检 PASS（发送前本地"
                    "public.pem 验签通过）——签名数据无差分，疑设备端公钥与 "
                    "private.pem 不配对或设备验签对象不同；若自检 FAIL 脚本已"
                    "拦截发送，不会到达此处")
        elif e.nrc in (NRC_EXCEEDED_ATTEMPTS, NRC_REQUIRED_TIME_DELAY):
            hint = "设备可能仍在锁定中——建议断电重启后重跑"
        elif e.nrc == 0x24:
            hint = "g_seed_generated 已被清（裸发 27 02 撞序检查），流程异常"
        _record("Step5-恢复解锁", False, "67 02", "NRC " + _fmt_nrc(e_nrc), hint)
    except RuntimeError as e:
        _log("  恢复解锁失败: %s" % e)
        if "[自检]" in str(e):
            _record("Step5-恢复解锁", False, "67 02", "自检 FAIL",
                    "签名本地验签失败（签名数据差分），脚本已拦截发送 27 03——"
                    "检查 ECDSA 实现/公钥文件，设备侧未受本次失败影响")
        else:
            _record("Step5-恢复解锁", False, "67 02", str(e),
                    "设备可能仍在锁定/会话复位，建议断电重启后重跑")

    # ---- Step 6a: 解锁态确认 ----
    _log("---- Step 6a: 解锁态确认（27 01 → 预期 67 01 + 32×0x00）----")
    _log("固件侧解读：已解锁时 27 01 返回 67 01 + 32×0x00（can_protocol.c:1343-1352）")
    try:
        rx = uds_req(bus_id, SID_SA, [0x01])
        if len(rx) >= 34 and rx[0] == 0x67 and rx[1] == 0x01:
            seed_check = _to_bytes(rx[2:34])
            if seed_check == b"\x00" * 32:
                _log("  已解锁确认：67 01 + 32×0x00 ✓")
                _record("Step6a-解锁确认", True, "67 01+32×00", "67 01+32×00",
                        "security_unlocked=1 确认")
            else:
                _log("  27 01 返回 67 01 但 seed 非全 0（设备未解锁？）")
                _record("Step6a-解锁确认", False, "67 01+32×00", "67 01+seed非全0",
                        "设备 security_unlocked=0")
        else:
            _log("  27 01 应答异常: %s" % _hex(rx[:6]))
            _record("Step6a-解锁确认", False, "67 01+32×00", _hex(rx[:6]),
                    "应答格式不符")
    except UdsNrcError as e:
        e_nrc = getattr(e, "nrc", None)
        _log("  27 01 NRC %s（%s）" % (_fmt_nrc(e_nrc), NRC_DESC.get(e_nrc, "未知")))
        _record("Step6a-解锁确认", False, "67 01+32×00", "NRC " + _fmt_nrc(e_nrc),
                NRC_DESC.get(e_nrc, "未知"))
    except RuntimeError as e:
        _log("  27 01 异常: %s" % e)
        _record("Step6a-解锁确认", False, "67 01+32×00", str(e), "通信异常")

    # ---- Step 6b: 幂等写验证（S3 防护实机验证点） ----
    _log("---- Step 6b: 幂等写验证（2E F1 8C + 相同 SN → 预期 6E F1 8C）----")
    _log("固件侧解读：写门禁 0xF18C = SESSION_PROGRAMMING + security_unlocked"
         "（can_protocol.c:1051-1062）；相同值重写=幂等无副作用；"
         "S3 防护失效时此处必撞 NRC 0x22（会话回 default）/ 0x33（安全态被清）")
    if sys.version_info[0] >= 3:
        sn_bytes = SN_CODE.encode("ascii")
    else:
        sn_bytes = str(SN_CODE)
    sn32 = _to_list(sn_bytes) + [0x20] * (32 - len(sn_bytes))
    _log("  写入数据（相同 SN，幂等）: " + _hex(sn32))
    step6b_ok = False
    step6b_nrc = None

    try:
        rx = uds_req(bus_id, SID_WDBI, [0xF1, 0x8C] + sn32, wait_pending_s=10)
        if len(rx) >= 3 and rx[0] == 0x6E and rx[1] == 0xF1 and rx[2] == 0x8C:
            step6b_ok = True
            _log("  写入成功：6E F1 8C ✓——S3 keepalive 防护有效！"
                 "31s 锁定等待期间会话+安全态保持")
            _record("Step6b-幂等写", True, "6E F1 8C", "6E F1 8C",
                    "S3 keepalive 防护实机验证通过：会话+安全态在 31s 等待期间保持")
        else:
            _log("  写入应答异常: %s" % _hex(rx[:6]))
            _record("Step6b-幂等写", False, "6E F1 8C", _hex(rx[:6]),
                    "应答格式不符")
    except UdsNrcError as e:
        e_nrc = getattr(e, "nrc", None)
        step6b_nrc = e.nrc
        _log("  写入失败：NRC %s（%s）" % (_fmt_nrc(e_nrc), NRC_DESC.get(e_nrc, "未知")))
        if e.nrc == NRC_CONDITIONS_NOT_CORRECT:
            hint = ("NRC 0x22 conditionsNotCorrect——疑似 S3 会话失效（S3 防护"
                    "失效判据）：31s 等待期间无 keepalive（或间隔>5s），固件 poll"
                    "（:2094-2098）已将会话回 default → 写门禁 SESSION_PROGRAMMING"
                    " 不满足（can_protocol.c:1055 先查会话回 0x22）")
        elif e.nrc == NRC_SECURITY_ACCESS_DENIED:
            if not step5_unlock_ok:
                hint = ("NRC 0x33 securityAccessDenied——下游失败（Step 5 未解锁"
                        "所致），非 S3 失效：固件写门禁先查会话（:1055-1057，"
                        "不满足回 0x22）后查安全（:1060-1062，回 0x33）——拿到"
                        " 0x33 = 会话检查已通过 = 31s 等待后会话仍为 Programming"
                        "，S3 keepalive 防护有效；安全态缺失来源=Step 5 验签未"
                        "解锁，处置=先排查 Step 5（0x33≠0x22）")
            else:
                hint = ("NRC 0x33 securityAccessDenied——异常：Step 5 已解锁但"
                        "写步安全门禁失败（会话检查已过、非 S3 会话失效，"
                        "0x33≠0x22），疑安全态在写步前被其他因素清零，建议"
                        "断电重启后单独复测 SA+写步")
        else:
            hint = NRC_DESC.get(e.nrc, "未知 NRC")
        _record("Step6b-幂等写", False, "6E F1 8C", "NRC " + _fmt_nrc(e_nrc), hint)
    except RuntimeError as e:
        _log("  写入失败: %s" % e)
        _record("Step6b-幂等写", False, "6E F1 8C", str(e), "通信异常")

    # ---- 结束判定 ----
    _log("======== 测试结束判定 ========")

    # ---- S3 keepalive 联合判定（2026-09-18：Step 4 达标 + Step 6b 写步证据）----
    # 判据：0xF18C 写门禁先查会话（can_protocol.c:1055-1057 回 0x22）后查
    # 安全（:1060-1062 回 0x33）→ 写步非 0x22 = 会话检查通过 = keepalive
    # 保住会话的正证据（含 0x33——Step 5 未解锁的下游失败不否定 S3 防护）
    step6b_evidence_ok = step6b_ok or (
        step6b_nrc is not None and step6b_nrc != NRC_CONDITIONS_NOT_CORRECT)
    if keepalive_ok and ka_interval_ok and step6b_evidence_ok:
        _log("[正面判定] S3 keepalive 防护实机有效：Step 4 keepalive %d 次%s "
             "达标（<5s）+ 后续写步未见 NRC 0x22（固件写门禁先查会话已通过）"
             "——31s 锁定等待期间会话存活" % (
                 keepalive_count,
                 ("/最大间隔 %.1fs" % ka_max_gap) if ka_max_gap is not None else ""))
    elif keepalive_ok and step6b_nrc == NRC_CONDITIONS_NOT_CORRECT:
        _log("[负面判定] Step 6b 得 NRC 0x22（疑似 S3 会话失效）与 Step 4 "
             "keepalive 次数/间隔达标矛盾——请核查 keepalive 实际生效性"
             "（发送时刻/间隔）与设备 S3 poll 状态")

    all_pass = all(r[1] for r in results)
    fail_items = [r for r in results if not r[1]]

    if all_pass:
        _log("[判定结论] SA 锁定恢复测试 PASS（keepalive %d 次/%.0fs，会话保持有效，写步通过）"
             % (keepalive_count, SA_LOCKOUT_WAIT_S))
        _log("验证结论：S3 keepalive 防护在 SA 锁定等待场景实机有效——"
             "31s 等待期间会话+安全态未被复位，恢复解锁后幂等写通过")
    else:
        _log("[判定结论] SA 锁定恢复测试 FAIL（%d/%d 步未命中预期）"
             % (len(fail_items), len(results)))
        _log("---- 逐项差异明细 ----")
        for step, _p, expected, actual, note in fail_items:
            _log("  ✗ %s: 预期=%s 实际=%s%s"
                 % (step, expected, actual, (" | " + note) if note else ""))
        _log("---- 固件侧解释 ----")
        for step, _p, _e, _a, note in fail_items:
            if note:
                _log("  %s: %s" % (step, note))
        _log("---- 处置建议 ----")
        if any("Step5" in r[0] for r in fail_items):
            _log("  恢复解锁失败→签名已过宿主自检时疑设备公钥与 private.pem "
                 "不配对；自检 FAIL 时为签名数据差分（脚本已拦截发送，设备侧"
                 "未受影响）；设备可能仍在锁定/会话复位时建议断电重启后重跑")
        if any("Step6b" in r[0] for r in fail_items):
            step6b_fail_actuals = [str(r[3]) for r in fail_items if "Step6b" in r[0]]
            if any("NRC 0x33" in a for a in step6b_fail_actuals) and any(
                    "Step5" in r[0] for r in fail_items):
                _log("  写步 NRC 0x33 = 下游失败（Step 5 未解锁所致），非 S3 "
                     "失效：0x33≠0x22——固件写门禁先查会话（:1055-1057 回 "
                     "0x22）后查安全（:1060-1062 回 0x33），拿到 0x33 即会话"
                     "检查通过 = S3 keepalive 有效证据；处置=排查 Step 5")
            elif any("NRC 0x22" in a for a in step6b_fail_actuals):
                _log("  写步 NRC 0x22 = 疑似 S3 会话失效——检查 keepalive 日志"
                     "（Step 4）确认发送次数与间隔")
            else:
                _log("  写步失败→按上方固件侧解释处置（0x33+Step 5 未解锁=下游"
                     "失败；0x22=疑似 S3 会话失效）")
        if any("Step2" in r[0] for r in fail_items):
            if residual_failcount:
                _log("  Step2 序列不符 = fail_count 残留形态（复测前未断电重启），"
                     "非固件 SA 锁定逻辑差异——断电重启清零 fail_count 后重跑"
                     "可获得标准 0x35/0x35/0x36 序列")
            else:
                _log("  错 key NRC 不符→固件 SA 锁定逻辑可能与预期不同，"
                     "请核对 can_protocol.c 锁定分支（:1333-1341/:1465-1472）")
        _log("  建议与其他测试错开执行（避免 fail_count 残留干扰其他 SA 流程）")

    return all_pass


# ======== 入口 ========

def z_main():
    global stopTask
    stopTask = False
    _log("======== Qi Charger SA 锁定恢复测试工具 ========")
    _log("测试项：P1 — SA 锁定恢复（d770e86 S3 keepalive 防护实机验证）")
    _log("错 key 次数: %d | 锁定等待: %.0fs | keepalive 间隔: %.0fs"
         % (WRONG_KEY_ATTEMPTS, SA_LOCKOUT_WAIT_S, S3_KEEPALIVE_INTERVAL_S))
    buses = zcanpro.get_buses()
    if not buses:
        _log("请先打开 CAN 通道 250kbps 扩展帧")
        return
    try:
        ok = run_sa_lockout_test(buses[0]["busID"])
        if not ok:
            _log("SA 锁定恢复测试未通过（详见上方判定结论）")
    except Exception as e:
        _log("SA 锁定恢复测试异常: " + str(e))
    finally:
        try:
            zcanpro.uds_deinit()
        except Exception:
            pass
