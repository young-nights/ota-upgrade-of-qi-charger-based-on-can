# Qi 无线充电模块 — CAN-UDS OTA 固件升级系统

> 基于 AT32F426 的车载 Qi 无线充电模块，通过 CAN 总线实现 UDS 诊断与双槽 OTA 固件升级。
>
> **架构、协议、功能需求等技术细节统一在 [docs/16. 功能需求文档](docs/16.%20功能需求文档.md) 描述**，本 README 只负责版本跟踪与功能开发状态。

---

## 目录

- [1. 项目简介](#1-项目简介)
- [2. 仓库目录结构](#2-仓库目录结构)
- [3. 快速上手](#3-快速上手)
- [4. Python 工具集](#4-python-工具集)
- [5. 版本跟踪](#5-版本跟踪)
- [6. 功能开发状态](#6-功能开发状态)
- [7. 文档索引](#7-文档索引)

---

## 1. 项目简介

### 1.1 项目目标

为车载 Qi 无线充电模块开发完整的 CAN-UDS OTA 固件升级方案，满足整车级 ECU 远程刷写需求。

### 1.2 硬件平台

| 项目 | 规格 |
|------|------|
| 主控 MCU | AT32F426KBU7-4 (Cortex-M4F, QFN32) |
| 主频 | 180 MHz (HEXT 8MHz + PLL) |
| Flash / SRAM | 128 KB / 20 KB |
| CAN 收发器 | SIT1145 (SPI 配置 Normal Mode) |
| CAN 总线 | CAN 2.0B, 29-bit 扩展帧, 250 kbps |
| 无线充接口 | USART2 (PA2/PA3, 19200 8N1) ↔ Qi 芯片 |
| 霍尔传感器 | PA0 — 磁场检测 (有磁=低/无磁=高) |
| 供电控制 | PB1 (12V Buck, 低有效), PB2 (5V Qi, 高有效) |
| 调试接口 | SWD (PA13/PA14), USART1 预留 (PB6/PB7) |

### 1.3 工程组成

项目包含 **两个 Keil 工程** + **一套 Python 工具链**：

| 工程 | 目录 | 职责 |
|------|------|------|
| Bootloader | `qi_wireless_bootloader/` | 上电引导、镜像验签、双槽选择、Trial Boot 管理（无 UDS，Safe Mode 仅挂起） |
| APP | `qi_wireless_code_app/` | Qi 充电业务、CAN 生命周期广播、UDS 诊断与 OTA 下载（APP 接收写备份区，BOOT 搬运至 App 区） |
| 工具集 | `python_tools/` | 镜像打包、签名、合并、验证、一键 OTA、功能测试脚本 |

> **说明**：APP 固件是位置无关的（运行时按 PC 判断所在槽，写入前自动重定位重签），因此**只需维护一份 APP 源码工程**。
> 2026-09-20 起架构重构（OTA-ARCH-0920）：A/B 双槽永久取消，改为 Boot + App + 备份区单 App 升级流（详见 docs/2）。

---

## 2. 仓库目录结构

```
ota-upgrade-of-qi-charger-based-on-can/
│
├── README.md                               ← 本文件（版本跟踪 + 开发状态）
│
├── qi_wireless_bootloader/                 ← Bootloader 工程 (16KB)
│   ├── mdk_project/                        ← Keil 工程 (.uvprojx)，输出 bootloader.bin
│   ├── mdk_user/                           ← main.c 入口、时钟、中断
│   ├── mdk_can/                            ← CAN 底层驱动
│   ├── mdk_app/                            ← boot_jump / boot_metadata / boot_trial
│   │   │                                   boot_safe_mode / boot_verify / isotp
│   │   │                                   sha256 / uECC / sit1145 / timer_drv
│   └── libraries/                          ← CMSIS + AT32 SPL 驱动库
│
├── qi_wireless_code_app/                   ← APP 工程 (App 区 48KB, IROM1=0x08004100)
│   ├── mdk_project/                        ← Keil 工程，输出 qi_wireless_code_app.bin
│   ├── mdk_user/                           ← main.c 入口、时钟、中断
│   ├── mdk_can/                            ← CAN 底层驱动
│   ├── mdk_app/                            ← can_protocol / ota_download / ota_trigger
│   │   │                                   qi_uart / qi_protocol / board_gpio
│   │   │                                   lifecycle / device_info / nvm_drv
│   │   │                                   isotp / sha256 / uECC / sit1145 / timer_drv
│   └── libraries/                          ← CMSIS + AT32 SPL 驱动库
│
├── python_tools/                           ← Python 工具集
│   ├── packaging/                 ← 打包签名子目录
│   │   ├── pack_image.py                    ← 裸 bin → XATO 头 .ota.bin
│   │   ├── merge_prod_bin.py               ← Boot + APP 合并产线镜像
│   │   ├── verify_image.py                 ← 镜像完整性 + 签名校验
│   │   └── sign_seed.py                    ← SecurityAccess seed 签名
│   ├── zcanpro/non-qi/           ← 功能测试子目录（6 个脚本）
│   ├── iap bin/                            ← Qi 芯片 IAP 固件 (log1.BIN / log2.BIN)
│   ├── zcanpro_ext_ota_auto.py             ← 一键 OTA（APP 内擦写 + 重定位 + 重签）
│   ├── zcanpro_qi_iap_log1.py              ← Qi 芯片 IAP 刷写 (V1.1)
│   ├── zcanpro_qi_iap_log2.py              ← Qi 芯片 IAP 刷写 (V1.2)
│   └── 脚本使用说明.md
│
├── .gitattributes                          ← 换行符规范化 (LF 入库)
├── .gitignore
│
└── docs/                                   ← 项目文档
    ├── 16. 功能需求文档.md                  ← ★ 架构 / 协议 / 需求 唯一权威来源
    ├── 合并-CAN协议-UDS-OTA工作流.md        ← CAN 协议与 OTA 完整工作流
    ├── 2. Flash 分配方案.md                 ← Flash 分区细节
    ├── 9. APP镜像打包与产线烧录.md          ← 打包脚本用法
    ├── 10. CAN-UDS OTA 测试用例表.md        ← 95 条测试用例
    ├── 11~14. 签名 / IAP 对比 / 宏定义      ← 专题文档
    ├── 15. 计划安排表.md
    ├── keys/                               ← ECDSA P-256 密钥对
    └── pdf/                                ← 参考 PDF
```

---

## 3. 快速上手

### 3.1 编译环境

| 工具 | 版本要求 |
|------|----------|
| Keil MDK | v5.38+ |
| AT32 IDE Pack | AT32F426 支持包 |
| Python | 3.8+ (工具脚本) |

### 3.2 Bootloader 编译

1. 打开 `qi_wireless_bootloader/mdk_project/qi_wireless.uvprojx`
2. Target → IROM1: `0x08000000` / `0x4000`
3. Build → 输出 `Objects/bootloader.bin`

### 3.3 APP 编译

1. 打开 `qi_wireless_code_app/mdk_project/qi_wireless_code_app.uvprojx`
2. Target → IROM1: `0x08004100` / `0xBF00`
3. Linker → 勾选 "Use Memory Layout from Target Dialog"
4. Build → 输出 `Objects/qi_wireless_code_app.bin`（裸 bin，不含 XATO 头）

### 3.4 产线烧录

```bash
cd "python_tools/packaging"

# 1. 打包 APP 镜像（加 XATO 头 + CRC32 + ECDSA 签名）
python pack_image.py

# 2. 合并 Boot + APP
python merge_prod_bin.py

# 3. 烧录 prod_image.bin 到 0x08000000 (J-Link / AT-Link / SWD)
```

> 产线烧录 Bootloader + App 区镜像（merge_prod_bin.py 合并）。备份区出厂为空，OTA 时主机写入、BOOT 搬运。
> 空片 / 双槽无效时 Boot 挂起，靠 `merge_prod_bin.py` 产线镜像救砖。

---

## 4. Python 工具集

### 4.1 打包与签名（`python_tools/packaging/`）

| 脚本 | 功能 |
|------|------|
| `pack_image.py` | 裸 bin → XATO 头 .ota.bin (CRC32 + ECDSA P-256) |
| `merge_prod_bin.py` | Bootloader + App 镜像合并为单文件产线镜像 |
| `verify_image.py` | 校验 XATO 镜像完整性 + 签名 |
| `sign_seed.py` | SecurityAccess seed 签名生成 (ECDSA P-256) |

### 4.2 OTA 与 Qi IAP（`python_tools/` 根目录）

| 脚本 | 说明 |
|------|------|
| `zcanpro_ext_ota_auto.py` | 唯一 OTA 脚本：APP 内擦非活跃槽 + 下载 + 重定位 + 重签 |
| `zcanpro_qi_iap_log1.py` | Qi 芯片 IAP 刷写 (固件包 log1.BIN, V1.1) |
| `zcanpro_qi_iap_log2.py` | Qi 芯片 IAP 刷写 (固件包 log2.BIN, V1.2) |

### 4.3 功能测试（`python_tools/zcanpro/non-qi/`）

| 脚本 | 功能 |
|------|------|
| `zcanpro_read_app_version.py` | 读取 APP 版本 (DID 0xF195) |
| `zcanpro_sn_write.py` | 写入设备 SN |
| `zcanpro_charge_start.py` | 启动充电 |
| `zcanpro_qi_read_status.py` | 读取 Qi 芯片状态 |
| `zcanpro_qi_read_version.py` | 读取 Qi 芯片版本 |
| `zcanpro_qi_set_power.py` | 设置 Qi 充电功率 |
| `zcanpro_qi_uart_bridge.py` | Qi UART 桥：`--listen` 抓取 0x2140 + 帧解析/状态位解码；`--send` 解锁后 0x2141 透传 |

---

## 5. 版本跟踪

### 5.1 当前版本

| 组件 | 版本号 | 版本字符串位置 |
|------|--------|----------------|
| APP 固件 | **QC_JYF_FW_1.1.16** | `can_protocol.c` → `SW_VERSION_STR` |
| Bootloader | QC_JYF_BL_1.0.0 | `can_protocol.c` → `BOOTLOADER_VER_STR` |
| 硬件版本 | QC_JYF_HW_1.1.5 | `can_protocol.c` → `HW_VERSION_STR` |

查询方式：UDS `22 F195`（APP 版本）/ `22 F180`（Bootloader 版本）/ `22 F193`（硬件版本）。

### 5.2 变更记录

| 日期 | 变更内容 |
|------|----------|
| 2026-10-06 | **fix Qi IAP ACK 短帧兼容 + docs/4 短 ACK/取消帧定义**：实测（0x2140 ISR 级全字节抓取）Qi 芯片对 IAP prepare 只发短帧 `55 AA 02 CC 01 CE`（仅子命令回显、无状态字节，CS 验证过），docs/4 §5.2 标准应答 `55 AA 04 CC 01 00 00 CS` 线上从未出现；固件 `qi_iap_frame_cb` 0xCC 分支对 `data_len==1` 且子命令 0x01/0x02 误判 data[0]（0x01）≠ ACK_OK(0x00) 为 NAK → 0x2130 回 NRC 0x72（芯片实际 ACK 成功）。修复：data_len==1 且子命令回显 → 短 ACK=成功（data_len≥2 现行逻辑/else 兤底不动；与 0x00 通用 ACK 分支区分）；qi_protocol.c 解析层核对一致无需改（0xCC 已按无 SEQ 切分）。docs/4 §5.2/§9.4 补实测短 ACK 形态、§5.4 补取消升级子命令 0x03（协议预留/待芯片确认 + 现状：0x2E 21 30 0x02 中止仅清 AT32 侧、芯片退出需 12V 断电）。`SW_VERSION_STR` 1.1.15→1.1.16 |
| 2026-10-06 | **fix Qi UART 波特率 9600→19200 匹配（并列根因）**：用户 2026-10-06 确认 Qi 芯片实际波特率为 19200，此前固件/文档按 9600 配置系文档口径错误——即使 AF 修对（1.1.14 MUX_7→MUX_1），波特率不匹配也会收到乱码帧，为链路不通的并列因素之一。`qi_uart.h` `QI_UART_BAUDRATE 9600U→19200U`（8N1 不变），qi_uart.c/qi_protocol.h 注释、README 硬件接口表、.agent-notes 同步；`SW_VERSION_STR` 1.1.14→1.1.15 |
| 2026-10-06 | **TC-0614 实测通过勾选**（SW 1.1.16：`2E 21 41` 发数据包正响应 `6E 21 41`，芯片 5s 内回 `55 AA 04 CC 02 00 16 E7` 经 `22 21 40` 抓到，透传回包闭环；门禁项 `7F 2E 22`/`7F 2E 33`/`7F 2E 13` 此前已实测）；测试进度 58→59/95，Qi 组 1→2/26 |
| 2026-10-06 | **TC-0613 实测通过勾选**（SW 1.1.15，六项预期全达标：充电使能后 8s 抓 22 帧 0x01 定时上报 CS 全对/SEQ 连续、空回 `00 00`、原始字节含跨读分裂、siphon 240+28、静默后溢出 bit0 1→0、组合读 `7F 22 22`）；测试进度 57→58/95，Qi 组 0→1/26 |
| 2026-10-06 | **fix Qi UART PA2/PA3 复用号错误（第三根因）**：`qi_uart.c:66/:76` 给 PA2(USART2_TX)/PA3(USART2_RX) 配的 `GPIO_MUX_7` 与 AT32F422/426 IOMUX 表（表6-1）不符——PA2 MUX1=USART2_TX、PA3 MUX1=USART2_RX，**MUX7 不在两脚功能清单内（空接）** → USART2 TX/RX 与引脚完全断开（RX 永远收不到字节：0x2140 恒空、0x2133 恒 0x0000；TX 波形上不了线：0x2141 透传、0x2013 问询、IAP prepare 全部无回包/NRC 0x72 超时）——此前所有「Qi 无声」现象由此闭环，与硬件无关（交叉验证：PA5/6/7=SPI1 用 MUX0、PA11/12=CAN1 用 MUX4 均正确且工作）。修复 `GPIO_MUX_7`→`GPIO_MUX_1`（引脚模式/上下拉不动）；`SW_VERSION_STR` 1.1.13→1.1.14 |
| 2026-10-06 | **fix Qi UART 抓取路径死代码 + 回调注册链断裂（DID 0x2140 恒回空）**：根因① `qi_protocol_init()` 全工程无调用点（main.c 只调 `qi_uart_init()`）→ `rx_callback` 恒 NULL；② feed 错挂在 `qi_uart_poll` 的回调门内 → 喂入从未执行、0x2140 恒回 `62 21 40 00 00`（实测假阴性，曾误判硬件问题）；③ 次要：主循环 `can_protocol_poll()` 先于 `qi_uart_poll()` 抽干软环，回调注册了也会饿死喂入点。修复：main.c `qi_uart_init()`→`qi_protocol_init()`（含 uart_init+register+rx_reset+tx_seq=0）；feed 移入 ISR 级 `qi_uart_rx_irq_handler` 读出后立即喂（线上真字节，含 64B 软环丢弃字节），qi_uart_sniff 读侧 `__disable_irq()` 临界区（并发模型 ISR 写/主循环读）；`SW_VERSION_STR` 1.1.12→1.1.13 |
| 2026-10-05 | **Qi UART 抓取/透传桥**：新增 qi_uart_sniff.c/h（256B 环形抓取缓冲+溢出标志，qi_uart_poll 收字节处旁路 feed，零侵入不改解析路径/ISR）；新 DID `0x2140` 抓取读取（任意会话，`62 21 40 [flags][len][data]`，siphon 分次 ≤240B）与 `0x2141` 透传发送（门禁同 0x2130/0x2131，payload 1~64B，Qi IAP 进行中拒 0x22）；上位机桥 `zcanpro_qi_uart_bridge.py`（--listen 轮询+docs/4 帧解析/Δt/状态位解码，--send 解锁透传，可组合）；docs/3 §21 DID 表、docs/10 TC-0613/0614 同步；用例总数 93→95；`SW_VERSION_STR` 1.1.11→1.1.12 |
| 2026-10-05 | **docs/10 测试验证批 + 涉 Qi 归类约定**：TC-0401~0508（13/13）、TC-0901~0910（10/10，TC-0903 经 F193 修复后复测通过）、TC-1001~1006（6/6）实测勾选；识别 DID（读取信息）凡涉 Qi 一律归 Qi 组，补 TC-0612（0x2013 Qi 版本主动问询），用例总数 92→93；测试进度累计 57/93 |
| 2026-10-05 | **fix F193 硬件版本读取恒读常量**：`fill_did_payload` DID_HW_VERSION 分支改为恒定 `device_info_pad32(out, HW_VERSION_STR)`，删除 NVM 优先逻辑（NVM `device_info.hw_version` 弃用：8B 装不下全串 + 写 SN/pubkey 建块分支 memset 清零，首写后 F193 恒读 32B 空格，TC-0903 FAIL）；device_info.c 两处 hw_version 置零处补注释。`SW_VERSION_STR` 1.1.8→1.1.11（跳过 1.1.9/1.1.10：版本串已被 TC-0508 十个测试镜像占用，且 `app_image_vX_Y_Z.bin` 打包输出名会冲突，故保版本唯一性跳至 1.1.11） |
| 2026-10-02 | **docs/10 测试用例 TC 编号全表重排**：数字段按文档顺序连续化（组内 01..N 连续、补回 TC-0103 空洞），映射 01xx→01xx（0104/0105/0106→0103/0104/0105）、05xx→02xx、06xx→03xx、08xx→04xx、13xx→05xx、10xx→06xx、11xx→07xx、12xx→08xx、07xx→09xx、09xx→10xx；TC-B/D/S 三段不变；总览表按文档顺序重排+补编号列；交叉引用同步（ota_download.c 注释、.agent-notes.md）；测试脚本改名 tc0104/0105/0106→tc0103/0104/0105（含内部标识符）；勾选状态逐条随迁（21 条已勾不变）。固件源码注释引用变更按规则递增 `SW_VERSION_STR` 1.1.7→1.1.8 |
| 2026-10-02 | **验签强制使能（OTA_VERIFY_ENFORCE 0→1）**：解除 2026-09-23 测试期临时开关（验签失败强制放行+g_verify_bypass_hit 标记），恢复严格校验——0x37 验签失败回 NRC 0x72 拒绝提交、不复位；复核公钥一致性（`g_app_ecdsa_pubkey` == `docs/keys/private.pem` 派生公钥，正常签名镜像升级不受影响）；BOOT 侧 ECDSA 复验已于 2026-09-24 移除（签名由 App 层 0x37 验签把关），如需恢复另行派单；`SW_VERSION_STR` 1.1.6→1.1.7（按代码变更版本递增规则） |
| 2026-10-02 | **版本号联动新规则 + 版本升至 QC_JYF_FW_1.1.6**：新增「代码变更版本递增规则」（每次代码修改同一提交内递增 `SW_VERSION_STR`，默认 patch 位 +1，同步 README/docs 当前版本值），写入 AGENTS.md 版本号联动规则段；`SW_VERSION_STR` 由 `QC_JYF_FW_1.1.1` 升至 `QC_JYF_FW_1.1.6`（修正历史上版本串只在 Windows 侧构建时临时改动、仓库源码停留在 1.1.1 的脱节）；docs/3（§19 闸门表、§21 DID 0xF195、§26 常量表）与 docs/11 示例同步当前值 |
| 2026-09-23 | **恢复 0x37 升级完成自复位 + 删除 11 01 ECUReset 服务**：升级完成后 APP 自动复位（`0x77`→SHUTDOWN→`NVIC_SystemReset`），主机无需 `11 01`；`0x11` 服务删除（不再应答 `51 01`/不再应答后复位，请求回 NRC serviceNotSupported）；OTA 脚本删除 `uds_ecu_reset` 步骤，升级完成信号后直接等待复位重启并验证 |
| 2026-09-23 | **README 测试用例数量对齐**：目录树（§2）与文档索引（§7）两处「62 条测试用例」更正为 **69 条**，与 docs/10 实际清点一致（69 个唯一 TC 编号，分项表 5+11+6+6+5+7+10+5+6+8=69）；docs/10 本身已为 69 无需改动 |
| 2026-09-19 | **OTA 恢复包移植批**（移植自 `backup/fix-package-0f4583f`，终审 PASS_WITH_RISKS 85，适配基线 f933c2d）：**0x37 收尾自复位切槽**——APP 在 verify+commit_trial 成功后先发 77、等 TX 空闲→SHUTDOWN→NVIC_SystemReset，Boot 按 trial PENDING 切槽，主机 11 01 保留为旧 APP 兼容/复位未生效补发；**擦除路径有界等待**（`flash_sector_erase_bounded` 局部轮询上限 `OTA_ERASE_POLLS_MAX`，超时 `flash_lock`+`__enable_irq` 恢复后回 NRC 0x72 可观测失败）；OTA 脚本判定闭环三条件（APP 应答+0x2113==目标槽+0xF195==预期版本，缺一即 FAIL+差异明细）+ 非 suppress 11 01 + 重定位宿主自检 + 版本感知拒闪 + 失败日志 0x37 NRC 语义输出；注释同步 can_protocol.c/h；docs/3 工作流同步 0x37 自复位语义；**`SW_VERSION_STR` 保持 `QC_JYF_FW_1.1.1`**（1.1.2 升级载荷由用户按需自行构建） |
| 2026-09-18 | **XATO 镜像头删除 version 字段声明**：双工程 `image_header_t`/`ota_image_header_t` 的 `version[16]`（0x4C）从定义删除，同偏移改 `hdr_reserved_ver[16]` 保留占位（打包固定填 `0x00`，偏移锁定不可回收）；打包/OTA 脚本拼装处同改保留 padding，解析侧无 0x4C 残留引用（已复核）；头总长 256B 与 magic/长度/CRC32/签名/时间戳偏移逐字节不变（gcc offsetof 前后实测一致），旧 bin/新 bin 双向兼容；固件仅头文件声明变化、无代码引用该区，无需重编译烧录 |
| 2026-09-18 | **打包产物 XATO 镜像头不再携带版本号**：头 version 区（0x4C，16B）打包固定填 `0x00`；删打包脚本 `IMAGE_VERSION` 常量及 pack/verify/OTA/relocate 全链路对头 version 字段的写入/解析/日志；固件删 `ota_get_image_version()`（Boot/APP 校验均不依赖该字段）；版本号唯一定义在固件 `SW_VERSION_STR`，发版只改固件常量 + 文档；头总长 256B/magic/长度/CRC/签名偏移全部不变，旧 bin 兼容 |
| 2026-09-18 | **软件版本读取源改造**：DID 0xF195 应答改取 APP 编译常量 `SW_VERSION_STR`（`can_protocol.c` 唯一真相源），不再读 OTA metadata / XATO 镜像头；镜像头 version 字段保留镜像标识/打包校验用途；`zcanpro_ext_ota_auto.py` 复位确认增加 0xF195 编译版本日志 |
| 2026-09-17 | 删除冗余 `qi_wireless_code_slotB/` 源码工程（固件位置无关，一份 bin 可跑 A/B 槽）；仓库治理：新增 `.gitattributes` 换行符规范化、移除误跟踪构建产物 |
| 2026-09-17 | APP 版本号回正为 QC_JYF_FW_1.1.1（与当时打包脚本的 `IMAGE_VERSION` 常量一致；该常量已于 2026-09-18 删除，镜像头不再携带版本号） |
| 2026-09-17 | APP 版本号升至 QC_JYF_FW_1.1.2（固件 `SW_VERSION_STR` + 打包脚本 `IMAGE_VERSION` + 版本读取脚本三处同步） |
| 2026-09-17 | APP 版本号回退至 QC_JYF_FW_1.1.1（固件 + 脚本 + 文档同步，1.1.2 未发布即回退） |
| 2026-09-17 | 全部文档对齐 OTA 架构反转（Boot 16KB / Slot 48KB / 地址上移） |
| 2026-09-16 | **OTA 架构反转**：下载迁入 APP（0x31/0x34/0x36/0x37 在 APP 内擦写非活跃槽），Boot 16KB 只负责选槽 + 验签 + 跳转；镜像写入前按目标槽自动重定位并重签 |
| 2026-09-16 | 脚本收敛：只留 Slot A 打包 + 单一 OTA 脚本 `zcanpro_ext_ota_auto.py`，删除 from_boot / 指定槽副本 |
| 2026-09-16 | Bootloader Safe Mode 改为挂起（`while(1)`），空片 / 双槽无效靠产线 `merge_prod_bin.py` 救砖 |
| 2026-09-15 | SecurityAccess seed 4 字节 → 32 字节；CAN 采样点改 75%（BTS1=54, BTS2=18, 18MHz÷72Tq） |
| 2026-09-15 | Qi 芯片 IAP：拆分 log1 / log2 两版脚本，实现完整 ACK 链路 + NRC 重试 |
| 2026-09-15 | 版本号统一加 QC_JYF 前缀；打包脚本迁移至 `packaging/` 子目录 |
| 2026-09-14 | 项目初始化：Bootloader + 双槽 APP + Python 工具链 |

> 完整提交历史见 `git log`。

---

## 6. 功能开发状态

### 6.1 已实现并跑通

**Bootloader**

- 选槽 + 镜像验签 (ECDSA P-256, uECC 库) + 跳转（无 UDS 服务，Safe Mode = 挂起）
- Trial Boot 试运行管理 (PENDING → ACTIVE → CONFIRMED, 10s 窗口)
- Metadata 双备份掉电保护（先写备、后写主）
- 回滚保护：Trial Boot 试运行（metadata pending→confirm）+ metadata 双副本掉电保护；Boot 校验（magic/长度/CRC32/Reset Handler/ECDSA）不依赖镜像头 0x4C 保留占位区（原 version 字段声明已删除，打包固定填 `0x00`，无基于版本号的防回滚比较）
- CAN 采样点 75% (BTS1=54, BTS2=18, 18MHz÷72Tq)

**APP 侧**

- OTA 下载：APP 内完成 0x31 擦非活跃槽 / 0x34 / 0x36 / 0x37 验签 → commit metadata PENDING → 复位 → Boot 切槽
- 镜像写入前按目标槽重定位 + 重签（位置无关，同一份固件适配 A/B）
- DID 读写（版本 / SN / 配置 / OTA 状态等）
- 生命周期 CAN 状态广播
- Qi 芯片 IAP 支持 (DID 0x2130~0x2133, UART 0xCC 协议)
- SN 序列号存储（Device Info 区，OTA 擦写跳过）
- 充电基本控制（使能 / 禁用 / 功率限值 NVM 持久化）

**Python 工具链**

- 打包 / 合并 / 校验 / 签名 / 一键 OTA / Qi IAP / 功能测试脚本
- **端到端 MCU OTA 已验证通过**

### 6.2 验证中（当前红项）

| 项 | 说明 |
|----|------|
| 0x31 擦除 + 0x37 TransferExit 非阻塞 0x78/0x77 处理 | 真实总线压力下的 Bus-Off 稳定性回归验证 |

对应计划 A1.6，是当前 MCU OTA 链路唯一未关闭项，需在真实 CAN 总线压力测试中确认无遗漏。

### 6.3 待实现 / 待完善

| 类别 | 项 | 说明 |
|------|------|------|
| 充电状态机 | 11 态 (M6) | 依赖 Qi 芯片接口完善 |
| 故障处理 | 热管理 / FOD / 硬件故障 (E1~E7) | 待实现 |
| 生命周期广播 | 完整字节格式与事件驱动 (D3/D5/D6) | 当前为简化版 |
| 低功耗管理 | H1~H8 | 当前仅 SIT1145 收发器级 Standby |
| | | APP 进入后收发器即 Normal、立即可通信；UDS 空闲 30s → SIT1145 切 Standby 监听，总线活动唤醒 |
| | | MCU 主循环纯轮询，无 WFI/Stop，MCU 本身未休眠 |
| | | 超时 30s 为硬编码（`CAN_LP_IDLE_TIMEOUT_MS`），非 SRS 要求的 DID 0x2117 可配（该 DID 未实现） |
| UDS 业务流程 | F1~F6 | 待实现 |

### 6.4 测试进度（2026-10-06）

| 分组 | 已测/总数 |
|------|-----------|
| TC-B Boot 层 | 6/10 |
| TC-D 驱动层 | 2/10 |
| TC-S 低功耗/唤醒 | 3/3 |
| TC-01xx~05xx（基本下载/会话/安全/固件管理/升级验证） | 30/30 |
| TC-06xx~08xx（Qi 充电 DID / Qi IAP / 充电控制） | 2/26 |
| TC-09xx 识别 DID | 10/10 |
| TC-10xx UDS 响应与 NRC | 6/6 |
| **合计** | **59/95** |

---

## 7. 文档索引

| 文档 | 内容 |
|------|------|
| **[16. 功能需求文档](docs/16.%20功能需求文档.md)** | ★ 系统架构、协议栈、Flash 布局、OTA 流程、UDS 服务、DID 列表、功能需求 OTA-REQ-001~015 |
| [合并-CAN协议-UDS-OTA工作流](docs/合并-CAN协议-UDS-OTA工作流.md) | CAN 协议与 UDS OTA 完整工作流实操手册 |
| [2. Flash 分配方案](docs/2.%20Flash%20分配方案.md) | 128KB Flash 分区、Metadata 结构、XATO 头 |
| [9. APP镜像打包与产线烧录](docs/9.%20APP镜像打包与产线烧录.md) | 打包脚本用法、Keil IROM 配置 |
| [10. CAN-UDS OTA 测试用例表](docs/10.%20CAN-UDS%20OTA%20测试用例表.md) | 95 条测试用例 (P0/P1/P2) |
| [11. 签名校验与脚本使用](docs/11.%20签名校验与脚本使用.md) | 签名工具使用说明 |
| [12. 签名原理与Seed机制](docs/12.%20签名原理与Seed机制.md) | ECDSA P-256 + SHA-256 原理 |
| [13. 官方IAP例程vs自定义Bootloader对比](docs/13.%20官方IAP例程vs自定义Bootloader对比.md) | 官方 IAP 方案与本项目 Bootloader 对比 |
| [14. 宏定义切换方式](docs/14.%20宏定义切换方式.md) | 编译宏配置与功能切换 |
| [15. 计划安排表](docs/15.%20计划安排表.md) | 项目开发计划 |
| [4. IAP数据通信协议规范](docs/4.%20IAP数据通信协议规范.md) | MCU ↔ Qi 芯片 UART 通信协议 |
| [6. qi_charger_srs_zh](docs/6.%20qi_charger_srs_zh.md) | 软件需求规格说明书 |
| [1. AT32F426KBU7-4_引脚定义](docs/1.%20AT32F426KBU7-4_引脚定义.md) | QFN32 全引脚功能定义 |

---

## 许可证

本项目为内部开发项目，未经授权不得外传。

---

> **Lime 固件团队** — 2026
