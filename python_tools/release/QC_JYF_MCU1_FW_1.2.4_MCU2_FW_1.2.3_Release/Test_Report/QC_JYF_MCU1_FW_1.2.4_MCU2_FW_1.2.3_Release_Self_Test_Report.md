# QC_JYF_MCU1_FW_1.2.4_MCU2_FW_1.2.3_Release Self Test Report

> Skeleton per *JYF Qi Charger Firmware Release and Delivery Requirements v1.01* §5/§6.
> **Language: English** (delivery documents to Lime are in English).
> **Not-tested items are marked Pending — Pass/Fail is not fabricated.**

## 1. Build Under Test

| Item | Value |
|------|-------|
| MCU1 version | `QC_JYF_MCU1_FW_1.2.4` |
| MCU2 version | `QC_JYF_MCU2_FW_1.2.3` (**unchanged**) |
| MCU1 `.img` under test | `QC_JYF_MCU1_FW_1.2.4_5a46cf9c65d5227c4632e6ad4907d507.img` |
| MCU1 `.bin` under test | `QC_JYF_MCU1_FW_1.2.4_c62d9b66dcf5fff0a922a725106d8ade.bin` |
| MCU2 `.img` under test (IAP) | `QC_JYF_MCU2_FW_1.2.3_52e6d5da8225b93d830d1d25c53a916f.img` |
| MD5 (MCU1 img) | `5a46cf9c65d5227c4632e6ad4907d507` |
| MD5 (MCU1 bin) | `c62d9b66dcf5fff0a922a725106d8ade` |
| MD5 (MCU2 img) | `52e6d5da8225b93d830d1d25c53a916f` |
| Hardware version | `QC_JYF_HW_1.1.5` |
| Date | 2026-11-22 |
| Engineer | (TBD) |
| Environment | CAN 250 kbps extended frames + QiChargerStationTool / production fixture (TBD) |
| Git commit | `755a6f6a310aca191dd08f791ced5b53012183d8` |

## 2. Mandatory Baseline Regression (§5.1)

| Case ID | Test item | Expected | Actual | Pass/Fail |
|---------|-----------|----------|--------|-----------|
| REG-01 | Power-on init / read SW version 0xF195 | Returns `QC_JYF_MCU1_FW_1.2.4` | Pending | — |
| REG-02 | Normal charge enable/disable | Positive response for 0x2101 write | Pending | — |
| REG-03 | Qi device detect / status read | DIDs such as 0x2111 readable | Pending | — |
| REG-04 | Charge power setting | 0x210D level effective | Pending | — |
| REG-05 | OVP/UVP/OCP/OTP protection reporting | 0x210B set/clear with fault | Pending | — |
| REG-06 | Config R/W and NVM | Power level retained after power cycle | Pending | — |
| REG-07 | Dual-MCU version reporting | F195 + 0x2013/0x2133 | Pending | — |
| REG-08 | Reset / power cycle | Version and config correct | Pending | — |
| REG-09 | OTA upgrade (`.img`) | Old → 1.2.4 success, version correct | Pending | — |
| REG-10 | Full burn (`.bin`) | Boots, version correct | Pending | — |

## 3. Change-Specific Tests (§5.2)

| Case ID | Related change | Condition / steps | Expected | Actual | Pass/Fail |
|---------|----------------|-------------------|----------|--------|-----------|
| CHG-01 | #1 Version 1.2.4 | Read DID 0xF195 after upgrade/burn | `QC_JYF_MCU1_FW_1.2.4` | Pending | — |
| CHG-02 | Sleep disable (carried) | Idle >10s, capture 0x18FF260D and send 22 F195 | No SLEEP(04); UDS answers immediately | Pending | — |
| CHG-03 | MCU2 unchanged | Read MCU2 IAP version after using package IAP file | `QC_JYF_MCU2_FW_1.2.3` | Pending | — |

## 4. Verified Facts (not device tests)

| Item | Result |
|------|--------|
| MCU1 XATO image validate | **PASS** (magic / CRC / reset vector) |
| MCU1 image version string | `QC_JYF_MCU1_FW_1.2.4` |
| Source sleep switch | `CAN_LP_SLEEP_ENABLE = 0` |
| MD5 of three delivered files | See `Checksum/MD5.txt` and Release Note §2 |
| MCU2 source mapping | `QI_IAP_Ver_1_2_3.BIN` → `Firmware/MCU2/…img` only |
| Git commit | `755a6f6a310aca191dd08f791ced5b53012183d8` |

## 5. Conclusion

- **Baseline regression and change-specific tests: not completed on device — must not be marked formal Pass.**
- Package file integrity (MD5 / naming / MCU mapping): **generated and self-checked**.
- Before formal delivery: complete all mandatory items in §2/§3 and update this report.

*Generated: 2026-11-22 11:16:37*
