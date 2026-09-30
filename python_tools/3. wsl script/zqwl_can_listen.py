#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ZQWL-USBCANFD (VID 3562:0101) CDC 收包 + Boot M1–M4 监听。

用法见仓库 AGENTS.md「WSL2 串口 CAN 监听」。
  python3 "python_tools/3. wsl script/zqwl_can_listen.py" --port /dev/ttyACM0
"""
from __future__ import print_function
import argparse
import os
import sys
import time
import serial

HDR = b"\x49\x3B"
FTR = b"\x45\x2E"
CAN_HDR = 0x5A
CAN_FTR = 0xA5

# IDs of interest for Boot TC
WATCH = {
    0x18FF480D: "BOOT-M",
    0x18FF260D: "LIFE",
    0x18DA030D: "UDS-RX",
    0x18DA0D03: "UDS-TX",
    0x18DB33F1: "FUNC",
}


def cfg(func, rw, payload=b""):
    d = bytearray(16)
    d[: len(payload)] = payload[:16]
    return HDR + bytes([func, rw]) + bytes(d) + FTR


def decode_boot(can_id, data):
    if can_id != 0x18FF480D or not data:
        if can_id == 0x18FF260D and len(data) >= 8:
            if data[0:4] == bytes([0x01, 0x41, 0x42, 0x54]):
                return "Safe-mode heartbeat cause=%02X fail_step=%02X" % (data[4], data[5])
            return "lifecycle %s" % data[:4].hex(" ")
        return ""
    b0 = data[0]
    if b0 == 0xA1:
        return "M1 src=%d magic=%d ver=%d crc=%d" % (
            data[1], data[2], data[3], data[4])
    if b0 == 0xA2:
        return "M2 result=%02X detail=%02X" % (data[1], data[2])
    if b0 == 0xA3:
        return "M3 pass=%02X fail_step=%02X target=%02X" % (data[1], data[2], data[3])
    if b0 == 0xA4:
        return "M4 jump %02X%02X%02X%02X" % (data[4], data[3], data[2], data[1])
    return ""


class Zqwl:
    def __init__(self, port="/dev/ttyACM0", baud=1000000):
        self.ser = serial.Serial(port, baud, timeout=0.05)
        self.buf = bytearray()

    def close(self):
        try:
            self.ser.close()
        except Exception:
            pass

    def write(self, pkt):
        self.ser.write(pkt)
        self.ser.flush()

    def read_device(self):
        self.write(cfg(0x40, 0x52))
        time.sleep(0.2)
        raw = self.ser.read(256)
        return raw

    def open_can0_250k(self):
        # 42 W: ch=0, 常用波特率表，仲裁 250k(4) / 数据 500k(5) => 0x45
        self.write(cfg(0x42, 0x57, bytes([0x00, 0x00, 0x45])))
        time.sleep(0.05)
        # 43 W: 组0 扩展帧全收（id/mask=0 表示不比对）
        self.write(cfg(0x43, 0x57, bytes([
            0x00, 0x00, 0x01, 0x01,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00])))
        time.sleep(0.05)
        # 43 W: 组1 标准帧全收
        self.write(cfg(0x43, 0x57, bytes([
            0x00, 0x01, 0x01, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00])))
        time.sleep(0.05)
        # 44 W: 不写 flash、不复位、打开 CAN0
        self.write(cfg(0x44, 0x57, bytes([0x00, 0x00, 0x01, 0x00])))
        time.sleep(0.15)

    def send_can(self, can_id, data, ext=True):
        data = bytes(data)
        dlc = min(len(data), 8)
        b1 = dlc & 0x7F  # ch0 lsb=0
        b2 = 0x04 if ext else 0x00  # bit2 extended, data frame, normal send
        cid = can_id & 0x1FFFFFFF
        idb = bytes([
            (cid >> 24) & 0x7F,
            (cid >> 16) & 0xFF,
            (cid >> 8) & 0xFF,
            cid & 0xFF,
        ])
        pkt = bytes([CAN_HDR, b1, b2]) + idb + data[:dlc] + bytes([CAN_FTR])
        self.write(pkt)

    def pump(self):
        chunk = self.ser.read(512)
        if chunk:
            self.buf.extend(chunk)
        out = []
        while True:
            if len(self.buf) < 2:
                break
            # heartbeat / cfg replies start with 49 3B
            if self.buf[0] == 0x49 and len(self.buf) >= 2 and self.buf[1] == 0x3B:
                if len(self.buf) < 22:
                    break
                out.append(("cfg", bytes(self.buf[:22])))
                del self.buf[:22]
                continue
            if self.buf[0] != CAN_HDR:
                del self.buf[0]
                continue
            if len(self.buf) < 8:
                break
            b1 = self.buf[1]
            if b1 in (0xFF, 0xFE):
                # heartbeat 17 (1ch/2ch) or 32 (4ch)
                n = 17 if b1 == 0xFF else 32
                if len(self.buf) < n:
                    break
                out.append(("hb", bytes(self.buf[:n])))
                del self.buf[:n]
                continue
            dlc = b1 & 0x7F
            if dlc > 64:
                del self.buf[0]
                continue
            total = 3 + 4 + dlc + 1
            if len(self.buf) < total:
                break
            frame = bytes(self.buf[:total])
            del self.buf[:total]
            if frame[-1] != CAN_FTR:
                continue
            b2 = frame[2]
            ext = bool(b2 & 0x04)
            cid = ((frame[3] & 0x7F) << 24) | (frame[4] << 16) | (frame[5] << 8) | frame[6]
            data = frame[7:7 + dlc]
            out.append(("can", cid, ext, data))
        return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", default="/dev/ttyACM0")
    ap.add_argument("--log", default="/tmp/can_boot_listen.log")
    ap.add_argument("--event", default="/tmp/can_boot_events.log")
    args = ap.parse_args()

    dev = Zqwl(args.port)
    info = dev.read_device()
    sys.stderr.write("device_info_raw %d %s\n" % (len(info), info[:48].hex(" ")))
    sys.stderr.flush()
    dev.open_can0_250k()
    sys.stderr.write("CAN0 250kbps opened, listening\n")
    sys.stderr.flush()

    t0 = time.time()
    n_can = 0
    with open(args.log, "a") as log, open(args.event, "a") as ev:
        log.write("\n===== start %.3f =====\n" % time.time())
        log.flush()
        ev.write("LISTENING t=%.3f\n" % time.time())
        ev.flush()
        last_ev = 0
        while True:
            for item in dev.pump():
                now = time.time() - t0
                if item[0] != "can":
                    continue
                _, cid, ext, data = item
                n_can += 1
                tag = WATCH.get(cid, "")
                line = "%8.3f  %08X%s  %s  %s" % (
                    now, cid, "x" if ext else " ",
                    data.hex(" ").upper(), tag)
                log.write(line + "\n")
                log.flush()
                note = decode_boot(cid, data)
                interesting = tag or note
                if interesting:
                    ev.write(line + (("  " + note) if note else "") + "\n")
                    ev.flush()
                    # wake parent: print one line for Boot markers
                    if cid in (0x18FF480D, 0x18FF260D) or (cid == 0x18DA030D):
                        sys.stdout.write(line + (("  " + note) if note else "") + "\n")
                        sys.stdout.flush()
            # 不 sleep：M1/M2/M4 间隔 <2ms，10ms 空转会把适配器 FIFO 里的 M2/M4 挤掉


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
