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

### 3.1 相对上一发布（1.1.x 基线 → 1.2.1，2026-10-18~19）

> 版本族切换：`SW_VERSION_STR` 由 `QC_JYF_MCU1_FW_1.1.69` 升至 `QC_JYF_MCU1_FW_1.2.1`（commit `1e26526`）。
> 1.2.1 为 **1.1.x 通信/OTA 可靠性修复系列的固化基线**。MCU2 随包对齐 1.2.1。

| # | 问题/需求描述 | 修改细节 | 修改后预期行为 | 受影响 MCU/模块 | issue/ticket |
|---|---------------|----------|----------------|-----------------|--------------|
| 1 | host→ECU 多帧 UDS 请求（`0x34`/`0x36`/`27 02`）无响应或超时 | ISO-TP FC 发送前等 TX 空闲；FF 收到立即发 FC；FC 确认超时不误清 `rx_ctx.state`；重复/乱序 CF 忽略不 abort；FF 路径裸发 FC 绕开 bus-off 恢复清 FIFO；`ISOTP_N_CR` 1s→2s；主机 FF 无 FC 重发、末帧 CF 重发 | `0x34` 回正响应 `74 20 01 00`，`0x36` 多块传输完整 | MCU1 / ISO-TP + OTA | — |
| 2 | SA 分片 / `0x36` 组包期间会话误超时，解锁与擦除状态被清 | `SESSION_TIMEOUT_MS` 5s→30s；ISO-TP 每帧刷新 S3；长操作刷 S3；主机 `27 03` 分片每 4 帧 `3E` 保活 | 分片/组包期间会话保持，后续服务不再回 NRC `0x22`/`0x24` | MCU1 / 会话管理 | — |
| 3 | SF 应答在 CF 回调上下文发送失败被静默丢弃 | SF 优先立即发送，失败落 `g_tx_pend` 兜底；睡眠态也泵出待发应答；多帧 UDS 期间抑制 lifecycle/ident 抢 TX 邮箱（`g_uds_mf_pending`） | 单帧/多帧应答均可靠送达，无静默丢失 | MCU1 / CAN 协议栈 | — |
| 4 | `27 03` 重试撞 NRC `0x71`（sequence error） | `blockSeq` 幂等：重复帧直接回正响应，不重复计数 | 主机重试安全，解锁可恢复 | MCU1 / SecurityAccess | — |
| 5 | 生命周期周期广播总线负载偏高 | `app_config.h` 集中开关；周期广播 1s→10s（`LIFECYCLE_PERIODIC_INTERVAL_MS=10000`） | 广播 0.1Hz，降低总线负载 | MCU1 / lifecycle | — |
| 6 | 编译期开关散落、难复现构建 | 新增 `app_config.h` 集中 `SA_VERIFY_ENFORCE` / `OTA_VERIFY_ENFORCE` / `CAN_LP_SLEEP_ENABLE`；移除 `27 01` 冗余探测 | 构建配置可追溯、可复现 | MCU1 / 构建 | — |

### 3.2 未变 MCU 说明（§4）

- 本包为 **MCU1+MCU2 成套 1.2.1**：MCU2 载荷版本字段 `01 02 01`，与 MCU1 版本号对齐。
- MCU2 功能相对早期 QI IAP 无独立变更说明（版本字节随包对齐）。

## 4. Known Issues（已知问题）

| # | 问题描述 | 触发条件 | 影响 | 规避方法 |
|---|----------|----------|------|----------|
| 1 | 发布包内 Release Note / Self Test Report 为 **Markdown 草稿**，非正式 PDF | 当前交付准备阶段 | 不满足 §4/§5 正式格式 | 补齐内容后导出 PDF 并替换 |
| 2 | 正式发布尚未打 Git tag | 交付前 | 不满足 §8 可追溯闭环 | 按 `QC_JYF_MCU1_FW_1.2.1` / `QC_JYF_MCU2_FW_1.2.1` 打 tag 且不得移动 |
| 3 | 回滚（新→旧）与升级中断恢复未做完整验证 | 量产前 | §7 要求项缺口 | 补 UD-08 / UD-04 后写入结论 |

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

