# -*- coding: utf-8 -*-
"""
ZCANPRO 脚本 — 读取 Qi 芯片固件版本

读 DID 0x2133（uint16 LE），来自 UART 0x01 上报缓存。
未收到上报时为 0x0000。

不需要编程会话或安全解锁，任意会话可读。

用法: ZCANPRO → 高级功能 → 扩展脚本 → 打开本文件
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
SID_NRC  = 0x7F
SID_PR   = 0x40

NRC_RCRRP = 0x78

DID_QI_FW_VERSION = 0x2133

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


# ======== 主流程 ========

def run(bus_id):
    _log("======== 读取 Qi 芯片固件版本 ========")
    uds_init()

    rx = uds_req(bus_id, SID_RDBI, [(DID_QI_FW_VERSION >> 8) & 0xFF, DID_QI_FW_VERSION & 0xFF])

    # 响应: 62 [DID_H] [DID_L] [ver_lo] [ver_hi]
    if len(rx) < 5:
        raise RuntimeError("DID 0x%04X 响应过短: %s" % (DID_QI_FW_VERSION, _hex(rx)))

    ver_lo = rx[3]
    ver_hi = rx[4]
    version = ver_lo | (ver_hi << 8)

    _log("DID 0x%04X = %04X" % (DID_QI_FW_VERSION, version))
    # 渲染与固件 0x2013 一致：整数 X 拼 "QC_JYF_MCU2_FW_1.1.X"，非 BCD 拆分
    _log("Qi 芯片固件版本: QC_JYF_MCU2_FW_1.1.%d (raw 0x%04X)" % (version, version))

    if version == 0x0000:
        _log("提示: 版本为 0，MCU 尚未收到 Qi 芯片 UART 0x01 上报")


def z_main():
    global stopTask
    stopTask = False
    _log("======== Qi 芯片版本读取工具 ========")
    buses = zcanpro.get_buses()
    if not buses:
        _log("请先打开 CAN 通道 (250kbps, 扩展帧)")
        return
    try:
        run(buses[0]["busID"])
    except Exception as e:
        _log("失败: " + str(e))
    finally:
        try:
            zcanpro.uds_deinit()
        except Exception:
            pass
