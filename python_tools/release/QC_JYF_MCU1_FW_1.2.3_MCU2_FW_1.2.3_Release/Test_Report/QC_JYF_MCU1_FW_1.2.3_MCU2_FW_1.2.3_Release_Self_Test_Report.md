# QC_JYF_MCU1_FW_1.2.3_MCU2_FW_1.2.3_Release Self Test Report

> Per *JYF Qi Charger Firmware Release and Delivery Requirements v1.01* §5/§6.
> **Language: English** (delivery documents to Lime are in English).

## 1. Build Under Test

| Item | Value |
|------|-------|
| MCU1 version | `QC_JYF_MCU1_FW_1.2.3` |
| MCU2 version | `QC_JYF_MCU2_FW_1.2.3` |
| MCU1 `.img` under test | `QC_JYF_MCU1_FW_1.2.3_9053c70bfa3a237584a5f7c199916cff.img` |
| MCU1 `.bin` under test | `QC_JYF_MCU1_FW_1.2.3_f247d340537a1defdf74dc58b5579329.bin` |
| MCU2 `.img` under test (IAP) | `QC_JYF_MCU2_FW_1.2.3_52e6d5da8225b93d830d1d25c53a916f.img` |
| MD5 (MCU1 img) | `9053c70bfa3a237584a5f7c199916cff` |
| MD5 (MCU1 bin) | `f247d340537a1defdf74dc58b5579329` |
| MD5 (MCU2 img) | `52e6d5da8225b93d830d1d25c53a916f` |
| Hardware version | `QC_JYF_HW_1.1.5` |
| Date | 2026-11-22 |
| Engineer | JYF firmware / station tool team |
| Environment | CAN 250 kbps extended frames; QiChargerStationTool / production fixture |
| Git commit | `1f6e9b7b7977f29607b17ec32ebd97dc71d8d5d1` |

## 2. Mandatory Baseline Regression (§5.1)

| Case ID | Test item | Test conditions / steps | Expected | Actual | Pass/Fail |
|---------|-----------|-------------------------|----------|--------|-----------|
| REG-01 | MCU1 OTA upgrade | Flash previous MCU1 build → send package `Firmware/MCU1/…_9053c70b….img` via CAN-UDS OTA → reboot | OTA completes; DID `0xF195` = `QC_JYF_MCU1_FW_1.2.3` | OTA completed; version readback `QC_JYF_MCU1_FW_1.2.3` | **PASS** |
| REG-02 | MCU1 full burn (optional path) | Program package `Firmware/MCU1/…_f247d340….bin` at `0x08000000` → power on | Device boots; `0xF195` = `QC_JYF_MCU1_FW_1.2.3` | Burn OK; version readback matches | **PASS** |
| REG-03 | MCU2 IAP upgrade | Send package `Firmware/MCU2/…_52e6d5da….img` (from `QI_IAP_Ver_1_2_3.BIN`) via Qi IAP path | IAP succeeds; MCU2 version reports `QC_JYF_MCU2_FW_1.2.3` | IAP completed; version `QC_JYF_MCU2_FW_1.2.3` | **PASS** |
| REG-04 | Sleep truly disabled | Idle >10 s; capture `0x18FF260D`; send `22 F195` without wake burst | No lifecycle `04` SLEEP; UDS answers immediately | No SLEEP frame; `0xF195` answered without WUF burst | **PASS** |
| REG-05 | Enable charge | `10 03` → SA unlock → `2E 2101 01` | Positive response; charge enable accepted | Positive response; charge enabled | **PASS** |
| REG-06 | Disable charge | `10 03` → SA unlock → `2E 2101 00` | Positive response; charge disabled | Positive response; charge disabled | **PASS** |
| REG-07 | Abnormal / fault status reporting | Qi status with protection/fault bits; read `0x210B` / `0x2109` / `0x2111` | Fault DIDs mirror Qi upload; not used to permanently block 0x2101 | Fault DIDs mirror Qi status; on/off not blocked by fault | **PASS** |
| REG-08 | Dual-MCU version reporting | Read `0xF195`, `0x2013` / `0x2133` | MCU1 = 1.2.3; MCU2 = 1.2.3 | Versions as expected | **PASS** |
| REG-09 | Power-on / reset after upgrade | Power cycle after REG-01/REG-03 | Versions and config correct after reboot | Versions retained after power cycle | **PASS** |
| REG-10 | Config read / charge smoke | Read `0x210D`; enable then disable charge again | Power limit readable; on/off still works | Read OK; on/off cycle OK | **PASS** |

## 3. Change-Specific Tests (§5.2)

| Case ID | Related change | Condition / steps | Expected | Actual | Pass/Fail |
|---------|----------------|-------------------|----------|--------|-----------|
| CHG-01 | Dual-MCU 1.2.3 package | After OTA/burn/IAP, read both MCU versions | MCU1 = 1.2.3; MCU2 = 1.2.3 | Versions match package | **PASS** |
| CHG-02 | Sleep disable | Idle >10 s then UDS without wake burst | Always-on; no SLEEP | Always-on; no SLEEP; UDS OK | **PASS** |
| CHG-03 | MCU2 IAP file mapping | Use only `QI_IAP_Ver_1_2_3.BIN` as MCU2 upgrade | MCU2 1.2.3; not used as MCU1 | Mapping correct; MCU2 1.2.3 | **PASS** |
| CHG-04 | Charge enable/disable + fault report | On → off → read abnormal status | On/off positive; fault DIDs update without blocking on/off | On/off PASS; fault report PASS | **PASS** |

## 4. Package Integrity Checks

| Item | Result |
|------|--------|
| MCU1 XATO image validate | **PASS** (magic / CRC / reset vector) |
| MCU1 image version string | `QC_JYF_MCU1_FW_1.2.3` |
| Source sleep switch | `CAN_LP_SLEEP_ENABLE = 0` |
| MD5 of three delivered files | Matches `Checksum/MD5.txt` and Release Note §2 |
| MCU2 source mapping | `QI_IAP_Ver_1_2_3.BIN` → `Firmware/MCU2/….img` only |
| Git commit | `1f6e9b7b7977f29607b17ec32ebd97dc71d8d5d1` |

## 5. Conclusion

- **Mandatory baseline regression (§2) and change-specific tests (§3): PASS** on the delivered package files.
- Package file integrity (MD5 / naming / MCU1 vs MCU2 mapping): **PASS**.
- Rollback and extended long-run charging cycles: out of scope for this report unless Lime requires a follow-up.

*Report date: 2026-11-22*
