# QC_JYF_MCU1_FW_1.2.4_MCU2_FW_1.2.3_Release Self Test Report

> Per *JYF Qi Charger Firmware Release and Delivery Requirements v1.01* §5/§6.
> **Language: English** (delivery documents to Lime are in English).

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
| Engineer | JYF firmware / station tool team |
| Environment | CAN 250 kbps extended frames; QiChargerStationTool / production fixture |
| Git commit | `755a6f6a310aca191dd08f791ced5b53012183d8` |

## 2. Mandatory Baseline Regression (§5.1)

| Case ID | Test item | Test conditions / steps | Expected | Actual | Pass/Fail |
|---------|-----------|-------------------------|----------|--------|-----------|
| REG-01 | MCU1 OTA upgrade | Flash previous MCU1 build → send package `Firmware/MCU1/…_5a46cf9c….img` via CAN-UDS OTA → wait for reboot | OTA completes; DID `0xF195` = `QC_JYF_MCU1_FW_1.2.4` | OTA completed; version readback `QC_JYF_MCU1_FW_1.2.4` | **PASS** |
| REG-02 | MCU1 full burn (optional path) | Program package `Firmware/MCU1/…_c62d9b66….bin` at `0x08000000` → power on | Device boots; `0xF195` = `QC_JYF_MCU1_FW_1.2.4` | Burn OK; version readback matches | **PASS** |
| REG-03 | MCU2 IAP upgrade | Send package `Firmware/MCU2/…_52e6d5da….img` (from `QI_IAP_Ver_1_2_3.BIN`) via Qi IAP path (`2E 20 10 04` → 31 → 34/36 → 37) | IAP succeeds; MCU2 version remains / reports `QC_JYF_MCU2_FW_1.2.3` | IAP completed; version `QC_JYF_MCU2_FW_1.2.3` | **PASS** |
| REG-04 | Sleep truly disabled | After boot, leave bus idle >10 s; capture `0x18FF260D`; send `22 F195` without wake burst | No lifecycle `04` SLEEP; UDS answers immediately; `CAN_LP_SLEEP_ENABLE=0` behavior | No SLEEP frame; `0xF195` answered without WUF burst | **PASS** |
| REG-05 | Enable charge | `10 03` → SA unlock → `2E 2101 01` | Positive response `6E 21 01`; charge enable accepted | Positive response; charge enabled | **PASS** |
| REG-06 | Disable charge | `10 03` → SA unlock → `2E 2101 00` | Positive response `6E 21 01`; charge disabled | Positive response; charge disabled | **PASS** |
| REG-07 | Abnormal / fault status reporting | With Qi status frames reporting protection/fault bits (or forced status), read `0x210B` (fault), `0x2109` (FOD), `0x2111` (raw status) | Fault codes mirror Qi upload; set/clear follow status frame (no sticky gate on 0x2101) | Fault DIDs mirror Qi status; enable charge not blocked by fault | **PASS** |
| REG-08 | Dual-MCU version reporting | Read `0xF195` (MCU1), `0x2013` / `0x2133` (MCU2) | MCU1 = 1.2.4; MCU2 = 1.2.3 | Versions as expected | **PASS** |
| REG-09 | Power-on / reset after upgrade | Power cycle after REG-01/REG-03 | Versions and charge config still correct after reboot | Versions retained after power cycle | **PASS** |
| REG-10 | Config read / charge smoke | Read `0x210D` power limit; enable then disable charge again | Power limit readable; charge on/off still works | Read OK; on/off cycle OK | **PASS** |

## 3. Change-Specific Tests (§5.2)

| Case ID | Related change | Condition / steps | Expected | Actual | Pass/Fail |
|---------|----------------|-------------------|----------|--------|-----------|
| CHG-01 | MCU1 version 1.2.4 | After OTA/burn, read `0xF195` | `QC_JYF_MCU1_FW_1.2.4` | Version string matches package | **PASS** |
| CHG-02 | Sleep disable (`CAN_LP_SLEEP_ENABLE=0`) | Idle >10 s then `22 F195` / charge commands without wake burst | Device stays Normal; no SLEEP; commands work | Always-on; no SLEEP; UDS OK | **PASS** |
| CHG-03 | MCU2 IAP file mapping | Use only `QI_IAP_Ver_1_2_3.BIN` as MCU2 upgrade; verify MCU2 version after IAP | MCU2 = 1.2.3; file not used as MCU1 | MCU2 1.2.3 confirmed; package layout MCU1/MCU2 correct | **PASS** |
| CHG-04 | Charge enable/disable + fault report | Enable charge → disable charge → inject/read abnormal status | On/off positive; fault DIDs update without blocking on/off | On/off PASS; fault report PASS | **PASS** |

## 4. Package Integrity Checks

| Item | Result |
|------|--------|
| MCU1 XATO image validate | **PASS** (magic / CRC / reset vector) |
| MCU1 image version string | `QC_JYF_MCU1_FW_1.2.4` |
| Source sleep switch | `CAN_LP_SLEEP_ENABLE = 0` |
| MD5 of three delivered files | Matches `Checksum/MD5.txt` and Release Note §2 |
| MCU2 source mapping | `QI_IAP_Ver_1_2_3.BIN` → `Firmware/MCU2/….img` only |
| Git commit | `755a6f6a310aca191dd08f791ced5b53012183d8` |

## 5. Conclusion

- **Mandatory baseline regression (§2) and change-specific tests (§3): PASS** on the delivered package files.
- Package file integrity (MD5 / naming / MCU1 vs MCU2 mapping): **PASS**.
- Rollback to older versions and full multi-cycle long-run charging: not in this scope for this delivery package (record as follow-up if required by Lime).

*Report date: 2026-11-22*
