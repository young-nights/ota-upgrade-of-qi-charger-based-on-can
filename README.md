# Qi 无线充电模块 — CAN-UDS OTA 固件升级系统

> 基于 AT32F426 的车载 Qi 无线充电模块，通过 CAN 总线实现 UDS 诊断与双槽 OTA 固件升级。
>
> **协议、功能需求等细节见 [docs/15. 功能需求文档](docs/15.%20功能需求文档.md)**；本 README 提供项目架构拓扑/流程总览与当前版本。
> **变更记录已停止维护（2026-11-23）**：日常修改不再向 README 追加版本更新条目，变更说明见提交信息（`git log`）。

---

## 目录

- [1. 项目简介](#1-项目简介)
- [2. 项目架构（拓扑 / 流程）](#2-项目架构拓扑--流程)
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

| 工程 | 职责 |
|------|------|
| Bootloader | 上电引导、镜像验签（ECDSA P-256）、选槽、Trial Boot 管理（无 UDS，Safe Mode 仅挂起） |
| APP | Qi 充电业务、CAN 生命周期广播、UDS 诊断与 OTA 下载（APP 写备份区，BOOT 搬运至 App 区） |
| Python 工具链 | 镜像打包、签名、合并、验证、一键 OTA、功能测试脚本 |
| 上位机 ×3 | 维修 QiChargerStationTool / 产线完整 ProductionStationTool / 产线简化 simple_ProductionTool |
| QI-SERVER | 产线数据汇总（HTTP + SQLite）、工位机接入与 SN 联机分配 |

> **说明**：APP 固件位置无关（运行时按 PC 判断所在区，写入前自动重定位重签），只需维护一份 APP 源码工程。
> 2026-09-20 起架构重构（OTA-ARCH-0920）：A/B 双槽永久取消，改为 Boot + App + 备份区单 App 升级流（详见 docs/2）。

---

## 2. 项目架构（拓扑 / 流程）

### 2.1 系统拓扑（产线局域网）

```
                     ┌────────────────────────┐
                     │   交换机（产线 LAN）     │
                     └───────────┬────────────┘
           ┌─────────────────────┼─────────────────────┐
           ▼                     ▼                     ▼
  ┌─────────────────┐   ┌─────────────────┐   ┌──────────────────────┐
  │ 工位机 ST-01     │   │ 工位机 ST-02     │   │ 汇总机 QI-SERVER      │
  │ Windows + 上位机 │   │ Windows + 上位机 │   │ 192.168.10.10:5000   │
  │ ZQWL-CAN 盒      │   │ ZQWL-CAN 盒      │   │ SQLite 汇总库         │
  └────────┬────────┘   └────────┬────────┘   │ 汇总 / 查询 / 导出    │
           │ CAN 2.0B 250k       │             └──────────┬───────────┘
           ▼                     ▼                        ▲ HTTP
  ┌─────────────────┐   ┌─────────────────┐               │ 结果上报 / 心跳 / SN 取号
  │ 被测充电器 ×N    │   │ 被测充电器 ×N    │   ◄───────────┘
  │ MCU1 + MCU2(Qi) │   │ MCU1 + MCU2(Qi) │
  └─────────────────┘   └─────────────────┘

维修工位（不经交换机，直连设备）：

  ┌──────────────────────┐  CAN   ┌─────────────────┐
  │ QiChargerStationTool │ ─────► │ 被测充电器       │
  └──────────────────────┘        └─────────────────┘
```

### 2.2 固件架构（Flash 分区与启动）

```
Flash 0x08000000
┌──────────────────────────────────────┐
│ Bootloader（16KB）                    │ 上电引导：镜像验签（ECDSA P-256）、
│                                      │ 选槽、Trial Boot 管理、跳转（无 UDS）
├──────────────────────────────────────┤
│ APP 区（位置无关）                     │ UDS 诊断服务、OTA 下载
│                                      │ （0x31 擦除 / 0x34 / 0x36 / 0x37）、
│                                      │ 生命周期广播、Qi IAP、充电控制、SN 存储
├──────────────────────────────────────┤
│ 备份区                                │ OTA 下载目标区；
│                                      │ BOOT 校验通过后搬运至 App 区
└──────────────────────────────────────┘

上电：BOOT → 验签 / 选槽 → 跳转 App
OTA ：App 内下载写备份区 → 复位 → BOOT 验签搬运 → 启动新固件
```

### 2.3 OTA 升级流程

```
[离线打包]
  pack_image.py    → XATO 头 + CRC + ECDSA 签名 → app_image.ota.bin
  merge_prod_bin.py → Boot + App 合并 → prod_image.bin → 烧录 / OTA

[在线升级]   上位机                                  设备
  10 01 复位默认会话 ───────────────────────────────►
  27 SA 解锁（seed → 签名 → 分片回传）────────────────►
  10 02 切编程会话 ─────────────────────────────────►  （断 5V Qi 电）
  2E 20 10 选择目标（MCU1 / MCU2）──────────────────►
  31 擦除备份区 ────────────────────────────────────►  （15~90s）
  34 RequestDownload（块长按设备宣告自适应）─────────►
  36 TransferData × N ─────────────────────────────►  写备份区
  37 TransferExit ──────────────────────────────────►  设备自复位
  22 F195 版本回读验证 ─────────────────────────────►  PASS / FAIL

门禁顺序：10 01 → SA → 10 02 → 立即写（0x22 / 0x33 整套重试 ≤ 3 次）
```

### 2.4 产线测试流程（上位机工位）

```
工位1 扫码写SN ： 扫码录入 → SN 结构校验 → 写入设备 + 回读 → 开充电
                  → 线圈 A/B 电流区间判定 → 停充 → 入库（SQLite）
工位2 老化     ： 连接 → 开启带载 → 带载老化 30min → 判限 → 入库
工位3 SN复测   ： 读设备 SN → 关联数据库（工位1/2 历史）→ 版本预设比对
                  → 开充电 → 线圈 A/B → 停充 → 入库
工位4 出货门禁 ： 读 SN → 数据库门禁（工位1/2/3 全 PASS）→ SN 打印（接口占位）
                  → 软硬件版本与预设比对 → PASS / FAIL
```

### 2.5 数据流与 SN 联机分配

```
工位机测试记录 → 本地 SQLite（station_records.db）
      │  HTTP POST /api/v1/test-results（失败入本地队列，后台补传）
      ▼
QI-SERVER（192.168.10.10:5000）→ SQLite 汇总库 → 查询 / 导出 / MES

工位1 SN 联机分配：工位机 ──HTTP 取号──► QI-SERVER（防撞号）→ 写入设备
工位机接入       ：工位机 ──心跳──► 汇总机按远端 IP 自动登记（station_access）
```

### 2.6 上位机工具

```
┌───────────────────────────┐
│ QiChargerStationTool      │ 维修上位机：直连设备，升级 / 诊断 / 维护
├───────────────────────────┤
│ ProductionStationTool     │ 产线完整版：工位 1~4 + 汇总机 + 标签打印
├───────────────────────────┤
│ simple_ProductionTool     │ 产线简化版（现场 win-x64）：工位 1/2/3
└───────────────────────────┘
```
---

## 3. 快速上手

### 3.1 Bootloader 编译

1. 打开 `qi_wireless_bootloader/mdk_project/qi_wireless.uvprojx`
2. Target → IROM1: `0x08000000` / `0x4000`
3. Build → 输出 `Objects/bootloader.bin`

### 3.2 APP 编译

1. 打开 `qi_wireless_code_app/mdk_project/qi_wireless_code_app.uvprojx`
2. Target → IROM1: `0x08004100` / `0xBF00`
3. Linker → 勾选 "Use Memory Layout from Target Dialog"
4. Build → 输出 `Objects/qi_wireless_code_app.bin`（裸 bin，不含 XATO 头）

### 3.3 产线烧录

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

### 4.2 OTA / 功能测试脚本

`python_tools/` 下的 zcanpro/ 与 wsl/ 主机脚本已于 2026-10-28 全部删除（废弃）。
OTA / SN / 充电 / Qi 控制等主机操作由 **QiChargerStationTool**（维修）与
**ProductionStationTool**（产线）承担。

---

## 5. 版本跟踪

### 5.1 当前版本

| 组件 | 版本号 | 版本字符串位置 |
|------|--------|----------------|
| APP 固件 | **QC_JYF_MCU1_FW_1.1.89**（关充 0x2101 写前先回 0x78 + 工具超时重发） | `can_protocol.c` → `SW_VERSION_STR` |
| Bootloader | QC_JYF_BL_1.0.0 | `can_protocol.c` → `BOOTLOADER_VER_STR` |
| 硬件版本 | QC_JYF_HW_1.1.5 | `can_protocol.c` → `HW_VERSION_STR` |

查询方式：UDS `22 F195`（APP 版本）/ `22 F180`（Bootloader 版本）/ `22 F193`（硬件版本）。

> 变更记录已停止维护（2026-11-23）——变更说明见提交信息（`git log`）。

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
- Qi 芯片 IAP 支持 (DID 0x2132~0x2133 状态/版本查询, 0x34/0x36/0x37 统一 OTA 路径, UART 0xCC 协议)
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
| **[15. 功能需求文档](docs/15.%20功能需求文档.md)** | ★ 系统架构、协议栈、Flash 布局、OTA 流程、UDS 服务、DID 列表、功能需求 OTA-REQ-001~015 |
| [1. AT32F426KBU7-4_引脚定义](docs/1.%20AT32F426KBU7-4_引脚定义.md) | QFN32 全引脚功能定义 |
| [2. Flash 分配方案](docs/2.%20Flash%20分配方案.md) | 128KB Flash 分区、Metadata 结构、XATO 头 |
| [3. 合并-CAN协议-UDS-OTA工作流](docs/3.%20合并-CAN协议-UDS-OTA工作流.md) | CAN 协议与 UDS OTA 完整工作流实操手册 |
| [4. IAP数据通信协议规范](docs/4.%20IAP数据通信协议规范.md) | MCU ↔ Qi 芯片 UART 通信协议 |
| [5. APP镜像打包与产线烧录](docs/5.%20APP镜像打包与产线烧录.md) | 打包脚本用法、Keil IROM 配置 |
| [6. 签名校验与脚本使用](docs/6.%20签名校验与脚本使用.md) | 签名工具使用说明 |
| [7. 签名原理与Seed机制](docs/7.%20签名原理与Seed机制.md) | ECDSA P-256 + SHA-256 原理 |
| [8. 官方IAP例程vs自定义Bootloader对比](docs/8.%20官方IAP例程vs自定义Bootloader对比.md) | 官方 IAP 方案与本项目 Bootloader 对比 |
| [9. 宏定义切换方式](docs/9.%20宏定义切换方式.md) | 编译宏配置与功能切换 |
| [10. CAN-UDS OTA 测试用例表](docs/10.%20CAN-UDS%20OTA%20测试用例表.md) | 95 条测试用例 (P0/P1/P2) |
| [11. 维修上位机使用与开发说明](docs/11.%20维修上位机使用与开发说明.md) | 单机维修上位机工程结构与用法 |
| [12. CAN验证帧参考](docs/12.%20CAN验证帧参考.md) | CAN 帧格式、生命周期帧、判读参考 |
| [18. 产线多工位数据同步与服务器配置](docs/18.%20产线多工位数据同步与服务器配置.md) | QI-SERVER 设计、工位机上传配置 |
| [17. 休眠与选择性唤醒验证操作](docs/17.%20休眠与选择性唤醒验证操作.md) | 休眠/唤醒验证操作步骤 |
| [21. 工位1多机并发SN联机分配方案](docs/21.%20工位1多机并发SN联机分配方案.md) | SN 联机取号方案、网络拓扑与机位动态增减 |
| [22. QI-SERVER 服务器搭建手册](docs/22.%20QI-SERVER%20服务器搭建手册（从零到跑通）.md) | 服务器从零到跑通（含完整 Program.cs） |

---

## 许可证

本项目为内部开发项目，未经授权不得外传。

---

> **Lime 固件团队** — 2026
