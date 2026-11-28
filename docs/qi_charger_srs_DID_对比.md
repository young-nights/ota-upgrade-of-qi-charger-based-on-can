# Qi 充电器 SRS ↔ 代码 DID 对比表

> 对比对象
> - **SRS**：`qi_charger_srs.md`（英文原版，v1.3 2026-09-22，共 1993 行）
> - **固件 MCU1 APP**：`qi_wireless_code_app/mdk_app/Inc/can_protocol.h`（DID 宏定义）+ `qi_wireless_code_app/mdk_app/Src/can_protocol.c` 的 `fill_did_payload()`（读应答组装）与 `handle_write_data_by_id()`（写门禁）
> - **Bootloader**：`qi_wireless_bootloader/`（未实现 RDBI/WDBI，仅 Safe Mode 下放行 `3E`/`22 2113`）
> - **上位机**：`QiChargerStationTool/src/QiChargerStationTool.Core/.../Protocol/UdsClient.cs`、`simple_ProductionTool/.../Station/*`
>
> 生成日期：2026-11-28。若与英文原版冲突，以 `qi_charger_srs.md` 为准。

---

## 0. 结论摘要

| 类别 | 数量 | 说明 |
|------|------|------|
| SRS 定义的 DID 总数 | 32 | §7.1 通用 7 个 + §7.2 充电器特定 25 个 |
| 完全匹配（名称/长度/格式/范围/权限一致） | 14 | 可直接交付 |
| **语义冲突（同 DID 不同含义）** | **3** | `0x210E` / `0x2111` / `0x2117` —— **最严重** |
| 格式/量纲/范围不一致 | 8 | 长度、偏移、量程、状态码范围有出入 |
| SRS 有、代码无 | 4 | `0x2014`、`0x210F`、`0x2012`、`0x2101` 读 |
| 代码有、SRS 无 | 8 | `0x2119`/`0x211A`/`0x2120`/`0x2132`/`0x2133`/`0x2140`/`0x2141`/`0x21FF` |
| 编号撞车（DID 与 DTC/OS 流程服务号同名） | 2 | `0x2132` / `0x2133` |

**必须优先处理的 3 个语义冲突**：SRS 把 `0x210E`/`0x2111`/`0x2117` 定义为「心跳周期 / 输送能量 / 空闲超时」，而固件把同一批号用作「充电次数 / Qi 状态原始字节 / 充电显示态」。任何按 SRS 开发的 CCU 读这三个 DID 会拿到完全无关的数据，写这三个 DID 会被 NRC `0x31` 拒绝。

---

## 1. SRS DID 全表（基准）

### 1.1 §7.1 通用 DID（7 个）

| DID | ISO 14229-1 名称 | 名称 | 归属 | 长度 | 读 | 写 | 值格式 |
|-----|------------------|------|------|------|----|----|--------|
| `0xF195` | systemSupplierECUSoftwareVersionNumber | Software Version（= MCU1 APP 版本） | MCU1 | 32B | Default / 无 | N/A | ASCII `QC_JYF_MCU1_FW_1.1.x` |
| `0xF18C` | ECUSerialNumber | Serial Number | 整机 | 32B | Default / 无 | N/A | ASCII SN（**不**用 `QC_JYF_` 格式） |
| `0xF180` | bootSoftwareIdentification | Bootloader Version | MCU1 | 32B | Default / 无 | N/A | ASCII `QC_JYF_MCU1_BL_FW_1.1.x` |
| `0xF193` | systemSupplierECUHardwareVersionNumber | Hardware Version | 整机 | 32B | Default / 无 | N/A | ASCII `QC_JYF_HW_1.1.x` |
| `0x2010` | —（厂商段） | Firmware Type（镜像类型/升级目标选择） | MCU1 | 1B | Default / 无 | — | `0x01` MCU1 APP / `0x02` MCU1 Boot / `0x03` MCU2 / `0x04` MCU2 APP；其余 NRC `0x31` |
| `0x2013` | —（厂商段） | MCU2 Software Version | MCU2 | 32B | Default / 无 | N/A | ASCII `QC_JYF_MCU2_FW_1.1.x` |
| `0x2014` | —（厂商段） | MCU2 Bootloader Version | MCU2 | 32B | Default / 无 | N/A | ASCII `QC_JYF_MCU2_BL_FW_1.1.x` |

SRS 另行声明：
- `0x2011`（旧 MCU1 APP 版本 DID）**已废弃**，应以 NRC `0x31` 拒绝。
- `0x2012`（Resource Package Version）本版本**不要求**，应以 NRC `0x31` 拒绝。
- 版本串五路**计数器各自独立**，CCU 不得跨角色比较 patch 号。
- `QC_JYF_HW_` **不含 MCU/角色标记**，比固件串少一段，客户端须按最后一个 `_` 切分，不得按固定偏移读。
- 每个交付版本须在 Release Note（交付物 D-16）中给出全部五串。

### 1.2 §7.2 充电器特定 DID（25 个，范围 `0x2100-0x21FF`）

| DID | 名称 | 长度 | 读会话/安全 | 写会话/安全 | 值格式与范围 |
|-----|------|------|-------------|-------------|--------------|
| `0x2100` | Charger Capability | 4B | Default / 无 | N/A | Bitfield，见 §7.9.1 |
| `0x2101` | Charger Enable | 1B | Default / 无 | **Extended / Level 1** | `0x00=disable` `0x01=enable`；易失，复位归 `0x00` |
| `0x2102` | Charging State | 1B | Default / 无 | N/A | 见 §6.1，`0x00-0x0A` |
| `0x2103` | Device Present | 1B | Default / 无 | N/A | `0x00=no` `0x01=yes` |
| `0x2104` | Real-Time Charging Power | 2B | Default / 无 | N/A | `uint16` mW 小端，`0x0000-0xFFFF`（0–65.535 W） |
| `0x2105` | Input Voltage | 2B | Default / 无 | N/A | `uint16` 0.01 V，`0x0000-0x1388`（0–50.00 V） |
| `0x2106` | Input Current | 2B | Default / 无 | N/A | `uint16` mA，`0x0000-0x2710`（0–10000 mA） |
| `0x2107` | Coil Temperature | 1B | Default / 无 | N/A | `uint8` °C，**偏移 −50**（实际 = 值 − 50），`0x00-0xFF`（−50–205 °C） |
| `0x2108` | PCB Temperature | 1B | Default / 无 | N/A | `uint8` °C，**偏移 −50**，`0x00-0xFF` |
| `0x2109` | FOD Status | 1B | Default / 无 | N/A | 见 §7.3，`0x00-0x03` |
| `0x210A` | Alignment Status | 1B | Default / 无 | N/A | 见 §7.4，`0x00-0x03` |
| `0x210B` | Fault Code | 1B | Default / 无 | N/A | 见 §8，`0x00-0x0B`、`0xFF` |
| `0x210C` | Thermal Derating Level | 1B | Default / 无 | N/A | 0–100 %；未充电或未降额时为 `0x64`(100%)，`0x00-0x64` |
| `0x210D` | Power Limit | 1B | Default / 无 | **Extended / Level 1** | `0x01`=5 W `0x02`=10 W `0x03`=15 W，`0x01-0x03`；**持久化**，默认 `0x02` |
| `0x210E` | **Heartbeat Period** | **2B** | Default / 无 | **Extended / Level 1** | `uint16` ms，`100-10000`；`0x0000` 关闭周期广播 |
| `0x210F` | **Operating Mode** | 1B | Default / 无 | **Extended / Level 1** | 见 §7.5，`0x00-0x03`；**持久化**，复位后重放 |
| `0x2110` | Last Fault Detail | 4B | Default / 无 | N/A | 供应商自定义，见 §7.9.2 |
| `0x2111` | **Energy Delivered** | **4B** | Default / 无 | N/A | `uint32` 0.1 Wh；`0x2101` 由 `0x00`→`0x01` 时清零 |
| `0x2112` | OTA Status | 1B | Default / 无 | N/A | 见 §7.6，`0x00-0x09` |
| `0x2113` | Active Firmware Slot | 1B | Default / 无 | N/A | 见 §7.7，`0x00-0x01`、`0xFF` |
| `0x2114` | Pending Firmware Slot | 1B | Default / 无 | N/A | `0x00-0x01`、`0xFE`、`0xFF` |
| `0x2115` | Last Boot Reason | 1B | Default / 无 | N/A | 见 §7.8，`0x00-0x05`、`0xFF` |
| `0x2116` | Rollback Counter | 1B | Default / 无 | N/A | 出厂或上次批准复位以来的 OTA 回滚次数，`0x00-0xFF` |
| `0x2117` | **Idle Timeout** | **2B** | Default / 无 | **Extended / Level 1** | `uint16` 分钟，`0x0000-0x003C`（0–60 min），默认 `0x000A`；`0x0000` 禁止进 LOW_POWER |
| `0x2118` | Clamp State | 1B | Default / 无 | N/A | `0x00=closed`（双臂静止、无手机）`0x01=open`（任一臂位移） |

SRS 持久化要求 `[QI-CFG-004]`：`0x210D`、`0x210E`、`0x210F`、`0x2117` 四项须掉电保存；`0x2101` **永不持久化**（`[QI-CFG-005]`）。

---

## 2. 逐 DID 对比

图例：✅ 一致 ｜ ⚠️ 不一致（格式/量纲/范围/权限）｜ ❌ 语义冲突 ｜ ➖ SRS 有代码无 ｜ ➕ 代码有 SRS 无

### 2.1 完全匹配（14 项）

| DID | SRS | 固件实现 | 判定 |
|-----|-----|----------|------|
| `0xF195` | 32B ASCII `QC_JYF_MCU1_FW_1.1.x` | `device_info_pad32(out, SW_VERSION_STR)`，32B | ✅ |
| `0xF180` | 32B ASCII `QC_JYF_MCU1_BL_FW_1.1.x` | `device_info_pad32(out, BOOTLOADER_VER_STR)`，32B | ✅ |
| `0xF193` | 32B ASCII `QC_JYF_HW_1.1.x` | `device_info_pad32(out, HW_VERSION_STR)`，32B（编译期常量，NVM `hw_version` 已弃用） | ✅ |
| `0xF18C` | 32B ASCII SN | `device_info_read` → `di.sn`，32B；可写（PROGRAMMING + SA） | ✅ |
| `0x2010` | 1B，`0x01`–`0x04`，其余 NRC `0x31` | `g_fw_type_target`，`FW_TYPE_APP`–`FW_TYPE_MCU2`，越界 NRC `0x31`；可写（PROGRAMMING + SA） | ✅ |
| `0x2013` | 32B ASCII `QC_JYF_MCU2_FW_1.1.x`；MCU2 无有效 APP 或链路断 → NRC `0x22`，不得回占位串 | 延迟应答 `7F 22 78` → Qi 应答后 `62 20 13 + ASCII`，变长 21–27B；缓存无效/超时 → NRC `0x22` | ✅（长度按实际串，非固定 32B，见 ⚠️A1） |
| `0x2100` | 4B Bitfield | `out[0]=15`(15W)、`out[1..3]=0`，4B | ✅ |
| `0x2102` | 1B `0x00-0x0A` | `QI_CHARGE_CHARGING/STANDBY/DISABLED`，1B | ✅ |
| `0x2103` | 1B `0x00/0x01` | `g_qi_device_present`，1B | ✅ |
| `0x2104` | 2B `uint16` mW 小端 | `g_qi_output_power_mw` 拆 `[lo, hi]`，2B | ✅ |
| `0x210B` | 1B `0x00-0x0B`/`0xFF` | `g_qi_fault_code` 透传，1B | ✅ |
| `0x2110` | 4B 供应商自定义 | `memcpy(out, g_qi_last_fault_detail, 4)`，4B | ✅ |
| `0x2112` | 1B 见 §7.6 | 1B，状态映射见 §2.4 | ⚠️ 范围多 `0x0A` |
| `0x2116` | 1B 回滚计数 | `meta.copy_retry_count & 0xFF`，1B | ✅ |
| `0x2118` | 1B `0x00=closed` `0x01=open` | PA0 电平：高→`0x01` 低→`0x00`，1B | ✅（源码注释文字与 SRS 措辞相反，取值映射一致） |
| `0x210D` | 1B `0x01/0x02/0x03`，持久化默认 `0x02`，写需 Extended+L1 | `g_qi_power_level`（`QI_POWER_5W/10W/15W`），NVM 持久；写需 EXTENDED + SA | ✅ |

### 2.2 ❌ 语义冲突（3 项，最高优先级）

| DID | SRS 定义 | 固件实际 | 影响 |
|-----|----------|----------|------|
| **`0x210E`** | **Heartbeat Period**，2B `uint16` ms，`100-10000`（`0`=关广播），可写（Extended+L1），持久化 | **充电次数**，4B `uint32` 小端，只读 + NVM；Qi 状态字节2 b0 上升沿（放机稳定 30s）计一次 | 长度 2B vs 4B、语义完全无关、读写权限相反。按 SRS 写心跳周期会被 NRC `0x31` 拒绝；按 SRS 读会把 4 字节充电次数误当 2 字节毫秒数 |
| **`0x2111`** | **Energy Delivered**，4B `uint32` 0.1 Wh，只读；`0x2101` 由 `0x00`→`0x01` 时清零 | **Qi Status Raw**，2B 原始状态字节（`[0]` b0=PING b1=CHARGING b2=FULL b3=OVP b4=UVP b5=OCP b6=OTP b7=FOD；`[1]` b0=充电触发位 30s 去抖） | 长度 4B vs 2B、语义完全无关。CCU 无法取得输送能量；位保护/显示逻辑与 SRS §2.5 状态摘要无对应 |
| **`0x2117`** | **Idle Timeout**，2B `uint16` 分钟，`0x0000-0x003C`，可写（Extended+L1），持久化，默认 `0x000A` | **充电显示态**，1B 只读：`0x02` 磁铁不存在·充电中 / `0x03` 磁铁不存在·未充电 / `0x04` 磁铁存在·未充电 / `0x05` 充电失能 / `0x06` 电已充满 | 长度 2B vs 1B、语义完全无关、读写权限相反。SRS 的 LOW_POWER 进入条件（§6 状态机 `DISABLED --> LOW_POWER: 0x2117 > 0`）在固件中无对应实现 |

> **处置建议**：这三项须由双方（Lime ↔ 供应商）书面裁定是「SRS 修订」还是「固件改号」。若保留固件语义，则 SRS §7.2、§3.4 `[QI-CFG-004]`、§6 状态机、§15 测试用例 `TC-QI-CFG-*`/`TC-QI-MODE-*`、§17 追溯条目需同步改；若以 SRS 为准，则固件须新增心跳周期、输送能量、空闲超时三项实现并把充电次数/Qi 状态/显示态迁到空闲号（建议 `0x2150`+）。

### 2.3 ⚠️ 格式 / 量纲 / 范围 / 权限不一致（8 项）

| DID | SRS | 固件 | 差异 |
|-----|-----|------|------|
| `0x2105` | **Input Voltage**，2B `uint16` 0.01 V，`0x0000-0x1388` | `[v_raw, i_raw]` **2×uint8** 原始量（`g_qi_voltage_raw`/`g_qi_current_raw`） | 单位/标度不符；SRS 是电压，固件是「电压+电流」两原始字节；量纲不可换算 |
| `0x2106` | **Input Current**，2B `uint16` mA，`0x0000-0x2710` | **NRC `0x31`**（`/* HW not supported */`） | 硬件不支持，恒 NRC `0x31`；SRS 未预留此不支持口径 |
| `0x2107` | **Coil Temperature**，1B °C **偏移 −50** | **NRC `0x31`**（HW not supported） | 同上 |
| `0x210A` | **Alignment Status**，1B `0x00-0x03`（§7.4） | **NRC `0x31`**（HW not supported） | 同上 |
| `0x2108` | **PCB Temperature**，1B °C **偏移 −50**（实际 = 值 − 50） | `g_qi_pcb_temp`，1B，**源码未声明偏移** | 是否已含 −50 偏移不明；CCU 若按 SRS 减 50 会偏低 50 °C |
| `0x210C` | Thermal Derating **0–100 %**，`0x64`=100%（未降额） | `g_qi_thermal_derate`，1B，**档位语义** | 量纲：SRS 为百分比，固件为降额档位；`0x64` 语义需确认 |
| `0x2112` | OTA Status 范围 **`0x00-0x09`** | `0x00-0x0A`（多 **`0x0A` MCU2 Confirmed**） | 超出 SRS 合法域；且 SRS §7.6 未收录 `0x0A` |
| `0x2113`/`0x2114` | 槽位 `0x00-0x01`；`0x2114` 另有 `0xFE`/`0xFF` | `0x2114` 用 **`0x02`** 表示待搬运备份槽，另有 `0xFE` | `0x02` 不在 SRS 合法域（`0x00-0x01`/`0xFE`/`0xFF`） |

补充：`0x2013` SRS 要求固定 32B 填充 ASCII，固件走延迟应答返回**变长 21–27B**（`62 20 13 + ASCII`，无 32B 填充）。同理 `0x2133` 为 3B `major.minor.patch`。若 CCU 严格按 REF-1 §8.1 固定 32B 解析会失配 —— 需在接口文件中明示。

### 2.4 `0x2112` OTA 状态码对照

| 值 | SRS §7.6 | 固件 `fill_did_payload` | 一致 |
|----|----------|-------------------------|------|
| `0x00` | Idle | `0x00` Idle（无 OTA、无待报结果） | ✅ |
| `0x01` | Downloading | `0x01` Downloading（下载到备份区） | ✅ |
| `0x02` | Validating | `0x02` Validating（CRC/签名校验） | ✅ |
| `0x03` | Pending Activation | `0x03` Pending Activation（`backup_valid` 置位，随即自复位激活） | ✅ |
| `0x04` | Trial Boot | `0x04` Trial Boot（源码注明「单 App 架构不适用」） | ⚠️ 恒不出现 |
| `0x05` | Confirmed | `0x05` Confirmed（`last_boot_reason==0x03`） | ✅ |
| `0x06` | Rolled Back | `0x06` Rolled Back（`last_boot_reason==0x04`） | ✅ |
| `0x07` | Failed | `0x07` Failed（旧 APP 保留） | ✅ |
| `0x08` | MCU2 In Progress | `0x08` MCU2 In Progress | ✅ |
| `0x09` | MCU2 No APP | `0x09` MCU2 No APP（持久态，成功重刷后清除） | ✅ |
| **`0x0A`** | **无** | **MCU2 Confirmed**（新 APP 运行且 `0x2013` 缓存已刷新） | ❌ 超范围 |

### 2.5 ➖ SRS 有、代码无（4 项）

| DID | SRS 定义 | 代码现状 | 影响 |
|-----|----------|----------|------|
| **`0x2014`** | MCU2 Bootloader Version，32B ASCII `QC_JYF_MCU2_BL_FW_1.1.x` | **完全未实现**（`can_protocol.h`/`fill_did_payload` 无此号） | SRS §5.3 启动发现序列第 6 步、§12.6 `[QI-OTA-015]`、`TC-QI-DID-*`、D-16 均依赖它；CCU 读它会得 NRC `0x31`，固件清单不完整 |
| **`0x210F`** | Operating Mode，1B `0x00-0x03`，可写（Extended+L1），持久化，复位重放 | **完全未实现** | §7.5 四种工作模式（Normal/Service/Manufacturing test/Shipping-storage）、`[QI-MODE-001..003]`、`[QI-CFG-004]`、`TC-QI-MODE-001/002`、`TS-QI-MODE-001`、`TRACE-QI-014` 全部无实现；发货/存储模式无法进入 |
| `0x2012` | Resource Package Version —— **SRS 明示本版本不要求**，应 NRC `0x31` | 未实现 → 落入 `default: return -1` → NRC `0x31` | ✅ 行为符合，但属「按设计不实现」而非缺漏 |
| `0x2101` **读** | Read Session=Default、Read Security=None，应返回当前使能值 | **只写不读**：`0x2101` 不在 `fill_did_payload`，读 → NRC `0x31` | SRS §3.2/§5.x 序列要求读回 `0x2101` 确认；缺读路径 |

> `0x2014` 与 `0x210F` 是**实打实的缺口**，建议在交付前补齐或由 Lime 书面豁免并修订 SRS。

### 2.6 ➕ 代码有、SRS 无（8 项）

| DID | 固件定义 | 长度 | 用途 | 备注 |
|-----|----------|------|------|------|
| `0x2119` | `DID_SIT1145_LP_STATUS` | 4B | SIT1145 低功耗状态：`[0]` flags（bit0 ever_sleep / bit1 last_wake_was_wup / bit2 sleep_fail sticky / bit3 rx_drop sticky / bit4-7 wake src）`[1]` wup_count `[2-3]` last_sleep_sec LE | 产线/诊断私有 |
| `0x211A` | `DID_SIT1145_LP_WIPE_DIAG` | 4B | 反幻影唤醒诊断：`[0]` 0x24 事件快照 `[1]` 0x63 事件快照 `[2]` WIPE_STAT 位 `[3]` 清除+等待尝试次数 | 产线/诊断私有 |
| `0x2120` | `DID_ECDSA_PUBKEY` | 65B | ECDSA P-256 公钥（SEC1 未压缩）；读需 `di.pubkey_valid==0x01`，否则 NRC；写需 PROGRAMMING + SA | SRS §10/§7 未定义公钥 DID（安全访问走 `0x27`）；与 SRS 「`0x2121` SecurityAccess」编号无关 |
| `0x2132` | `DID_QI_IAP_STATUS` | 9B | Qi IAP 状态：`[0]` state `[1]` progress `[2-4]` Qi 版本 major.minor.patch `[5-6]` 已发 LE `[7-8]` 总长 LE | **与 SRS DTC/OS 升级流程的服务号 `0x2132`「传输退出」撞名** |
| `0x2133` | `DID_QI_FW_VERSION` | 3B | Qi 芯片固件版本 major.minor.patch | **与 SRS DTC/OS 流程服务号 `0x2133`「安全连接」撞名** |
| `0x2140` | `DID_QI_UART_SNIFF` | 变长 ≤245B | Qi UART 抓取读取：`62 21 40 [flags][len][data...]`，siphon 分次消费；任意会话、无需安全；组合读含它 → NRC `0x22` | 仅单读 |
| `0x2141` | `DID_QI_UART_TX` | 1–64B | Qi UART 透传发送（写），门禁同 `0x2010`（PROGRAMMING + SA） | 仅写 |
| `0x21FF` | （无宏，直接 case） | 1B | `sit1145_get_mode()`：`0x04`=Standby `0x07`=Normal `0x01`=Sleep | 诊断私有 |

建议：这 8 个号段在 SRS §7.2 的 `0x2100-0x21FF` 区间内，但未列入表格。若属供应商扩展，应在 SRS 增补「供应商扩展 DID」小节或单独接口文件登记，否则 CCU 侧无法发现。

---

## 3. 编号撞车与 SRS 内部问题

### 3.1 `0x2132` / `0x2133` 撞名

SRS 的 DTC/OS OTA 流程（第三方 OS/DTC 升级通道）用 `0x2130` OS_Info、`0x2131` OS_Download_Data、`0x2132` 传输退出、`0x2133` 安全连接 作为**服务号**；固件把 `0x2132`/`0x2133` 用作 **DID**（Qi IAP 状态 / Qi 版本）。两套语义在同一 16 位命名空间内重叠，容易在上位机解析时误判。

### 3.2 `0x2134` 不是 DID

`0x2134` 在 SRS 中只作为**数值示例**出现（`0x2104` = 8500 mW = `0x2134`，见 §7.10 表与 §11 报文示例）。SRS **未定义** `0x2134` 为任何 DID。上位机 `QiChargerStationTool` 侧的「HwVersion_Pn `0x2134`」属**上位机自行扩展**，SRS 与固件均无此号 —— 若需保留，应补进 SRS 或改用 `0xF193`。

### 3.3 SRS 内部不一致（供 Lime 勘误）

| 位置 | 问题 |
|------|------|
| §7.2 vs §3.4 `[QI-CFG-004]` | `0x210E`/`0x210F`/`0x2117` 要求持久化，但固件对 `0x210E`/`0x2117` 无写路径 |
| §7.2 `0x2112` 范围 `0x00-0x09` vs §12.6 / §7.6 | 正文引入 MCU2 状态却未扩展合法域 |
| §7.2 `0x2107`/`0x2108` 偏移 −50 vs §11 报文示例 `0x5F=95, 95-50=45` | 示例自洽，但固件 `g_qi_pcb_temp` 是否已减 50 未声明 |
| §7.2 `0x210C` 写 `0x64`=100% 未降额 | 与「降额档位」直觉相反，建议补一句「100% = 全功率」 |
| `0x2013` 长度 | §7.1 说 32B 固定填充，实际应答变长 21–27B（见 §2.3 补充） |
| `0xF18C` 写权限 | §7.1 表标 N/A（只读），但固件允许 PROGRAMMING + SA 写 SN（产线必需） |

---

## 4. 门禁（会话 / 安全访问）对比

| DID | SRS 读 | SRS 写 | 固件读 | 固件写 | 判定 |
|-----|--------|--------|--------|--------|------|
| `0x2101` | Default / 无 | Extended / L1 | **NRC `0x31`（无读实现）** | EXTENDED + SA | ⚠️ 缺读 |
| `0x210D` | Default / 无 | Extended / L1 | Default / 无 | EXTENDED + SA | ✅ |
| `0x210E` | Default / 无 | Extended / L1 | Default / 无 | **NRC `0x31`** | ❌ 语义冲突 |
| `0x210F` | Default / 无 | Extended / L1 | **NRC `0x31`** | **NRC `0x31`** | ➖ 未实现 |
| `0x2117` | Default / 无 | Extended / L1 | Default / 无 | **NRC `0x31`** | ❌ 语义冲突 |
| `0x2010` | Default / 无 | —（SRS 表未列写） | Default / 无 | PROGRAMMING + SA | ⚠️ 固件写门禁更严，且 SRS §12.1 要求可写以选目标 |
| `0xF18C` | Default / 无 | N/A | Default / 无 | PROGRAMMING + SA | ⚠️ 见 §3.3 |
| `0x2120` | —（SRS 无） | — | 有（`pubkey_valid`） | PROGRAMMING + SA | ➕ |
| `0x2141` | —（SRS 无） | — | — | PROGRAMMING + SA | ➕ |

固件写门禁统一口径（`handle_write_data_by_id`）：
- **EXTENDED + SA**：`0x2101`、`0x210D`
- **PROGRAMMING + SA**：`0x2010`、`0xF18C`、`0x2120`、`0x2141`
- 其他 DID 写 → NRC `0x31`
- 同一 case 内**会话检查先于安全检查**（`0x22`=会话丢失，`0x33`=会话在但未解锁）

固件读特殊口径：
- `0x2013`：仅单读；组合读含它 → NRC `0x22`（延迟应答无法服务多 DID）
- `0x2140`：仅单读；组合读含它 → NRC `0x22`（变长 + siphon 消耗语义）
- `0x2013` 在 Qi IAP 升级中 → NRC `0x22`（避免 UART 命令交叉）

---

## 5. Bootloader 侧

| 项 | SRS | `qi_wireless_bootloader` | 判定 |
|----|-----|--------------------------|------|
| Bootloader Version DID `0xF180` | 应可读（§5.3 第 3 步） | **无 RDBI 实现**（`boot_safe_mode.c` 仅放行 `3E` / `22 2113`） | ➖ Safe Mode 下 CCU 读 `0xF180` 会失败 |
| `0x2113` Active FW Slot | 应可读 | Safe Mode 下放行 `22 2113` | ✅ 唯一放行的 DID |
| 其他 DID | — | 一律不应答 | ➖ |

SRS §5.3 启动发现序列要求 CCU 在 Bootloader 阶段也能读 `0xF180`；当前 Bootloader 只在 Safe Mode 放行 `0x2113`，与 SRS 不符。

---

## 6. 上位机使用情况（参考）

`QiChargerStationTool.Core/Protocol/UdsClient.cs`：
- `TryReadAllVersions` 读 `0xF195` / `0x2133` / `0xF180` / `0xF193`（MCU1 APP / Qi 版本 / Boot / HW）
- 注释中提到 `0x2132`（读 Qi IAP 状态判断首帧是否已处理）、`0x2133`（读 Qi 3B 版本）
- **未使用** `0x2013`（MCU2 ASCII 版本串）与 `0x2014`；`VersionSet` 直接收 3B 数值版本

`simple_ProductionTool` 工位 1/3：本次已加「软硬件版本检测」步骤，读 MCU1 / MCU2 / Boot / HW 四路版本，读失败不中断测试。

> 上位机读的是 `0x2133`（3B 数值）而非 SRS 规定的 `0x2013`（ASCII 版本串）。两者都由固件提供，但 SRS 只认 `0x2013`/`0x2014`。建议上位机版本检测同时读 `0x2013` 并按 REF-1 §8.1 解析，以满足 D-16 交付要求。

---

## 7. 待办清单（按优先级）

| # | 优先级 | 项 | 建议动作 |
|---|--------|----|----------|
| 1 | **P0** | `0x210E` / `0x2111` / `0x2117` 语义冲突 | 双方书面裁定；无论改 SRS 还是改固件，须同步改测试用例与追溯条目 |
| 2 | **P0** | `0x2014` MCU2 Bootloader Version 未实现 | 固件补 DID（可从 Qi Bootloader 查询或缓存），或 Lime 豁免并改 SRS §5.3/§12.6 |
| 3 | **P0** | `0x210F` Operating Mode 未实现 | 实现四种模式 + 持久化，或 Lime 豁免（牵动 `[QI-MODE-001..003]`、`TC-QI-MODE-*`、`TS-QI-MODE-001`、`TRACE-QI-014`） |
| 4 | P1 | `0x2101` 无读实现 | 在 `fill_did_payload` 补 `case DID_CHARGER_ENABLE`（回 `g_qi_charger_enable`） |
| 5 | P1 | `0x2106` / `0x2107` / `0x210A` 恒 NRC `0x31` | 在 SRS §7.2 标注「硬件不支持，恒 NRC `0x31`」，或补硬件/算法实现 |
| 6 | P1 | `0x2105` 量纲不符（2×uint8 vs uint16 0.01V） | 固件改 `uint16` 0.01 V 小端，或 SRS 改为原始量并注明标定 |
| 7 | P1 | `0x2108` 偏移 −50 是否已应用 | 固件注释明确；若未减 50 则补齐，若已减则 SRS 保留 |
| 8 | P1 | `0x2112` 多 `0x0A`、`0x2114` 多 `0x02` | SRS §7.6/§7.7 扩域并补说明 |
| 9 | P2 | `0x2132`/`0x2133` 与 DTC/OS 服务号撞名 | 固件改用 `0x2150+`，或 SRS 明示两套命名空间 |
| 10 | P2 | `0x2134` 上位机扩展号 | 上位机改用 `0xF193`，或补进 SRS |
| 11 | P2 | `0x2013` 变长 vs 32B 固定 | 接口文件明示实际应答长度 |
| 12 | P2 | Bootloader 无 `0xF180` 读 | Bootloader 补 RDBI，或 SRS §5.3 说明 Bootloader 阶段只读 `0x2113` |
| 13 | P2 | 8 个供应商扩展 DID 未登记 | 补 SRS「供应商扩展 DID」小节或独立接口文件 |
| 14 | P2 | 上位机未用 `0x2013` | `TryReadAllVersions` 增读 `0x2013` 并按 REF-1 §8.1 解析 |

---

## 8. 附：固件 DID 速查（`can_protocol.h`）

```
标准标识        0xF195 SW_VERSION           32B  R    ASCII SW_VERSION_STR
                0xF18C SERIAL_NUMBER        32B  R/W  ASCII SN (PROG+SA)
                0xF180 BOOTLOADER_VERSION   32B  R    ASCII BOOTLOADER_VER_STR
                0xF193 HW_VERSION           32B  R    ASCII HW_VERSION_STR
固件管理        0x2010 FW_TYPE              1B   R/W  PROG+SA
                0x2013 QI_VERSION_QUERY     变长 R    ASCII QC_JYF_MCU2_FW_x.x.x
充电器          0x2100 CHARGER_CAPABILITY   4B   R
                0x2101 CHARGER_ENABLE       1B   W    EXT+SA
                0x2102 CHARGE_STATE         1B   R
                0x2103 DEVICE_PRESENT       1B   R
                0x2104 OUTPUT_POWER         2B   R    mW LE
                0x2105 INPUT_VI             2B   R    [v_raw, i_raw]
                0x2106 INPUT_CURRENT        —    R    NRC 0x31
                0x2107 COIL_TEMP            —    R    NRC 0x31
                0x2108 PCB_TEMP             1B   R
                0x2109 FOD_STATUS           1B   R
                0x210A ALIGNMENT            —    R    NRC 0x31
                0x210B FAULT_CODE           1B   R
                0x210C THERMAL_DERATE       1B   R
                0x210D POWER_LIMIT          1B   R/W  EXT+SA, NVM
                0x210E (charge count)       4B   R    NVM   ← 与 SRS 冲突
                0x2110 LAST_FAULT_DETAIL    4B   R
                0x2111 (qi status raw)      2B   R        ← 与 SRS 冲突
                0x2112 OTA_STATE            1B   R
                0x2113 ACTIVE_SLOT          1B   R
                0x2114 PENDING_SLOT         1B   R
                0x2115 LAST_BOOT_REASON     1B   R
                0x2116 ROLLBACK_COUNT       1B   R
                0x2117 (qi display state)   1B   R        ← 与 SRS 冲突
                0x2118 CLAMP_STATE          1B   R
诊断/私有       0x2119 SIT1145_LP_STATUS    4B   R
                0x211A SIT1145_LP_WIPE_DIAG 4B   R
                0x2120 ECDSA_PUBKEY         65B  R/W  PROG+SA
                0x2132 QI_IAP_STATUS        9B   R
                0x2133 QI_FW_VERSION        3B   R
                0x2140 QI_UART_SNIFF        变长 R    siphon
                0x2141 QI_UART_TX           1-64B W  PROG+SA
                0x21FF (sit1145 mode)       1B   R
```
