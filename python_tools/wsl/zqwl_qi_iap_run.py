#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""WSL2 Qi 芯片数据抓取 / IAP 升级 runner（ZQWL 串口 CAN）。

用法（仓库根目录）：
  抓 Qi 数据（版本/状态/UART）：
    python3 python_tools/wsl/zqwl_qi_iap_run.py --sniff
  读版本 + IAP 状态：
    python3 python_tools/wsl/zqwl_qi_iap_run.py --read
  Qi IAP 升级（log1.BIN 或 log2.BIN）：
    python3 python_tools/wsl/zqwl_qi_iap_run.py --iap log1
    python3 python_tools/wsl/zqwl_qi_iap_run.py --iap log2

实现要点：
  1. import zcanpro_shim_zqwl 并 sys.modules['zcanpro'] = shim；
  2. 会话/SA 自动完成，全程 3E 80 保活（S3=5s）；
  3. IAP 数据包按 0x2132 的 sent 计数推进，不依赖 6E 21 31
     （固件延迟应答竞态下 6E 可能丢，但 Qi ACK 后 sent 会涨）；
  4. 单包 NRC 0x72 / 超时且 sent 未涨 → 重试；连续失败过多才退出。
"""
from __future__ import print_function

import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(PT, "zcanpro", "qi"))

UDS_REQ_ID = 0x18DA0D03
UDS_RESP_ID = 0x18DA030D

DID_QI_IAP_CONTROL = 0x2130
DID_QI_IAP_DATA = 0x2131
DID_QI_IAP_STATUS = 0x2132
DID_QI_FW_VERSION = 0x2133
DID_QI_SNIFF = 0x2140
DID_CHG = 0x2102
DID_HALL = 0x2118

QI_IAP_DATA_LEN = 22
QI_IAP_MAX_RETRIES = 8
QI_IAP_PACKET_DELAY = 0.05
KEEPALIVE_S = 2.5


def _hex(data):
    return " ".join("%02X" % (int(b) & 0xFF) for b in (data or []))


class QiIapRunner(object):
    def __init__(self, shim, iap_mod):
        self.shim = shim
        self.iap = iap_mod
        self.bus = shim.get_buses()[0]["busID"]
        self._tp_last = 0.0

    def log(self, msg):
        sys.stdout.write(str(msg) + "\n")
        sys.stdout.flush()

    def keepalive(self):
        now = time.time()
        if now - self._tp_last < KEEPALIVE_S:
            return
        self._tp_last = now
        self.shim.uds_request(self.bus, {
            "src_addr": UDS_REQ_ID, "dst_addr": UDS_RESP_ID,
            "suppress_response": 1, "sid": 0x3E, "data": [0x80],
        })

    def uds(self, sid, data, wait=3.0, suppress=0):
        self.keepalive()
        req = {
            "src_addr": UDS_REQ_ID, "dst_addr": UDS_RESP_ID,
            "suppress_response": 1 if suppress else 0,
            "sid": sid, "data": list(data),
        }
        t_end = time.time() + float(wait)
        while True:
            resp = self.shim.uds_request(self.bus, req)
            if suppress:
                return []
            d = list((resp or {}).get("data") or [])
            if len(d) >= 3 and d[0] == 0x7F:
                if d[2] == 0x78 and time.time() < t_end:
                    time.sleep(0.25)
                    continue
                return d
            return d

    def rdbi(self, did, wait=3.0):
        return self.uds(0x22, [(did >> 8) & 0xFF, did & 0xFF], wait=wait)

    def sa_unlock(self):
        rx = self.uds(0x27, [0x01])
        if len(rx) < 4:
            raise RuntimeError("27 01 失败: " + _hex(rx))
        seed = rx[2:34]
        if seed == [0] * 32:
            self.log("SA: 已解锁")
            return
        self.log("SA seed " + _hex(seed[:8]) + " ...")
        sig = self.iap.ecdsa_sign_msg(
            self.iap.load_ec_private_key(self.iap.PRIVATE_KEY_PATH), bytes(seed))
        off = 0
        seq = 1
        while off < 64:
            self.uds(0x27, [0x03, seq] + list(sig[off:off + 4]))
            off += 4
            seq += 1
        time.sleep(0.1)
        self.keepalive()
        r = self.uds(0x27, [0x02], wait=45)
        if not r or r[0] != 0x67:
            raise RuntimeError("27 02 失败: " + _hex(r))
        self.log("SA: 解锁成功")

    def session(self, typ):
        r = self.uds(0x10, [typ])
        self.log("10 %02X -> %s" % (typ, _hex(r)))
        return r

    def iap_status(self):
        d = self.rdbi(DID_QI_IAP_STATUS)
        if len(d) < 11:
            return None
        return {
            "state": d[3], "progress": d[4],
            "ver": d[5] | (d[6] << 8),
            "sent": d[7] | (d[8] << 8),
            "total": d[9] | (d[10] << 8),
        }

    def qi_version(self):
        d = self.rdbi(DID_QI_FW_VERSION)
        if len(d) < 5:
            return None
        return d[3] | (d[4] << 8)

    def sniff_once(self, timeout_s=6.0, label=""):
        """轮询 0x2140，打印非空 UART 批次。"""
        self.log("---- Qi UART 抓取 %s (%.0fs) ----" % (label, timeout_s))
        t0 = time.time()
        n = 0
        while time.time() - t0 < timeout_s:
            self.keepalive()
            d = self.rdbi(DID_QI_SNIFF)
            if len(d) >= 5 and d[4] > 0:
                payload = d[5:5 + d[4]]
                self.log("  +%.2fs flags=%02X len=%d %s" % (
                    time.time() - t0, d[3], d[4], _hex(payload[:40])))
                n += 1
            time.sleep(0.05)
        self.log("非空批次: %d" % n)
        return n

    def read_info(self):
        self.log("==== Qi 信息读取 ====")
        self.log("2118 hall : %s" % _hex(self.rdbi(DID_HALL)))
        self.log("2102 chg  : %s" % _hex(self.rdbi(DID_CHG)))
        ver = self.qi_version()
        self.log("2133 qi   : 0x%04X (%s)" % (
            ver if ver is not None else 0,
            "QC_JYF_MCU2_FW_1.1.%d" % ver if ver else "未上报"))
        st = self.iap_status()
        self.log("2132 iap  : %s" % st)

    def enable_charge(self):
        """扩展会话+解锁后写 0x2101=0x01，给 Qi 芯片上电（PB2）。"""
        self.session(0x02)   # 先过一次编程会话，避免首次 10 03 门禁异常
        self.session(0x03)
        self.sa_unlock()
        d = self.uds(0x2E, [0x21, 0x01, 0x01])
        self.log("2E 2101 01 -> %s" % _hex(d))
        time.sleep(1.0)
        self.log("2102: %s  2133: %s" % (
            _hex(self.rdbi(DID_CHG)), _hex(self.rdbi(DID_QI_FW_VERSION))))

    def run_sniff(self, sniff_s=8.0):
        self.enable_charge()
        self.read_info()
        self.sniff_once(sniff_s, "charge-on")
        self.log("2013 版本问询: %s" % _hex(self.rdbi(0x2013, wait=3)))

    def run_read(self):
        self.read_info()

    def run_iap(self, which):
        name = which.lower()
        if name not in ("log1", "log2"):
            raise RuntimeError("--iap 需 log1 或 log2")
        fw_name = "log1.BIN" if name == "log1" else "log2.BIN"
        fw_path = os.path.join(self.iap.FIRMWARE_DIR, fw_name)
        if not os.path.isfile(fw_path):
            raise RuntimeError("找不到固件: " + fw_path)
        fw = open(fw_path, "rb").read()
        fw_size = len(fw)
        self.log("固件: %s (%d 字节)" % (fw_path, fw_size))

        # 先给 Qi 上电，拿到版本/心跳再进编程会话做 IAP
        self.enable_charge()
        self.sniff_once(2.0, "pre-iap")

        self.session(0x02)
        self.sa_unlock()

        # 清残留 IAP
        self.uds(0x2E, [0x21, 0x30, 0x02], wait=3)
        self.log("abort 后 2132: %s" % self.iap_status())

        self.log("---- 启动 IAP ----")
        d = self.uds(0x2E, [0x21, 0x30, 0x01, (fw_size >> 8) & 0xFF, fw_size & 0xFF], wait=5)
        self.log("2130 -> %s" % _hex(d))
        st = self.iap_status()
        self.log("status: %s" % st)
        if not st or st.get("total") != fw_size:
            # 延迟应答可能丢，状态确认即可
            time.sleep(0.5)
            st = self.iap_status()
            self.log("status: %s" % st)
            if not st or st.get("total") != fw_size:
                raise RuntimeError("IAP 启动失败: %s" % st)

        self.log("---- 发送固件数据 ----")
        addr = 0
        pkt = 0
        fails = 0
        t0 = time.time()
        # Qi 侧 IAP 写缓冲/Flash 页 256B：包不可跨页，否则 ACK status=0x01
        PAGE = 256
        while addr < fw_size:
            page_left = PAGE - (addr % PAGE)
            chunk_len = min(QI_IAP_DATA_LEN, page_left, fw_size - addr)
            chunk = fw[addr:addr + chunk_len]
            before = self.iap_status()
            if before is None:
                time.sleep(0.2)
                continue
            if before["state"] == 0x03:
                raise RuntimeError("Qi IAP FAILED at 0x%04X %s" % (addr, before))
            if before["state"] == 0x02:
                self.log("提前 SUCCESS")
                break
            d = self.uds(
                0x2E,
                [0x21, 0x31, (addr >> 8) & 0xFF, addr & 0xFF] + list(chunk),
                wait=2.2)
            pkt += 1
            after = self.iap_status()
            ok = False
            if d and d[0] == 0x6E:
                ok = True
            if after and before and after["sent"] > before["sent"]:
                ok = True
            if after and after.get("state") == 0x02:
                ok = True
                break
            if ok:
                addr += len(chunk)
                fails = 0
            else:
                fails += 1
                if fails <= 3 or fails % 10 == 0:
                    self.log("  retry#%d pkt%d addr=0x%04X len=%d %s %s" % (
                        fails, pkt, addr, chunk_len, _hex(d), after))
                time.sleep(0.35)
                if fails >= QI_IAP_MAX_RETRIES:
                    raise RuntimeError("连续 %d 次失败 @0x%04X" % (fails, addr))
            if pkt % 20 == 0:
                self.log("  pkt%d addr=0x%04X (%d%%) %s t=%.0fs" % (
                    pkt, addr, addr * 100 // fw_size,
                    after, time.time() - t0))
            time.sleep(QI_IAP_PACKET_DELAY)

        self.log("---- 等待完成 ----")
        ok = False
        for _ in range(15):
            time.sleep(0.7)
            self.keepalive()
            st = self.iap_status()
            self.log("  %s" % st)
            if st and st["state"] == 0x02:
                ok = True
                break
            if st and st["state"] == 0x03:
                break
        if not ok:
            raise RuntimeError("IAP 未完成: %s" % st)
        self.log("======== Qi IAP 升级成功 ========")
        self.log("2133: %s" % _hex(self.rdbi(DID_QI_FW_VERSION)))
        return 0


def main():
    args = sys.argv[1:]
    sniff_s = 8.0
    if "--sniff" in args:
        for i, a in enumerate(args):
            if a == "--sniff-sec" and i + 1 < len(args):
                sniff_s = float(args[i + 1])

    import zcanpro_shim_zqwl as shim
    sys.modules["zcanpro"] = shim
    import zcanpro_qi_iap_log1 as iap_mod

    runner = QiIapRunner(shim, iap_mod)
    rc = 0
    try:
        shim.uds_init({
            "response_timeout_ms": 3000, "use_canfd": 0, "canfd_brs": 0,
            "trans_ver": 0, "fill_byte": 0xCC, "frame_type": 1,
            "trans_stmin_valid": 1, "trans_stmin": 1,
            "enhanced_timeout_ms": 8000,
        })
        if "--sniff" in args:
            runner.run_sniff(sniff_s)
        elif "--read" in args:
            runner.run_read()
        elif "--iap" in args:
            idx = args.index("--iap")
            which = args[idx + 1] if idx + 1 < len(args) else "log1"
            runner.run_iap(which)
        else:
            runner.log("用法: --sniff | --read | --iap log1|log2")
            rc = 2
    except Exception as e:
        runner.log("FAIL: %s" % e)
        rc = 1
    finally:
        try:
            shim.uds_deinit()
        except Exception:
            pass
        shim.close()
    return rc


if __name__ == "__main__":
    sys.exit(main())
