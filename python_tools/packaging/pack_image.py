# -*- coding: utf-8 -*-
"""Pack the App Keil raw .bin into a XATO image (256B header + firmware).

Single-App architecture (OTA-ARCH-0920): one image, linked for the App
window (Keil IROM1 = 0x08004100). Factory flashing writes the packed
image at 0x08004000 (merge_prod_bin.py); OTA streams it into the Backup
region (0x08010000) and BOOT copies it into the App region after verify.

Header layout is byte-frozen: @0x4C is a 16B reserved placeholder (the
original version field was removed; packing fills 0x00). Version identity
lives only in firmware SW_VERSION_STR (can_protocol.c).
"""

from __future__ import print_function

import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PARENT = os.path.dirname(HERE)
ZCANPRO_NONQI = os.path.join(PARENT, "zcanpro", "non-qi")
if PARENT not in sys.path:
    sys.path.insert(0, PARENT)
if ZCANPRO_NONQI not in sys.path:
    sys.path.insert(0, ZCANPRO_NONQI)
if HERE not in sys.path:
    sys.path.insert(0, HERE)

from zcanpro_ext_ota_auto import (  # noqa: E402
    APP_BASE,
    IMAGE_HEADER_SIZE,
    load_ec_private_key,
    pack_image_if_needed,
    validate_image,
)

REPO_ROOT = os.path.dirname(os.path.dirname(HERE))
APP_BIN_DIR = os.path.join(PARENT, "app bin")
DEFAULT_BIN = os.path.join(REPO_ROOT, "qi_wireless_code_app", "mdk_project",
                           "Objects", "qi_wireless_code_app.bin")
DEFAULT_KEY = os.path.join(REPO_ROOT, "docs", "keys", "private.pem")
DEFAULT_OUT_NAME = "app_image.bin"
# 版本串与 zcanpro_ext_ota_auto._extract_sw_version 同源：bin strings 扫描
# QC_JYF_FW_ 前缀（版本唯一真相源=固件 can_protocol.c SW_VERSION_STR）。
# 同样取第一处命中，与 OTA 执行器版本闸门的提取语义保持一致。
_FW_VER_RE = re.compile(rb"QC_JYF_FW_(\d+)\.(\d+)\.(\d+)")


def _extract_version_from_bin(bin_data):
    """从 bin 数据中提取 QC_JYF_FW_x.y.z 版本号，返回 "x.y.z"。

    未找到标记串或格式不符时返回 None（调用方 fallback 固定输出名）。"""
    if not bin_data:
        return None
    m = _FW_VER_RE.search(bin_data)
    if m is None:
        return None
    return "%d.%d.%d" % (int(m.group(1)), int(m.group(2)), int(m.group(3)))


def pack_one(bin_path, priv, out_name=None):
    """Pack one App firmware; return 0 on success, 1 on failure.

    out_name=None（默认）→ 按版本号自动命名：打包前先从原始 bin 提取
    QC_JYF_FW_x.y.z，输出 app_image_v{x}_{y}_{z}.bin；提取失败 fallback
    app_image.bin 并打印 WARNING（兼容无版本串的旧载荷）。
    显式传 out_name → 手动覆盖，忽略自动命名。"""
    if not os.path.isfile(bin_path):
        print("skip (not found): %s" % bin_path)
        return 1

    raw = open(bin_path, "rb").read()
    version = _extract_version_from_bin(raw)
    if out_name:
        final_name = out_name
        print("version  : %s（--out-name 手动指定 %s，忽略自动命名）"
              % (version or "未检测到", out_name))
    elif version:
        final_name = "app_image_v%s.bin" % version.replace(".", "_")
        print("version  : 检测到 %s（bin strings 扫描 QC_JYF_FW_ 前缀）" % version)
    else:
        final_name = DEFAULT_OUT_NAME
        print("version  : WARNING 未检测到 QC_JYF_FW_x.y.z 版本号，"
              "fallback 输出名 %s" % DEFAULT_OUT_NAME)

    image = pack_image_if_needed(bin_path, priv)
    validate_image(image)

    if not os.path.isdir(APP_BIN_DIR):
        os.makedirs(APP_BIN_DIR)
    out_path = os.path.join(APP_BIN_DIR, final_name)
    with open(out_path, "wb") as f:
        f.write(image)

    print("output   : %s" % os.path.abspath(out_path))
    print("named    : %s（版本 %s）" % (final_name, version or "未检测到"))
    print("total    : %d  (header %d + firmware %d)"
          % (len(image), IMAGE_HEADER_SIZE, len(image) - IMAGE_HEADER_SIZE))
    print("link     : App window  -> factory burn address 0x%08X" % APP_BASE)
    print("OTA flow : host streams this image to Backup 0x08010000;")
    print("           BOOT copies Backup -> App 0x08004000 after verify")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Pack App firmware into a XATO image (single-App arch)")
    parser.add_argument("--bin", default=DEFAULT_BIN,
                        help="Keil raw bin path (default: qi_wireless_code_app "
                             "Objects/qi_wireless_code_app.bin)")
    parser.add_argument("--key", default=DEFAULT_KEY,
                        help="ECDSA private key PEM (default: docs/keys/private.pem)")
    parser.add_argument("--out-name", default=None,
                        help="手动指定 app bin/ 内输出文件名（指定时忽略按版本号"
                             "自动命名；默认自动：app_image_v{major}_{minor}_"
                             "{patch}.bin，未提取到版本号时 %s）" % DEFAULT_OUT_NAME)
    args = parser.parse_args(argv)

    priv = load_ec_private_key(args.key)
    return pack_one(args.bin, priv, args.out_name)


if __name__ == "__main__":
    sys.exit(main())
