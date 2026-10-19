# QC_JYF_MCU1_FW_1.2.2_MCU2_FW_1.2.2_Release Release Note

> **草稿占位** — 按《JYF Qi 充电器固件发布与交付要求 v1.01》§4 生成，
> 变更细节 / 已知问题 / 测试结论需研发与测试补充后转 PDF 归档。

## 1. 发布标识

| 项 | 值 |
|----|----|
| MCU1 固件版本 | `QC_JYF_MCU1_FW_1.2.2` |
| MCU2 固件版本 | `QC_JYF_MCU2_FW_1.2.2` |
| 支持硬件版本 | `QC_JYF_HW_1.1.5`（固件内嵌；兼容性限制：待补） |
| Bootloader 版本 | `QC_JYF_BL_1.0.0`（MCU1，固件内嵌） |
| 发布日期 | 2026-10-19 |
| 发布类型 | 正式交付包（首次按 v1.01 规范命名打包） |
| Git commit ID | `72c24b3b71c193929c1ad9d63bae9d4a78d9d8f9` |
| Git tag | （未打 tag，交付前须按 §8 打 QC_JYF_MCU1_FW_x.y.z / QC_JYF_MCU2_FW_x.y.z） |
| 发布包名 | `QC_JYF_MCU1_FW_1.2.2_MCU2_FW_1.2.2_Release.zip` |

## 2. 交付文件清单（含完整 MD5）

| 文件名 | MCU | 用途 | MD5 | 大小 | 源 |
|--------|-----|------|-----|------|----|
| QC_JYF_MCU1_FW_1.2.2_f6ec9fa87cf5c9d6c4eb426726197d52.img | MCU1 | img | f6ec9fa87cf5c9d6c4eb426726197d52 | 49440 | app_image_v1_2_2.bin |
| QC_JYF_MCU2_FW_1.2.2_cc8c6a6ea31fa2c1d0255ed0c08fa842.img | MCU2 | img | cc8c6a6ea31fa2c1d0255ed0c08fa842 | 12956 | QI_IAP_Ver_1_2_2.BIN |
| QC_JYF_MCU1_FW_1.2.2_<MD5>.bin | MCU1 | 烧录 | — | — | **本版本不交付**（确认仅交 1.2.1 烧录件） |
| QC_JYF_MCU2_FW_1.2.2_<MD5>.bin | MCU2 | 烧录 | — | — | **本版本不交付**（无 1.2.2 整片映射） |

### 文件类型约定（§2.2）

| 扩展名 | 用途 |
|--------|------|
| `.img` | 应用 OTA 升级固件 |
| `.bin` | 完整 MCU 烧录固件（含 bootloader + 应用），直接烧录 |

## 3. Change Log

### 3.1 相对上一发布（如有）

| # | 问题/需求描述 | 修改细节 | 修改后预期行为 | 受影响 MCU/模块 | issue/ticket |
|---|---------------|----------|----------------|-----------------|--------------|
| 1 | 版本号递增 1.2.1 → 1.2.2 | MCU1：`SW_VERSION_STR` = `QC_JYF_MCU1_FW_1.2.2`（镜像 strings 已核对）；MCU2：IAP 载荷版本字段 0x302D `01 02 01`→`01 02 02`（与 QI_IAP_Ver_1_2_2.BIN 一致） | UDS `0xF195` / Qi 版本查询上报 1.2.2 | MCU1 APP / MCU2 IAP 载荷 | — |
| 2 | **功能变更明细待补充** | 1.2.1↔1.2.2 的 `app_image` 除版本串/签名区外共 71 字节差异，功能意图未在 `pac/` 侧说明 | — | MCU1 | — |

> **§4.1 要求**：上表第 2 行必须由研发补全问题描述/修改细节/预期行为后再转 PDF。

### 3.2 未变 MCU 说明（§4）

- MCU1、MCU2 **均有**版本递增（1.2.1 → 1.2.2）。

## 4. Known Issues（已知问题）

| # | 问题描述 | 触发条件 | 影响 | 规避方法 |
|---|----------|----------|------|----------|
| 1 | 发布包内 Release Note / Self Test Report 为 **Markdown 草稿**，非正式 PDF | 当前交付准备阶段 | 不满足 §4/§5 正式格式 | 补齐内容后导出 PDF 并替换 |
| 2 | 本版本缺少 MCU1/MCU2 整片烧录 `.bin` | 产线整片烧录 | 只能 OTA/IAP 升级到 1.2.2；整片烧录请用 1.2.1 包或后续补件 | 确认产线策略：先烧 1.2.1 再 OTA 到 1.2.2 |

> 影响安全/充电功能/可靠性/固件升级/生产的已知问题须 **主动提前通知 Lime**（§4.2）。

## 5. 升级 / 烧录 / 回滚说明（§7）

| 路径 | 文件 | 步骤 |
|------|------|------|
| MCU1 OTA | `.img` | CAN-UDS OTA（备份区下载 → 验签 → 切槽）；升级后读 `0xF195` 核对版本 |
| MCU1 直接烧录 | `.bin` | 烧录器基址 `0x08000000`（Boot+App 合并）；上电核对版本与基本功能 |
| MCU2 IAP 升级 | `.img` | 同上；本版本无整片烧录件 |

**限制**：回滚支持情况、升级中断恢复能力 **待补测后写明**（§7）。

## 6. 构建与可追溯（§8/§9）

| 项 | 值 |
|----|----|
| MCU1 版本真相源 | `qi_wireless_code_app/mdk_app/Src/can_protocol.c` → `SW_VERSION_STR` |
| MCU1 打包 | `python_tools/packaging/pack_image.py`（XATO 256B 头 + 签名） |
| MCU1 合并烧录 | `python_tools/packaging/merge_prod_bin.py`（Boot 0x08000000 + App 镜像） |
| 签名密钥 | `docs/keys/private.pem`（公钥已预置固件） |
| 工具链 | Keil MDK（AT32F426）；构建复现配置 **待补**（§9） |
| Git commit | `72c24b3b71c193929c1ad9d63bae9d4a78d9d8f9` |

## 7. 硬件兼容性（§3）

- 固件内嵌硬件版本：`QC_JYF_HW_1.1.5`
- 支持硬件版本清单 / 限制：**待补**（须在正式 Release Note 声明）

