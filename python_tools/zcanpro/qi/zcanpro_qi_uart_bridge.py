# -*- coding: utf-8 -*-
"""
ZCANPRO 脚本 — Qi UART 抓取/透传桥（上位机 ↔ AT32 ↔ Qi 芯片）

经 CAN-UDS 读写两个新 DID，打通「上位机 ↔ AT32 ↔ Qi 芯片」串口链路：
  DID 0x2140（读 0x22，任意会话）Qi UART 抓取读取：
    正响应 62 21 40 [flags u8][len u8][len 字节...]
    flags bit0 = 自上次读以来发生过溢出丢弃（256B 环形缓冲满，丢新保旧）
    len 单次 ≤240；缓冲多于此分次续读（siphon，只清已返回部分）；空回 [00][00]
  DID 0x2141（写 0x2E，10 02 编程会话 + 安全解锁）Qi UART 透传发送：
    payload 1~64B 原样逐字节经 UART 发给 Qi 芯片 → 6E 21 41
    门禁同 0x2130/0x2131；len<1 或 >64 → 7F 2E 13；Qi IAP 进行中 → 7F 2E 22

模式：
  --listen          轮询 22 21 40（默认 50ms），逐批打时间戳 + hex 原文；
                    按 docs/4 §1 帧格式解析（55 AA / LEN / CMD / DATA / SEQ / CS，
                    和校验），帧级打印 Δt / 命令码 / 校验对错；
                    cmd=0x01 状态上报按 §2.2/2.3 位定义解码
  --send "<hex>"    构造透传帧（10 02 + 27 01/03/02 解锁后 2E 21 41），
                    例如版本问询帧：55 AA 02 03 01 05
  两者可组合（先 send 后 listen）；无参数默认 --listen。
  Ctrl+C 干净退出。

用法: ZCANPRO → 高级功能 → 扩展脚本 → 打开本文件（宿主调 z_main()）
      WSL: python3 本脚本 --listen / --send "55 AA 02 03 01 05"（zcanpro_shim 注入）
"""

import os
import sys
import time

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

LISTEN_INTERVAL_S = 0.050   # 轮询 22 2140 默认 50ms
SEND_MAX_LEN = 64           # 0x2141 payload 上限（固件 NRC 0x13 分界）

# ======== UDS 常量 ========
UDS_REQ_ID  = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

SID_DSC  = 0x10
SID_SA   = 0x27
SID_RDBI = 0x22
SID_WDBI = 0x2E
SID_TP   = 0x3E
SID_NRC  = 0x7F
SID_PR   = 0x40

NRC_RCRRP = 0x78
NRC_EXCEEDED_ATTEMPTS   = 0x36  # 27 02 验签失败超限（fail_count≥3，固件锁定约30s）
NRC_REQUIRED_TIME_DELAY = 0x37  # 锁定期内 27 01 的应答（requiredTimeDelay）
NRC_CONDITIONS_NOT_CORRECT = 0x22   # 写门禁：会话不满足（S3 超时回 default）
NRC_SECURITY_ACCESS_DENIED = 0x33   # 写门禁：security_unlocked=0

# ======== S3/锁定防护参数（与 zcanpro_qi_set_power.py 同构）========
S3_KEEPALIVE_INTERVAL_S = 3.0   # keepalive 周期：3s < SESSION_TIMEOUT_MS 5s
S3_SIGN_GAP_GUARD_S     = 3.0   # 签名耗时超过该值：先发 3E 再进 27 03 分片
SA_LOCKOUT_WAIT_S       = 31.0  # SA 锁定等待总时长（固件 SECURITY_LOCKOUT_MS=30s）

DID_QI_SNIFF = 0x2140
DID_QI_UART_TX = 0x2141

# Qi 命令码（docs/4 §1.2）
QI_CMD_NAMES = {
    0x00: "ACK",
    0x01: "状态上报",
    0x02: "设置功率",
    0x03: "版本问询",
    0xCC: "IAP",
}

# docs/4 §2.2 状态字节1 位定义
STATUS1_BITS = [
    (0, "PING标识"), (1, "充电中"), (2, "已满充"), (3, "过压保护"),
    (4, "欠压保护"), (5, "过流保护"), (6, "过温保护"), (7, "FOD保护"),
]

SA_SIG_CHUNK = 4

stopTask = False


class UdsNrcError(RuntimeError):
    """带 NRC 码的 UDS 异常；SA 锁定/写门禁分支按 e.nrc 判别。"""

    def __init__(self, sid, nrc):
        RuntimeError.__init__(self, "NRC SID=0x%02X NRC=0x%02X" % (sid, nrc))
        self.sid = sid
        self.nrc = nrc


def z_notify(type, obj):
    global stopTask
    if type == "stop":
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


def _ts():
    return time.strftime("%H:%M:%S") + ".%03d" % int((time.time() % 1) * 1000)


def _to_list(b):
    return list(b) if isinstance(b, (list, bytes, bytearray)) else [ord(c) for c in b]


def _to_bytes(seq):
    if isinstance(seq, bytes):
        return seq
    return bytes(seq) if sys.version_info[0] >= 3 else "".join(chr(x & 0xFF) for x in seq)


# ======== ECDSA 签名 (P-256) —— 与 zcanpro_qi_set_power.py 同构 ========
_P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
_N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
_GX = 0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296
_GY = 0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5


def _inv(x, m):
    return pow(x % m, -1, m) if sys.version_info[0] >= 3 else pow(x % m, m - 2, m)


def _jp_double(x, y, z):
    if z == 0 or y == 0:
        return 0, 0, 0
    ysq = (y * y) % _P
    s = (4 * x * ysq) % _P
    m = (3 * x * x + (_P - 3) * pow(z, 4, _P)) % _P
    # y3 = m*(s - x3) - 8*y^4，x3 = m^2 - 2s（与 zcanpro_qi_set_power.py 验证过的实现一致）
    return (m * m - 2 * s) % _P, (m * (s - (m * m - 2 * s) % _P) - 8 * pow(ysq, 2, _P)) % _P, (2 * y * z) % _P


def _jp_add(x1, y1, z1, x2, y2, z2):
    if z1 == 0:
        return x2, y2, z2
    if z2 == 0:
        return x1, y1, z1
    z1z1, z2z2 = pow(z1, 2, _P), pow(z2, 2, _P)
    u1 = x1 * z2z2 % _P
    u2 = x2 * z1z1 % _P
    s1 = y1 * z2 * z2z2 % _P
    s2 = y2 * z1 * z1z1 % _P
    h = (u2 - u1) % _P
    r = (s2 - s1) % _P
    if h == 0:
        if r == 0:
            return _jp_double(x1, y1, z1)
        return 0, 0, 0
    hh = h * h % _P
    hhh = h * hh % _P
    v = u1 * hh % _P
    nx = (r * r - hhh - 2 * v) % _P
    ny = (r * (v - nx) - s1 * hhh) % _P
    nz = h * z1 % _P * z2 % _P
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
    z2 = zinv * zinv % _P
    # y_affine = ry * zinv^3 = ry * z2 * zinv（此前写成 ry*z2%zinv 为取模误写，签名必验失败）
    return rx * z2 % _P, ry * z2 % _P * zinv % _P


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
        s = _inv(k, _N) * (z + r * priv) % _N
        if s == 0:
            continue
        return r.to_bytes(32, "big") + s.to_bytes(32, "big")


def _collect_octet32(buf, out, start, end):
    i = start
    while i < end:
        tag = buf[i]
        i += 1
        if i >= end:
            break
        first = buf[i]
        i += 1
        ln = first if first < 0x80 else 0
        if first >= 0x80:
            n = first & 0x7F
            if 0 < n <= 4 and i + n <= end:
                ln = 0
                for _ in range(n):
                    ln = (ln << 8) | buf[i]
                    i += 1
            else:
                break
        if i + ln > end:
            break
        if tag == 0x04 and ln == 32:
            out.append(buf[i:i + 32])
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
        lines = [l.strip() for l in text.splitlines()
                 if l.strip() and "BEGIN" not in l and "END" not in l]
        import base64
        der = base64.b64decode("".join(lines))
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


# ======== UDS 通信 ========

def uds_init():
    zcanpro.uds_init({
        "response_timeout_ms": 3000, "use_canfd": 0, "canfd_brs": 0,
        "trans_ver": 0, "fill_byte": 0xCC, "frame_type": 1,
        "trans_stmin_valid": 1, "trans_stmin": 1, "enhanced_timeout_ms": 30000,
    })


def uds_req(bus_id, sid, payload, suppress=0, wait_pending_s=0):
    if stopTask:
        raise RuntimeError("用户停止")
    req = {
        "src_addr": UDS_REQ_ID, "dst_addr": UDS_RESP_ID,
        "suppress_response": 1 if suppress else 0, "sid": sid, "data": list(payload),
    }
    t_end = time.time() + float(wait_pending_s)
    logged = False
    while True:
        if stopTask:
            raise RuntimeError("用户停止")
        if not logged:
            _log("[Tx] %02X %s" % (sid, _hex(payload[:40])))
            logged = True
        resp = zcanpro.uds_request(bus_id, req)
        if suppress:
            return None
        data = list((resp or {}).get("data") or [])
        if data:
            _log("[Rx] " + _hex(data[:40]))
        if len(data) >= 3 and data[0] == SID_NRC:
            if data[2] == NRC_RCRRP:
                if wait_pending_s <= 0 or time.time() >= t_end:
                    raise RuntimeError("NRC 0x78 超时")
                _log("NRC 0x78，等待中...")
                time.sleep(1.0)
                continue
            raise UdsNrcError(data[1], data[2])
        if not resp or not resp.get("result"):
            raise RuntimeError("无应答")
        if not data:
            raise RuntimeError("空响应")
        if data[0] != (sid + SID_PR):
            raise RuntimeError("非正响应: " + _hex(data))
        return data


# ======== SecurityAccess 解锁（10 02 + 27 01/03/02）=======

def _sa_fetch_seed(bus_id):
    """27 01 取 seed（67 01 + 32B）。固件每次 27 01 都刷新 seed。"""
    rx = uds_req(bus_id, SID_SA, [0x01])
    if len(rx) < 34:
        raise RuntimeError("seed 响应过短: %d 字节（期望 ≥34）" % len(rx))
    return rx


def _sa_send_sig(bus_id, sig):
    """27 03 分片发送 64 字节签名：4B/帧 × 16 帧，blockSeq 0x01 起。"""
    sig = _to_list(sig)
    if len(sig) != 64:
        raise RuntimeError("ECDSA 签名须 64 字节")
    seq, off = 1, 0
    while off < 64:
        piece = sig[off:off + SA_SIG_CHUNK]
        uds_req(bus_id, SID_SA, [0x03, seq] + piece)
        off += len(piece)
        seq += 1
    time.sleep(0.15)


def send_security_key(bus_id, priv):
    """完整解锁流程：27 01 → 重签 → 27 03 分片 → 27 02；最多 5 次。
    NRC 0x36/0x37 = 固件 SecurityAccess 锁定（fail_count≥3，约 30s，
    can_protocol.c handle_security_access）：S3 keepalive 等待 31s 后完整重试；
    签名耗时 >3s 时先发 3E 80 刷新 S3 计时再进 27 03 分片（防会话超时）。"""
    last = None
    for attempt in range(1, 6):
        if stopTask:
            raise RuntimeError("用户停止脚本")
        try:
            _log("SecurityAccess 第 %d/5 次：27 01 → 重签 → 27 03 → 27 02" % attempt)
            rx = _sa_fetch_seed(bus_id)
            seed = _to_bytes(rx[2:34])
            if seed == b"\x00" * 32:
                _log("27 01 seed=0（32B 全 0），已解锁")
                return rx
            t_sign = time.time()
            sig = ecdsa_sign_msg(priv, seed)
            if time.time() - t_sign > S3_SIGN_GAP_GUARD_S:
                _log("ECDSA 签名耗时较长：先发 3E 80 刷新 S3 计时再进 27 03 分片")
                uds_req(bus_id, SID_TP, [0x80], suppress=1)
            _sa_send_sig(bus_id, sig)
            return uds_req(bus_id, SID_SA, [0x02], wait_pending_s=45)
        except UdsNrcError as e:
            last = e
            if e.nrc in (NRC_EXCEEDED_ATTEMPTS, NRC_REQUIRED_TIME_DELAY):
                _log("NRC 0x%02X：设备 SecurityAccess 锁定（约30s），S3 keepalive 等待 %.0fs 后完整重试"
                     % (e.nrc, SA_LOCKOUT_WAIT_S))
                _s3_keepalive_wait(bus_id)
                continue
            _log("解锁尝试失败: %s（重试将重新取 seed 重签重发分片）" % e)
            time.sleep(1.0)
        except RuntimeError as e:
            last = e
            _log("解锁尝试失败: %s" % e)
            time.sleep(1.0)
    raise last if last is not None else RuntimeError("SecurityAccess 5 次尝试全部失败")


def _s3_keepalive_wait(bus_id, total_s=SA_LOCKOUT_WAIT_S, interval_s=S3_KEEPALIVE_INTERVAL_S):
    """SA 锁定等待期间周期发 3E 80 suppress 刷新 S3 计时，防会话回 default。"""
    t_end = time.time() + float(total_s)
    _log("S3 keepalive 等待 %.0fs（每 %.0fs 发 3E 80 suppress）" % (total_s, interval_s))
    while time.time() < t_end:
        if stopTask:
            raise RuntimeError("用户停止")
        remain = t_end - time.time()
        time.sleep(interval_s if remain > interval_s else remain)
        keepalive(bus_id)


def unlock_programming(bus_id, priv):
    """10 02 编程会话 + 完整安全解锁（0x2141 写门禁：同 0x2130/0x2131）。"""
    uds_req(bus_id, SID_DSC, [0x02])
    send_security_key(bus_id, priv)


# ======== 0x2140 抓取读取 ========

def sniff_read(bus_id):
    """读一次 DID 0x2140，返回 (flags, payload_list)。"""
    rx = uds_req(bus_id, SID_RDBI,
                 [(DID_QI_SNIFF >> 8) & 0xFF, DID_QI_SNIFF & 0xFF])
    # 响应: 62 21 40 [flags][len][data...]
    if len(rx) < 5:
        raise RuntimeError("0x2140 响应过短: " + _hex(rx))
    flags = rx[3]
    n = rx[4]
    data = rx[5:5 + n]
    if len(data) < n:
        raise RuntimeError("0x2140 数据不足: 期望 %d 实收 %d" % (n, len(data)))
    return flags, data


# ======== Qi 帧解析（docs/4 §1）=========

class QiFrameParser(object):
    """流式解析 Qi UART 帧：55 AA LEN CMD DATA [SEQ] CS。
    帧总长 = 3 + LEN + 1（头2 + LEN1 + 内容LEN + CS1）；
    CS = (55+AA+LEN+内容 LEN 字节) & 0xFF（内容含 SEQ 则 CS 含 SEQ）。"""

    def __init__(self):
        self.buf = bytearray()
        self.last_frame_t = None
        self.ok_cnt = 0
        self.bad_cnt = 0

    def feed(self, data):
        """喂入原始字节，返回解析出的帧列表 [(frame_bytes, cs_ok)]。"""
        self.buf.extend(bytearray(data))
        frames = []
        while True:
            # 找帧头 55 AA
            idx = self._find_header()
            if idx < 0:
                # 无头：保留最后 1 字节（可能是 55）
                if len(self.buf) > 1:
                    del self.buf[:-1]
                break
            if idx > 0:
                _log("[杂散] %d 字节: %s" % (idx, _hex(self.buf[:idx])))
                del self.buf[:idx]
            if len(self.buf) < 3:
                break
            ln = self.buf[2]
            if ln < 1 or ln > 64:
                # LEN 非法：丢弃 55 重同步
                _log("[坏帧] LEN=0x%02X 非法: %s" % (ln, _hex(self.buf[:8])))
                del self.buf[0]
                continue
            total = 3 + ln + 1
            if len(self.buf) < total:
                break
            frame = bytes(self.buf[:total])
            del self.buf[:total]
            cs = 0
            for b in frame[:-1]:
                cs = (cs + b) & 0xFF
            ok = (cs == frame[-1])
            frames.append((frame, ok))
        return frames

    def _find_header(self):
        n = len(self.buf)
        for i in range(n - 1):
            if self.buf[i] == 0x55 and self.buf[i + 1] == 0xAA:
                return i
        return -1

    def report(self, frame, cs_ok, overflow):
        """帧级打印：Δt / 命令码 / 校验对错；cmd=0x01 按 §2.2/2.3 解码。"""
        now = time.time()
        dt = ""
        if self.last_frame_t is not None:
            dt = " Δt=%.3fs" % (now - self.last_frame_t)
        self.last_frame_t = now
        ln = frame[2]
        content = frame[3:3 + ln]      # CMD + DATA [+ SEQ]
        cs_str = "CS=OK" if cs_ok else ("CS=BAD(算得0x%02X)" % (sum(frame[:-1]) & 0xFF))
        if cs_ok:
            self.ok_cnt += 1
        else:
            self.bad_cnt += 1
        cmd = content[0]
        cmd_name = QI_CMD_NAMES.get(cmd, "未知0x%02X" % cmd)
        ov = " [溢出丢弃!]" if overflow else ""
        _log("[%s] 帧 %s | cmd=0x%02X(%s) %s%s%s"
             % (_ts(), _hex(frame), cmd, cmd_name, cs_str, dt, ov))
        if cmd == 0x01 and len(content) >= 7:
            self._decode_status(content)

    @staticmethod
    def _decode_status(content):
        """状态上报解码（docs/4 §2.1/2.2/2.3）：
        content = 01 st1 st2 pw(2) ver(2) [SEQ]"""
        st1, st2 = content[1], content[2]
        # 实时功率/版本号均为 uint16 LE（固件解析 data[2-3]/data[4-5] 同为 LE）
        pw = content[3] | (content[4] << 8)
        ver = content[5] | (content[6] << 8)
        bits = [name for b, name in STATUS1_BITS if (st1 >> b) & 1]
        _log("        状态1=0x%02X[%s] 状态2=0x%02X(充电次数低8=%d) "
             "实时功率=0x%04X Qi版本=0x%04X"
             % (st1, ",".join(bits) if bits else "无", st2, st2, pw, ver))


# ======== 模式 A：--listen ========

def run_listen(bus_id, interval_s=LISTEN_INTERVAL_S):
    """--listen 主循环：轮询 22 2140；每 3s 发 3E 80 保活（S3 超时防护，
    22 2140 轮询本身也会刷新 S3，keepalive 仅兜底 send 后会话存活）。"""
    _log("======== 模式A --listen：轮询 22 21 40（间隔 %.0fms，Ctrl+C 停止）========"
         % (interval_s * 1000))
    parser = QiFrameParser()
    last_ka = time.time()
    while not stopTask:
        try:
            flags, data = sniff_read(bus_id)
        except RuntimeError as e:
            if stopTask:
                break
            _log("读取失败: %s" % e)
            time.sleep(interval_s)
            continue
        if time.time() - last_ka > 3.0:
            keepalive(bus_id)
            last_ka = time.time()
        overflow = flags & 0x01
        if overflow:
            _log("[%s] 溢出丢弃发生（bit0），缓冲曾满，部分字节已丢失" % _ts())
        if data:
            _log("[%s] 抓取 %dB flags=0x%02X: %s"
                 % (_ts(), len(data), flags, _hex(data)))
            for frame, ok in parser.feed(data):
                parser.report(frame, ok, overflow)
                overflow = False  # 溢出只随第一次读上报
        time.sleep(interval_s)
    _log("listen 结束：帧统计 OK=%d BAD=%d" % (parser.ok_cnt, parser.bad_cnt))


# ======== 模式 B：--send ========

def parse_send_hex(s):
    """解析 hex 字符串（空格/逗号分隔或连续串）为字节数组；校验 1~64B。"""
    s = s.replace(",", " ").replace("0x", " ").strip()
    toks = s.split()
    if len(toks) == 1 and len(toks[0]) % 2 == 0:
        h = toks[0]
        data = [int(h[i:i + 2], 16) for i in range(0, len(h), 2)]
    else:
        data = [int(t, 16) for t in toks]
    if len(data) < 1:
        raise ValueError("payload 为空（固件回 7F 2E 13）")
    if len(data) > SEND_MAX_LEN:
        raise ValueError("payload %d 字节 > 64（固件回 7F 2E 13）" % len(data))
    return data


def run_send(bus_id, payload, priv):
    _log("======== 模式B --send：%s ========" % _hex(payload))
    unlock_programming(bus_id, priv)
    frame = [(DID_QI_UART_TX >> 8) & 0xFF, DID_QI_UART_TX & 0xFF] + payload
    try:
        rx = uds_req(bus_id, SID_WDBI, frame)
    except UdsNrcError as e:
        # 固件 0x2141 门禁（handle_write_data_by_id）：10 02 编程会话 + SA；
        # 0x22=会话不满足（S3 超时回 default），0x33=安全态被清 → 重建后重试一次
        if e.nrc not in (NRC_CONDITIONS_NOT_CORRECT, NRC_SECURITY_ACCESS_DENIED):
            raise
        _log("2E 写 NRC 0x%02X：疑似 S3 超时——重发 10 02 + 重解锁 + 重试写一次" % e.nrc)
        uds_req(bus_id, SID_DSC, [0x02])
        send_security_key(bus_id, priv)
        rx = uds_req(bus_id, SID_WDBI, frame)
        _log("S3 超时恢复：会话+安全态重建后重试写成功")
    _log("透传发送成功: %s" % _hex(rx))
    # 后续 listen 期间每 3s 3E 80 保活（且 22 2140 轮询本身也刷新 S3）


def keepalive(bus_id):
    """S3 keepalive：3E 80 suppress（会话/安全态 5s 超时防护）。"""
    try:
        uds_req(bus_id, SID_TP, [0x80], suppress=1)
    except RuntimeError:
        pass


# ======== 入口 ========

def _parse_args(argv):
    mode_listen = False
    send_hex = None
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--listen":
            mode_listen = True
        elif a == "--send":
            if i + 1 >= len(argv):
                raise ValueError("--send 缺少 hex 字符串参数")
            send_hex = argv[i + 1]
            i += 1
        elif a in ("--help", "-h"):
            raise SystemExit(__doc__)
        # 其余参数忽略（宿主 argv 噪声不拦截）
        i += 1
    if not mode_listen and send_hex is None:
        mode_listen = True   # 无参数默认 listen
    return mode_listen, send_hex


def run(mode_listen, send_hex):
    uds_init()
    if send_hex is not None:
        payload = parse_send_hex(send_hex)
        if not os.path.isfile(PRIVATE_KEY_PATH):
            raise RuntimeError("找不到私钥: " + PRIVATE_KEY_PATH)
        priv = load_ec_private_key(PRIVATE_KEY_PATH)
        run_send(_BUS_ID, payload, priv)
        if not mode_listen:
            return
    # listen 主循环（与 send 组合时先发后听；期间每 3s 3E 保活）
    run_listen(_BUS_ID)


_BUS_ID = None


def z_main():
    """ZCANPRO 宿主入口（宿主无 argv → 默认 listen 模式）。"""
    global stopTask, _BUS_ID
    stopTask = False
    _log("======== Qi UART 抓取/透传桥（0x2140 读 / 0x2141 写）========")
    buses = zcanpro.get_buses()
    if not buses:
        _log("请先打开 CAN 通道 (250kbps, 扩展帧)")
        return
    _BUS_ID = buses[0]["busID"]
    try:
        mode_listen, send_hex = _parse_args(sys.argv[1:])
        run(mode_listen, send_hex)
    except (KeyboardInterrupt, SystemExit):
        _log("已退出（Ctrl+C）")
    except Exception as e:
        _log("失败: %s" % e)
    finally:
        try:
            zcanpro.uds_deinit()
        except Exception:
            pass


if __name__ == "__main__":
    # 命令行直跑（WSL shim 注入 zcanpro 后 python3 本脚本 --listen / --send ...）
    z_main()
