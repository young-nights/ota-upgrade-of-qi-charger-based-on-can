# -*- coding: utf-8 -*-
"""Verify a packed XATO app image (magic/length/CRC/reset vector/ECDSA).

Single-App architecture (OTA-ARCH-0920): the image must be linked for the
App window — reset vector inside [0x08004100, 0x08010000). The image is
staged at Backup 0x08010000 during OTA and copied verbatim to App
0x08004000 by BOOT; one image serves both locations.

Usage:
    python verify_image.py app bin/app_image.bin
    python verify_image.py app bin/app_image.bin --key docs/keys/public.pem
"""

from __future__ import print_function

import argparse
import binascii
import os
import struct
import sys

IMAGE_MAGIC = 0x4F544158  # "XATO"
IMAGE_HEADER_SIZE = 256

APP_BASE_ADDR = 0x08004000      # App image region (incl. header)
APP_SIZE = 0xC000               # 48KB
APP_ENTRY_ADDR = APP_BASE_ADDR + IMAGE_HEADER_SIZE   # 0x08004100
BACKUP_BASE_ADDR = 0x08010000   # OTA staging region (same image verbatim)

HDR_MAGIC_OFF = 0x00
HDR_LEN_OFF = 0x04
HDR_CRC_OFF = 0x08
HDR_SIG_OFF = 0x0C              # 64B ECDSA R||S
HDR_RESERVED_VER_OFF = 0x4C     # 16B reserved placeholder, filled 0x00
HDR_BUILD_TS_OFF = 0x5C


def verify_magic(header):
    magic = struct.unpack_from("<I", header, HDR_MAGIC_OFF)[0]
    return magic == IMAGE_MAGIC, magic


def verify_image_length(header, firmware_len):
    image_length = struct.unpack_from("<I", header, HDR_LEN_OFF)[0]
    return image_length == firmware_len and image_length <= (APP_SIZE - IMAGE_HEADER_SIZE), image_length


def verify_crc32(header, firmware):
    stored = struct.unpack_from("<I", header, HDR_CRC_OFF)[0]
    computed = binascii.crc32(firmware) & 0xFFFFFFFF
    return stored == computed, stored, computed


def p1363_to_der(sig_bytes):
    """Convert 64B IEEE P1363 R||S to DER SEQUENCE for openssl checks."""
    r = int.from_bytes(sig_bytes[:32], "big")
    s = int.from_bytes(sig_bytes[32:], "big")

    def _int_der(v):
        b = v.to_bytes((v.bit_length() + 7) // 8 or 1, "big")
        if b[0] & 0x80:
            b = b"\x00" + b
        return b"\x02" + bytes([len(b)]) + b

    body = _int_der(r) + _int_der(s)
    return b"\x30" + bytes([len(body)]) + body


def pem_to_sec1(public_key_path):
    """Extract uncompressed SEC1 point (04||X||Y) from an SPKI PEM.

    DEPRECATED (D4-M1): verify_ecdsa no longer converts the key to raw
    SEC1 — openssl dgst -verify requires a PEM public key, so the key
    file is now written through verbatim. Kept for reference/back-compat
    only; do not use on signing/verification paths.
    """
    raw = open(public_key_path, "rb").read()
    text = raw.decode("ascii", "ignore")
    lines = [l.strip() for l in text.splitlines() if "BEGIN" not in l and "END" not in l]
    der = binascii.a2b_base64("".join(lines))
    i = der.find(b"\x03")
    while i >= 0:
        j = i + 1
        ln = der[j]
        j += 1
        if ln & 0x80:
            k = ln & 0x7F
            ln = 0
            for _ in range(k):
                ln = (ln << 8) | der[j]
                j += 1
        if ln == 66 and der[j] == 0x00 and der[j + 1] == 0x04:
            return der[j + 2:j + 66]
        i = der.find(b"\x03", i + 1)
    raise ValueError("no P-256 uncompressed point found in %s" % public_key_path)


def verify_reset_handler(image):
    """Reset vector must land inside the App run window."""
    if len(image) < IMAGE_HEADER_SIZE + 8:
        return False, 0
    reset = struct.unpack_from("<I", image, IMAGE_HEADER_SIZE + 4)[0] & 0xFFFFFFFE
    ok = APP_ENTRY_ADDR <= reset < (APP_BASE_ADDR + APP_SIZE)
    return ok, reset


def verify_ecdsa(firmware, signature, public_key_path):
    """Independent host-side ECDSA check via openssl (best effort)."""
    try:
        import subprocess
        import tempfile
        der_sig = p1363_to_der(signature)
        with tempfile.NamedTemporaryFile(delete=False) as f_sig, \
             tempfile.NamedTemporaryFile(delete=False) as f_pub, \
             tempfile.NamedTemporaryFile(delete=False) as f_dgst:
            sig_path, pub_path, dgst_path = f_sig.name, f_pub.name, f_dgst.name
            f_sig.write(der_sig)
            # D4-M1 ②: write firmware verbatim — the signature domain is a
            # single SHA256 over the payload; let openssl dgst -sha256 do
            # that one hash (pre-hashing here caused a double SHA256).
            f_dgst.write(firmware)
            # D4-M1 ①: openssl dgst -verify needs a PEM *public* key, not a
            # raw SEC1 point (the old 0x04-prefix SEC1 mangling dropped X[0]
            # and fed raw bytes -> "Could not read public key"). Public PEM
            # is written through verbatim; a private-key PEM (e.g.
            # docs/keys/private.pem) cannot be read by OpenSSL 3.x dgst as a
            # verify key, so derive the public PEM via `openssl pkey
            # -pubout`; fall back to a verbatim write if that ever fails.
            key_raw = open(public_key_path, 'rb').read()
            if b"PRIVATE" in key_raw:
                f_pub.close()
                rc_pub = subprocess.call(
                    ["openssl", "pkey", "-in", public_key_path,
                     "-pubout", "-out", pub_path],
                    stdout=open(os.devnull, "w"),
                    stderr=open(os.devnull, "w"))
                if rc_pub != 0:
                    with open(pub_path, "wb") as fh:
                        fh.write(key_raw)
            else:
                f_pub.write(open(public_key_path, 'rb').read())
        rc = subprocess.call(["openssl", "dgst", "-sha256", "-verify", pub_path,
                              "-signature", sig_path, dgst_path],
                             stdout=open(os.devnull, "w"),
                             stderr=open(os.devnull, "w"))
        for p in (sig_path, pub_path, dgst_path):
            os.unlink(p)
        return rc == 0, "openssl exit %d" % rc
    except Exception as e:
        return None, "skipped (%s)" % e


def main(argv=None):
    parser = argparse.ArgumentParser(description="Verify packed XATO app image")
    parser.add_argument("image", help="packed image path (app bin/app_image.bin)")
    parser.add_argument("--key", default=None,
                        help="public key PEM for ECDSA check (optional)")
    args = parser.parse_args(argv)

    data = open(args.image, "rb").read()
    if len(data) < IMAGE_HEADER_SIZE:
        sys.stderr.write("ERROR: image too short: {} bytes\n".format(len(data)))
        return 1
    header = data[:IMAGE_HEADER_SIZE]
    firmware = data[IMAGE_HEADER_SIZE:]

    ok_magic, magic = verify_magic(header)
    ok_len, image_length = verify_image_length(header, len(firmware))
    ok_crc, stored_crc, computed_crc = verify_crc32(header, firmware)
    ok_reset, reset = verify_reset_handler(data)
    reserved_ver = header[HDR_RESERVED_VER_OFF:HDR_RESERVED_VER_OFF + 16]
    build_ts = struct.unpack_from("<I", header, HDR_BUILD_TS_OFF)[0]

    print("file         : {}".format(args.image))
    print("total        : {} bytes (header {} + firmware {})".format(
        len(data), IMAGE_HEADER_SIZE, len(firmware)))
    print("target       : App window base=0x{:08X} size=0x{:04X} (OTA staging: Backup 0x{:08X})".format(
        APP_BASE_ADDR, APP_SIZE, BACKUP_BASE_ADDR))
    print("magic        : {} (0x{:08X})".format("OK" if ok_magic else "FAIL", magic))
    print("image_length : {} (header={}, firmware={}) -> {}".format(
        image_length, image_length, len(firmware), "OK" if ok_len else "FAIL"))
    print("crc32        : stored=0x{:08X} computed=0x{:08X} -> {}".format(
        stored_crc, computed_crc, "OK" if ok_crc else "FAIL"))
    print("reset vector : 0x{:08X} window=[0x{:08X},0x{:08X}) -> {}".format(
        reset, APP_ENTRY_ADDR, APP_BASE_ADDR + APP_SIZE,
        "OK" if ok_reset else "FAIL"))
    print("hdr @0x4C    : reserved placeholder = {}".format(reserved_ver.hex()))
    # D4-R1: audit visibility only — does not change PASS/FAIL.
    if reserved_ver != b"\x00" * 16:
        print("WARNING: hdr_reserved_ver @0x4C not all-zero (expected 0x00)")
    print("build ts     : {}".format(build_ts))

    ok_sig = None
    if args.key:
        ok_sig, note = verify_ecdsa(firmware, header[HDR_SIG_OFF:HDR_SIG_OFF + 64], args.key)
        print("ecdsa        : {}{}".format(
            "OK" if ok_sig else ("FAIL" if ok_sig is False else "N/A"), " " + note))

    all_ok = ok_magic and ok_len and ok_crc and ok_reset and (ok_sig is not False)
    print("")
    print("RESULT: {}".format("PASS" if all_ok else "FAIL"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
