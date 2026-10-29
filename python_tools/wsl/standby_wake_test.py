#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Standby 唤醒复现测试：开充电 → 1s 后关充电 → 等 40s → 读版本。

用法（仓库根目录）:
  python3 "python_tools/wsl/standby_wake_test.py"

流程:
  1. 唤醒 + 探测 APP 在线
  2. 进扩展会话 + SA 解锁 + 2E 21 01 01（开充电）
  3. 等 1s
  4. 2E 21 01 00（关充电）
  5. 等 40s（30s Standby 超时 + 10s 余量）
  6. 3E 00 唤醒 burst + 读 22 F195 版本
  7. 判定：PASS=读到版本 / FAIL=无应答
"""
from __future__ import print_function
import os, sys, time, re

HERE = os.path.dirname(os.path.abspath(__file__))
PT = os.path.dirname(HERE)
ZCANPRO_NONQI = os.path.join(PT, "zcanpro", "non-qi")
sys.path.insert(0, HERE)
sys.path.insert(0, ZCANPRO_NONQI)

import zcanpro_shim_zqwl as shim
sys.modules["zcanpro"] = shim

import zcanpro_ext_ota_auto as ext

BUS = shim.get_buses()[0]["busID"]


def log(msg):
    print("[%s] %s" % (time.strftime("%H:%M:%S"), msg))
    sys.stdout.flush()


def wake_and_probe():
    """唤醒 + 22 2113 探测 APP 在线。"""
    log("---- 唤醒 + 探测 ----")
    ext.uds_init()
    # 用 shim 的 raw 设备发唤醒 burst
    dev = getattr(shim, '_dev', None)
    if dev:
        for i in range(8):
            dev.send_can(0x18DA0D03,
                         bytes([0x02, 0x3E, 0x00]),
                         ext=True)
            log("[Tx] raw 3E 00 (DLC=3) (%d/8)" % (i + 1))
            time.sleep(0.25)
        t0 = time.time()
        while time.time() - t0 < 0.5:
            for item in dev.pump():
                pass
            time.sleep(0.02)
    else:
        log("shim._dev 为 None，用 _raw_send 发 DLC=3 唤醒帧")
        for i in range(8):
            try:
                ext._raw_send(BUS, 0x18DA0D03, [0x02, 0x3E, 0x00])
                log("[Tx] raw 3E 00 (DLC=3) (%d/8)" % (i + 1))
            except Exception:
                pass
            time.sleep(0.25)
    time.sleep(0.5)
    for i in range(8):
        try:
            rx = ext.uds_req(BUS, 0x22, [0x21, 0x13])
            log("探测 22 2113 成功 slot=%s" % ext._hex((rx or [])[3:4]))
            return True
        except ext.SafeModeError as e:
            log("SafeMode: %s" % e)
            return False
        except Exception as e:
            log("探测 %d/8: %s" % (i + 1, e))
            time.sleep(0.5)
    return False


def do_charge_enable():
    """10 03 → 27 01/02 → 2E 21 01 01（开充电）。"""
    log("---- 开充电 ----")
    ext.uds_req(BUS, 0x10, [0x03])
    log("10 03 扩展会话 OK")
    # SA 解锁
    priv = ext.load_ec_private_key(ext.PRIVATE_KEY_PATH)
    rx = ext.uds_req(BUS, 0x27, [0x01])
    seed = ext._to_bytes(rx[2:34])
    if seed == b"\x00" * 32:
        log("已解锁 (seed=0)")
    else:
        sig = ext.ecdsa_sign_msg(priv, seed)
        ext.send_security_key(BUS, sig, seed=seed, priv=priv)
        log("SA 解锁 OK")
    # 开充电
    ext.uds_req(BUS, 0x2E, [0x21, 0x01, 0x01])
    log("2E 21 01 01 充电已开启")


def do_charge_disable():
    """2E 21 01 00（关充电）。会话应仍保持扩展会话+已解锁。"""
    log("---- 关充电 ----")
    ext.uds_req(BUS, 0x2E, [0x21, 0x01, 0x00])
    log("2E 21 01 00 充电已关闭")


def read_version():
    """3E 00 唤醒 burst + 22 F195 读版本。"""
    log("---- 读版本号 ----")
    # 唤醒 burst（DLC=3 裸帧，WUF 严格匹配）
    dev = getattr(shim, '_dev', None)
    if dev:
        for i in range(8):
            dev.send_can(0x18DA0D03,
                         bytes([0x02, 0x3E, 0x00]),
                         ext=True)
            log("[Tx] raw 3E 00 (DLC=3) (%d/8)" % (i + 1))
            time.sleep(0.25)
        t0 = time.time()
        while time.time() - t0 < 0.5:
            for item in dev.pump():
                pass
            time.sleep(0.02)
    else:
        for i in range(8):
            try:
                ext._raw_send(BUS, 0x18DA0D03, [0x02, 0x3E, 0x00])
            except Exception:
                pass
            time.sleep(0.25)
    time.sleep(1.0)
    # 读版本（等设备完全唤醒）
    time.sleep(1.0)
    for i in range(8):
        try:
            rx = ext.uds_req(BUS, 0x22, [0xF1, 0x95])
            raw = bytes(int(b) & 0xFF for b in rx[3:35])
            m = re.search(rb"QC_JYF_MCU1_FW_[0-9A-Za-z._]+", raw)
            ver = m.group(0).decode("ascii") if m else raw.split(b"\x00")[0].decode("ascii", "ignore").strip()
            log("版本 = %s" % ver)
            return ver
        except Exception as e:
            log("读版本 %d/8: %s" % (i + 1, e))
            time.sleep(1.0)
    return None


def main():
    log("======== Standby 唤醒复现测试 ========")
    log("流程: 开充电 → 1s 后关充电 → 等 40s → 读版本")

    try:
        # Step 1: 唤醒 + 探测
        if not wake_and_probe():
            log("FAIL: 设备无应答，无法开始测试")
            return 1

        # Step 2: 开充电
        do_charge_enable()

        # Step 3: 等 1s
        log("等待 1s ...")
        time.sleep(1.0)

        # Step 4: 关充电
        do_charge_disable()

        # Step 5: 等 40s
        log("等待 40s（30s Standby 超时 + 10s 余量）...")
        for i in range(40):
            time.sleep(1.0)
            if (i + 1) % 10 == 0:
                log("  已等待 %ds" % (i + 1))

        # Step 6: 读版本
        ver = read_version()

        # Step 7: 判定
        log("")
        if ver:
            log("======== PASS ========")
            log("设备从 Standby 唤醒成功，版本 = %s" % ver)
            return 0
        else:
            log("======== FAIL ========")
            log("设备无应答——Standby 唤醒失败（复现了问题）")
            return 1
    except Exception as e:
        log("测试异常: %s" % e)
        import traceback
        traceback.print_exc()
        return 1
    finally:
        try:
            shim.uds_deinit()
        except Exception:
            pass


if __name__ == "__main__":
    sys.exit(main())
