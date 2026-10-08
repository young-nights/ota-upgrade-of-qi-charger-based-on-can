#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""TC-0104 ResponsePending（NRC 0x78）时序测试 —— WSL2 独立执行。

用例 TC-0104（P1）：APP 编程会话下，观察擦备份区 31 01 FF 00 与验签 37
两个关键步骤的响应时序，预期两段式：
  TX → 第一帧 = 7F xx 78（ResponsePending）→ 等待 → 正响应
  （31 → 71 01 FF 00；37 → 77）
两项都观察到该两段式才算 PASS；任一步第一帧直接是正响应（无 78）或
超时未收到正响应 → FAIL，并打印实测帧序列（不放宽断言）。

实现要点（时序证据链）：
  · 10/27/2E/22 2114/34/36 等前置与常规步骤走 shim 高层 uds_req（省事）；
  · 关键观测点 31/37 不走 shim uds_req（其内部吞掉 7F xx 78），改为直接
    复用同一进程内同一 Zqwl 实例（shim._ensure_dev()，串口独占）做低层
    收发：SF 单帧发出请求后 2ms 级 pump，按到达顺序记录每个 RX 帧及
    相对 TX 的毫秒时间戳，天然可见 78 → 正响应两段式；
  · 观测期间临时把 serial timeout 调到 2ms（默认 50ms 会把帧到达时刻
    拖迟最多 50ms，失真 P2 实测），观测结束恢复；
  · ISO-TP 组装自持（SF/FF 回 FC/CF），不依赖 shim _rx_wait。

流程：
  基线 22 F195 → 10 02 → 27 01/02 签名解锁 → 2E 20 10 01
  → 【观测点①】31 01 FF 00 → 22 2114==0x02 → 34 → 36 全块（无 TP 停顿）
  → 【观测点②】37 → 等自复位 → 判定闭环
  （0x2113=0x00、0xF195==载荷版本）→ RESULT PASS/FAIL，exit 0/1。

用法（仓库任意位置）：
  python3 "python_tools/wsl/tc0104_response_pending.py" [--firmware "路径.bin"]
  默认载荷：python_tools/app bin/app_image_v1_1_1.bin（设备当前 1.1.2，刷回 1.1.1）
退出码：0=PASS，1=FAIL。

不修改任何现有文件（sys.modules['zcanpro'] 注入 shim，复用 ext 函数）。
"""
from __future__ import print_function

import argparse
import os
import re
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
# 同级 shim 脚本用 HERE，zcanpro_ext_ota_auto 在 zcanpro/non-qi/。
PT = os.path.dirname(HERE)
ZCANPRO_NONQI = os.path.join(PT, "zcanpro", "non-qi")
sys.path.insert(0, HERE)
sys.path.insert(0, ZCANPRO_NONQI)

import zcanpro_shim_zqwl as shim  # noqa: E402
sys.modules["zcanpro"] = shim
import zcanpro_ext_ota_auto as ext  # noqa: E402

DEFAULT_FIRMWARE = os.path.join(PT, "app bin", "app_image_v1_1_1.bin")

UDS_REQ_ID = 0x18DA0D03   # 主机 TX
UDS_RESP_ID = 0x18DA030D  # ECU TX / 主机 RX
FILL = 0xCC

# 观测点：从 TX 起等待第一帧（P2 内应有 78）与最终正响应的绝对上限。
# 31 擦除实测可到数十秒（ext._erase_with_retry 上限 90s pending）；37 验签
# 同样可能长等待，取 120s 覆盖。
OBSERVE_TOTAL_S = 120.0

TRANSFER_BLOCK = 128    # 36 每块字节数（≤0x400，与 ext 一致）

# 各观测点的预期正响应（终态）
OBS_31 = {
    "step": "31_01_FF_00擦Backup区",
    "sid": 0x31,
    "payload": [0x01, 0xFF, 0x00],
    "expect": [0x71, 0x01, 0xFF, 0x00],
}
OBS_37 = {
    "step": "37_TransferExit验签提交",
    "sid": 0x37,
    "payload": [],
    "expect": [0x77],
}


class TestFail(RuntimeError):
    """TC-0104 判定 FAIL（带失败步骤标签）。"""


BUS = None
FAILED_STEP = None


def _log(msg):
    sys.stdout.write(str(msg) + "\n")
    sys.stdout.flush()


def _fail(step, reason):
    global FAILED_STEP
    if FAILED_STEP is None:
        FAILED_STEP = step
    raise TestFail("步骤[%s] FAIL: %s" % (step, reason))


def req(step, sid, payload, **kw):
    """ext.uds_req 包装：NRC/异常归入当前步骤标签。返回正响应 payload。"""
    _log("---- 步骤[%s] TX: %02X %s ----" % (step, sid, ext._hex(payload[:16])))
    try:
        rx = ext.uds_req(BUS, sid, payload, **kw)
    except ext.UdsNrcError as e:
        _fail(step, "NRC 7F %02X %02X" % (e.sid, e.nrc))
    except ext.SafeModeError as e:
        _fail(step, str(e))
    except TestFail:
        raise
    except Exception as e:
        _fail(step, str(e))
    return rx


def read_version(step):
    """22 F195 → ASCII 版本串（QC_JYF_FW_x.y.z）。"""
    rx = req(step, 0x22, [0xF1, 0x95])
    raw = bytes(int(b) & 0xFF for b in rx[3:]) if len(rx) > 3 else b""
    m = re.search(rb"QC_JYF_FW_[0-9A-Za-z._]+", raw)
    if m:
        return m.group(0).decode("ascii")
    return raw.split(b"\x00")[0].decode("ascii", "ignore").strip() or None


# ---------------------------------------------------------------- 观测点核心

def observe_pending(obs, timeout_s=OBSERVE_TOTAL_S):
    """低层观测一次 UDS 请求的完整应答时序。

    对 obs["sid"]/obs["payload"] 发 SF 单帧，随后 2ms 级 pump，按到达顺序
    记录每个 RX 帧（含非目标 ID 帧）及相对 TX 的时间戳，自行做 ISO-TP 组装
    （SF/FF 回 FC/CF），直到收到终态（正响应或非 78 的 NRC）或超时。

    返回 {first, events, t78_ms, tpos_ms, wait_ms, pos}：
      first   = 第一个组装完成的 UDS payload（时序断言对象）
      events  = [(t_ms, can_id, payload|None), ...]（payload=None 为未完成
                ISO-TP 分片帧/非目标帧原文）
      t78_ms  = 第一帧 7F xx 78 相对 TX 的到达时刻（ms）；首帧非 78 则 None
      tpos_ms = 正响应相对 TX 的到达时刻（ms）；未收到则 None
      wait_ms = 78 → 正响应等待时长（ms）
      pos     = 组装出的正响应 payload；未收到则 None

    断言在这里做（证据打印后）：
      ① 第一帧必须 = 7F <sid> 78，否则 FAIL（打印实测帧序列）；
      ② 超时未见正响应 → FAIL（打印实测帧序列）；
      ③ 正响应必须 == obs["expect"]，否则 FAIL。
    """
    dev = shim._ensure_dev()
    step = obs["step"]
    sid = obs["sid"]
    nrc_key = 0x7F, sid, 0x78

    # ---- 先排空积压帧（旧请求迟到应答/生命周期帧），防止串台 ----
    old_to = dev.ser.timeout
    dev.ser.timeout = 0.002
    drained = 0
    t_drain_end = time.time() + 0.05
    try:
        while time.time() < t_drain_end:
            for item in dev.pump():
                if item[0] == "can":
                    drained += 1
        if drained:
            _log("[OBS] TX 前排空积压帧 %d 个" % drained)

        # ---- TX：SF 单帧（PCI=len + sid + payload，8B 0xCC 填充）----
        raw = [len(obs["payload"]) + 1, sid] + list(obs["payload"])
        raw += [FILL] * (8 - len(raw))
        _log("---- 步骤[%s] TX@0x%08X: %s ----"
             % (step, UDS_REQ_ID, ext._hex([sid] + list(obs["payload"]))))
        t_tx = time.time()
        dev.send_can(UDS_REQ_ID, bytes(raw), ext=True)

        # ---- RX：2ms 级 pump + ISO-TP 组装 ----
        events = []            # (t_ms, cid, payload|None)
        assembled = []         # (t_ms, payload)
        buf = bytearray()
        expected = None
        first = None
        t78 = None
        tpos = None
        pos = None
        pending_done = False   # 见过 78（放宽语义：此后一直等终态）
        other_nrc = None

        def _on_payload(t_ms, payload):
            nonlocal first
            assembled.append((t_ms, payload))
            if first is None:
                first = list(payload)
            if (len(payload) >= 3 and payload[0] == 0x7F
                    and tuple(payload[:3]) == nrc_key):
                return "pending"
            return "final"

        while time.time() - t_tx < timeout_s:
            items = dev.pump()
            if not items:
                continue
            now_ms = (time.time() - t_tx) * 1000.0
            for item in items:
                if item[0] != "can":
                    continue
                cid = item[1] & 0x1FFFFFFF
                data = [int(b) & 0xFF for b in item[3]]
                if cid != UDS_RESP_ID:
                    events.append((now_ms, cid, None))
                    _log("[OBS][+%9.2fms] RX 0x%08X %s (非目标ID)"
                         % (now_ms, cid, ext._hex(data)))
                    continue
                pci = (data[0] >> 4) if data else -1
                done = None
                if pci == 0:                       # SF
                    ln = data[0] & 0x0F
                    done = data[1:1 + ln] if ln else data[1:]
                elif pci == 1:                     # FF：立即回 FC
                    expected = ((data[0] & 0x0F) << 8) | (data[1] if len(data) > 1 else 0)
                    buf = bytearray(data[2:])
                    dev.send_can(UDS_REQ_ID, bytes([0x30, 0x00, 0x00]
                                                    + [FILL] * 5), ext=True)
                    _log("[OBS][+%9.2fms] FF len=%d → 已回 FC(30 00 00)"
                         % (now_ms, expected))
                    if expected and len(buf) >= expected:
                        done = list(buf[:expected])
                elif pci == 2:                     # CF
                    if expected is None:
                        events.append((now_ms, cid, None))
                        _log("[OBS][+%9.2fms] RX %s (孤儿CF, 忽略)"
                             % (now_ms, ext._hex(data)))
                        continue
                    buf.extend(data[1:])
                    if len(buf) >= expected:
                        done = list(buf[:expected])
                        expected = None
                    else:
                        events.append((now_ms, cid, None))
                        _log("[OBS][+%9.2fms] RX %s (CF %d/%d)"
                             % (now_ms, ext._hex(data), len(buf), expected))
                        continue
                elif pci == 3:                     # ECU 侧 FC（不应出现）
                    events.append((now_ms, cid, None))
                    _log("[OBS][+%9.2fms] RX %s (意外FC帧)"
                         % (now_ms, ext._hex(data)))
                    continue
                else:                              # 裸 payload 兜底
                    done = data
                if done is None:
                    events.append((now_ms, cid, None))
                    _log("[OBS][+%9.2fms] RX %s (分片)"
                         % (now_ms, ext._hex(data)))
                    continue

                payload = [int(b) & 0xFF for b in done]
                events.append((now_ms, cid, payload))
                _log("[OBS][+%9.2fms] RX %s"
                     % (now_ms, ext._hex(payload[:16])))
                kind = _on_payload(now_ms, payload)
                if kind == "pending":
                    if t78 is None:
                        t78 = now_ms
                    pending_done = True
                    continue
                # 终态：正响应 或 非 78 的 NRC
                if payload[0] == ((sid + 0x40) & 0xFF):
                    tpos = now_ms
                    pos = payload
                else:
                    other_nrc = payload
                pending_done = pending_done or t78 is not None
                break
            if tpos is not None or other_nrc is not None:
                break
        finally_time = time.time()
    finally:
        dev.ser.timeout = old_to

    wait_ms = (tpos - t78) if (t78 is not None and tpos is not None) else None

    # ---- 观测小结（时序证据）----
    _log("[OBS] %s 观测小结：帧数=%d，第一帧=%s，78到达=%s，"
         "正响应到达=%s，78→正响应=%s"
         % (step, len(events), ext._hex(first) if first else "无",
            ("+%.2fms" % t78) if t78 is not None else "无",
            ("+%.2fms" % tpos) if tpos is not None else "无",
            ("%.2fms" % wait_ms) if wait_ms is not None else "无"))
    if t78 is not None:
        _log("[OBS] %s 78 到达延迟（相对 TX）= %.2f ms（P2 预期 ≈50ms 量级，"
             "实测值供记录）" % (step, t78))
    if wait_ms is not None:
        _log("[OBS] %s 78 → 正响应等待 = %.2f ms" % (step, wait_ms))

    def _seq_str():
        parts = []
        for t_ms, cid, payload in events:
            if payload is not None:
                parts.append("+%.0fms:%s" % (t_ms, ext._hex(payload[:8])))
            else:
                parts.append("+%.0fms:0x%08X:frame" % (t_ms, cid))
        return " | ".join(parts) if parts else "（无任何 RX 帧）"

    # ---- 断言（先打印完整实测序列，再判定）----
    if first is None:
        _fail(step, "%.0fs 内未收到任何应答帧。实测帧序列: %s"
              % (timeout_s, _seq_str()))
    if first[:3] != list(nrc_key):
        _fail(step, "第一帧不是 7F %02X 78（实测第一帧=%s），"
                    "两段式 ResponsePending 时序不成立。实测帧序列: %s"
              % (sid, ext._hex(first), _seq_str()))
    if other_nrc is not None:
        _fail(step, "78 之后收到终态 NRC %s（非正响应）。实测帧序列: %s"
              % (ext._hex(other_nrc), _seq_str()))
    if pos is None:
        _fail(step, "已见 7F %02X 78 但 %.0fs 内未收到正响应 %s。"
                    "实测帧序列: %s"
              % (sid, timeout_s, ext._hex(obs["expect"]), _seq_str()))
    if pos != list(obs["expect"]):
        _fail(step, "正响应内容不符：预期 %s，实测 %s。实测帧序列: %s"
              % (ext._hex(obs["expect"]), ext._hex(pos), _seq_str()))

    _log("[OBS] %s 两段式成立：7F %02X 78（+%.2fms）→ %s（+%.2fms，"
         "等待 %.2fms）"
         % (step, sid, t78, ext._hex(pos), tpos, wait_ms))
    return {"first": first, "events": events, "t78_ms": t78,
            "tpos_ms": tpos, "wait_ms": wait_ms, "pos": pos}


# ---------------------------------------------------------------- 主流程

def run_tc0104(fw_path):
    global BUS
    # ---- 载荷与签名准备（不触碰硬件）----
    if not os.path.isfile(fw_path):
        raise TestFail("载荷不存在: %s" % fw_path)
    bin_data = open(fw_path, "rb").read()
    bin_ver = ext._extract_sw_version(bin_data)
    if not os.path.isfile(ext.PRIVATE_KEY_PATH):
        raise TestFail("找不到私钥: %s" % ext.PRIVATE_KEY_PATH)
    priv = ext.load_ec_private_key(ext.PRIVATE_KEY_PATH)
    image = ext.pack_image_if_needed(fw_path, priv)
    ext.validate_image(image)
    expect_ver = bin_ver or ext._extract_sw_version(image)
    if not expect_ver:
        raise TestFail("载荷内未找到 QC_JYF_FW_ 版本串，判定闭环无法成立")
    _log("载荷 %s（%d 字节，SW_VERSION_STR=%s），判定预期版本=%s"
         % (fw_path, len(image), expect_ver, expect_ver))

    # ---- 1. 打开通道，基线 22 F195 打印 ----
    ext.uds_init()
    buses = shim.get_buses()
    if not buses:
        raise TestFail("无 CAN 总线（Zqwl 通道打开失败）")
    BUS = buses[0]["busID"]
    baseline = read_version("基线22F195")
    _log("基线 22 F195 = %s（载荷目标 %s）" % (baseline, expect_ver))

    # ---- 2. 10 02 进编程会话 ----
    req("10_02进编程会话", 0x10, [0x02])

    # ---- 3. 27 01/02 签名解锁 ----
    rx = req("27_01请求seed", 0x27, [0x01])
    if len(rx) < 6:
        _fail("27_01请求seed", "seed 响应过短: %s" % ext._hex(rx))
    if len(rx) >= 34:
        seed = ext._to_bytes(rx[2:34])
    else:
        seed = ext._to_bytes(rx[2:6])
    if seed == b"\x00" * len(seed):
        _log("已解锁（seed=0），跳过 SendKey")
    else:
        sig = ext.ecdsa_sign_msg(priv, seed)
        _log("seed %s；SendKey 签名 %d 字节" % (ext._hex(rx[2:34]), len(sig)))
        try:
            ext.send_security_key(BUS, sig, seed=seed, priv=priv)
        except ext.UdsNrcError as e:
            _fail("27_02送密钥", "NRC 7F %02X %02X" % (e.sid, e.nrc))
        except Exception as e:
            _fail("27_02送密钥", str(e))

    # ---- 4. 2E 20 10 01 写 DID ----
    req("2E_20_10_01写DID", 0x2E, [0x20, 0x10, 0x01])

    # ---- 5.【观测点①】31 01 FF 00 擦除（低层收发看 78 时序）----
    _log("======== 观测点①：31 01 FF 00 擦备份区（预期 7F 31 78 → 71 01 FF 00）========")
    obs31 = observe_pending(OBS_31)

    # ---- 6. 22 2114 校验 = 0x02（Backup 区标记）----
    try:
        did = ext.read_did_u8(BUS, 0x2114)
    except Exception as e:
        _fail("22_2114校验", "读取失败: %s" % e)
    _log("DID 0x2114=0x%02X（0x02=Backup 区标记）" % did)
    if did != 0x02:
        _fail("22_2114校验", "0x2114=0x%02X ≠ 0x02（擦除后目标区标记），"
                             "无法进入下载" % did)

    # ---- 7. 34 RequestDownload → 36 循环（全块，无 TP 停顿）----
    size = len(image)
    addr_val = ext.BACKUP_BASE
    sz = [(size >> 24) & 0xFF, (size >> 16) & 0xFF, (size >> 8) & 0xFF, size & 0xFF]
    addr = [(addr_val >> 24) & 0xFF, (addr_val >> 16) & 0xFF,
            (addr_val >> 8) & 0xFF, addr_val & 0xFF]
    req("34_RequestDownload_%d_@0x%08X" % (size, addr_val), 0x34,
        [0x00, 0x44] + addr + sz)
    seq = 1
    off = 0
    blocks = 0
    while off < size:
        chunk = image[off:off + TRANSFER_BLOCK]
        req("36_seq%03d" % seq, 0x36, [seq] + ext._to_list(chunk))
        off += len(chunk)
        blocks += 1
        seq = 1 if seq == 0xFF else seq + 1
        if off == size or (off % (TRANSFER_BLOCK * 32) == 0):
            _log("  进度 %d/%d（%d 块）" % (off, size, blocks))

    # ---- 8.【观测点②】37 TransferExit 验签提交（低层收发看 78 时序）----
    _log("======== 观测点②：37 TransferExit 验签提交（预期 7F 37 78 → 77）========")
    obs37 = observe_pending(OBS_37)

    # ---- 9. 等自复位 + 判定闭环 ----
    time.sleep(2.0)
    _log("---- 步骤[复位后判定闭环] ----")
    try:
        to_slot, got_ver, ota_status = ext.confirm_app_after_reset(BUS)
    except Exception as e:
        _fail("复位后判定闭环", str(e))
    problems = []
    if got_ver != expect_ver:
        problems.append("0xF195=%s ≠ 载荷版本 %s" % (got_ver or "未读到", expect_ver))
    if to_slot is None:
        problems.append("0x2113 读取失败")
    elif to_slot != 0x00:
        problems.append("0x2113=0x%02X ≠ 0x00" % (to_slot & 0xFF))
    if problems:
        _fail("复位后判定闭环", "；".join(problems))
    _log("判定闭环满足：① 复位后 APP 应答；② 0x2113=0x00；"
         "③ 0xF195=%s==载荷版本 %s" % (got_ver, expect_ver))

    # ---- 10. 两观测点时序汇总 ----
    _log("======== 时序实测汇总 ========")
    _log("31 01 FF 00：78 到达 +%.2fms（相对 TX）→ 正响应 71 01 FF 00 "
         "+%.2fms，78→正响应等待 %.2fms"
         % (obs31["t78_ms"], obs31["tpos_ms"], obs31["wait_ms"]))
    _log("37：78 到达 +%.2fms（相对 TX）→ 正响应 77 +%.2fms，"
         "78→正响应等待 %.2fms"
         % (obs37["t78_ms"], obs37["tpos_ms"], obs37["wait_ms"]))


def main():
    parser = argparse.ArgumentParser(description="TC-0104 ResponsePending 时序测试")
    parser.add_argument("--firmware", default=DEFAULT_FIRMWARE,
                        help="OTA 载荷 bin（默认 app bin/app_image_v1_1_1.bin）")
    args = parser.parse_args()
    fw = args.firmware
    if not os.path.isabs(fw):
        fw = os.path.abspath(os.path.join(os.getcwd(), fw))
        if not os.path.isfile(fw):
            fw = os.path.abspath(os.path.join(HERE, args.firmware))
    _log("======== TC-0104 ResponsePending（NRC 0x78）时序测试 ========")
    _log("观测点：31 01 FF 00 与 37；判定=第一帧 7F xx 78 且其后收到正响应")
    t0 = time.time()
    try:
        run_tc0104(fw)
    except TestFail as e:
        _log("TC-0104 RESULT: FAIL —— %s" % e)
        _log("失败步骤: %s（总耗时 %.0fs）" % (FAILED_STEP or "未知", time.time() - t0))
        return 1
    except Exception as e:
        _log("TC-0104 RESULT: FAIL —— 未预期异常: %s" % e)
        _log("失败步骤: %s（总耗时 %.0fs）" % (FAILED_STEP or "准备阶段", time.time() - t0))
        return 1
    finally:
        try:
            shim.uds_deinit()
        except Exception:
            pass
        shim.close()  # 释放 /dev/ttyACM0
    _log("TC-0104 RESULT: PASS —— 31 与 37 两步均先收 7F xx 78 后收正响应，"
         "两段式 ResponsePending 时序成立，复位后 0x2113=0x00、"
         "0xF195==载荷版本（总耗时 %.0fs）" % (time.time() - t0))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
