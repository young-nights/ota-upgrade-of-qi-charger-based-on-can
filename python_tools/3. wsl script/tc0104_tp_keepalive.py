#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""TC-0104 TesterPresent (3E 80) 保活下载测试 —— WSL2 独立执行。

用例 TC-0104（P1）TesterPresent 保活：执行完整下载流程，关键步骤间插入
3E 80（suppress，不等应答），预期全流程无 S3 超时。

核心证明点 —— 蓄意超 S3 停顿：
  31 01 FF 00 擦除完成后、34 之前，制造 ≥8s 空闲（>S3=5000ms，
  qi_wireless_code_app/mdk_app/Inc/can_protocol.h SESSION_TIMEOUT_MS），
  期间每 2s 发一帧 3E 80。若保活无效（会话已回退 default），
  停顿后的 34 必被拒 NRC 0x7E/0x7F（serviceNotSupportedInActiveSession
  等会话类 NRC）→ 脚本据此判 FAIL。

流程：
  基线 22 F195 → 10 02 → [3E 80] → 27 前 [3E 80] → 27 01/02
  （ECDSA 64B，docs/keys/private.pem 签 seed）→ [3E 80] →
  2E 20 10 01 → [3E 80] → 31 01 FF 00 → [3E 80] → ≥8s 保活停顿
  （每 2s 一帧 3E 80）→ 34 00 44 @0x08010000 → 36 循环（每块 128B，
  每 8 块 [3E 80]，N≤16）→ [3E 80] → 37 提交 → 等自复位 →
  判定闭环（复位后 0x2113==0x00 且 0xF195==被刷镜像版本）。

全程任何请求收到 7F xx 7E/7F（会话类 NRC）或 S3 回退迹象 → 立即判 FAIL
并打印失败步骤。

用法（仓库任意位置）：
  python3 "python_tools/3. wsl script/tc0104_tp_keepalive.py" [--firmware "路径.bin"]
  默认载荷：python_tools/app bin/app_image_v1_1_2.bin
退出码：0=PASS，1=FAIL。

实现：sys.modules['zcanpro'] 注入 zcanpro_shim_zqwl（Zqwl 串口底层），
复用 zcanpro_ext_ota_auto 的 uds_req/签名/擦除/复位确认函数；
不修改任何现有文件。
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

# TC-0104 保活停顿参数（核心证明点）
S3_MS = 5000            # can_protocol.h SESSION_TIMEOUT_MS
PAUSE_S = 10.0          # 蓄意停顿时长（> S3）
PAUSE_TP_INTERVAL_S = 2  # 停顿期间 3E 80 发送间隔
TRANSFER_BLOCK = 128    # 36 每块字节数（≤0x400）
TP_EVERY_N_BLOCKS = 8   # 36 每 N 块插一帧 3E 80（N≤16）


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
    """ext.uds_req 包装：会话类 NRC 0x7E/0x7F 立即判 FAIL，其他异常
    归入当前步骤标签。返回正响应 payload 列表。"""
    _log("---- 步骤[%s] TX: %02X %s ----" % (step, sid, ext._hex(payload[:16])))
    try:
        rx = ext.uds_req(BUS, sid, payload, **kw)
    except ext.UdsNrcError as e:
        if e.nrc in (0x7E, 0x7F):
            _fail(step, "收到会话类 NRC 7F %02X %02X（S3 超时/会话已回退"
                        "或该服务在活动会话不支持）——TP 保活无效" % (e.sid, e.nrc))
        _fail(step, "NRC 7F %02X %02X" % (e.sid, e.nrc))
    except ext.SafeModeError as e:
        _fail(step, str(e))
    except TestFail:
        raise
    except Exception as e:
        _fail(step, str(e))
    return rx


def tp(tag):
    """发一帧 3E 80（suppress，不等应答）；发送失败即保活链路失效 → FAIL。"""
    try:
        ext.uds_req(BUS, 0x3E, [0x80], suppress=1)
    except Exception as e:
        _fail("TP保活[%s]" % tag, "3E 80 发送失败: %s" % e)
    _log("[TP] 3E 80 suppress 已发送 → %s" % tag)


def read_version(step):
    """22 F195 → ASCII 版本串（QC_JYF_FW_x.y.z）；读失败抛 TestFail。"""
    rx = req(step, 0x22, [0xF1, 0x95])
    raw = bytes(int(b) & 0xFF for b in rx[3:]) if len(rx) > 3 else b""
    m = re.search(rb"QC_JYF_FW_[0-9A-Za-z._]+", raw)
    if m:
        return m.group(0).decode("ascii")
    return raw.split(b"\x00")[0].decode("ascii", "ignore").strip() or None


def s3_pause_with_keepalive():
    """核心证明点：蓄意超 S3 停顿，期间每 2s 一帧 3E 80。

    停顿结束条件 = 满 PAUSE_S（≥8s，脚本取 10s）且最后一帧 3E 80 距
    结束 ≤2s（保证 34 落在 S3 窗口内——若保活无效，此刻会话已回退）。
    """
    _log("======== 蓄意超 S3 停顿开始：%.1fs 空闲（>S3=%dms），"
         "期间每 %.0fs 一帧 3E 80 ========" % (PAUSE_S, S3_MS, PAUSE_TP_INTERVAL_S))
    t0 = time.time()
    n = 0
    while True:
        tp("S3停顿 #%d" % (n + 1))
        n += 1
        remain = PAUSE_S - (time.time() - t0)
        if remain <= 0:
            break
        time.sleep(min(PAUSE_TP_INTERVAL_S, remain))
    elapsed = time.time() - t0
    if elapsed < 8.0:
        _fail("S3停顿", "停顿仅 %.1fs，未达 ≥8s 证明要求" % elapsed)
    _log("======== 停顿结束：耗时 %.1fs，期间发送 3E 80 共 %d 帧，"
         "继续 34 ========" % (elapsed, n))


def run_tc0104(fw_path):
    global BUS
    # ---- 载荷与签名准备（不触碰硬件） ----
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

    # ---- 1. 打开通道，探测 22 F195 基线 ----
    ext.uds_init()
    buses = shim.get_buses()
    if not buses:
        raise TestFail("无 CAN 总线（Zqwl 通道打开失败）")
    BUS = buses[0]["busID"]
    baseline = read_version("基线22F195")
    _log("基线 22 F195 = %s（载荷目标 %s）" % (baseline, expect_ver))
    if baseline == expect_ver:
        _log("提示：设备当前版本已等于目标版本，本次刷写仍完整执行全链路"
             "（10/27/2E/31/8s停顿/34/36/37），保活证明不受影响")

    # ---- 2. 10 02 进编程会话 + 会话后 3E 80 ----
    req("10_02进编程会话", 0x10, [0x02])
    tp("进入编程会话后")

    # ---- 3. 27 前后保活 + SecurityAccess ----
    tp("27前")
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
            if e.nrc in (0x7E, 0x7F):
                _fail("27_02送密钥", "收到会话类 NRC 7F %02X %02X——TP 保活无效"
                      % (e.sid, e.nrc))
            _fail("27_02送密钥", "NRC 7F %02X %02X" % (e.sid, e.nrc))
        except Exception as e:
            _fail("27_02送密钥", str(e))
    tp("27后")

    # ---- 4. 2E 20 10 01 写 DID + 保活 ----
    req("2E_20_10_01写DID", 0x2E, [0x20, 0x10, 0x01])
    tp("2E后")

    # ---- 5. 31 01 FF 00 擦除 + 保活 ----
    _log("---- 步骤[31_01_FF_00擦除Backup区] ----")
    try:
        ext._erase_with_retry(BUS)
    except ext.UdsNrcError as e:
        if e.nrc in (0x7E, 0x7F):
            _fail("31擦除", "收到会话类 NRC 7F %02X %02X——TP 保活无效"
                  % (e.sid, e.nrc))
        _fail("31擦除", "NRC 7F %02X %02X" % (e.sid, e.nrc))
    except TestFail:
        raise
    except Exception as e:
        _fail("31擦除", str(e))
    tp("31后")

    # ---- 6. 核心证明点：蓄意超 S3 的停顿（期间 3E 80 连发）----
    s3_pause_with_keepalive()

    # ---- 7. 34 RequestDownload（保活无效则此处必被拒）----
    size = len(image)
    addr_val = ext.BACKUP_BASE
    sz = [(size >> 24) & 0xFF, (size >> 16) & 0xFF, (size >> 8) & 0xFF, size & 0xFF]
    addr = [(addr_val >> 24) & 0xFF, (addr_val >> 16) & 0xFF,
            (addr_val >> 8) & 0xFF, addr_val & 0xFF]
    req("34_RequestDownload_%d_@0x%08X" % (size, addr_val), 0x34,
        [0x00, 0x44] + addr + sz)
    _log("[人话] 停顿后 34 正响应 —— S3 停顿期间会话未回退，TP 保活有效")

    # ---- 8. 36 循环（每 8 块插一帧 3E 80，N≤16）----
    seq = 1
    off = 0
    blocks = 0
    while off < size:
        chunk = image[off:off + TRANSFER_BLOCK]
        req("36_seq%03d" % seq, 0x36, [seq] + ext._to_list(chunk))
        off += len(chunk)
        blocks += 1
        seq = 1 if seq == 0xFF else seq + 1
        if off < size and blocks % TP_EVERY_N_BLOCKS == 0:
            tp("36第%d块后" % blocks)
        if off == size or (off % (TRANSFER_BLOCK * 32) == 0):
            _log("  进度 %d/%d" % (off, size))

    # ---- 9. 37 前保活 + TransferExit 提交 ----
    tp("37前")
    _log("---- 步骤[37_TransferExit提交] ----")
    try:
        ext.uds_req(BUS, 0x37, [], wait_pending_s=45)
        _log("[人话] 37 已提交，设备自复位后 BOOT 搬运 Backup→App")
    except ext.UdsNrcError as e:
        if e.nrc in (0x7E, 0x7F):
            _fail("37提交", "收到会话类 NRC 7F %02X %02X——TP 保活无效"
                  % (e.sid, e.nrc))
        _log("37 NRC 0x%02X —— 交由复位后判定闭环裁决" % e.nrc)
    except Exception as e:
        _log("37 应答异常（自复位常致应答丢失）—— 交由复位后判定闭环裁决: %s" % e)

    # ---- 10. 等自复位 + 判定闭环 ----
    time.sleep(2.0)
    _log("---- 步骤[复位后判定闭环] ----")
    try:
        to_slot, got_ver, ota_status = ext.confirm_app_after_reset(BUS)
    except Exception as e:
        _fail("复位后判定闭环", str(e))
    problems = []
    if got_ver != expect_ver:
        problems.append("0xF195=%s ≠ 被刷镜像版本 %s" % (got_ver or "未读到", expect_ver))
    if to_slot is None:
        problems.append("0x2113 读取失败")
    elif to_slot != 0x00:
        problems.append("0x2113=0x%02X ≠ 0x00" % (to_slot & 0xFF))
    if problems:
        _fail("复位后判定闭环", "；".join(problems))
    _log("判定闭环满足：① 复位后 APP 应答；② 0x2113=0x00；"
         "③ 0xF195=%s==载荷版本 %s" % (got_ver, expect_ver))


def main():
    parser = argparse.ArgumentParser(description="TC-0104 TP 保活下载测试")
    parser.add_argument("--firmware", default=DEFAULT_FIRMWARE,
                        help="OTA 载荷 bin（默认 app bin/app_image_v1_1_2.bin）")
    args = parser.parse_args()
    fw = args.firmware
    if not os.path.isabs(fw):
        fw = os.path.abspath(os.path.join(os.getcwd(), fw))
        if not os.path.isfile(fw):
            fw = os.path.abspath(os.path.join(HERE, args.firmware))
    _log("======== TC-0104 TesterPresent 保活下载测试 ========")
    _log("S3=%dms，蓄意停顿 %.1fs，停顿期间每 %.0fs 一帧 3E 80"
         % (S3_MS, PAUSE_S, PAUSE_TP_INTERVAL_S))
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
    _log("TC-0104 RESULT: PASS —— 8s+ S3 停顿（期间 3E 80 连发）后全流程"
         "正响应，复位后 0x2113=0x00、0xF195==载荷版本，保活有效"
         "（总耗时 %.0fs）" % (time.time() - t0))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
