# QC_JYF_MCU1_FW_1.2.3_MCU2_FW_1.2.3_Release Release Note

> 按《JYF Qi 充电器固件发布与交付要求 v1.01》§4 生成。
> 本文为 Markdown 交付稿；正式归档前可导出 PDF 替换。

## 1. 发布标识

| 项 | 值 |
|----|----|
| MCU1 固件版本 | `QC_JYF_MCU1_FW_1.2.3` |
| MCU2 固件版本 | `QC_JYF_MCU2_FW_1.2.3` |
| 支持硬件版本 | `QC_JYF_HW_1.1.5`（MCU1 固件内嵌） |
| Bootloader 版本 | `QC_JYF_BL_1.0.0`（MCU1，随 burn `.bin` 合并交付） |
| 发布日期 | 2026-11-22 |
| 发布类型 | 正式交付包（app OTA + MCU1 烧录 + MCU2 IAP） |
| Git commit ID | `0fa411132714b888c6af56dd95af3e2c2e428cf8` |
| Git tag | MCU1: `（未打 tag，交付前须按 §8 创建且不得移动）`；MCU2: `（未打 tag，交付前须按 §8 创建且不得移动）` |
| 发布包名 | `QC_JYF_MCU1_FW_1.2.3_MCU2_FW_1.2.3_Release.zip` |
| 主要变更 | **屏蔽空闲休眠（全程醒着）** + Qi 异常标识全量透传（详见 §3） |

## 2. 交付文件清单（含完整 32 字符 MD5）

| 文件名 | MCU | 用途 | MD5 | 大小(bytes) | 源文件 |
|--------|-----|------|-----|-------------|--------|
| `QC_JYF_MCU1_FW_1.2.3_9053c70bfa3a237584a5f7c199916cff.img` | MCU1 | `.img` OTA 升级 | `9053c70bfa3a237584a5f7c199916cff` | 37768 | `python_tools/app bin/app_image_v1_2_3.bin` |
| `QC_JYF_MCU1_FW_1.2.3_f247d340537a1defdf74dc58b5579329.bin` | MCU1 | `.bin` 直接烧录（Boot+App） | `f247d340537a1defdf74dc58b5579329` | 50056 | `python_tools/burn bin/prod_image_v1_2_3.bin` |
| `QC_JYF_MCU2_FW_1.2.3_52e6d5da8225b93d830d1d25c53a916f.img` | MCU2 | `.img` IAP/OTA 升级载荷 | `52e6d5da8225b93d830d1d25c53a916f` | 12916 | `python_tools/iap bin/QI_IAP_Ver_1_2_3.BIN` |
| `QC_JYF_MCU2_FW_1.2.3_<MD5>.bin` | MCU2 | 整片烧录 | — | — | **本版本不交付**（无 MCU2 整片源；IAP 用 `.img`） |

### 文件类型约定（§2.2）

| 扩展名 | 用途 |
|--------|------|
| `.img` | 应用 OTA / IAP 升级固件 |
| `.bin` | 完整 MCU 烧录固件（含 bootloader + 应用），直接烧录 |

**MD5 规则**：`.img` 与 `.bin` 各自独立计算；文件名内嵌最终文件的完整 32 字符 MD5。已发布文件不得在版本号与文件名不变的情况下被修改或替换。

## 3. Change Log

### 3.1 相对上一发布（1.2.2 → 1.2.3）

| # | 问题/需求描述 | 修改细节 | 修改后预期行为 | 受影响 MCU/模块 | issue/ticket |
|---|---------------|----------|----------------|-----------------|--------------|
| 1 | **调试/产线场景需要设备常在线，空闲自动休眠导致 UDS 间歇无应答、需反复唤醒** | `app_config.h`：`CAN_LP_SLEEP_ENABLE` **1→0**。`=0` 时睡眠进入整段不编译（`can_lp_go_sleep`/`can_lp_enter_sleep`、poll 空闲入睡判定、E0~E3 入睡诊断帧、SLEEP 生命周期发送均屏蔽）；唤醒转换随 `g_can_awake` 恒 1 不再触发；仅保留上电 CAN 拉起（`g_lp_need_online` → `can_lp_enter_normal`） | 设备上电后 **CAN 全程 Normal、空闲不入睡**；空闲任意时刻发 UDS 均应答；无 `0x18FF260D` 周期 `04` SLEEP 标记；无需 WUF burst 唤醒即可通信 | MCU1 / 低功耗（SIT1145 Sleep 编译开关） | — |
| 2 | Qi 芯片异常（FOD/过温/过压/欠压/过流）恢复后上位机可能仍读到旧故障 | `can_protocol.c` Qi 状态帧回调：保护位派生块每帧先清 `g_qi_fod_status`/`g_qi_fault_code` 再按 status1 置位；扩展帧 `data[10]`/`data[12]` **全量镜像**（去掉 `!=0` 守卫，恢复即写 0） | 故障只透传 Qi 上报：异常置位、恢复即清零；与开/关充门禁解耦（开充不因 fault≠0 回 NRC 0x22） | MCU1 / Qi 状态上报（DID 0x2109/0x210B/0x2111） | — |
| 3 | 欠压等故障后再开充被 NRC 0x22 卡死 | 删除 `0x2101=1` 时 `g_qi_fault_code!=0 → 0x22` 门禁；故障仅经 DID 只读透传 | 开/关充只校验会话+SA+取值范围；故障状态仍可读 | MCU1 / 充电使能 DID 0x2101 | — |
| 4 | 关闭充电偶发「UDS 无应答 + 噪声帧」 | `0x2101` 写路径在 NVM 整扇区擦写前先回 `7F 2E 78` responsePending，写完再回正响应；上位机关充 WDBI 超时重发 2 次 | 关充在擦写期间主机收到 0x78 续命；应答偶发丢失可重发自愈 | MCU1 / 充电写路径 + QiChargerStationTool | — |
| 5 | 写 SN 门禁偶发 0x22（会话/解锁时序） | 上位机写 SN 门禁改确定性顺序 `10 01→SA→10 02→立即写` + 3 次重试（对齐 OTA 门禁定序） | 写 SN 在会话抖动下可自愈 | 上位机（配套工具，非本包二进制） | — |

> 其余自 1.2.2 以来的 main/formal 合入（Hall 极性、产线 UI、版本对齐 1.2.3 等）以 Git commit `0fa4111` 为准；**本包核心交付差异为上表第 1 条：屏蔽休眠、全程醒着**。

### 3.2 未变 MCU / 组件说明（§4）

- MCU1、MCU2 版本号均对齐为 **1.2.3**（MCU2 以 `QI_IAP_Ver_1_2_3.BIN` 为准）。
- Bootloader `QC_JYF_BL_1.0.0`、硬件 `QC_JYF_HW_1.1.5` 未变。

## 4. Known Issues（已知问题）

| # | 问题描述 | 触发条件 | 影响 | 规避方法 |
|---|----------|----------|------|----------|
| 1 | **本包屏蔽休眠后设备永不空闲入睡** | 使用本包 MCU1 固件 | 空闲功耗高于休眠版；现场若依赖 10s 自睡需改回 `CAN_LP_SLEEP_ENABLE=1` 重编 | 调试/产线常在线场景保持本包；量产睡眠策略另发版本 |
| 2 | Release Note / Self Test Report 为 Markdown，非 PDF | 当前交付准备 | 不满足 §4/§5 正式 PDF 归档格式 | 补齐测试后导出 PDF 替换 |
| 3 | MCU2 无整片烧录 `.bin` | MCU2 产线整片烧录 | MCU2 只能走 IAP `.img` 升级 | 按产线既有 IAP 流程；整片件后续补交 |
| 4 | 正式 Git tag 若尚未创建/推送 | 交付追溯 | 不满足 §8 可追溯闭环 | 打 `QC_JYF_MCU1_FW_1.2.3` / `QC_JYF_MCU2_FW_1.2.3` 且不得移动 |
| 5 | 强制回归/变更专项/回滚测试未在本包内附完整 Pass 记录 | 正式发布前置（§13） | 测试报告为骨架+已核对事实，**不伪造 Pass** | 按 §5/§6 补测并更新 Test_Report 后再标正式交付 |

> 影响安全/充电功能/可靠性/固件升级/生产的已知问题须主动通知 Lime（§4.2）。屏蔽休眠属**可靠性/功耗策略变更**，交付时应明确告知。

## 5. 升级 / 烧录 / 回滚说明（§7）

| 路径 | 文件 | 步骤 |
|------|------|------|
| MCU1 OTA | `Firmware/MCU1/QC_JYF_MCU1_FW_1.2.3_9053c70bfa3a237584a5f7c199916cff.img` | CAN-UDS OTA（备份区 → 验签 → 搬运）；升级后读 `0xF195` = `QC_JYF_MCU1_FW_1.2.3` |
| MCU1 直接烧录 | `Firmware/MCU1/QC_JYF_MCU1_FW_1.2.3_f247d340537a1defdf74dc58b5579329.bin` | 烧录基址 **0x08000000**（Boot+App 合并）；上电核对版本 |
| MCU2 IAP | `Firmware/MCU2/QC_JYF_MCU2_FW_1.2.3_52e6d5da8225b93d830d1d25c53a916f.img` | Qi 芯片 IAP（`2E 20 10 04` → 31 → 34/36 → 37）；升级后读 `0x2013`/`0x2133` |

**限制**：回滚到 1.2.2 及更早的验证状态、升级中断恢复 **待补测**（§7）。

## 6. 构建与可追溯（§8/§9）

| 项 | 值 |
|----|----|
| MCU1 版本真相源 | `qi_wireless_code_app/mdk_app/Src/can_protocol.c` → `SW_VERSION_STR` = `QC_JYF_MCU1_FW_1.2.3` |
| 休眠开关 | `qi_wireless_code_app/mdk_app/Inc/app_config.h` → `CAN_LP_SLEEP_ENABLE = 0` |
| MCU1 OTA 打包 | `python_tools/packaging/pack_image.py` |
| MCU1 烧录合并 | `python_tools/packaging/merge_prod_bin.py` |
| 签名密钥 | `docs/keys/private.pem` |
| 工具链 | Keil MDK（AT32F426）；完整可复现构建配置 **待补**（§9） |
| Git commit | `0fa411132714b888c6af56dd95af3e2c2e428cf8` |

## 7. 硬件兼容性（§3）

- 固件内嵌硬件版本：`QC_JYF_HW_1.1.5`
- 支持硬件版本清单 / 限制：**待补**（正式 Release Note 须声明）

## 8. 变更专项测试对应（§5.2，状态如实标注）

| Change # | 测试项 | 状态 |
|----------|--------|------|
| 1 屏蔽休眠 | 上电后空闲 >10s 仍应答 UDS；无 SLEEP 生命周期帧；读 `0xF195` 正确 | **待设备实测** |
| 2 故障透传 | 注入/恢复欠压或 FOD，读 `0x210B` 置位/清零；开充不回 0x22 | **待设备实测** |
| 3/4 开关充与 0x78 | 开→关→开循环；关充日志无「UDS 无应答」或重发后成功 | **待设备实测** |
| 基础回归（§5.1） | 充电启停、功率、保护、配置读写、OTA 等强制项 | **待按基线清单补测** |

---

*生成时间: 2026-11-22 02:15:48；源目录: `python_tools/app bin` + `burn bin` + `iap bin`*
