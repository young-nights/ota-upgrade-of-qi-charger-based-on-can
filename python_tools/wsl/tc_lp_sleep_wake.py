#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""低功耗 Sleep 进入/唤醒稳定性测试。

验证链路：
  1. 等设备空闲 30s 自动进 Sleep（或手动触发）
  2. 抓 0x18FF270D 诊断帧（E0 标记），验证 Sleep 进入成功
  3. 发 CAN 帧唤醒
  4. 抓 WK 生命周期帧（01 41 57 4B）或 UDS 响应，验证唤醒成功
  5. 读 DID 0x2119 确认睡眠/唤醒状态
  6. 循环 N 次统计稳定性

用法（在仓库根目录执行）:
  python3 python_tools/wsl/tc_lp_sleep_wake.py --cycles 5 --idle 35
  python3 python_tools/wsl/tc_lp_sleep_wake.py --cycles 1 --idle 35 --no-wait-idle
"""
from __future__ import print_function
import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from zqwl_can_listen import Zqwl  # noqa: E402

# CAN ID
UDS_REQ_ID   = 0x18DA0D03
UDS_RESP_ID  = 0x18DA030D
LIFE_ID      = 0x18FF260D
LP_DIAG_ID   = 0x18FF270D   # 低功耗诊断帧

# 生命周期帧（0x18FF260D）byte0 分类（CCU 词汇表 2026-10-28，[1..7] 全零）：
# 0x01 BOOTUP / 0x02 OPERATIONAL / 0x03 WAKEUP / 0x04 SLEEP
LIFE_BOOTUP = 0x01
LIFE_OPERATIONAL = 0x02
LIFE_WAKEUP = 0x03
LIFE_SLEEP = 0x04
# legacy（旧固件 [1]=0x41 + 'WK'/'SB' magic，兼容旧日志）
WK_MAGIC = bytes([0x01, 0x41, 0x57, 0x4B])
SB_MAGIC = bytes([0x06, 0x41, 0x53, 0x42])

# DID 0x2119 读请求（SIT1145 LP 状态）
DID_LP_STATUS_REQ = bytes([0x03, 0x22, 0x21, 0x19, 0xCC, 0xCC, 0xCC, 0xCC])


def _hex(b):
    return " ".join("%02X" % x for x in b)


def _log(msg):
    print("[%s] %s" % (time.strftime("%H:%M:%S"), msg))


class LPTest:
    def __init__(self, port, idle_s, no_wait_idle):
        self.z = Zqwl(port)
        self.idle_s = idle_s
        self.no_wait_idle = no_wait_idle
        self.results = []

    def open(self):
        self.z.read_device()
        self.z.open_can0_250k()
        _log("CAN0 250kbps opened")

    def close(self):
        self.z.close()

    def pump_frames(self):
        """从 pump() 结果中提取 CAN 帧，返回 [(can_id, data)] 列表。"""
        frames = []
        for item in self.z.pump():
            if item[0] == "can":
                frames.append((item[1], item[3]))  # (cid, data)
        return frames

    def pump_for(self, duration_s, on_frame=None):
        """轮询 duration_s 秒，每帧回调 on_frame(can_id, data)。
        返回收集到的帧列表。"""
        frames = []
        t0 = time.time()
        while (time.time() - t0) < duration_s:
            for cid, d in self.pump_frames():
                frames.append((cid, d, time.time()))
                if on_frame:
                    on_frame(cid, d)
        return frames

    def send_wake(self):
        """发唤醒 burst：SIT1145 WUF 严格匹配 ID=0x18DA0D03 + DLC=3 + 数据 02 3E 00
        （PNDM=1，数据位 DM5=0x02/DM6=0x3E 近似），必须 raw 3 字节帧、不填充。
        ≥6 帧、间隔 250ms（AGENTS.md 唤醒 burst 约定）。DLC=8 填充帧不再唤醒。"""
        for i in range(8):
            self.z.send_can(UDS_REQ_ID, bytes([0x02, 0x3E, 0x00]))
            _log("[Tx] raw 3E 00 唤醒 (DLC=3) (%d/8)" % (i + 1))
            time.sleep(0.25)

    def read_did_2119(self, timeout_s=2.0):
        """读 DID 0x2119（SIT1145 LP 状态），返回 (flags, wup_cnt, last_sec) 或 None。
        CAN 数据含 ISO-TP SF PCI 字节：07 62 21 19 [flags] [cnt] [sec_lo] [sec_hi]。"""
        self.z.send_can(UDS_REQ_ID, DID_LP_STATUS_REQ)
        t0 = time.time()
        while (time.time() - t0) < timeout_s:
            for cid, d in self.pump_frames():
                if cid != UDS_RESP_ID or len(d) < 8:
                    continue
                # ISO-TP SF: d[0]=PCI(0x07), d[1]=0x62, d[2]=0x21, d[3]=0x19
                if (d[0] & 0xF0) == 0x00 and d[1] == 0x62 and d[2] == 0x21 and d[3] == 0x19:
                    flags = d[4]
                    wup_cnt = d[5]
                    last_sec = d[6] | (d[7] << 8)
                    return (flags, wup_cnt, last_sec)
        return None

    def wait_sleep_diag(self, timeout_s):
        """等 Sleep 进入诊断帧（0x18FF270D, E0 标记）。返回 diag payload 或 None。"""
        _log("等待 Sleep 进入诊断帧（0x18FF270D E0）...")
        t0 = time.time()
        while (time.time() - t0) < timeout_s:
            for cid, d in self.pump_frames():
                if cid == LP_DIAG_ID and len(d) >= 8 and d[0] == 0xE0:
                    _log("收到 Sleep 诊断帧: %s" % _hex(d))
                    return d
        return None

    def wait_wake(self, timeout_s):
        """等唤醒标识帧（WK 生命周期帧或 UDS 响应）。返回 src 或 None。"""
        _log("等待唤醒标识（WK 生命周期帧 / UDS 响应）...")
        t0 = time.time()
        while (time.time() - t0) < timeout_s:
            for cid, d in self.pump_frames():
                if cid == LIFE_ID and len(d) >= 1 and (d[0] == LIFE_WAKEUP or bytes(d[:4]) == WK_MAGIC):
                    _log("收到 WAKEUP 唤醒帧: %s" % _hex(d))
                    return "WK"
                if cid == UDS_RESP_ID:
                    _log("收到 UDS 响应: %s" % _hex(d))
                    return "UDS"
        return None

    def probe_device_state(self):
        """探测设备当前状态：返回 'awake' 或 'sleep'。
        超时 3s（唤醒+响应需 ~2s，1.5s 不够会误判 Sleep）。"""
        _log("探测设备状态...")
        self.z.send_can(UDS_REQ_ID, bytes([0x03, 0x22, 0xF1, 0x95, 0xCC, 0xCC, 0xCC, 0xCC]))
        t0 = time.time()
        while (time.time() - t0) < 3.0:
            for cid, d in self.pump_frames():
                if cid == UDS_RESP_ID:
                    _log("设备在线（有 UDS 响应）")
                    return "awake"
        _log("设备无响应（可能已在 Sleep）")
        return "sleep"

    def run_cycle(self, cycle_num):
        """单次进入/唤醒测试。返回 (enter_ok, wake_ok, detail)。"""
        _log("==== 周期 %d ====" % cycle_num)

        # 0. 探测设备状态
        state = self.probe_device_state()

        if state == "sleep":
            # 设备已在 Sleep，跳过进入验证，直接测唤醒
            _log("设备已在 Sleep，跳过进入验证，直接测唤醒")
            time.sleep(0.3)  # 过 inhibit 窗口
            self.send_wake()
            wake_src = self.wait_wake(5.0)
            if wake_src is None:
                self.send_wake()
                wake_src = self.wait_wake(5.0)
            if wake_src is None:
                return (True, False, "already_sleep_no_wake;")
            _log("唤醒成功（%s）" % wake_src)
            time.sleep(0.2)
            lp = self.read_did_2119(2.0)
            if lp:
                _log("DID 0x2119: flags=0x%02X wup_cnt=%d last_sleep=%ds" % lp)
            return (True, True, "already_sleep;")
        # 1. 等待/确认进入 Sleep
        if self.no_wait_idle:
            _log("跳过空闲等待，直接监测 Sleep 诊断帧（最多 %ds）" % int(self.idle_s + 5))
            diag = self.wait_sleep_diag(self.idle_s + 5)
        else:
            _log("等待 %ds 空闲超时自动进 Sleep..." % int(self.idle_s))
            diag = self.wait_sleep_diag(self.idle_s + 10)

        if diag is None:
            _log("FAIL: 未收到 Sleep 进入诊断帧")
            return (False, False, "no_sleep_diag")

        # 解析诊断帧：[0]=E0 [1]=MODE [2]=EVENT_EN [3]=EV24 [4]=CAN_CTRL [5]=EV63 [6]=flags [7]=A5
        # 注意：MODE 是进 Sleep 前的快照（=0x07 Normal 是预期的）
        mode = diag[1]
        ev_en = diag[2]
        ev24 = diag[3]
        can_ctrl = diag[4]
        ev63 = diag[5]
        flags = diag[6]
        _log("诊断: MODE=0x%02X EVENT_EN=0x%02X EV24=0x%02X CAN_CTRL=0x%02X EV63=0x%02X flags=0x%02X"
             % (mode, ev_en, ev24, can_ctrl, ev63, flags))

        # 验证进入：诊断帧后设备应无 UDS 响应（=已进 Sleep）
        enter_ok = True
        detail = ""
        if (ev_en & 0x01) == 0:
            _log("WARN: CWE=0（CAN 唤醒未使能）")
            enter_ok = False
            detail += "cwe=0;"
        if (flags & 0x01) != 0:
            _log("WARN: g_lp_sleep_fail=1（进 Sleep 前 CAN Control 回读失败）")
            detail += "sleep_fail;"

        # 等 300ms 过 inhibit 窗口，然后验证 Sleep：发 UDS 请求，无响应 = 在 Sleep
        time.sleep(0.3)
        _log("验证 Sleep：发 UDS 22 F1 95，无响应 = 已进 Sleep...")
        self.z.send_can(UDS_REQ_ID, bytes([0x03, 0x22, 0xF1, 0x95, 0xCC, 0xCC, 0xCC, 0xCC]))
        uds_got = False
        t0 = time.time()
        while (time.time() - t0) < 1.5:
            for cid, d in self.pump_frames():
                if cid == UDS_RESP_ID:
                    uds_got = True
                    break
            if uds_got:
                break
        if uds_got:
            _log("WARN: 设备有 UDS 响应，可能未真正进 Sleep")
            enter_ok = False
            detail += "not_in_sleep;"
        else:
            _log("无 UDS 响应 — 已进 Sleep ✓")
            detail += "sleep_ok;"

        # 2. 唤醒（已过 inhibit 窗口，直接发）
        _log("发送唤醒帧...")
        self.send_wake()
        wake_src = self.wait_wake(5.0)

        if wake_src is None:
            _log("首次唤醒未检测到，重发唤醒帧...")
            self.send_wake()
            wake_src = self.wait_wake(5.0)

        if wake_src is None:
            _log("FAIL: 未收到唤醒标识")
            return (enter_ok, False, detail + "no_wake;")

        _log("唤醒成功（%s）" % wake_src)

        # 3. 读 DID 0x2119 确认状态
        time.sleep(0.2)
        lp = self.read_did_2119(2.0)
        if lp is not None:
            flags2119, wup_cnt, last_sec = lp
            _log("DID 0x2119: flags=0x%02X wup_cnt=%d last_sleep=%ds"
                 % (flags2119, wup_cnt, last_sec))
        else:
            _log("WARN: DID 0x2119 读失败")

        return (enter_ok, True, detail)

    def run(self, cycles):
        self.open()
        try:
            for i in range(1, cycles + 1):
                enter_ok, wake_ok, detail = self.run_cycle(i)
                self.results.append((i, enter_ok, wake_ok, detail))
                if i < cycles:
                    _log("冷却 3s...")
                    time.sleep(3)
        finally:
            self.close()

        # 汇总
        _log("==== 测试汇总 ====")
        n_enter = sum(1 for _, e, _, _ in self.results if e)
        n_wake = sum(1 for _, _, w, _ in self.results if w)
        n_both = sum(1 for _, e, w, _ in self.results if e and w)
        for i, e, w, d in self.results:
            status = "PASS" if (e and w) else ("ENTER_ONLY" if e else ("WAKE_ONLY" if w else "FAIL"))
            _log("  周期 %d: %s %s" % (i, status, d))
        _log("  进入成功: %d/%d  唤醒成功: %d/%d  双成功: %d/%d"
             % (n_enter, cycles, n_wake, cycles, n_both, cycles))
        return n_both == cycles


def main():
    ap = argparse.ArgumentParser(description="低功耗 Sleep 进入/唤醒稳定性测试")
    ap.add_argument("--port", default="/dev/ttyACM0")
    ap.add_argument("--cycles", type=int, default=3, help="测试周期数，默认 3")
    ap.add_argument("--idle", type=float, default=35,
                    help="空闲超时秒数（CAN_LP_IDLE_TIMEOUT_MS=30s，默认 35s 留余量）")
    ap.add_argument("--no-wait-idle", action="store_true",
                    help="不等空闲超时，直接监测（设备可能已在 Sleep）")
    args = ap.parse_args()

    t = LPTest(args.port, args.idle, args.no_wait_idle)
    ok = t.run(args.cycles)
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
