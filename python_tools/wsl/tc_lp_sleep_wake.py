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

# 唤醒标识帧：01 41 57 4B = "AWK"
WK_MAGIC = bytes([0x01, 0x41, 0x57, 0x4B])
# 生命周期 shutdown 标记：06 41 53 42 = "ASB" (A+SB)
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

    def pump_for(self, duration_s, on_frame=None):
        """轮询 duration_s 秒，每帧回调 on_frame(can_id, data)。
        返回收集到的帧列表。"""
        frames = []
        t0 = time.time()
        while (time.time() - t0) < duration_s:
            for cid, d in self.z.pump():
                frames.append((cid, d, time.time()))
                if on_frame:
                    on_frame(cid, d)
        return frames

    def send_wake(self):
        """发一帧 CAN 唤醒（任意 250kbps 帧触发 WUP）。"""
        self.z.send_can(UDS_REQ_ID, bytes([0x3E, 0x00, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC]))
        _log("发送唤醒帧 3E 00")

    def read_did_2119(self, timeout_s=2.0):
        """读 DID 0x2119（SIT1145 LP 状态），返回 (flags, wup_cnt, last_sec) 或 None。"""
        self.z.send_can(UDS_REQ_ID, DID_LP_STATUS_REQ)
        t0 = time.time()
        while (time.time() - t0) < timeout_s:
            for cid, d in self.z.pump():
                if cid == UDS_RESP_ID and len(d) >= 6 and d[0] == 0x62 and d[1] == 0x21 and d[2] == 0x19:
                    flags = d[3]
                    wup_cnt = d[4]
                    last_sec = (d[5] << 8) | d[6] if len(d) >= 7 else 0
                    return (flags, wup_cnt, last_sec)
        return None

    def wait_sleep_diag(self, timeout_s):
        """等 Sleep 进入诊断帧（0x18FF270D, E0 标记）。返回 diag payload 或 None。"""
        _log("等待 Sleep 进入诊断帧（0x18FF270D E0）...")
        t0 = time.time()
        while (time.time() - t0) < timeout_s:
            for cid, d in self.z.pump():
                if cid == LP_DIAG_ID and len(d) >= 8 and d[0] == 0xE0:
                    _log("收到 Sleep 诊断帧: %s" % _hex(d))
                    return d
        return None

    def wait_wake(self, timeout_s):
        """等唤醒标识帧（WK 生命周期帧或 UDS 响应）。返回 src 或 None。"""
        _log("等待唤醒标识（WK 生命周期帧 / UDS 响应）...")
        t0 = time.time()
        while (time.time() - t0) < timeout_s:
            for cid, d in self.z.pump():
                if cid == LIFE_ID and len(d) >= 4 and bytes(d[:4]) == WK_MAGIC:
                    _log("收到 WK 唤醒帧: %s" % _hex(d))
                    return "WK"
                if cid == UDS_RESP_ID:
                    _log("收到 UDS 响应: %s" % _hex(d))
                    return "UDS"
        return None

    def run_cycle(self, cycle_num):
        """单次进入/唤醒测试。返回 (enter_ok, wake_ok, detail)。"""
        _log("==== 周期 %d ====" % cycle_num)

        # 1. 等待/确认进入 Sleep
        if self.no_wait_idle:
            # 不等空闲超时，直接发 3E 保活然后等诊断帧
            # （设备可能已经过了 idle timeout 在 Sleep）
            _log("跳过空闲等待，直接监测 Sleep 诊断帧（最多 %ds）" % int(self.idle_s + 5))
            diag = self.wait_sleep_diag(self.idle_s + 5)
        else:
            _log("等待 %ds 空闲超时自动进 Sleep..." % int(self.idle_s))
            # 先发一帧保活确认设备在线
            self.send_wake()
            time.sleep(0.1)
            diag = self.wait_sleep_diag(self.idle_s + 10)

        if diag is None:
            _log("FAIL: 未收到 Sleep 进入诊断帧")
            return (False, False, "no_sleep_diag")

        # 解析诊断帧：[0]=E0 [1]=MODE [2]=EVENT_EN [3]=EV24 [4]=CAN_CTRL [5]=EV63 [6]=flags [7]=A5
        mode = diag[1]
        ev_en = diag[2]
        ev24 = diag[3]
        can_ctrl = diag[4]
        ev63 = diag[5]
        flags = diag[6]
        _log("诊断: MODE=0x%02X EVENT_EN=0x%02X EV24=0x%02X CAN_CTRL=0x%02X EV63=0x%02X flags=0x%02X"
             % (mode, ev_en, ev24, can_ctrl, ev63, flags))

        # 验证 Sleep 进入
        enter_ok = True
        detail = ""
        if mode != 0x01:
            _log("WARN: MODE=0x%02X ≠ 0x01（Sleep）" % mode)
            enter_ok = False
            detail += "mode!=01;"
        if (ev_en & 0x01) == 0:
            _log("WARN: CWE=0（CAN 唤醒未使能）")
            enter_ok = False
            detail += "cwe=0;"
        if (flags & 0x01) != 0:
            _log("WARN: g_lp_sleep_fail=1（进 Sleep 前 CAN Control 回读失败）")
            detail += "sleep_fail;"

        # 2. 唤醒
        _log("发送唤醒帧...")
        self.send_wake()
        wake_src = self.wait_wake(3.0)

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
