# QC_JYF_MCU1_FW_1.2.4_MCU2_FW_1.2.3_Release Release Note

> Generated per *JYF Qi Charger Firmware Release and Delivery Requirements v1.01* (Chinese digest doc §4).
> **Language: English** (delivery documents to Lime are in English).

## 1. Release Identification

| Item | Value |
|------|-------|
| MCU1 firmware version | `QC_JYF_MCU1_FW_1.2.4` |
| MCU2 firmware version | `QC_JYF_MCU2_FW_1.2.3` (**unchanged** in this package) |
| Supported hardware version | `QC_JYF_HW_1.1.5` (embedded in MCU1 firmware) |
| Bootloader version | `QC_JYF_BL_1.0.0` (MCU1, merged into burn `.bin`) |
| Release date | 2026-11-22 |
| Release type | Formal delivery package (MCU1 OTA + MCU1 burn + MCU2 IAP) |
| Git commit ID | `755a6f6a310aca191dd08f791ced5b53012183d8` |
| Git tag | MCU1: `(not created yet)`; MCU2: `(not created yet)` |
| Package name | `QC_JYF_MCU1_FW_1.2.4_MCU2_FW_1.2.3_Release.zip` |

## 2. Delivered Files (full 32-character MD5)

| File name | MCU | Purpose | MD5 | Size (bytes) | Source |
|-----------|-----|---------|-----|--------------|--------|
| `QC_JYF_MCU1_FW_1.2.4_5a46cf9c65d5227c4632e6ad4907d507.img` | MCU1 | `.img` OTA upgrade | `5a46cf9c65d5227c4632e6ad4907d507` | 37768 | `python_tools/app bin/app_image_v1_2_4.bin` |
| `QC_JYF_MCU1_FW_1.2.4_c62d9b66dcf5fff0a922a725106d8ade.bin` | MCU1 | `.bin` full-flash burn (Boot+App) | `c62d9b66dcf5fff0a922a725106d8ade` | 50056 | `python_tools/burn bin/prod_image_v1_2_4.bin` |
| `QC_JYF_MCU2_FW_1.2.3_52e6d5da8225b93d830d1d25c53a916f.img` | MCU2 | `.img` **IAP upgrade payload** | `52e6d5da8225b93d830d1d25c53a916f` | 12916 | `python_tools/iap bin/QI_IAP_Ver_1_2_3.BIN` |
| `QC_JYF_MCU2_FW_1.2.3_<MD5>.bin` | MCU2 | Full-flash burn | — | — | **Not delivered** (no full-flash source) |

### File type convention (§2.2)

| Extension | Purpose |
|-----------|---------|
| `.img` | Application OTA / IAP upgrade firmware |
| `.bin` | Full MCU flash image (bootloader + application) for direct programming |

**MD5 rule**: `.img` and `.bin` are hashed independently; each file name embeds the full 32-character MD5 of that file.

**MCU2 note**: `QI_IAP_Ver_1_2_3.BIN` is the **MCU2 (Qi chip) IAP upgrade file only**. It is **not** an MCU1 image. Deliver it under `Firmware/MCU2/` as `.img`.

## 3. Change Log

### 3.1 Relative to previous release (MCU1 1.2.3 → 1.2.4; MCU2 unchanged)

| # | Issue / Requirement | Change detail | Expected behavior after change | Affected MCU / module | Issue / ticket |
|---|---------------------|---------------|--------------------------------|----------------------|----------------|
| 1 | Version must be bumped to 1.2.4 to identify this delivery (**firmware payload vs 1.2.3 differs only in the version string — 1 byte**) | `SW_VERSION_STR` set to `QC_JYF_MCU1_FW_1.2.4`, then packed/merged; XATO CRC/signature updated with payload | Reading DID `0xF195` returns `QC_JYF_MCU1_FW_1.2.4`; OTA/burn can be distinguished from 1.2.3 | MCU1 / version identity (DID 0xF195) | — |

> **Payload comparison**: firmware region of `app_image_v1_2_4.bin` vs `app_image_v1_2_3.bin` (excluding 256-byte header) differs only at the version string (1 byte).  
> **Idle-sleep disable (`CAN_LP_SLEEP_ENABLE=0`)** has been part of the MCU1 line since the 1.2.3 package and **is retained** in this package (not listed again as a delta vs 1.2.3).  
> Whether additional formal-branch fixes (e.g. `sit1145_get_mode` SPI timeout) are present in **this** binary must be confirmed by on-device test / binary inspection; this note only states **verified byte-level facts**.

### 3.2 Unchanged MCU / component statement (§4)

- **MCU2 is unchanged**: still `QC_JYF_MCU2_FW_1.2.3` (`QI_IAP_Ver_1_2_3.BIN`), same MD5 as in the previous package.
- Bootloader `QC_JYF_BL_1.0.0` and hardware `QC_JYF_HW_1.1.5` are unchanged.

## 4. Known Issues

| # | Description | Trigger | Impact | Workaround |
|---|-------------|---------|--------|------------|
| 1 | MCU2 has no new 1.2.4 IAP/full-flash image | When MCU2 version bump is required | This package only ships MCU2 1.2.3 IAP | Keep MCU2 at 1.2.3; release MCU2 separately if needed |
| 2 | MCU1 1.2.4 firmware body is nearly identical to 1.2.3 (version string only) | Binary diff / behavior comparison | Functional behavior expected same as 1.2.3 | If formal fixes (e.g. sit1145) must ship, merge source and release 1.2.5 |
| 3 | Release Note / Self Test Report are Markdown drafts, not PDF | Formal archive | Does not meet PDF format in §4/§5 | Export to PDF after tests complete |
| 4 | Git tags may not exist yet for this package | Traceability (§8) | Incomplete audit trail | Create `QC_JYF_MCU1_FW_1.2.4` / `QC_JYF_MCU2_FW_1.2.3` and never move them |
| 5 | Full mandatory regression / change-specific / rollback Pass records not attached | Release gate (§13) | Cannot mark formal release complete | Complete §5/§6 tests and update Test_Report |

> Issues affecting safety / charging / reliability / upgrade / production must be notified to Lime in advance (§4.2).

## 5. Upgrade / Burn / Rollback Notes (§7)

| Path | File | Steps |
|------|------|-------|
| MCU1 OTA | `Firmware/MCU1/QC_JYF_MCU1_FW_1.2.4_5a46cf9c65d5227c4632e6ad4907d507.img` | CAN-UDS OTA (backup region → verify → copy); after upgrade read `0xF195` = `QC_JYF_MCU1_FW_1.2.4` |
| MCU1 full burn | `Firmware/MCU1/QC_JYF_MCU1_FW_1.2.4_c62d9b66dcf5fff0a922a725106d8ade.bin` | Program at base **0x08000000** (Boot+App merged); power-on check version |
| MCU2 IAP | `Firmware/MCU2/QC_JYF_MCU2_FW_1.2.3_52e6d5da8225b93d830d1d25c53a916f.img` | Qi chip IAP (`2E 20 10 04` → 31 → 34/36 → 37); after upgrade read `0x2013` / `0x2133` |

**Limits**: Rollback verification to 1.2.2 and earlier is **pending test** (§7).

## 6. Build & Traceability (§8/§9)

| Item | Value |
|------|-------|
| MCU1 version source of truth | `qi_wireless_code_app/mdk_app/Src/can_protocol.c` → `SW_VERSION_STR` = `QC_JYF_MCU1_FW_1.2.4` |
| Sleep switch | `qi_wireless_code_app/mdk_app/Inc/app_config.h` → `CAN_LP_SLEEP_ENABLE = 0` |
| MCU1 OTA pack | `python_tools/packaging/pack_image.py` |
| MCU1 burn merge | `python_tools/packaging/merge_prod_bin.py` |
| Signing key | `docs/keys/private.pem` |
| Toolchain | Keil MDK (AT32F426); full reproducible build config **TBD** (§9) |
| Git commit | `755a6f6a310aca191dd08f791ced5b53012183d8` |

## 7. Hardware Compatibility (§3)

- Embedded hardware version: `QC_JYF_HW_1.1.5`
- Supported hardware list / limits: **TBD** (must be declared in the formal Release Note)

## 8. Change-Specific Test Map (§5.2)

| Change # | Test | Status |
|----------|------|--------|
| 1 Version 1.2.4 | Read `0xF195` = `QC_JYF_MCU1_FW_1.2.4` | **Pending device test** |
| Baseline regression | §5.1 mandatory items | **Pending** |
| Sleep disable (carried from 1.2.3) | UDS still answers after idle >10s | **Pending device test** |

---

*Generated: 2026-11-22 11:16:37; sources: `python_tools/app bin` + `burn bin` (MCU1 1.2.4) + `iap bin` (MCU2 1.2.3)*
