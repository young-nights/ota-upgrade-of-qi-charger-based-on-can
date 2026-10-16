# -*- coding: utf-8 -*-
"""
ZCANPRO 脚本 — Qi 芯片状态全量查询（IAP + 充电状态 + 传感器）

读取以下 DID 并解码显示：
  0x2132  IAP 状态（9 字节）: state/progress/版本3B/已发/总长
  0x2102  充电状态机（uint8）: PING/充电/充满/保护等
  0x2104  输出功率（uint16 LE, mW）
  0x2105  输入电压/电流（2×uint8, raw）
  0x2108  PCB 温度（uint8, ℃）
  0x2109  FOD 异物检测状态（uint8）
  0x210B  故障码（uint8）
  0x210C  热降额等级（uint8, 100%=无降额）

不需要编程会话或安全解锁，任意会话可读。

用法: ZCANPRO → 高级功能 → 扩展脚本 → 打开本文件（默认单次查询）
      附加 --poll 进入轮询模式（每 1s 一次）
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

DID_QI_IAP_STATUS  = 0x2132
DID_CHARGE_STATE   = 0x2102
DID_OUTPUT_POWER   = 0x2104
DID_INPUT_VI       = 0x2105
DID_PCB_TEMP       = 0x2108
DID_FOD_STATUS     = 0x2109
DID_FAULT_CODE     = 0x210B
DID_THERMAL_DERATE = 0x210C

# IAP 状态码
IAP_STATE_NAMES = {
    0x00: "空闲 (IDLE)",
    0x01: "升级中 (IN_PROGRESS)",
    0x02: "成功 (SUCCESS)",
    0x03: "失败 (FAILED)",
}

# 充电状态机（DID 0x2102，与 can_protocol.h QI_CHARGE_* 一致）
CHARGE_STATE_NAMES = {
    0x00: "关闭 (DISABLED)",
    0x01: "待机 (STANDBY)",
    0x02: "设备已检测 (DEVICE_DETECTED)",
    0x03: "协商中 (NEGOTIATING)",
    0x04: "充电中 (CHARGING)",
    0x05: "充满 (COMPLETE)",
    0x06: "热保护暂停 (SUSPENDED_THERMAL)",
    0x07: "FOD 暂停 (SUSPENDED_FOD)",
    0x08: "故障 (FAULT)",
    0x09: "服务模式 (SERVICE_MODE)",
    0x0A: "低功耗 (LOW_POWER)",
}

# 状态字节位定义（qi_protocol.h QI_STATUS_*）
STATUS_BITS = [
    (0, "PING(设备放置)"),
    (1, "CHARGING(充电中)"),
    (2, "FULL(充满)"),
    (3, "OVP(过压)"),
    (4, "UVP(欠压)"),
    (5, "OCP(过流)"),
    (6, "OTP(过温)"),
    (7, "FOD(异物)"),
]

# 故障码
FAULT_NAMES = {
    0x00: "无故障",
    0x04: "输入过压 (OVP)",
    0x05: "输入欠压 (UVP)",
    0x06: "异物检测 (FOD)",
    0x07: "线圈过温 (OTP)",
    0x0A: "输入过流 (OCP)",
}

# FOD 状态
FOD_NAMES = {
    0x00: "正常",
    0x01: "检测中",
    0x02: "已确认异物",
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


def _read_did_u8(bus_id, did):
    """读单字节 DID，失败返回 None"""
    try:
        rx = uds_req(bus_id, SID_RDBI, [(did >> 8) & 0xFF, did & 0xFF])
        return rx[3] if len(rx) > 3 else None
    except Exception:
        return None


def _read_did_u16le(bus_id, did):
    """读 uint16 LE DID，失败返回 None"""
    try:
        rx = uds_req(bus_id, SID_RDBI, [(did >> 8) & 0xFF, did & 0xFF])
        return (rx[3] | (rx[4] << 8)) if len(rx) > 4 else None
    except Exception:
        return None


# ======== 解析 ========

def read_iap_status(bus_id):
    """读 DID 0x2132"""
    rx = uds_req(bus_id, SID_RDBI, [(DID_QI_IAP_STATUS >> 8) & 0xFF, DID_QI_IAP_STATUS & 0xFF])
    if len(rx) < 12:
        raise RuntimeError("DID 0x%04X 响应过短: %s" % (DID_QI_IAP_STATUS, _hex(rx)))
    d = rx[3:]
    return {
        "state": d[0], "progress": d[1],
        "ver": (d[2], d[3], d[4]),
        "sent": d[5] | (d[6] << 8),
        "total": d[7] | (d[8] << 8),
    }


def decode_charge_state(state_byte):
    """解码充电状态机 + PING 标识"""
    name = CHARGE_STATE_NAMES.get(state_byte, "未知(0x%02X)" % state_byte)
    ping = "有" if state_byte >= 0x02 else "无"
    return name, ping


def read_and_display_charge(bus_id):
    """读取并显示充电状态 + 传感器数据"""
    _log("")
    _log("---- 充电状态 ----")

    charge_state = _read_did_u8(bus_id, DID_CHARGE_STATE)
    if charge_state is not None:
        cs_name, ping = decode_charge_state(charge_state)
        _log("充电状态: %s" % cs_name)
        _log("PING:     %s设备" % ping)
    else:
        _log("充电状态: 读取失败")

    power_mw = _read_did_u16le(bus_id, DID_OUTPUT_POWER)
    if power_mw is not None:
        _log("输出功率: %d mW" % power_mw)

    vi = uds_try(bus_id, SID_RDBI, [(DID_INPUT_VI >> 8) & 0xFF, DID_INPUT_VI & 0xFF])
    if vi and len(vi) > 4:
        _log("输入电压: %d (raw)" % vi[3])
        _log("输入电流: %d (raw)" % vi[4])

    temp = _read_did_u8(bus_id, DID_PCB_TEMP)
    if temp is not None:
        _log("PCB 温度: %d℃" % temp)

    fod = _read_did_u8(bus_id, DID_FOD_STATUS)
    if fod is not None:
        _log("FOD 状态: %s" % FOD_NAMES.get(fod, "0x%02X" % fod))

    fault = _read_did_u8(bus_id, DID_FAULT_CODE)
    if fault is not None:
        _log("故障码:   %s" % FAULT_NAMES.get(fault, "0x%02X" % fault))

    thermal = _read_did_u8(bus_id, DID_THERMAL_DERATE)
    if thermal is not None:
        _log("热降额:   %d%%" % thermal)

    # 保护位提示（从充电状态推断）
    if charge_state in (0x06, 0x07, 0x08):
        _log("⚠ 保护/故障状态激活，检查上方故障码和 FOD")


def run_once(bus_id):
    """单次全量查询"""
    _log("======== Qi 芯片状态全量查询 ========")
    uds_init()

    # IAP 状态
    _log("---- IAP 状态 ----")
    iap = read_iap_status(bus_id)
    state_str = IAP_STATE_NAMES.get(iap["state"], "未知(0x%02X)" % iap["state"])
    _log("IAP 状态: %s" % state_str)
    _log("升级进度: %d%%" % iap["progress"])
    vmaj, vmin, vpat = iap["ver"]
    _log("Qi 版本:  %d.%d.%d" % (vmaj, vmin, vpat))
    _log("已发送:   %d / %d 字节" % (iap["sent"], iap["total"]))
    if iap["total"] > 0:
        _log("传输比:   %.1f%%" % (iap["sent"] * 100.0 / iap["total"]))

    # 充电状态
    read_and_display_charge(bus_id)
    _log("======== 查询完成 ========")


def run_poll(bus_id):
    """轮询模式"""
    _log("======== Qi 状态轮询 (Ctrl+C 停止) ========")
    uds_init()

    while not stopTask:
        try:
            iap = read_iap_status(bus_id)
            state_str = IAP_STATE_NAMES.get(iap["state"], "0x%02X" % iap["state"])
            vmaj, vmin, vpat = iap["ver"]
            _log("[%s] IAP %d%%  已发 %d/%d  Qi版本 %d.%d.%d" % (
                state_str, iap["progress"], iap["sent"], iap["total"],
                vmaj, vmin, vpat))

            cs = _read_did_u8(bus_id, DID_CHARGE_STATE)
            if cs is not None:
                cs_name, _ = decode_charge_state(cs)
                pw = _read_did_u16le(bus_id, DID_OUTPUT_POWER)
                _log("  充电: %s  功率: %s" % (cs_name, "%dmW" % pw if pw is not None else "?"))

            if iap["state"] == 0x02:
                _log("升级成功!")
                return
            if iap["state"] == 0x03:
                _log("升级失败!")
                return
        except Exception as e:
            _log("读取失败: %s" % e)

        uds_try(bus_id, SID_TP, [0x00])
        time.sleep(POLL_INTERVAL)

    _log("轮询已停止")


def z_main():
    global stopTask
    stopTask = False

    _log("======== Qi 芯片状态查询工具 ========")

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
