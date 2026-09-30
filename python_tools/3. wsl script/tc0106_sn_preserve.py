#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""TC-0106 SN 保留验证测试 —— WSL2 独立执行。

用例 TC-0106（P2）：SN 已写入前置下，执行完整 OTA 升级，复位后读
DID 0xF18C（SN）与 0xF195（SW 版本），断言：
  ① SN == OTA 前记录值（严格逐字节，含尾部填充/空格差异都如实打印）；
  ② 复位后 0xF195 == 载荷版本（判定闭环）；
  ③ 复位后 0x2113 == 0x00（App 区）。
任一断言不满足 → FAIL 并打印前后对照值；全过 → RESULT: PASS。

前置门禁：OTA 前先读 22 F18C，SN 为空/全 0/全 0xFF/全空格/读取失败
= 前置不满足 → FAIL 退出。本脚本绝不写 SN（用例只验证保留）。

流程：
  OTA 前 22 F18C 记录 SN（hex+ASCII 打印）→ 22 F195 记录版本基线
  → 完整 OTA（10 02 → 27 01/02 签名解锁 → 2E 20 10 01 → 31 01 FF 00
  → 34 → 36 全块 → 37 → 等自复位，45s 窗口）
  → 复位后 22 F18C / 22 F195 / 0x2113 → 三断言判定 → RESULT PASS/FAIL。

用法（仓库任意位置）：
  python3 "python_tools/3. wsl script/tc0106_sn_preserve.py" [--firmware "路径.bin"]
  默认载荷：python_tools/app bin/app_image_v1_1_2.bin（设备当前 1.1.1，本次刷 1.1.2）
退出码：0=PASS，1=FAIL。

实现：sys.modules['zcanpro'] 注入 zcanpro_shim_zqwl（Zqwl 串口底层，
uds_request 含 ISO-TP FF/CF 组装与 NRC78 消化），复用 zcanpro_ext_ota_auto
的 uds_req/签名/擦除/复位确认函数；不修改任何现有文件。
"""
from __future__ import print_function

import argparse
import os
import re
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
# 本脚本已移入 python_tools/3. wsl script/：同级 shim 用 HERE，
# 上一级 python_tools/（zcanpro_ext_ota_auto 与 app bin）用 PT。
PT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
sys.path.insert(0, PT)

import zcanpro_shim_zqwl as shim  # noqa: E402
sys.modules["zcanpro"] = shim
import zcanpro_ext_ota_auto as ext  # noqa: E402

DEFAULT_FIRMWARE = os.path.join(PT, "app bin", "app_image_v1_1_2.bin")

TRANSFER_BLOCK = 128    # 36 每块字节数（≤0x400，与 ext/tc0104 一致）

# SN 前置门禁：这些形态视为"未写入/无效"
SN_NOT_WRITTEN_STATES = ("empty", "all_zero", "all_ff", "all_space")


class TestFail(RuntimeError):
    """TC-0106 判定 FAIL（带失败步骤标签）。"""


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
    """ext.uds_req 包装：NRC/异常归入当前步骤标签。返回正响应 payload 列表。"""
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


# ---------------------------------------------------------------- SN 读取/展示

def read_sn(step):
    """22 F18C → SN 原始字节列表（rx[3:]，多帧响应由 shim 组装）。

    读取异常/NRC 一律抛 TestFail（对 SN 而言 = 前置不满足，见 run 流程
    对 precondition 的捕获处理）。
    """
    rx = req(step, 0x22, [0xF1, 0x8C])
    if len(rx) < 3:
        _fail(step, "22 F18C 响应过短: %s" % ext._hex(rx))
    return [int(b) & 0xFF for b in rx[3:]]


def sn_state(sn):
    """SN 形态分类：返回 None（有效）或 SN_NOT_WRITTEN_STATES 之一。"""
    if not sn:
        return "empty"
    if all(b == 0x00 for b in sn):
        return "all_zero"
    if all(b == 0xFF for b in sn):
        return "all_ff"
    if all(b in (0x20, 0x00) for b in sn):   # 全空格（含 0x00 混充）= 未写入
        return "all_space"
    return None


def fmt_sn(sn):
    """SN → 'hex | ASCII' 单行展示（ASCII 不可见字节显示 '.'，保留尾部填充）。"""
    hx = " ".join("%02X" % b for b in sn)
    asc = "".join(chr(b) if 0x20 <= b < 0x7F else "." for b in sn)
    return "%s | %s（%d 字节）" % (hx, asc, len(sn))


def read_version(step):
    """22 F195 → ASCII 版本串（QC_JYF_FW_x.y.z）；读失败抛 TestFail。"""
    rx = req(step, 0x22, [0xF1, 0x95])
    raw = bytes(int(b) & 0xFF for b in rx[3:]) if len(rx) > 3 else b""
    m = re.search(rb"QC_JYF_FW_[0-9A-Za-z._]+", raw)
    if m:
        return m.group(0).decode("ascii")
    return raw.split(b"\x00")[0].decode("ascii", "ignore").strip() or None


def diff_bytes(before, after):
    """逐字节差异明细（含长度差）——SN 比对失败时打印证据。"""
    lines = []
    if len(before) != len(after):
        lines.append("长度不同: OTA 前 %d 字节 vs OTA 后 %d 字节"
                     % (len(before), len(after)))
    n = max(len(before), len(after))
    diffs = []
    for i in range(n):
        b = before[i] if i < len(before) else None
        a = after[i] if i < len(after) else None
        if b != a:
            diffs.append("  [%02d] 前=%s 后=%s"
                         % (i, "%02X" % b if b is not None else "--",
                            "%02X" % a if a is not None else "--"))
    if diffs:
        lines.append("差异字节 %d 处:" % len(diffs))
        lines.extend(diffs)
    else:
        lines.append("逐字节内容一致（长度差异见上）")
    return "\n".join(lines)


# ---------------------------------------------------------------- 主流程

def run_tc0106(fw_path):
    global BUS
    # ---- 1. 载荷与签名准备（不触碰硬件）----
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

    # ---- 2. 打开通道 ----
    ext.uds_init()
    buses = shim.get_buses()
    if not buses:
        raise TestFail("无 CAN 总线（Zqwl 通道打开失败）")
    BUS = buses[0]["busID"]

    # ---- 3. OTA 前基线：运行侧探测 + 读 SN + 读版本（前置门禁在这里判）----
    _log("======== OTA 前基线采集 ========")
    # Step 0 运行侧探测（与 zcanpro_sn_read.py 同构）：区分
    # APP / Boot safe mode / 无应答，避免把 Boot safe mode 的
    # "其他 22 DID 回 0x11" 或无应答误读成 SN 判据
    try:
        in_app = ext.probe_in_app(BUS)
    except ext.SafeModeError as e:
        _fail("前置门禁", "设备在 Boot safe mode，无法读 SN（前置不满足，不写 SN）: %s" % e)
    except Exception as e:
        _fail("前置门禁", "运行侧探测异常（前置不满足）: %s" % e)
    if not in_app:
        _fail("前置门禁", "运行侧探测无正证据（不在 APP，前置不满足，不写 SN）")
    try:
        sn_before = read_sn("OTA前22F18C读SN")
    except TestFail as e:
        detail = str(e)
        if "7F 22 31" in detail:
            detail += (" —— 固件判据：22 F18C 回 NRC 0x31 requestOutOfRange = "
                       "device_info_read 失败（magic/version/CRC32 不符，"
                       "device_info.c:57-79 → can_protocol.c:994）→ SN 未写入/"
                       "Device Info 块无效；本脚本绝不写 SN，请先用 "
                       "python_tools/2.functional test script/zcanpro_sn_write.py "
                       "写入 SN 后重跑本用例")
        _fail("前置门禁", "SN 读取失败=前置不满足（不写 SN，直接 FAIL）: %s" % detail)
    _log("OTA 前 SN(F18C): %s" % fmt_sn(sn_before))
    state = sn_state(sn_before)
    if state is not None:
        _log("[前置门禁] SN 形态=%s → 未写入/空，OTA 升级不执行（本脚本绝不写 SN）"
             % state)
        _fail("前置门禁", "SN 未写入（形态=%s，hex+ASCII 见上行）——"
                          "请先用 zcanpro_sn_write.py 写入 SN 再跑本用例" % state)
    baseline_ver = read_version("OTA前22F195读版本基线")
    _log("OTA 前版本基线 0xF195 = %s（载荷目标 %s）" % (baseline_ver, expect_ver))
    if baseline_ver == expect_ver:
        _log("提示：设备当前版本已等于目标版本，本次仍完整执行 OTA 链路"
             "（版本断言按复位后实测值判定）")

    # ---- 4. 完整 OTA 流程：10 02 → 27 → 2E → 31 → 34 → 36 全块 → 37 ----
    _log("======== 完整 OTA 流程开始 ========")
    req("10_02进编程会话", 0x10, [0x02])

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
        _log("---- 步骤[27_02送密钥] ----")
        try:
            ext.send_security_key(BUS, sig, seed=seed, priv=priv)
        except ext.UdsNrcError as e:
            _fail("27_02送密钥", "NRC 7F %02X %02X" % (e.sid, e.nrc))
        except Exception as e:
            _fail("27_02送密钥", str(e))

    req("2E_20_10_01写DID", 0x2E, [0x20, 0x10, 0x01])

    _log("---- 步骤[31_01_FF_00擦除Backup区] ----")
    try:
        ext._erase_with_retry(BUS)
    except ext.UdsNrcError as e:
        _fail("31擦除", "NRC 7F %02X %02X" % (e.sid, e.nrc))
    except TestFail:
        raise
    except Exception as e:
        _fail("31擦除", str(e))

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

    _log("---- 步骤[37_TransferExit提交] ----")
    try:
        ext.uds_req(BUS, 0x37, [], wait_pending_s=45)
        _log("[人话] 37 已提交，设备自复位后 BOOT 搬运 Backup→App")
    except ext.UdsNrcError as e:
        _log("37 NRC 0x%02X —— 交由复位后判定闭环裁决" % e.nrc)
    except Exception as e:
        _log("37 应答异常（自复位常致应答丢失）—— 交由复位后判定闭环裁决: %s" % e)

    # ---- 5. 等自复位（45s 窗口）----
    time.sleep(2.0)
    _log("---- 步骤[复位后等APP起来（45s窗口）] ----")
    try:
        to_slot, _got_ver, _ota_status = ext.confirm_app_after_reset(BUS)
    except Exception as e:
        _fail("复位后等APP", str(e))

    # ---- 6. 复位后读 SN + 版本（三断言判定）----
    _log("======== 复位后基线采集与三断言判定 ========")
    sn_after = read_sn("复位后22F18C读SN")
    _log("复位后 SN(F18C): %s" % fmt_sn(sn_after))
    ver_after = read_version("复位后22F195读版本")
    _log("复位后版本 0xF195 = %s（载荷目标 %s）" % (ver_after, expect_ver))
    _log("复位后 0x2113 slot = %s" %
         ("0x%02X" % (to_slot & 0xFF) if to_slot is not None else "读取失败"))

    problems = []
    # 断言①：SN 逐字节一致
    if len(sn_before) != len(sn_after) or sn_before != sn_after:
        problems.append("SN 逐字节不一致（前后对照见下）")
        _log("[SN 对照] OTA 前: %s" % fmt_sn(sn_before))
        _log("[SN 对照] OTA 后: %s" % fmt_sn(sn_after))
        _log("[SN 对照] %s" % diff_bytes(sn_before, sn_after))
    else:
        _log("断言① SN 逐字节一致（%d 字节，含尾部填充）" % len(sn_before))
    # 断言②：版本 = 载荷版本
    if ver_after != expect_ver:
        problems.append("0xF195=%s ≠ 载荷版本 %s（OTA 前基线 %s）"
                        % (ver_after or "未读到", expect_ver, baseline_ver))
    else:
        _log("断言② 0xF195=%s == 载荷版本（OTA 前基线 %s）"
             % (ver_after, baseline_ver))
    # 断言③：App 区
    if to_slot is None:
        problems.append("0x2113 读取失败")
    elif to_slot != 0x00:
        problems.append("0x2113=0x%02X ≠ 0x00" % (to_slot & 0xFF))
    else:
        _log("断言③ 0x2113=0x00（App 区）")

    if problems:
        _fail("三断言判定", "；".join(problems))
    _log("三断言全部满足：① SN==OTA 前；② 0xF195==载荷版本；③ 0x2113=0x00")


def main():
    parser = argparse.ArgumentParser(description="TC-0106 SN 保留验证测试")
    parser.add_argument("--firmware", default=DEFAULT_FIRMWARE,
                        help="OTA 载荷 bin（默认 app bin/app_image_v1_1_2.bin）")
    args = parser.parse_args()
    fw = args.firmware
    if not os.path.isabs(fw):
        fw = os.path.abspath(os.path.join(os.getcwd(), fw))
        if not os.path.isfile(fw):
            fw = os.path.abspath(os.path.join(HERE, args.firmware))
    _log("======== TC-0106 SN 保留验证测试 ========")
    _log("判定：OTA 前后 SN(F18C) 逐字节一致 + 复位后 F195==载荷版本 + 0x2113=0x00")
    t0 = time.time()
    try:
        run_tc0106(fw)
    except TestFail as e:
        _log("TC-0106 RESULT: FAIL —— %s" % e)
        _log("失败步骤: %s（总耗时 %.0fs）" % (FAILED_STEP or "未知", time.time() - t0))
        return 1
    except Exception as e:
        _log("TC-0106 RESULT: FAIL —— 未预期异常: %s" % e)
        _log("失败步骤: %s（总耗时 %.0fs）" % (FAILED_STEP or "准备阶段", time.time() - t0))
        return 1
    finally:
        try:
            shim.uds_deinit()
        except Exception:
            pass
        shim.close()  # 释放 /dev/ttyACM0
    _log("TC-0106 RESULT: PASS —— OTA 升级复位后 SN 逐字节保留、"
         "0xF195==载荷版本、0x2113=0x00（总耗时 %.0fs）" % (time.time() - t0))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
