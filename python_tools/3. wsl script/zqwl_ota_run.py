#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""WSL2 独立 OTA runner —— 注入 zcanpro shim 后运行 zcanpro_ext_ota_auto.py。

用法（仓库根目录）：
  自测（唯一允许的硬件命令，只读 22 F195，不碰会话/写入类 SID）：
    python3 "python_tools/3. wsl script/zqwl_ota_run.py" --smoke-read
  完整 OTA（由主管验收后执行；脚本参数原样透传）：
    python3 "python_tools/3. wsl script/zqwl_ota_run.py" [--firmware "python_tools/app bin/xxx.bin"]

实现：
  1. import zcanpro_shim_zqwl 并 sys.modules['zcanpro'] = shim，
     zcanpro_ext_ota_auto.py 的 `import zcanpro` 即拿到兼容层；
  2. --smoke-read：走脚本自身 uds_req() 路径读 DID 0xF195 打印版本串，
     finally 释放 UDS + 关串口；
  3. 否则 runpy.run_path(zcanpro_ext_ota_auto.py, run_name="__main__")，
     argv 不改（脚本 parse_known_args 自行解析 --firmware，argv[0] 以 .py
     结尾触发其 __main__ → z_main() 自跑）。
"""
from __future__ import print_function

import os
import re
import runpy
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
# 本脚本已移入 python_tools/3. wsl script/，同级 shim 脚本用 HERE，
# 上一级 python_tools/（主脚本与 app bin）用 PT。
PT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
sys.path.insert(0, PT)

MAIN_SCRIPT = os.path.join(PT, "zcanpro_ext_ota_auto.py")


def _smoke_read():
    """只读链路自测：uds_init → uds_request(22 F1 95) → 打印版本串 → 释放。"""
    import zcanpro_shim_zqwl as shim
    sys.modules["zcanpro"] = shim
    import zcanpro_ext_ota_auto as ext  # 模块级仅常量/函数定义，无副作用

    bus_id = shim.get_buses()[0]["busID"]
    rx = None
    try:
        ext.uds_init()
        rx = ext.uds_req(bus_id, 0x22, [0xF1, 0x95])
        raw = bytes(int(b) & 0xFF for b in rx[3:])
        m = re.search(rb"QC_JYF_FW_[0-9A-Za-z._]+", raw)
        ver = (m.group(0).decode("ascii")
               if m else raw.split(b"\x00")[0].decode("ascii", "ignore").strip())
        print("SMOKE RX hex: " + " ".join("%02X" % b for b in rx))
        print("SMOKE VERSION: %s" % ver)
        if ver != "QC_JYF_FW_1.1.1":
            print("SMOKE FAIL: 版本串不符（预期 QC_JYF_FW_1.1.1）")
            return 1
        print("SMOKE PASS: 22 F195 TX/RX/ISO-TP 组装链路正常")
        return 0
    except Exception as e:
        print("SMOKE FAIL: %s" % e)
        return 1
    finally:
        try:
            shim.uds_deinit()
        except Exception:
            pass
        shim.close()  # 释放 /dev/ttyACM0


def main():
    if "--smoke-read" in sys.argv[1:]:
        return _smoke_read()
    import zcanpro_shim_zqwl as shim  # noqa: F401  （import 即注册 atexit 关口）
    sys.modules["zcanpro"] = shim
    runpy.run_path(MAIN_SCRIPT, run_name="__main__")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
