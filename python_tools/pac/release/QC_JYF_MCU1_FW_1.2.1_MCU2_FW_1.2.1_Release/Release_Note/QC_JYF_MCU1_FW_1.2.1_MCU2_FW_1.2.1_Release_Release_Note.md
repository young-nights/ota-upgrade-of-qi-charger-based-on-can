# QC_JYF_MCU1_FW_1.2.1_MCU2_FW_1.2.1_Release Release Note

> **草稿占位** — 按《JYF Qi 充电器固件发布与交付要求 v1.01》§4 生成，
> 变更细节 / 已知问题 / 测试结论需研发与测试补充后转 PDF 归档。

## 1. 发布标识

| 项 | 值 |
|----|----|
| MCU1 固件版本 | `QC_JYF_MCU1_FW_1.2.1` |
| MCU2 固件版本 | `QC_JYF_MCU2_FW_1.2.1` |
| 支持硬件版本 | `QC_JYF_HW_1.1.5`（固件内嵌；兼容性限制：待补） |
| Bootloader 版本 | `QC_JYF_BL_1.0.0`（MCU1，固件内嵌） |
| 发布日期 | 2026-10-19 |
| 发布类型 | 正式交付包（首次按 v1.01 规范命名打包） |
| Git commit ID | `72c24b3b71c193929c1ad9d63bae9d4a78d9d8f9` |
| Git tag | （未打 tag，交付前须按 §8 打 QC_JYF_MCU1_FW_x.y.z / QC_JYF_MCU2_FW_x.y.z） |
| 发布包名 | `QC_JYF_MCU1_FW_1.2.1_MCU2_FW_1.2.1_Release.zip` |

## 2. 交付文件清单（含完整 MD5）

| 文件名 | MCU | 用途 | MD5 | 大小 | 源 |
|--------|-----|------|-----|------|----|
| QC_JYF_MCU1_FW_1.2.1_529b857f006167d3a98ee6ef668605c8.img | MCU1 | img | 529b857f006167d3a98ee6ef668605c8 | 49440 | app_image_v1_2_1.bin |
| QC_JYF_MCU1_FW_1.2.1_b1fe4429f2e4f2b78db198c122c093d6.bin | MCU1 | bin | b1fe4429f2e4f2b78db198c122c093d6 | 61728 | prod_image_v1_2_1.bin |
| QC_JYF_MCU2_FW_1.2.1_26008d5764676d856a3ffb80ca797317.img | MCU2 | img | 26008d5764676d856a3ffb80ca797317 | 12956 | QI_IAP_Ver_1_2_1.BIN |
| QC_JYF_MCU2_FW_1.2.1_c2ebde0deb69f7bf417e110dfef4729f.bin | MCU2 | bin | c2ebde0deb69f7bf417e110dfef4729f | 15004 | Z0001_015_boot轮询_APP_1016_010201.hex（Intel HEX，0x0000~0x3A9B，空洞填 0xFF → raw bin） |

### 文件类型约定（§2.2）

| 扩展名 | 用途 |
|--------|------|
| `.img` | 应用 OTA 升级固件 |
| `.bin` | 完整 MCU 烧录固件（含 bootloader + 应用），直接烧录 |

## 3. Change Log

### 3.1 相对上一发布（如有）

| # | 问题/需求描述 | 修改细节 | 修改后预期行为 | 受影响 MCU/模块 | issue/ticket |
|---|---------------|----------|----------------|-----------------|--------------|
| 1 | **待补充** | 当前 `pac/` 内为构建产物，仓库侧无对应变更说明 | — | MCU1 / MCU2 | — |

> **§4.1 要求**：不接受「Bug fix / 代码优化 / 小改动」等无细节描述。上表必须由研发填写完整后再转 PDF。

### 3.2 未变 MCU 说明（§4）

- 本包为成套 1.2.1：MCU1 与 MCU2 版本号一致对齐。

## 4. Known Issues（已知问题）

| # | 问题描述 | 触发条件 | 影响 | 规避方法 |
|---|----------|----------|------|----------|
| 1 | 发布包内 Release Note / Self Test Report 为 **Markdown 草稿**，非正式 PDF | 当前交付准备阶段 | 不满足 §4/§5 正式格式 | 补齐内容后导出 PDF 并替换 |

> 影响安全/充电功能/可靠性/固件升级/生产的已知问题须 **主动提前通知 Lime**（§4.2）。

## 5. 升级 / 烧录 / 回滚说明（§7）

| 路径 | 文件 | 步骤 |
|------|------|------|
| MCU1 OTA | `.img` | CAN-UDS OTA（备份区下载 → 验签 → 切槽）；升级后读 `0xF195` 核对版本 |
| MCU1 直接烧录 | `.bin` | 烧录器基址 `0x08000000`（Boot+App 合并）；上电核对版本与基本功能 |
| MCU2 IAP 升级 | `.img` | UDS `2E 20 10 04` 选 MCU2 → `31` 擦除 → `34`/`36` 转发 → `37` 完成；或上位机 Qi IAP |
| MCU2 整片烧录 | `.bin` | 按 Qi 芯片编程器流程烧录整片映射（含中断向量区） |

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

