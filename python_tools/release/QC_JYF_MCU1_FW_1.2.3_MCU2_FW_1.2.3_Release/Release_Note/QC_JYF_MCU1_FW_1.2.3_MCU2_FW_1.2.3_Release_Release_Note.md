# QC_JYF_MCU1_FW_1.2.3_MCU2_FW_1.2.3_Release Release Note

> Generated per *JYF Qi Charger Firmware Release and Delivery Requirements v1.01* (Chinese digest doc §4).
> **Language: English** (delivery documents to Lime are in English).

## 1. Release Identification

| Item | Value |
|------|-------|
| MCU1 firmware version | `QC_JYF_MCU1_FW_1.2.3` |
| MCU2 firmware version | `QC_JYF_MCU2_FW_1.2.3` |
| Supported hardware version | `QC_JYF_HW_1.1.5` (embedded in MCU1 firmware) |
| Bootloader version | `QC_JYF_BL_1.0.0` (MCU1, merged into burn `.bin`) |
| Release date | 2026-11-22 |
| Release type | Formal delivery package (MCU1 OTA + MCU1 burn + MCU2 IAP) |
| Git commit ID | `1f6e9b7b7977f29607b17ec32ebd97dc71d8d5d1` |
| Git tag | MCU1: `(not created yet)`; MCU2: `(not created yet)` |
| Package name | `QC_JYF_MCU1_FW_1.2.3_MCU2_FW_1.2.3_Release.zip` |

## 2. Delivered Files (full 32-character MD5)

| File name | MCU | Purpose | MD5 | Size (bytes) | Source |
|-----------|-----|---------|-----|--------------|--------|
| `QC_JYF_MCU1_FW_1.2.3_9053c70bfa3a237584a5f7c199916cff.img` | MCU1 | `.img` OTA upgrade | `9053c70bfa3a237584a5f7c199916cff` | 37768 | `python_tools/mcu1 app bin/app_image_v1_2_3.bin` |
| `QC_JYF_MCU1_FW_1.2.3_f247d340537a1defdf74dc58b5579329.bin` | MCU1 | `.bin` full-flash burn (Boot+App) | `f247d340537a1defdf74dc58b5579329` | 50056 | `python_tools/mcu1 burn bin/prod_image_v1_2_3.bin` |
| `QC_JYF_MCU2_FW_1.2.3_52e6d5da8225b93d830d1d25c53a916f.img` | MCU2 | `.img` **IAP upgrade payload** | `52e6d5da8225b93d830d1d25c53a916f` | 12916 | `python_tools/mcu2 iap bin/QI_IAP_Ver_1_2_3.BIN` |
| `QC_JYF_MCU2_FW_1.2.3_16f5dba276923fe2ca81257ed6d7ee0a.bin` | MCU2 | `.bin` **full-flash burn** | `16f5dba276923fe2ca81257ed6d7ee0a` | 14963 | `python_tools/mcu2 burn/MCU2_Burn_V1.2.3.bin` (from `MCU2_Burn_V1.2.3.hex`) |
| `QC_JYF_MCU2_FW_1.2.3_16f5dba276923fe2ca81257ed6d7ee0a.bin` | MCU2 | Full-flash burn | `16f5dba276923fe2ca81257ed6d7ee0a` | 14963 | **Delivered** (was TBD) |

### File type convention (§2.2)

| Extension | Purpose |
|-----------|---------|
| `.img` | Application OTA / IAP upgrade firmware |
| `.bin` | Full MCU flash image (bootloader + application) for direct programming |

**MD5 rule**: `.img` and `.bin` are hashed independently; each file name embeds the full 32-character MD5 of that file.

**MCU2 note**: `QI_IAP_Ver_1_2_3.BIN` is the **MCU2 (Qi chip) IAP upgrade file only**. It is **not** an MCU1 image. Deliver it under `Firmware/MCU2/` as `.img`.

## 3. Change Log

### 3.1 Relative to previous formal MCU1 line (baseline → 1.2.3 package)

| # | Issue / Requirement | Change detail | Expected behavior after change | Affected MCU / module | Issue / ticket |
|---|---------------------|---------------|--------------------------------|----------------------|----------------|
| 1 | Ship a consistent dual-MCU 1.2.3 package (MCU1 app/burn + MCU2 IAP) | MCU1 XATO `.img` from `app_image_v1_2_3.bin`; MCU1 burn `.bin` from `prod_image_v1_2_3.bin`; MCU2 IAP from `QI_IAP_Ver_1_2_3.BIN` | MCU1 reports `QC_JYF_MCU1_FW_1.2.3`; MCU2 IAP payload version 1.2.3; OTA and burn paths available for MCU1 | MCU1 OTA/burn + MCU2 IAP | — |
| 2 | Keep idle-sleep disabled for always-on station/debug use | Source `CAN_LP_SLEEP_ENABLE = 0` retained in this MCU1 build line | Device does not enter SIT1145 Sleep on idle; UDS remains reachable without wake burst | MCU1 / low-power | — |

> **MCU2 note**: This package uses the same `QI_IAP_Ver_1_2_3.BIN` as the 1.2.4 MCU1 package — MCU2 is **1.2.3** in both packages.

### 3.2 Unchanged MCU / component statement (§4)

- MCU1 and MCU2 versions are aligned at **1.2.3** in this package.
- Bootloader `QC_JYF_BL_1.0.0` and hardware `QC_JYF_HW_1.1.5` are unchanged.

## 4. Known Issues

| # | Description | Trigger | Impact | Workaround |
|---|-------------|---------|--------|------------|
| 1 | ~~MCU2 full-flash missing~~ | — | — | **Resolved**: `Firmware/MCU2/QC_JYF_MCU2_FW_1.2.3_16f5dba276923fe2ca81257ed6d7ee0a.bin` included |
| 2 | Release Note / Self Test Report are Markdown | Formal PDF archive | §4/§5 PDF format not met | Export PDF after sign-off |
| 3 | Git tags may not exist yet | Traceability (§8) | Incomplete audit trail | Create `QC_JYF_MCU1_FW_1.2.3` / `QC_JYF_MCU2_FW_1.2.3`; never move |
| 4 | Rollback / long-run multi-cycle charging not in this report scope | Extended validation | Incomplete §7 coverage if Lime requires it | Add follow-up tests if requested |

> Issues affecting safety / charging / reliability / upgrade / production must be notified to Lime in advance (§4.2).

## 5. Upgrade / Burn / Rollback Notes (§7)

| Path | File | Steps |
|------|------|-------|
| MCU1 OTA | `Firmware/MCU1/QC_JYF_MCU1_FW_1.2.3_9053c70bfa3a237584a5f7c199916cff.img` | CAN-UDS OTA; after upgrade read `0xF195` = `QC_JYF_MCU1_FW_1.2.3` |
| MCU1 full burn | `Firmware/MCU1/QC_JYF_MCU1_FW_1.2.3_f247d340537a1defdf74dc58b5579329.bin` | Program at base **0x08000000** |
| MCU2 full burn | `Firmware/MCU2/QC_JYF_MCU2_FW_1.2.3_16f5dba276923fe2ca81257ed6d7ee0a.bin` | Program MCU2 flash (hex→bin, holes 0xFF) |
| MCU2 IAP | `Firmware/MCU2/QC_JYF_MCU2_FW_1.2.3_52e6d5da8225b93d830d1d25c53a916f.img` | Qi chip IAP (`2E 20 10 04` → 31 → 34/36 → 37) |

**Limits**: Rollback verification is **pending** if required (§7).

## 6. Build & Traceability (§8/§9)

| Item | Value |
|------|-------|
| MCU1 version source of truth | `SW_VERSION_STR` in `can_protocol.c` (package image strings = `QC_JYF_MCU1_FW_1.2.3`) |
| Sleep switch | `CAN_LP_SLEEP_ENABLE = 0` |
| MCU1 OTA pack | `python_tools/packaging/pack_image.py` |
| MCU1 burn merge | `python_tools/packaging/merge_prod_bin.py` |
| Signing key | `docs/keys/private.pem` |
| Toolchain | Keil MDK (AT32F426); full reproducible config **TBD** (§9) |
| Git commit | `1f6e9b7b7977f29607b17ec32ebd97dc71d8d5d1` |

## 7. Hardware Compatibility (§3)

- Embedded hardware version: `QC_JYF_HW_1.1.5`
- Supported hardware list / limits: **TBD** in formal signed Note if required

## 8. Change-Specific Test Map (§5.2)

| Change # | Test | Status |
|----------|------|--------|
| 1 Dual-MCU 1.2.3 package | MCU1 OTA/burn version; MCU2 IAP mapping | See Self Test Report |
| 2 Sleep disable | Idle >10s still answers UDS | See Self Test Report |

---

*Generated: 2026-11-23 18:06:16; MCU2 burn added to 1.2.3 package only*
