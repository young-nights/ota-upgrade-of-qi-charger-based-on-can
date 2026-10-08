# -*- coding: utf-8 -*-
"""
ZCANPRO 扩展脚本 — OTA 升级压力测试循环（默认 1.1.1 ~ 1.1.10）

功能：按语义版本号（patch 位递增：1.1.9 → 1.1.10，不是 1.2.0）逐轮执行
CAN-UDS OTA 升级；每轮升级完成后独立读取 DID 0xF195 版本号，与目标版本
完整字符串（QC_JYF_FW_x.y.z）匹配才判定 PASS 并继续，默认失败即停。

版本真相源：固件 can_protocol.c SW_VERSION_STR（DID 0xF195 应答数据源）。
与 pack_image.py 自动命名、zcanpro_ext_ota_auto.py 版本闸门同源（bin
strings 扫描 QC_JYF_FW_ 前缀，第一处命中）。

每轮流程：
  ① 拼接 bin 路径（BIN_NAME_TEMPLATE，ver 点号→下划线）；
  ② 文件不存在 → 记 FAIL（--continue-on-fail 控制继续/停止）；
  ③ 执行升级（runner 二选一，见下），解析输出「OTA 成功」判定结果；
  ④ 升级成功后 UDS 读 0xF195：3E 00 唤醒探测 → 22 F1 95 → ASCII 解析；
  ⑤ 读到版本 == QC_JYF_FW_{目标版本}（完整字符串匹配）→ PASS，否则 FAIL；
  ⑥ 汇总表输出轮次/目标版本/升级结果/读到版本/判定 + 总计。

执行后端（--runner {auto,inproc,subproc}）：
  inproc  —— 在 ZCANPRO 宿主内 import zcanpro_ext_ota_auto，按轮覆盖其
             EXPECTED_SW_VERSION / FIRMWARE_OVERRIDE 全局量后直接调
             run_ota(bus_id)，并接管其 _log 捕获输出（不修改执行器文件）。
             auto 判据：zcanpro + 执行器模块 + CAN 通道三者齐备时选用。
  subproc —— subprocess 启动独立 Python 进程跑 zcanpro_ext_ota_auto.py
             （-c bootstrap 运行期覆盖全局量 + tee 日志到 stdout）。要求
             子进程环境能独立访问 ZCANPRO 设备会话；扩展脚本宿主进程
             模型下子进程通常拿不到设备，每轮会如实 FAIL。

UDS 通信：照抄真机已验证模式（zcanpro_ext_ota_auto._uds_init_cfg /
uds_req dict 形态 / zcanpro_read_app_version.wake_mcu 3E 00 重试唤醒）。
升级执行器 run_ota 的 finally 会 uds_deinit，读版本前本脚本重新 uds_init。

导入运行：ZCANPRO → 高级功能 → 扩展脚本 → 打开本文件 → 运行（宿主调 z_main()）
命令行：python3 zcanpro_ota_stress_test.py --start 1.1.1 --end 1.1.10 \
        --bin-dir "ota test bin" [--continue-on-fail] [--runner auto]
前置：CAN 通道 250 kbps Classical CAN 扩展帧；bin 已按版本命名放入目录
      （pack_image.py 自动命名产物 app_image_v{x}_{y}_{z}.bin，输出在
      python_tools/app bin/，可复制到 ota test bin/ 或 --bin-dir 指定）。
注意：压测反复擦写 Backup 区并搬运 App 区，供电与 CAN 接线须稳定。
"""

from __future__ import print_function

import argparse
import os
import subprocess
import sys
import time

try:
    import zcanpro
except ImportError:
    zcanpro = None

_TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
if _TOOLS_DIR not in sys.path:
    sys.path.insert(0, _TOOLS_DIR)

try:
    import zcanpro_ext_ota_auto as _ota
except Exception:
    _ota = None

# ---------------- 配置区（宿主加载路径使用默认值；命令行参数可覆盖） ----------------
START_VERSION = "1.1.1"
END_VERSION = "1.1.10"
FIRMWARE_DIR = "ota test bin"                 # bin 目录，相对 python_tools/
BIN_NAME_TEMPLATE = "app_image_v{ver}.bin"    # ver 为下划线格式
OTA_SCRIPT = "zcanpro_ext_ota_auto.py"        # 升级执行器（本目录内）
EXPECTED_DID = 0xF195                         # 版本号 DID
VERSION_PREFIX = "QC_JYF_FW_"                 # 应答 ASCII 前缀（完整字符串比对）
CONTINUE_ON_FAIL = False                      # 失败即停
RUNNER = "auto"                               # auto | inproc | subproc
WAKE_RETRIES = 8                              # 3E 00 唤醒重试次数（D3/P0 实证参数）

UDS_REQ_ID = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D
SID_RDBI = 0x22
SID_TP = 0x3E
SID_NRC = 0x7F
SID_PR = 0x40

stopTask = False


def z_notify(type, obj):
    """宿主停止回调：同时传播给升级执行器模块（inproc 后端在跑时）。"""
    global stopTask
    if type == "stop":
        stopTask = True
        if _ota is not None:
            try:
                _ota.stopTask = True
            except Exception:
                pass


def _log(msg):
    text = str(msg)
    if zcanpro is not None:
        try:
            zcanpro.write_log(text)
            return
        except Exception:
            pass
    sys.stdout.write(text + "\n")


def _to_text(data):
    if data is None:
        return ""
    if isinstance(data, bytes):
        return data.decode("utf-8", "replace")
    return str(data)


# ---------------- 版本号枚举（patch 位递增） ----------------

def _parse_ver(text):
    parts = str(text).strip().split(".")
    if len(parts) != 3:
        raise ValueError("版本号格式应为 x.y.z：%r" % text)
    return (int(parts[0]), int(parts[1]), int(parts[2]))


def iter_versions(start, end):
    """从 start 到 end（含）按 patch 位递增生成版本号字符串。

    1.1.9 之后是 1.1.10（不是 1.2.0）。仅支持同一 major.minor 区间
    （压力测试语义：同系列构建逐 patch 连升）；跨越 minor 时截断并告警。"""
    cur = _parse_ver(start)
    end_t = _parse_ver(end)
    if cur > end_t:
        raise ValueError("起始版本 %s 大于结束版本 %s" % (start, end))
    base_mm = (cur[0], cur[1])
    out = []
    while cur <= end_t:
        if (cur[0], cur[1]) != base_mm:
            _log("版本递增已达 %d.%d，超出起始 major.minor=%d.%d，截断结束"
                 % (cur[0], cur[1], base_mm[0], base_mm[1]))
            break
        out.append("%d.%d.%d" % cur)
        if len(out) > 1000:   # 防御性上限
            _log("版本序列超过 1000 轮，截断")
            break
        cur = (cur[0], cur[1], cur[2] + 1)
    return out


def _ver_to_fname(ver):
    return BIN_NAME_TEMPLATE.format(ver=ver.replace(".", "_"))


def _bin_path(ver, bin_dir):
    return os.path.join(_TOOLS_DIR, bin_dir, _ver_to_fname(ver))


# ---------------- UDS 读版本机器（照抄真机已验证模式） ----------------

def _hex(data):
    if data is None:
        return ""
    return " ".join("%02X" % (int(b) & 0xFF) for b in data)


def _safe_mode_step(dat):
    """Boot safe mode 标记帧：ISO-TP SF 05 62 21 13 FE <step> 或历史裸帧
    62 21 13 FE <step>（与 boot_safe_mode.c 注释一致，双格式兼容）。"""
    if (len(dat) >= 6 and dat[0] == 0x05 and dat[1] == 0x62 and dat[2] == 0x21
            and dat[3] == 0x13 and dat[4] == 0xFE):
        return int(dat[5])
    if (len(dat) >= 5 and dat[0] == 0x62 and dat[1] == 0x21
            and dat[2] == 0x13 and dat[3] == 0xFE):
        return int(dat[4])
    return None


def _uds_init_cfg():
    """与 zcanpro_ext_ota_auto._uds_init_cfg 一致（真机 07:08 实证配置）。"""
    return {
        "src_addr": UDS_REQ_ID,
        "dst_addr": UDS_RESP_ID,
        "response_timeout_ms": 3000,
        "use_canfd": 0,
        "canfd_brs": 0,
        "trans_ver": 0,
        "fill_byte": 0xCC,
        "frame_type": 1,
        "trans_stmin_valid": 1,
        "trans_stmin": 1,
        "enhanced_timeout_ms": 120000,
    }


def uds_init():
    if zcanpro is None:
        raise RuntimeError("zcanpro 模块不可用（需 ZCANPRO 扩展脚本宿主环境）")
    zcanpro.uds_init(_uds_init_cfg())


def uds_deinit():
    if zcanpro is None:
        return
    try:
        zcanpro.uds_deinit()
    except Exception:
        pass


def uds_req(bus_id, sid, payload, suppress=0):
    """zcanpro.uds_request dict 形态（照抄执行器 uds_req 主路径，
    不做 raw 发送）。NRC/safe-mode 标记帧在此显式判别。"""
    if zcanpro is None:
        raise RuntimeError("zcanpro 模块不可用（需 ZCANPRO 扩展脚本宿主环境）")
    if stopTask:
        raise RuntimeError("用户停止脚本")
    req = {
        "src_addr": UDS_REQ_ID,
        "dst_addr": UDS_RESP_ID,
        "suppress_response": 1 if suppress else 0,
        "sid": sid,
        "data": list(payload),
    }
    _log("[Tx] %02X %s" % (sid, _hex(payload)))
    resp = zcanpro.uds_request(bus_id, req)
    if suppress:
        return None
    data = list((resp or {}).get("data") or [])
    if data:
        _log("[Rx] " + _hex(data[:24]))
        sm_step = _safe_mode_step(data)
        if sm_step is not None:
            raise RuntimeError("设备处于 Boot safe mode，fail_step=%d（读版本不可用，"
                               "需 merge_prod_bin 重刷）" % sm_step)
    if len(data) >= 3 and data[0] == SID_NRC:
        raise RuntimeError("NRC SID=0x%02X NRC=0x%02X" % (data[1], data[2]))
    if not resp or not resp.get("result") or not data:
        raise RuntimeError("无应答 SID=0x%02X %s"
                           % (sid, (resp or {}).get("result_msg", "")))
    if data[0] != (sid + SID_PR):
        raise RuntimeError("非正响应 SID=0x%02X %s" % (sid, _hex(data)))
    return data


def wake_bus(bus_id, retries=WAKE_RETRIES):
    """3E 00 等 7E 在线确认（zcanpro_read_app_version.wake_mcu 实证模式：
    重试 8 次、间隔 0.5s，覆盖 Boot→App CAN 黑窗最坏 ~5s + SIT1145 Standby
    首帧被 WUP 消耗）。返回 True=设备 APP 在线。"""
    for i in range(1, int(retries) + 1):
        if stopTask:
            return False
        try:
            rx = uds_req(bus_id, SID_TP, [0x00])
            if rx and rx[0] == (SID_TP + SID_PR):
                _log("唤醒确认：7E 在线（第 %d/%d 次）" % (i, retries))
                return True
        except RuntimeError as e:
            _log("唤醒 %d/%d: %s" % (i, retries, e))
        time.sleep(0.5)
    return False


def read_sw_version(bus_id):
    """UDS 22 F1 95 → ASCII 版本串（DID 应答=SW_VERSION_STR，32B 右补填
    充）。返回去尾部填充的字符串；读不到抛 RuntimeError。"""
    did = EXPECTED_DID
    rx = uds_req(bus_id, SID_RDBI, [(did >> 8) & 0xFF, did & 0xFF])
    if len(rx) < 3 or rx[0] != (SID_RDBI + SID_PR) or rx[1:3] != [(did >> 8) & 0xFF, did & 0xFF]:
        raise RuntimeError("DID 0x%04X 应答异常: %s" % (did, _hex(rx)))
    payload = bytes(bytearray(b & 0xFF for b in rx[3:]))
    text = payload.split(b"\x00")[0].decode("ascii", "ignore").strip()
    if not text:
        raise RuntimeError("DID 0x%04X 应答版本串为空: %s" % (did, _hex(rx)))
    return text


# ---------------- 升级执行后端 ----------------

def read_ota_status(bus_id):
    """UDS 22 21 12 → OTA status byte (0x05=Confirmed)。"""
    try:
        rx = uds_req(bus_id, 0x22, [0x21, 0x12])
        if len(rx) >= 4 and rx[0] == 0x62:
            return rx[3]
    except Exception:
        pass
    return None


def _parse_ota_result(text):
    """升级结果判定：输出含「OTA 成功」→ True（执行器 run_ota 成功路径
    固定打印 ======== OTA 成功 ========）。spec 兼容：「OTA FAIL」按失败。"""
    return "OTA 成功" in text


def _fail_reason(text, err):
    if err:
        return str(err)
    lines = [l for l in text.splitlines() if l.strip()]
    return " / ".join(lines[-3:]) if lines else "无输出"


def _run_ota_inproc(bus_id, bin_path, expected_full):
    """宿主内直接调用执行器 run_ota：按轮覆盖全局量 + _log tee 捕获。

    不修改 zcanpro_ext_ota_auto.py 文件——EXPECTED_SW_VERSION / FIRMWARE_OVERRIDE
    仅运行期覆盖（执行器版本闸门与判定闭环第③条件随之对齐本轮目标版本）。"""
    if _ota is None:
        raise RuntimeError("zcanpro_ext_ota_auto 模块导入失败，inproc 后端不可用")
    if zcanpro is None or bus_id is None:
        raise RuntimeError("inproc 后端需要 ZCANPRO 宿主设备会话（get_buses 有通道）")
    buf = []
    orig_log = _ota._log

    def _tee(msg):
        text = str(msg)
        buf.append(text)
        try:
            orig_log(text)
        except Exception:
            pass

    _ota._log = _tee
    _ota.EXPECTED_SW_VERSION = expected_full
    _ota.FIRMWARE_OVERRIDE = bin_path
    _ota.stopTask = False
    err = None
    try:
        _ota.run_ota(bus_id)
    except Exception as e:
        err = e
    finally:
        _ota._log = orig_log
    text = "\n".join(buf)
    ok = (err is None) and _parse_ota_result(text)
    return ok, text, err


def _run_ota_subprocess(bin_path, expected_full):
    """subprocess 后端：-c bootstrap 导入执行器 → 运行期覆盖全局量 →
    _log tee 到 stdout → z_main()。执行器文件零改动。

    bootstrap 里 sys.argv[0] 不是 .py，执行器 __main__ 守卫不会二次自跑，
    由 bootstrap 显式调用 z_main()。"""
    bootstrap = (
        "import sys\n"
        "sys.path.insert(0, {tools!r})\n"
        "import zcanpro_ext_ota_auto as m\n"
        "m.EXPECTED_SW_VERSION = {exp!r}\n"
        "m.FIRMWARE_OVERRIDE = {fw!r}\n"
        "_orig = m._log\n"
        "def _tee(msg):\n"
        "    if m.zcanpro is not None:\n"
        "        try:\n"
        "            m.zcanpro.write_log(str(msg))\n"
        "        except Exception:\n"
        "            pass\n"
        "    sys.stdout.write(str(msg) + chr(10))\n"
        "m._log = _tee\n"
        "m.z_main()\n"
    ).format(tools=_TOOLS_DIR, exp=expected_full, fw=bin_path)
    ota_py = os.path.join(_TOOLS_DIR, OTA_SCRIPT)
    _log("subprocess: %s --firmware %s（bootstrap 覆盖 EXPECTED_SW_VERSION=%s）"
         % (OTA_SCRIPT, bin_path, expected_full))
    try:
        proc = subprocess.Popen(
            [sys.executable, "-c", bootstrap],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        out = proc.communicate()[0]
        rc = proc.returncode
    except Exception as e:
        return False, "", "subprocess 启动失败: %s（脚本: %s）" % (e, ota_py)
    text = _to_text(out)
    for line in text.splitlines()[-20:]:
        _log("[ota] " + line)
    ok = (rc == 0) and _parse_ota_result(text)
    return ok, text, None


def _resolve_runner(pref, bus_id):
    if pref == "inproc":
        return "inproc"
    if pref == "subproc":
        return "subproc"
    if _ota is not None and zcanpro is not None and bus_id is not None:
        return "inproc"
    return "subproc"


# ---------------- 主循环 ----------------

def run_stress(bus_id, start, end, bin_dir, continue_on_fail, runner):
    try:
        versions = iter_versions(start, end)
    except ValueError as e:
        _log("参数错误: %s" % e)
        return None
    if not versions:
        _log("版本序列为空（start=%s end=%s）" % (start, end))
        return None

    _log("================ OTA 压力测试开始 ================")
    _log("版本序列 : %s ... %s（共 %d 轮，patch 位递增）"
         % (versions[0], versions[-1], len(versions)))
    _log("bin 目录 : %s（模板 %s）"
         % (os.path.join(_TOOLS_DIR, bin_dir), BIN_NAME_TEMPLATE))
    _log("执行后端 : %s；失败策略 : %s"
         % (runner, "继续" if continue_on_fail else "失败即停"))

    results = []
    for idx, ver in enumerate(versions, 1):
        if stopTask:
            _log("收到停止指令，中断循环（已完成 %d/%d 轮）"
                 % (len(results), len(versions)))
            break
        expected_full = VERSION_PREFIX + ver
        bin_p = _bin_path(ver, bin_dir)
        _log("--------------------------------------------------")
        _log("第 %d/%d 轮：目标版本 %s（期望 0x%04X 应答 %s）"
             % (idx, len(versions), ver, EXPECTED_DID, expected_full))
        _log("载荷     : %s" % os.path.abspath(bin_p))

        read_ver = "-"
        read_err = None
        ota_status = None
        if not os.path.isfile(bin_p):
            ota_res = "文件缺失"
            verdict = "FAIL"
            _log("FAIL：bin 文件不存在（pack_image.py 自动命名产物应为 %s；"
                 "可 --bin-dir 指定实际目录）" % _ver_to_fname(ver))
        else:
            t0 = time.time()
            if runner == "inproc":
                try:
                    ok, text, err = _run_ota_inproc(bus_id, bin_p, expected_full)
                except Exception as e:
                    ok, text, err = False, "", e
            else:
                ok, text, err = _run_ota_subprocess(bin_p, expected_full)
            ota_res = "成功" if ok else "失败"
            _log("升级结果 : %s（耗时 %.1fs）" % (ota_res, time.time() - t0))
            if not ok:
                _log("升级失败原因: %s" % _fail_reason(text, err))
            if ok:
                # 升级执行器 finally 已 uds_deinit；读版本前重新 init
                # （ZLG 纪律：uds_init 必须先于收发，否则 receive 恒空）。
                try:
                    uds_init()
                    time.sleep(0.05)
                    if not wake_bus(bus_id):
                        raise RuntimeError("唤醒探测 %d 次均无 7E 应答（设备可能仍在"
                                           " Boot 验签/safe mode，见上方执行器日志）"
                                           % WAKE_RETRIES)
                    read_ver = read_sw_version(bus_id)
                    _log("读到版本 : %s（DID 0x%04X）" % (read_ver, EXPECTED_DID))
                    ota_status = read_ota_status(bus_id)
                    _log("OTA 状态 : 0x%02X（DID 0x2112，0x05=Confirmed 搬运成功）"
                         % (ota_status if ota_status is not None else 0xFF))
                except Exception as e:
                    read_err = e
                    read_ver = "读取失败"
                    _log("版本读取失败: %s" % e)
                finally:
                    uds_deinit()
            ota_ok = (ota_status == 0x05) if ota_status is not None else False
            verdict = "PASS" if (ok and read_ver == expected_full and ota_ok) else "FAIL"
            if verdict == "FAIL" and ok:
                _log("FAIL：版本不匹配——读到 %s，预期 %s%s"
                     % (read_ver, expected_full,
                        "（%s）" % read_err if read_err else
                        "（0xF195≠目标版本：Backup→App 搬运未生效/载荷错误）"))

        results.append((idx, ver, ota_res, read_ver, verdict))
        _log("本轮判定 : %s" % verdict)
        if verdict == "FAIL" and not continue_on_fail:
            _log("失败即停（--continue-on-fail 可失败后继续）")
            break

    _print_summary(results, versions)
    return results


def _print_summary(results, versions):
    _log("================ OTA 压力测试汇总 ================")
    _log("%-6s%-11s%-10s%-13s%s" % ("轮次", "目标版本", "升级结果", "读到版本", "判定"))
    for idx, ver, ota_res, read_ver, verdict in results:
        _log("%-6d%-11s%-10s%-13s%s" % (idx, ver, ota_res, read_ver, verdict))
    _log("==================================================")
    passed = sum(1 for r in results if r[4] == "PASS")
    if len(results) < len(versions):
        _log("总计：%d/%d PASS（计划 %d 轮，第 %d 轮后停止，剩余 %d 轮未执行）"
             % (passed, len(results), len(versions), len(results),
                len(versions) - len(results)))
    else:
        _log("总计：%d/%d PASS" % (passed, len(results)))


def _get_bus_id():
    if zcanpro is None:
        return None
    try:
        buses = zcanpro.get_buses()
    except Exception as e:
        _log("get_buses 失败: %s" % e)
        return None
    if not buses:
        _log("请先打开 CAN 通道 (250kbps, Classical CAN, 扩展帧)")
        return None
    _log("bus = " + str(buses[0]))
    return buses[0]["busID"]


def z_main():
    """ZCANPRO 宿主入口：宿主无命令行参数，使用脚本头部配置区常量。"""
    global stopTask
    stopTask = False
    _log("======== Qi CAN-UDS OTA 压力测试 ========")
    bus_id = _get_bus_id()
    runner = _resolve_runner(RUNNER, bus_id)
    if runner == "inproc" and bus_id is None:
        _log("inproc 后端无 CAN 通道，无法执行")
        return
    if runner == "subproc":
        _log("提示：subprocess 后端需要子进程可独立访问 ZCANPRO 设备会话；"
             "扩展脚本宿主内推荐 --runner inproc（默认 auto 已自动选择）")
    try:
        run_stress(bus_id, START_VERSION, END_VERSION, FIRMWARE_DIR,
                   CONTINUE_ON_FAIL, runner)
    except Exception as e:
        _log("压力测试异常: " + str(e))


def _parse_cli_args():
    """命令行参数（standalone 路径专用）。parse_known_args：ZCANPRO 宿主/
    包装器 argv 中的未知参数不拦截脚本执行。"""
    parser = argparse.ArgumentParser(
        description="Qi CAN-UDS OTA 压力测试循环脚本（版本 patch 位递增，"
                    "每轮升级后读 0xF195 闭环验证）")
    parser.add_argument("--start", default=START_VERSION,
                        help="起始版本（默认 %s）" % START_VERSION)
    parser.add_argument("--end", default=END_VERSION,
                        help="结束版本，含（默认 %s）" % END_VERSION)
    parser.add_argument("--bin-dir", default=FIRMWARE_DIR,
                        help="bin 文件目录，相对 python_tools/（默认 %s）"
                             % FIRMWARE_DIR)
    parser.add_argument("--continue-on-fail", action="store_true",
                        default=CONTINUE_ON_FAIL,
                        help="失败后继续下一轮（默认失败即停）")
    parser.add_argument("--runner", choices=("auto", "inproc", "subproc"),
                        default=RUNNER,
                        help="升级执行后端（默认 auto：宿主内 inproc，否则 subproc）")
    args, _unknown = parser.parse_known_args()
    return args


if __name__ == "__main__":
    _cli = _parse_cli_args()
    START_VERSION = _cli.start
    END_VERSION = _cli.end
    FIRMWARE_DIR = _cli.bin_dir
    CONTINUE_ON_FAIL = _cli.continue_on_fail
    RUNNER = _cli.runner
    _argv0 = os.path.basename((sys.argv[0] if sys.argv else "") or "")
    if _argv0.endswith(".py"):
        # 命令行直跑（argv[0]=脚本路径）→ 执行压测；
        # ZCANPRO 宿主若以 __main__ 形态加载（argv[0]=宿主可执行体）不自跑，
        # 由宿主调用 z_main()，避免双重执行。
        stopTask = False
        _log("======== Qi CAN-UDS OTA 压力测试（standalone） ========")
        bus_id = _get_bus_id()
        runner = _resolve_runner(RUNNER, bus_id)
        if zcanpro is None:
            _log("zcanpro 模块不可用：升级走 subprocess 后端；版本读取需要 "
                 "ZCANPRO 扩展脚本宿主环境（Python 3.8 32 位 + zcanpro）")
        if runner == "inproc" and bus_id is None:
            _log("inproc 后端无 CAN 通道，回退 subprocess 后端")
            runner = "subproc"
        run_stress(bus_id, START_VERSION, END_VERSION, FIRMWARE_DIR,
                   CONTINUE_ON_FAIL, runner)
