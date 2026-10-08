# -*- coding: utf-8 -*-
"""
ZCANPRO 脚本 — 查询 Qi 芯片 IAP 升级状态

读 DID 0x2132（8 字节）：
  [0] state:    0x00=空闲, 0x01=升级中, 0x02=成功, 0x03=失败
  [1] progress: 0~100 百分比
  [2-3] version: uint16 LE, Qi 芯片版本
  [4-5] sent:   uint16 LE, 已传字节数
  [6-7] total:  uint16 LE, 固件总长

不需要编程会话或安全解锁，任意会话可读。

用法: ZCANPRO → 高级功能 → 扩展脚本 → 打开本文件（默认单次查询）
      附加 --poll 进入轮询模式（每 1s 一次，直到升级成功/失败）
"""

import sys
import time

try:
    import zcanpro
except ImportError:
    zcanpro = None

# ======== UDS 常量 ========
UDS_REQ_ID  = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

SID_RDBI = 0x22
SID_TP   = 0x3E
SID_NRC  = 0x7F
SID_PR   = 0x40

NRC_RCRRP = 0x78

DID_QI_IAP_STATUS = 0x2132

# IAP 状态码
STATE_NAMES = {
    0x00: "空闲 (IDLE)",
    0x01: "升级中 (IN_PROGRESS)",
    0x02: "成功 (SUCCESS)",
    0x03: "失败 (FAILED)",
}

# 轮询间隔（秒）
POLL_INTERVAL = 1.0

stopTask = False


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


# ======== UDS 通信 ========

def uds_init():
    zcanpro.uds_init({
        "response_timeout_ms": 3000, "use_canfd": 0, "canfd_brs": 0,
        "trans_ver": 0, "fill_byte": 0xCC, "frame_type": 1,
        "trans_stmin_valid": 1, "trans_stmin": 1, "enhanced_timeout_ms": 30000,
    })


def uds_req(bus_id, sid, payload, wait_pending_s=0):
    if stopTask:
        raise RuntimeError("用户停止")
    req = {
        "src_addr": UDS_REQ_ID, "dst_addr": UDS_RESP_ID,
        "suppress_response": 0, "sid": sid, "data": list(payload),
    }
    t_end = time.time() + float(wait_pending_s)
    logged = False
    while True:
        if stopTask:
            raise RuntimeError("用户停止")
        if not logged:
            _log("[Tx] %02X %s" % (sid, _hex(payload)))
            logged = True
        resp = zcanpro.uds_request(bus_id, req)
        data = list((resp or {}).get("data") or [])
        if data:
            _log("[Rx] " + _hex(data[:24]))
        if len(data) >= 3 and data[0] == SID_NRC:
            if data[2] == NRC_RCRRP:
                if wait_pending_s <= 0 or time.time() >= t_end:
                    raise RuntimeError("NRC 0x78 超时")
                _log("NRC 0x78，等待中...")
                time.sleep(1.0)
                continue
            raise RuntimeError("NRC 0x%02X" % data[2])
        if not resp or not resp.get("result"):
            raise RuntimeError("无应答")
        if not data:
            raise RuntimeError("空响应")
        if data[0] != (sid + SID_PR):
            raise RuntimeError("非正响应: " + _hex(data))
        return data


def uds_try(bus_id, sid, payload):
    try:
        return uds_req(bus_id, sid, payload)
    except Exception:
        return None


# ======== 主流程 ========

def read_status(bus_id):
    """读取一次 DID 0x2132，返回 (state, progress, version, sent, total)"""
    rx = uds_req(bus_id, SID_RDBI, [(DID_QI_IAP_STATUS >> 8) & 0xFF, DID_QI_IAP_STATUS & 0xFF])

    # 响应: 62 [DID_H] [DID_L] [8 bytes]
    if len(rx) < 5:
        raise RuntimeError("DID 0x%04X 响应过短: %s" % (DID_QI_IAP_STATUS, _hex(rx)))

    data = rx[3:]  # 跳过 62 DID_H DID_L
    state    = data[0] if len(data) > 0 else 0xFF
    progress = data[1] if len(data) > 1 else 0
    version  = (data[2] | (data[3] << 8)) if len(data) > 3 else 0
    sent     = (data[4] | (data[5] << 8)) if len(data) > 5 else 0
    total    = (data[6] | (data[7] << 8)) if len(data) > 7 else 0

    return state, progress, version, sent, total


def run_once(bus_id):
    """单次查询"""
    _log("======== Qi IAP 状态查询 ========")
    uds_init()

    state, progress, version, sent, total = read_status(bus_id)

    state_str = STATE_NAMES.get(state, "未知 (0x%02X)" % state)
    _log("状态:     %s" % state_str)
    _log("进度:     %d%%" % progress)
    # 版本渲染与固件 0x2013 一致：整数 X 直接拼 "QC_JYF_MCU2_FW_1.1.X"
    # （can_protocol.c qi_ver_send_full_response），不是 BCD 拆 nibble
    _log("Qi 版本:  0x%04X (QC_JYF_MCU2_FW_1.1.%d)" % (version, version))
    _log("已发送:   %d / %d 字节" % (sent, total))

    if total > 0:
        _log("传输比:   %.1f%%" % (sent * 100.0 / total))

    return state, progress, version, sent, total


def run_poll(bus_id):
    """轮询模式，持续查询直到 state 变为 0x02(成功) 或 0x03(失败)"""
    _log("======== Qi IAP 状态轮询 (Ctrl+C 停止) ========")
    uds_init()

    while not stopTask:
        try:
            state, progress, version, sent, total = read_status(bus_id)
        except Exception as e:
            _log("读取失败: %s" % e)
            time.sleep(POLL_INTERVAL)
            continue

        state_str = STATE_NAMES.get(state, "未知 (0x%02X)" % state)
        _log("[%s] 进度 %d%%  已发 %d/%d  版本 0x%04X" % (
            state_str, progress, sent, total, version))

        if state == 0x02:
            _log("升级成功!")
            return
        if state == 0x03:
            _log("升级失败!")
            return

        # 发 TesterPresent 保活
        uds_try(bus_id, SID_TP, [0x00])
        time.sleep(POLL_INTERVAL)

    _log("轮询已停止")


def z_main():
    global stopTask
    stopTask = False

    _log("======== Qi IAP 状态查询工具 ========")
    _log("DID 0x%04X: state/progress/version/sent/total" % DID_QI_IAP_STATUS)

    buses = zcanpro.get_buses()
    if not buses:
        _log("请先打开 CAN 通道 (250kbps, 扩展帧)")
        return

    try:
        if "--poll" in sys.argv[1:]:
            run_poll(buses[0]["busID"])
        else:
            run_once(buses[0]["busID"])
    except Exception as e:
        _log("失败: " + str(e))
    finally:
        try:
            zcanpro.uds_deinit()
        except Exception:
            pass
