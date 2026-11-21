# QC_JYF_MCU1_FW_1.2.4_MCU2_FW_1.2.3_Release Release Note

> 按《JYF Qi 充电器固件发布与交付要求 v1.01》§4 生成。
> Markdown 交付稿；正式归档前可导出 PDF 替换。

## 1. 发布标识

| 项 | 值 |
|----|----|
| MCU1 固件版本 | `QC_JYF_MCU1_FW_1.2.4` |
| MCU2 固件版本 | `QC_JYF_MCU2_FW_1.2.3`（**本包未变**，沿用上一发布 IAP 载荷） |
| 支持硬件版本 | `QC_JYF_HW_1.1.5`（MCU1 固件内嵌） |
| Bootloader 版本 | `QC_JYF_BL_1.0.0`（MCU1，随 burn `.bin` 合并交付） |
| 发布日期 | 2026-11-22 |
| 发布类型 | 正式交付包（MCU1 升版 1.2.4；MCU2 未变） |
| Git commit ID | `bb37917a4dd375cec9713a437e19049cdbd697e5` |
| Git tag | MCU1: `（未打 tag，交付前须按 §8 创建且不得移动）`；MCU2: `QC_JYF_MCU2_FW_1.2.3（本包 MCU2 未变，沿用 1.2.3 tag）` |
| 发布包名 | `QC_JYF_MCU1_FW_1.2.4_MCU2_FW_1.2.3_Release.zip` |
| 相对上一发布 | 1.2.3 → **1.2.4**（仅 MCU1） |

## 2. 交付文件清单（含完整 32 字符 MD5）

| 文件名 | MCU | 用途 | MD5 | 大小(bytes) | 源文件 |
|--------|-----|------|-----|-------------|--------|
| `QC_JYF_MCU1_FW_1.2.4_5a46cf9c65d5227c4632e6ad4907d507.img` | MCU1 | `.img` OTA 升级 | `5a46cf9c65d5227c4632e6ad4907d507` | 37768 | `python_tools/app bin/app_image_v1_2_4.bin` |
| `QC_JYF_MCU1_FW_1.2.4_c62d9b66dcf5fff0a922a725106d8ade.bin` | MCU1 | `.bin` 直接烧录（Boot+App） | `c62d9b66dcf5fff0a922a725106d8ade` | 50056 | `python_tools/burn bin/prod_image_v1_2_4.bin` |
| `QC_JYF_MCU2_FW_1.2.3_52e6d5da8225b93d830d1d25c53a916f.img` | MCU2 | `.img` IAP 升级（**未变**） | `52e6d5da8225b93d830d1d25c53a916f` | 12916 | `python_tools/iap bin/QI_IAP_Ver_1_2_3.BIN` |
| `QC_JYF_MCU2_FW_1.2.3_<MD5>.bin` | MCU2 | 整片烧录 | — | — | **本版本不交付**（无整片源） |

### 文件类型约定（§2.2）

| 扩展名 | 用途 |
|--------|------|
| `.img` | 应用 OTA / IAP 升级固件 |
| `.bin` | 完整 MCU 烧录固件（含 bootloader + 应用） |

**MD5 规则**：`.img` 与 `.bin` 各自独立计算；文件名内嵌最终文件完整 32 字符 MD5。

## 3. Change Log

### 3.1 相对上一发布（MCU1 1.2.3 → 1.2.4）

| # | 问题/需求描述 | 修改细节 | 修改后预期行为 | 受影响 MCU/模块 | issue/ticket |
|---|---------------|----------|----------------|-----------------|--------------|
| 1 | 版本号需升至 1.2.4 以标识本批交付（**镜像载荷相对 1.2.3 仅版本串 1 字节：`QC_JYF_MCU1_FW_1.2.3`→`1.2.4`**） | `SW_VERSION_STR` 改为 `QC_JYF_MCU1_FW_1.2.4` 后重新 pack/merge；XATO CRC/签名随载荷更新 | 读 `0xF195` 返回 `QC_JYF_MCU1_FW_1.2.4`；OTA/烧录后版本可区分于 1.2.3 | MCU1 / 版本标识（DID 0xF195） | — |

> **载荷对比说明**：`app_image_v1_2_4.bin` 与 `app_image_v1_2_3.bin` 的固件区（去 256B 头）仅版本串位置 1 字节不同，其余字节一致。  
> formal 线另有 `sit1145_get_mode` SPI 超时返回 0xFF、Keil IROM 对齐等提交（`d0907e4` 等）**是否已编入本 1.2.4 二进制，须以烧录后实测/反编译核对为准**；本 Release Note 只对**已核对的镜像字节事实**负责。  
> **屏蔽休眠（`CAN_LP_SLEEP_ENABLE=0`）** 自 1.2.3 发布包起已包含，本包延续该策略，不再重复列为相对 1.2.3 的增量。

### 3.2 未变 MCU 说明（§4）

- **MCU2 未变**：仍为 `QC_JYF_MCU2_FW_1.2.3`（`QI_IAP_Ver_1_2_3.BIN`），与上一发布包相同 MD5。  
- Bootloader `QC_JYF_BL_1.0.0`、硬件 `QC_JYF_HW_1.1.5` 未变。

## 4. Known Issues（已知问题）

| # | 问题描述 | 触发条件 | 影响 | 规避方法 |
|---|----------|----------|------|----------|
| 1 | MCU2 无 1.2.4 整片/IAP 新件 | 需要 MCU2 升版时 | 本包 MCU2 仅 1.2.3 | MCU2 维持 1.2.3；若需升 MCU2 另发包 |
| 2 | 1.2.4 与 1.2.3 固件区几乎相同（仅版本串） | 对比二进制/期望行为差异时 | 功能行为预期与 1.2.3 一致 | 若需 sit1145 等 formal 修复，须确认源码已合入后再出 1.2.5 |
| 3 | Release Note / Test Report 为 Markdown | 正式归档 | 非 PDF | 导出 PDF 替换 |
| 4 | Git tag 若未创建 | 交付追溯 | 不满足 §8 | 打 `QC_JYF_MCU1_FW_1.2.4` 且不得移动 |
| 5 | 强制回归/专项/回滚未附完整 Pass | §13 前置 | 不满足正式发布 | 按 §5/§6 补测 |

## 5. 升级 / 烧录 / 回滚说明（§7）

| 路径 | 文件 | 步骤 |
|------|------|------|
| MCU1 OTA | `Firmware/MCU1/QC_JYF_MCU1_FW_1.2.4_5a46cf9c65d5227c4632e6ad4907d507.img` | CAN-UDS OTA；升级后读 `0xF195` = `QC_JYF_MCU1_FW_1.2.4` |
| MCU1 直接烧录 | `Firmware/MCU1/QC_JYF_MCU1_FW_1.2.4_c62d9b66dcf5fff0a922a725106d8ade.bin` | 基址 **0x08000000** |
| MCU2 IAP | `Firmware/MCU2/QC_JYF_MCU2_FW_1.2.3_52e6d5da8225b93d830d1d25c53a916f.img` | 同 1.2.3 载荷（未变） |

**限制**：回滚验证 **待补测**（§7）。

## 6. 构建与可追溯（§8/§9）

| 项 | 值 |
|----|----|
| MCU1 版本真相源 | 镜像内 `QC_JYF_MCU1_FW_1.2.4`（源码树 `SW_VERSION_STR` 可能仍为 1.2.3，**以镜像 strings 为准**；合入后应同步源码） |
| 休眠开关 | `CAN_LP_SLEEP_ENABLE = 0`（延续 1.2.3 策略） |
| 打包 | `pack_image.py` / `merge_prod_bin.py` |
| Git commit（打包时 HEAD） | `bb37917a4dd375cec9713a437e19049cdbd697e5` |

## 7. 硬件兼容性（§3）

- 固件内嵌：`QC_JYF_HW_1.1.5`
- 支持清单/限制：**待补**

## 8. 变更专项测试（§5.2）

| Change # | 测试项 | 状态 |
|----------|--------|------|
| 1 版本 1.2.4 | 读 `0xF195` = `QC_JYF_MCU1_FW_1.2.4` | **待设备实测** |
| 基线回归 | §5.1 强制项（充电/保护/OTA 等） | **待补测** |
| 休眠屏蔽（自 1.2.3 延续） | 空闲 >10s 仍应答 | **待设备实测** |

---

*生成时间: 2026-11-22 02:24:30；源: app bin + burn bin (1.2.4) + iap bin (1.2.3)*
