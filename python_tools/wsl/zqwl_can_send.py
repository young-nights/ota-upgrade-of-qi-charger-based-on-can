#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ZQWL-USBCANFD (VID 3562:0101) 单帧发送 + 窗口抓包。

与 zqwl_can_listen.py 共用 Zqwl 协议实现；同一 /dev/ttyACM0 同一时刻只能被一个
进程打开，需要"边发边抓"时用本脚本的 --wait 窗口，不要与 zqwl_can_listen.py 并发。

用法（在仓库根目录执行）:
  # 读 APP 版本（DID 0xF195，ISO-TP 多帧自动回 FC）
  python3 "python_tools/wsl/zqwl_can_send.py" --id 18DA0D03 --data "03 22 F1 95 CC CC CC CC" --wait 2
  # Safe mode 探测（裸帧 22 21 13，TC-B004）
  python3 "python_tools/wsl/zqwl_can_send.py" --id 18DA0D03 --data "22 21 13" --wait 2
  # 只抓不发（如 Safe mode 心跳计时，TC-B006）
  python3 "python_tools/wsl/zqwl_can_send.py" --no-send --wait 3
"""
from __future__ import print_function
import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from zqwl_can_listen import Zqwl, WATCH, decode_boot  # noqa: E402

# ISO-TP 流控：CTS / BS=0 / STmin=0，DLC=8 余量 CC（与协议文档一致）
FC_FRAME = bytes([0x30, 0x00, 0x00, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC])


def parse_hex(s):
    return bytes(int(x, 16) for x in s.replace(",", " ").split())


def main():
    ap = argparse.ArgumentParser(description="ZQWL-CANFD 单帧发送 + 窗口抓包")
    ap.add_argument("--port", default="/dev/ttyACM0")
    ap.add_argument("--id", default="18DA0D03",
                    help="发送 CAN ID（hex），默认 UDS 请求 0x18DA0D03")
    ap.add_argument("--data", help="数据 hex，如 '03 22 F1 95 CC CC CC CC'")
    ap.add_argument("--no-send", action="store_true", help="只开通道抓包，不发送")
    ap.add_argument("--wait", type=float, default=2.0,
                    help="抓包窗口秒数（发送后开始计），默认 2")
    ap.add_argument("--repeat", type=int, default=1, help="发送次数，默认 1")
    ap.add_argument("--interval", type=float, default=0.5,
                    help="多次发送间隔秒数，默认 0.5")
    ap.add_argument("--std", action="store_true",
                    help="发送标准帧（默认 29-bit 扩展帧）")
    args = ap.parse_args()

    if not args.no_send and not args.data:
        ap.error("--data 必填（或加 --no-send 只抓包）")

    cid = int(args.id, 16)
    data = parse_hex(args.data) if args.data else b""
    ext = not args.std

    dev = Zqwl(args.port)
    info = dev.read_device()
    sys.stderr.write("device_info_raw %d %s\n" % (len(info), info[:48].hex(" ")))
    dev.open_can0_250k()
    sys.stderr.write("CAN0 250kbps opened\n")

    if not args.no_send:
        for i in range(args.repeat):
            dev.send_can(cid, data, ext=ext)
            sys.stderr.write("TX #%d %08X  %s\n" % (i + 1, cid, data.hex(" ").upper()))
            if i + 1 < args.repeat:
                time.sleep(args.interval)

    t0 = time.time()
    n = 0
    fc_sent = 0
    while time.time() - t0 < args.wait:
        for item in dev.pump():
            if item[0] != "can":
                continue
            _, rcid, rext, rdata = item
            n += 1
            tag = WATCH.get(rcid, "")
            note = decode_boot(rcid, rdata)
            line = "%8.3f  %08X%s  %s  %s%s" % (
                time.time() - t0, rcid, "x" if rext else " ",
                rdata.hex(" ").upper(), tag,
                ("  " + note) if note else "")
            print(line)
            sys.stdout.flush()
            # ISO-TP 首帧（PCI 1x）自动回 FC，方向 = 发送 CAN ID
            if rdata and (rdata[0] >> 4) == 0x1 and fc_sent < 8:
                dev.send_can(cid, FC_FRAME, ext=ext)
                fc_sent += 1
                sys.stderr.write("FC -> %08X\n" % cid)
    dev.close()
    sys.stderr.write("captured %d frames in %.1fs, fc sent %d\n" % (n, args.wait, fc_sent))


if __name__ == "__main__":
    main()
