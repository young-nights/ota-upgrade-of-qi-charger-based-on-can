#!/usr/bin/env python3
"""Generate SecurityAccess (0x27) signature for a given seed.

For manual testing: receive 32-byte seed from MCU, compute ECDSA P-256 signature,
output the hex CAN frames ready for ZCANPRO / manual send.

Usage:
    python sign_seed.py <seed_hex> [--key <private_key.pem>]
    python sign_seed.py 00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF
    python sign_seed.py 00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF \
        --key docs/keys/private.pem

Prerequisites:
    Python 3.6+, OpenSSL CLI (default) or pip install ecdsa (fallback)
"""

from __future__ import print_function

import argparse
import hashlib
import os
import subprocess
from ecdsa import SigningKey, der
import sys
import tempfile

def _find_repo_root(start):
    """向上探测仓库根目录（含 docs/keys 的祖先目录），不写死目录层级假设。"""
    d = os.path.abspath(start)
    while True:
        if os.path.isdir(os.path.join(d, "docs", "keys")):
            return d
        parent = os.path.dirname(d)
        if parent == d:
            return os.path.abspath(start)
        d = parent


REPO_ROOT = _find_repo_root(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_KEY = os.path.join(REPO_ROOT, "docs", "keys", "private.pem")

# CAN IDs (extended frame, match project config)
UDS_REQ_ID = "18DA0D03"
UDS_RESP_ID = "18DA030D"

# Protocol constants (match MCU code)
SIG_SIZE = 64                    # ECDSA P-256 signature = 64 bytes
CHUNK_SIZE = 4                   # signature bytes per 27 03 frame
HEADER_SIZE = 3                  # UDS overhead: SID + sub_func + blockSeq
CAN_FRAME_DATA = HEADER_SIZE + CHUNK_SIZE  # = 7 bytes per CAN frame


def sha256_hash(data):
    """SHA-256 hash, returns 32 bytes."""
    return hashlib.sha256(data).digest()


def sign_with_openssl(seed_bytes, private_key_path):
    """Sign using OpenSSL CLI (no pip dependency).

    Uses 'dgst -sha256 -sign' which hashes the raw input with SHA-256
    then signs with the ECDSA private key.  This matches MCU behavior:
      MCU: uECC_verify(pubkey, SHA256(firmware), sig)
      Here: SHA256(seed_bytes) signed by private key
    """
    tmpdir = tempfile.mkdtemp(prefix="sign_")
    try:
        seed_path = os.path.join(tmpdir, "seed.bin")
        with open(seed_path, "wb") as f:
            f.write(seed_bytes)

        result = subprocess.Popen(
            [
                "openssl", "dgst", "-sha256",
                "-sign", private_key_path,
                seed_path,
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        stdout, stderr = result.communicate()

        if result.returncode != 0:
            raise RuntimeError("OpenSSL sign failed: {}".format(stderr.decode().strip()))

        return der_to_p1363(stdout)
    finally:
        import shutil
        shutil.rmtree(tmpdir, ignore_errors=True)


def sign_with_ecdsa_lib(seed_hash, private_key_path):
    """Sign using ecdsa Python library (pip install ecdsa)."""

    with open(private_key_path, "r") as f:
        pem_data = f.read()

    sk = SigningKey.from_pem(pem_data)
    sig_der = sk.sign_digest(seed_hash, sigencode=der.sigencode)
    return der_to_p1363(sig_der)


def der_to_p1363(der_sig):
    """Convert DER-encoded ECDSA signature to IEEE P1363 R||S (64 bytes)."""
    if der_sig[0] != 0x30:
        raise ValueError("Invalid DER signature: not a sequence")

    idx = 2
    if der_sig[1] & 0x80:
        len_bytes = der_sig[1] & 0x7F
        idx = 2 + len_bytes

    if der_sig[idx] != 0x02:
        raise ValueError("Invalid DER: expected INTEGER for R")
    idx += 1
    r_len = der_sig[idx]
    idx += 1
    r_bytes = der_sig[idx:idx + r_len]
    idx += r_len

    if der_sig[idx] != 0x02:
        raise ValueError("Invalid DER: expected INTEGER for S")
    idx += 1
    s_len = der_sig[idx]
    idx += 1
    s_bytes = der_sig[idx:idx + s_len]

    # Take last 32 bytes: strips leading 0x00 padding from DER integer encoding
    # (rjust would NOT truncate a 33-byte value, causing >64 byte signatures)
    r = r_bytes[-32:]
    s = s_bytes[-32:]
    return r + s


def build_iso_tp_sf(payload):
    """Build ISO-TP Single Frame from UDS payload.

    Classic CAN (8-byte frame):
      Byte 0: PCI = 0x0N (N = payload length, max 7)
      Bytes 1..N: UDS payload

    For 7-byte payload: PCI = 0x07
    """
    n = len(payload)
    if n > 7:
        raise ValueError("SF payload too long: {} bytes (max 7)".format(n))
    frame = bytes([0x00 | n]) + payload
    return frame.ljust(8, b"\x00")  # pad to 8 bytes


def build_iso_tp_ff(payload):
    """Build ISO-TP First Frame from UDS payload.

    FF PCI: [0x1X, YY] where XXYY = payload length
    Data: first 6 bytes of payload
    """
    n = len(payload)
    ff_pci = bytes([0x10 | (n >> 8), n & 0xFF])
    ff_data = ff_pci + payload[:6]
    return ff_data


def build_iso_tp_cf(payload_offset, payload, sn):
    """Build ISO-TP Consecutive Frame.

    CF PCI: 0x2N where N = sequence number (0-F, wraps)
    Data: up to 7 bytes from payload
    """
    cf_pci = bytes([0x20 | (sn & 0x0F)])
    cf_data = cf_pci + payload[payload_offset:payload_offset + 7]
    return cf_data.ljust(8, b"\x00")


def generate_method_a(sig):
    """Method A: 27 02 full signature as ISO-TP multi-frame.

    UDS payload: [0x27, 0x02, sig[0]..sig[63]] = 66 bytes
    ISO-TP: First Frame + Consecutive Frames
    """
    uds_payload = bytes([0x27, 0x02]) + sig  # 66 bytes
    total_len = len(uds_payload)

    frames = []

    # First Frame
    ff = build_iso_tp_ff(uds_payload)
    frames.append({
        "id": UDS_REQ_ID,
        "data": ff.hex().upper(),
        "note": "ISO-TP FF: 27 02 + sig[0:5], len={}".format(total_len),
    })

    # Consecutive Frames
    offset = 6  # FF consumed 6 data bytes
    sn = 1
    while offset < total_len:
        cf = build_iso_tp_cf(offset, uds_payload, sn)
        frames.append({
            "id": UDS_REQ_ID,
            "data": cf.hex().upper(),
            "note": "ISO-TP CF #{}: sig[{}:{}]".format(
                sn, offset - 6, min(offset - 6 + 7, total_len)
            ),
        })
        offset += 7
        sn = (sn + 1) & 0x0F

    return frames


def generate_method_b(sig):
    """Method B: 27 03 chunked transfer (matches MCU code).

    Each frame: [0x27, 0x03, blockSeq, sig_chunk]
    Chunk size: 4 bytes per frame (7-byte CAN payload - 3-byte UDS header)
    Total: 16 frames (64 / 4 = 16)
    Final trigger: 27 02

    MCU blockSeq handling:
      - Starts at 0x01
      - Increments by 1 each frame
      - Skips 0x00 on wraparound (0xFF -> 0x01)
      - First frame (blockSeq=0x01) resets receive buffer
    """
    frames = []
    chunks = [sig[i:i + CHUNK_SIZE] for i in range(0, SIG_SIZE, CHUNK_SIZE)]

    for i, chunk in enumerate(chunks):
        block_seq = i + 1  # 1-based: 0x01, 0x02, ... 0x10
        # UDS payload: [0x27, 0x03, blockSeq, sig_chunk]
        uds_payload = bytes([0x27, 0x03, block_seq]) + chunk
        # Pad to 7 bytes (CAN data), then wrap in ISO-TP SF
        iso_tp_frame = build_iso_tp_sf(uds_payload)

        frames.append({
            "id": UDS_REQ_ID,
            "data": iso_tp_frame.hex().upper(),
            "note": "27 03 chunk #{:02X} (blockSeq={:02X}, sig[{}:{}])".format(
                i + 1, block_seq, i * CHUNK_SIZE, min(i * CHUNK_SIZE + CHUNK_SIZE, SIG_SIZE)
            ),
        })

    # Final trigger: 27 02 (MCU verifies accumulated signature)
    trigger_payload = bytes([0x27, 0x02])
    trigger_frame = build_iso_tp_sf(trigger_payload)
    frames.append({
        "id": UDS_REQ_ID,
        "data": trigger_frame.hex().upper(),
        "note": "27 02 Verify (triggers ECDSA check on accumulated sig)",
    })

    return frames


def print_frames(frames, title):
    """Pretty-print CAN frames for copy-paste."""
    print("")
    print(title)
    print("-" * 78)
    print("{:<6s} {:<4s} {:<24s} {}".format("No.", "Len", "Data (hex)", "Note"))
    print("-" * 78)
    for i, f in enumerate(frames):
        print("{:<6d} {:<4d} {:<24s} {}".format(
            i + 1, len(f["data"]) // 2, f["data"], f["note"]
        ))
    print("-" * 78)


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Generate ECDSA P-256 signature for SecurityAccess seed"
    )
    parser.add_argument(
        "seed",
        help="32-byte seed in hex (64 hex chars), from MCU 67 01 response",
    )
    parser.add_argument(
        "--key", default=DEFAULT_KEY,
        help="ECDSA P-256 private key PEM (default: docs/keys/private.pem)",
    )
    args = parser.parse_args(argv)

    # ---- validate seed ----
    seed_hex = args.seed.replace(" ", "").replace(":", "")
    if len(seed_hex) != 64:
        sys.stderr.write("ERROR: Seed must be 64 hex chars (32 bytes), got {}\n".format(len(seed_hex)))
        return 1
    try:
        seed_bytes = bytes.fromhex(seed_hex)
    except ValueError:
        sys.stderr.write("ERROR: Invalid hex: {}\n".format(seed_hex))
        return 1

    # ---- validate key ----
    if not os.path.isfile(args.key):
        sys.stderr.write("ERROR: Private key not found: {}\n".format(args.key))
        return 1

    # ---- compute ----
    print("=" * 78)
    print("SecurityAccess Signature Generator")
    print("=" * 78)
    print("")
    print("Seed (from MCU):  {}".format(seed_hex.upper()))

    seed_hash = sha256_hash(seed_bytes)
    print("SHA-256(seed):    {}".format(seed_hash.hex().upper()))

    print("Signing with:     {}".format(os.path.basename(args.key)))
    print("")

    try:
        sig = sign_with_openssl(seed_bytes, args.key)
    except Exception:
        try:
            sig = sign_with_ecdsa_lib(seed_bytes, args.key)
        except ImportError:
            sys.stderr.write(
                "ERROR: Neither OpenSSL CLI nor ecdsa library available.\n"
                "  Install: pip install ecdsa\n"
                "  Or install OpenSSL\n"
            )
            return 1
        except Exception as e:
            sys.stderr.write("ERROR: Signing failed: {}\n".format(e))
            return 1
    except Exception as e:
        sys.stderr.write("ERROR: Signing failed: {}\n".format(e))
        return 1

    sig_hex = sig.hex().upper()
    print("ECDSA Signature:  {}".format(sig_hex))
    print("")

    # ---- generate CAN frames ----
    frames_a = generate_method_a(sig)
    frames_b = generate_method_b(sig)

    print_frames(frames_a, "Method A: 27 02 full signature (ISO-TP multi-frame)")
    print("")
    print("  MCU reassembles ISO-TP, then verifies ECDSA directly")
    print("  Reply: 67 02 = OK | 7F 27 35 = Invalid Key")

    print("")
    print_frames(frames_b, "Method B: 27 03 chunked (matches MCU code)")
    print("")
    print("  Send 16 x 27 03 frames (4 bytes sig per frame), then 27 02 trigger")
    print("  Each 27 03 gets MCU reply 67 03 [blockSeq]")
    print("  Final 27 02 triggers ECDSA verification on accumulated buffer")
    print("  Reply: 67 02 = OK | 7F 27 35 = Invalid Key")

    print("")
    print("=" * 78)
    print("Complete flow:")
    print("  1. Send 27 01 Request Seed")
    print("  2. MCU replies 67 01 [seed: {}]".format(seed_hex.upper()))
    print("  3. Run: python sign_seed.py {}".format(seed_hex.upper()))
    print("  4. Send frames from Method B above")
    print("  5. MCU replies 67 02 = unlocked, can use 31 FF00 etc.")
    print("=" * 78)

    return 0


if __name__ == "__main__":
    sys.exit(main())
