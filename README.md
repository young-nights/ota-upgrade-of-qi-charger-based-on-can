# Qi 无线充电模块 — CAN-UDS OTA 固件升级系统

> 基于 AT32F426 的车载 Qi 无线充电模块，通过 CAN 总线实现 UDS 诊断与双槽 OTA 固件升级。
>
> **架构、协议、功能需求等技术细节统一在 [docs/15. 功能需求文档](docs/15.%20功能需求文档.md) 描述**，本 README 只负责版本跟踪与功能开发状态。

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
│   ├── iap bin/                            ← Qi 芯片 IAP 固件 (log1.BIN / log2.BIN)
│   └── 脚本使用说明.md                     ← （zcanpro/wsl 脚本已删除 2026-10-28）
│
├── .gitattributes                          ← 换行符规范化 (LF 入库)
├── .gitignore
│
└── docs/                                   ← 项目文档
    ├── 15. 功能需求文档.md                  ← ★ 架构 / 协议 / 需求 唯一权威来源
    ├── 1. AT32F426KBU7-4_引脚定义.md        ← QFN32 全引脚功能定义
    ├── 2. Flash 分配方案.md                 ← Flash 分区细节
    ├── 3. 合并-CAN协议-UDS-OTA工作流.md     ← CAN 协议与 OTA 完整工作流
    ├── 4. IAP数据通信协议规范.md            ← Qi 芯片 UART 通信协议
    ├── 5. APP镜像打包与产线烧录.md          ← 打包脚本用法
    ├── 6~9. 签名 / IAP 对比 / 宏定义        ← 专题文档
    ├── 10. CAN-UDS OTA 测试用例表.md        ← 95 条测试用例
    ├── 11. 维修上位机使用与开发说明.md      ← 单机上位机工程结构
    ├── 12. CAN验证帧参考.md                 ← CAN 帧格式与判读
    ├── 18. 产线多工位数据同步与服务器配置.md ← QI-SERVER 设计
    ├── 17. 休眠与选择性唤醒验证操作.md      ← 唤醒验证操作
    ├── 21. 工位1多机并发SN联机分配方案.md   ← SN 联机取号 + 网络拓扑
    ├── 22. QI-SERVER 服务器搭建手册.md      ← 服务器从零到跑通
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

### 4.2 OTA / 功能测试脚本

`python_tools/` 下的 zcanpro/ 与 wsl/ 主机脚本已于 2026-10-28 全部删除（废弃）。
OTA / SN / 充电 / Qi 控制等主机操作由 **QiChargerStationTool**（维修）与
**ProductionStationTool**（产线）承担。

---

## 5. 版本跟踪

### 5.1 当前版本

| 组件 | 版本号 | 版本字符串位置 |
|------|--------|----------------|
| APP 固件 | **QC_JYF_MCU1_FW_1.1.63** | `can_protocol.c` → `SW_VERSION_STR` |
| Bootloader | QC_JYF_BL_1.0.0 | `can_protocol.c` → `BOOTLOADER_VER_STR` |
| 硬件版本 | QC_JYF_HW_1.1.5 | `can_protocol.c` → `HW_VERSION_STR` |

查询方式：UDS `22 F195`（APP 版本）/ `22 F180`（Bootloader 版本）/ `22 F193`（硬件版本）。

### 5.2 变更记录

| 日期 | 变更内容 |
|------|----------|
| 2026-11-05 | **feat(tool): 霍尔真充判定显示——「磁铁存在/不存在」可视化实际充电开启**。背景：开启充电（DID 0x2101）只是使能，固件 `board_charge_poll` 门禁还要 PA0 霍尔在位才真正接通充电，上位机此前无法分辨"指令成功"与"真充开启"。**不新增 DID**（固件已有 `0x2118` PA0 霍尔：0x00=磁铁/手机在位、0x01=不在位，任意会话可读），补上位机读取与显示：①Core `QiChipStatus` 增 `ClampState`/`MagnetPresent`/`MagnetName`，`QiChipStatusCodec` 增纯函数 `MagnetName()`/`ChargingVerdict()`（真充判定=0x2101 使能且霍尔在位，与固件门禁同逻辑）；②仪表盘新增「磁铁 / 实际充电」大字：绿=实际开启、红=磁铁不存在未开启、蓝=在位未使能、灰=未知；摘要行带真充判定；③「开启充电」写入成功后立即读 0x2118 给判定（`已开启 — 实际充电已开启（磁铁存在）` / `…未开启（磁铁不存在）` + 操作日志提示），「Qi 状态检测」轮询同步刷新。WSL 验证：Core build 0W/0E，tests 19/19（新增 MagnetName/ChargingVerdict 门禁一致性用例） |
| 2026-11-05 | **fix(sit1145): 冷插拔后 WUF ID 写丢失仍睡死——校验补全四字节 + 逐写稳定延时 + E0~E3 改配置后快照**。实测（fw 1.1.62）：插拔→零指令自入睡（E0 EVENT_EN=0x03 门禁通过）→ 4 轮 WUF burst 全失败、车辆流量也唤不醒=睡死。根因：冷插拔 SPI 写丢失打在 wuf_config 的 PN 背靠背写入上（id1/id2 静默丢），而 wuf_ok 校验**只查 id0/id3**——中间字节丢了照样通过 → WUF ID=0x18xx0D03 错位永不匹配；CPNC=1 又关标准唤醒=睡死。且 E0~E3 发在 sleep_enter **配置之前**，快照显示的是残留/默认值，配置真值从不可见，故障无从定位。修复：①wuf_ok 校验补全 ID 四字节 + MASK + DM0/DM1（任一字节丢失→重试×3→不过则退化标准 WUP，车辆流量可唤醒）；②`sit1145_wuf_config` PN 寄存器逐写加稳定延时（消除背靠背写丢失窗口）；③`sit1145_sleep_enter` 拆分 `sit1145_sleep_config`/`sit1145_sleep_commit`，E0~E3 移到配置后、commit 前发送=真值快照（E2 现在直接显示 WUF ID 终态，故障自证）。`SW_VERSION_STR` 1.1.62→1.1.63 |
| 2026-11-05 | **feat(prodtool): 运行日志改弹窗，不占主页面**。工位1/工位2 主页面移除底部日志区与拖拽分隔条（数据区占满全屏高），流程区标题行加「日志」小按钮；点击弹出无模式 `LogWindow`（边测边看，重复点击激活已开窗口）。新增 `LogBuffer` 共享缓冲（流程写入/日志窗实时刷新，线程安全），`LogPanel` 改为绑定式视图（可绑多个视图，「清除」对缓冲生效）。编译 0 错误、单测 16/16 |
| 2026-11-05 | **feat(prodtool): 设置弹窗按类型分页，参数移出主界面**。「设置」弹窗分两页：**「机位与网络」**（机位命名/查看 IP/连通检测/跳转系统网络设置/变更记录）与**「检测参数」**（步骤 5 双线圈 A/B 阈值、A 窗口、B 持续、整体超时，校验后存 `machine.json`，保存后下次测试生效，留痕入 `machine_change.log`，带「恢复默认」）；主界面移除可现场改的阈值输入框（改一行只读提示指向设置页），采样参数改为测试开始时从 `machine.json` 读取。编译 0 错误、单测 16/16 |
| 2026-11-05 | **feat(prodtool): 工位1 步骤 5 改 A/B 双线圈采样判定**（`CoilCheck`）。按实测手法：①A 线圈开充前已接入 → 采集 10s **均值滤波**，均值 ≥ 1A 即 A 正常；②操作员摘 A 夹子电流跌落（低于 B 阈值持续 300ms）为换线圈**正常过渡、不判失败**；③B 线圈接入后电流恢复 ≥ 1A 并**持续 3s** 即 B 正常、步骤 5 整体 PASS；④**整体限时 60s**（A 采集+换线圈+B 判定），B 一直低电流/未见跌落超时即步骤 5 FAIL。阈值/窗口/超时可在页面「线圈检测参数」现场改；电压仍采样记录（不判限）；A 均值/B 均值入库、判 FAIL 也入库 FAIL 记录，无有效采样不入库。删除旧单次采样判限（`PowerLimits`）。编译 0 错误、单测 16/16 |
| 2026-11-04 | **fix(can): UDS 应答静默消失根源修复——上总线确认 + PTB 孤儿回退 + 升级窗口噪声屏蔽**。AA55 证据（块 235 无应答但组包标记在）+ S3=30s 排除会话复位 + 8s 内连 NRC 0x72 都没有 → 应答被生成过但在 CAN 发送层丢失：`proto_send_response` 发后不管（入队即返回），帧被 PTB 触发失败卡死后无任何补触发通道（1.1.56 的 poll 补触发只管 STB），或被总线竞争 ABORT 后无人重发 → 76/7F 静默消失。修复：①`can_try_txbuf` PTB 触发失败改返回 -1 → 双邮箱回退经 STB 送达（poll 补触发兜底，PTB 孤儿帧无触发入口不会重复上线）；②`can_driver_wait_tx_frame` 返回值分三态（0=已上线 / -1=超时在途 / -2=终态失败），`proto_send_response` SF 路径按 handle 确认上总线：终态失败立即重发，在途不重发（防重复应答错配）；`isotp_send_fc_try` 同步——FC 终态失败重发、在途不重试；③升级窗口噪声屏蔽（用户要求）：`ota_dl_active()` 期间停发 AA55 组包标记帧（588 块=588 帧噪声）与周期 OPERATIONAL 广播（1s/帧，抢 TX 邮箱干扰应答），传输结束自动恢复。`SW_VERSION_STR` 1.1.61→1.1.62 |
| 2026-11-04 | **fix(tool/fw): IAP 块 345 无应答中止——上位机 0x36 补超时重发 + 固件 IAP 睡眠门禁**。实测（QiChargerStationTool MCU2 IAP，22B/块）：块 345 请求发出后 8s 无任何应答（噪声仅 8×OPERATIONAL + 1×0x18FF270D），整单升级 FAIL；10.4s 后设备照常进睡。定位：固件 0x36 派发后必有应答（76 或 2s 内 NRC 0x72，且 BSC 幂等去重为上位机重发设计），纯静默 = 应答回程丢失（SF 发后不管）或 CF 组包丢失（N_Cr 静默超时）；而上位机 `UdsClient.TryRequest` **单发无重试**，一次瞬时丢包即整单 FAIL。修复：①工具 `TryRequest/Request` 新增 `requestRetries`（仅对「应答超时」原样重发，NRC/取消不重发），0x36 三个发送点（StationTool MainWindow / OtaSteps / ProductionStationTool `CanUdsDevice`）统配 2 次重发——重复包走固件 BSC 幂等直接回 76，应答丢失与组包丢失均自愈；②`IsoTpClient` 超时诊断识别 AA55 组包标记（18FF270D AA 55）：有=设备已派发、应答回程丢；无=组包未完成，下次故障自判因；③固件：IAP 进行中空闲超时 10s→30s（上界，工具消失仍回收），防工具重发节奏撞上睡眠线（断电=Qi 芯片中途断写）。`SW_VERSION_STR` 1.1.60→1.1.61 |
| 2026-11-04 | **fix(comm): 通信层风险加固（CAN 驱动 + ISO-TP + UDS 应答槽）**。①`can_driver`：RX 丢帧粘滞标志——软 FIFO 满/硬件溢出原为静默丢弃（主循环被长操作阻塞时突发请求帧丢失，主机侧只见「UDS 无应答」无从定位），现置标志经 DID `0x2119` `[0]` bit3 暴露（保持 4B SF 可发，不加长）；`can_driver_wait_tx_frame` 补 current 槽 ABORTED/REJECTED 终态判定（原只看 final 槽，帧不搬 final 时空等到超时，上层补发被拖慢）；STB 滞留补触发抽出 `can_driver_tx_pump()`。②`isotp`：FF 发送「确认超时」不再假成功——原 wait_tx_frame 超时仍 return 0，主机等不到 FF 没有 FC，整个多帧应答白等 2s 超时；现确认失败视为未发出按 N_As 重试（重复 FF 主机幂等，与 FC 重试同权衡）；`isotp_wait_cts`/FF 发送阻塞窗口内补跑 `can_driver_tx_pump()`（原 STB 补触发只在 can_driver_poll 跑，多帧应答阻塞窗口内停摆）。③`can_protocol`：`g_tx_pend` 单槽统一 prefer-old——SF 落槽后被后续 MF 应答无条件覆盖（主机永远等不到在途应答）；现槽忙一律丢新应答（含 7E keep-alive），主机超时重发可恢复。`SW_VERSION_STR` 1.1.59→1.1.60 |
| 2026-11-04 | **fix(sit1145): 插拔后入睡仍睡死（1.1.58 残留）——EVENT_EN.CWE 未武装即入睡**。CCU 实测（fw 1.1.58 在装）：插拔冷启动→10s 空闲入睡→WUF burst 8×250ms 与车辆流量均唤不醒；记录仪 E0=`07 00 00 41 00 00`（`CAN_CONTROL=0x41`=init 步骤7 已写，`EVENT_EN=0x00`=步骤9 未执行）→ init 在**步骤8 DATA_RATE 回读失败提前 return**（1.1.58 只给步骤6 加了重试，7/8 仍是单次写+回读）。随后 `sleep_enter` 的 wuf_ok 校验**不查 EVENT_EN**：`wuf_enable` 那一笔 CWE 写丢失仍判成功 → CPNC=1 关标准唤醒、CWE=0 关一切 CAN 唤醒 = 睡死（暖机正常因 SIT1145 不断电、EVENT_EN 残留 0x03 兜底）。修复：①init 步骤7/8 有界重试 10×(写+1ms+回读)（app + bootloader）；②`wake_enable`/`wuf_enable` 的 EVENT_EN 写改回读确认+重试 5 次；③wuf_ok 校验加 `EVENT_EN.CWE=1` 项；④进睡前最终门禁：CWE 仍为 0 则补救一次，仍不生效 return 0 不入睡（宁可不睡不可睡死），下轮空闲重试。`SW_VERSION_STR` 1.1.58→1.1.59 |
| 2026-11-04 | **fix(sit1145): 插拔后选择性唤醒无反应——冷上电 init 自愈 + 进睡前重写 DATA_RATE**。根因：插拔=SIT1145 整芯片断电重启，上电初期 SPI 未就绪 → `sit1145_init` 步骤6 读 ID 失败，而调用方的重试是无延时连打两次、同样失败后**静默继续**（CAN 照常通：收发器默认 Normal 直通 TXD/RXD）→ 芯片停在复位默认值（进睡前 E0~E3 回读 `CAN_CONTROL=0x01`/`EVENT_EN=0x00` 实证）。`sit1145_sleep_enter` 每次重配 WUF ID/掩码并锁定，但 **DATA_RATE(0x26) 只有 init 写**——WUF 检测器按 DATA_RATE 采样总线，速率错=选择性唤醒永不匹配，又因 CPNC=1 关了标准唤醒，车辆流量也唤不醒=睡死。修复：①init 步骤6 有界重试 25×2ms（app + bootloader 两份 sit1145.c 同改）；②`sleep_enter` 进睡前重写 DATA_RATE 并纳入 wuf_ok 校验（重试循环全量重配时一并重写）——不管 init 成败，进睡前自愈，校验不过则退化标准 WUP（宁可多醒不可睡死）。`SW_VERSION_STR` 1.1.57→1.1.58 |
| 2026-11-03 | **revert(prodtool): 撤销工位1 的 OTA 升级 UI，界面恢复原样**（用户指令：ProductionStationTool 不要 OTA 升级 UI）。移除流程区「固件升级（OTA）」卡（固件选择/进度条）及其浏览/升级处理器与复位逻辑；**通信层保留**——`CanUdsDevice.OtaUpgradeAsync`（0x34 自适应块长/耗时遥测/版本回读验证）与 expectDid 对齐仍作为 Core 能力供调用。编译验证 0 错误、单测 19/19 通过 |
| 2026-11-03 | **feat(prodtool): OTA 后版本回读验证——升级闭环 PASS/FAIL**（ProductionStationTool `OtaUpgradeAsync`）。37 收尾后等 ~5s 设备自复位搬运（BOOT 备份区→App + 验签）→ 唤醒就绪 → 读 0xF195 与镜像内版本串（`XatoImage.ExtractSwVersion`）比对：相符=升级生效；不符/读不到=**FAIL**（验签失败回滚旧 App 就在这里暴露），一行结论闭环到产测判定。镜像烧前校验确认已有（magic/长度/CRC32，`XatoImage.Parse`；ECDSA 验签在 BOOT 侧） |
| 2026-11-03 | **perf(ota): 传输块长按 0x34 宣告自适应——往返次数减半**（ProductionStationTool + QiChargerStationTool 通信层对齐）。固件 `OTA_DL_MAX_BLOCK_LEN=256`（ISO-TP RX 缓冲 4095B）一直宣告 256，但两个工具都写死 128B 块：52KB 镜像 410 次 0x36 往返。现解析 0x34 正响应 `74 20 <maxBlockLen:2B>` 按设备宣告取块长（MCU1=256 / MCU2=22，MCU2 路径 0x34 本就回 22 约束逐包），块长翻倍→往返减半≈传输提速一倍；工具侧封顶 256，`transferBlock` 参数降级为上限（0=自适应）。ProductionStationTool `OtaUpgradeAsync` 同步补逐块耗时遥测（`LastSendMs/LastRecvMs/LastFcTries` 均值 + `[计时]` 汇总，对齐 RunUpgradeAsync） |
| 2026-11-03 | **feat(prodtool): ProductionStationTool 接入 OTA 升级（对齐 QiChargerStationTool 设计）**。①`CanUdsDevice.OtaUpgradeAsync`：XATO 镜像解析（`XatoImage.Parse`，MCU1 目标）→ 唤醒就绪 → 门禁 `10 02`+SA+`2E 20 10`（10 02 双试 + NRC 0x22/0x33 会话/安全丢失整套重做一次，`expectDid` 校验）→ `31` 擦备份区（15s/90s）→ `34`/`36` 传输（2% 粒度 `IProgress<OtaProgress>` 进度回调，整段单 Task.Run）→ `37` 收尾（设备自复位容忍）。②既有请求对齐 expectDid（SN 读写 `DidSn`、充电开关 `DidChargerEnable`）。③工位1 UI 流程区下新增「固件升级（OTA，维护用）」卡：固件选择/浏览、升级按钮、进度条与百分比状态；升级中禁「开始测试/下一个」，「下一个」复位 OTA 显示 |
| 2026-11-03 | **feat(qi): 默认功率改 10W + 开充补发功率设置（延时 100ms）**。背景：Qi 芯片定制出厂默认 15W，芯片掉电重启后丢配置、上报 15W 而非 NVM 档位。①默认档位 `g_qi_power_level` 15W→**10W**（NVM 有值仍以 NVM 为准）。②每次开启充电（DID 0x2101=1）**补发功率设置**：置 `g_qi_pwr_cfg_sent=0` 并排 `g_qi_pwr_cfg_due=now+100ms` 计划，由 `qi_power_cfg_poll` 延时执行。③`qi_power_cfg_poll` 改为上电后**延时 `QI_PWR_CFG_DELAY_MS` 100ms 等芯片稳定再发**（原 PB2 上升沿立即发——芯片启动期收不到命令，是上报默认 15W 的根因）；PB2 未上电则等上电后重新计时。docs/16 OTA-REQ-015 同步。`SW_VERSION_STR` 1.1.56→1.1.57 |
| 2026-11-03 | **fix(tool): 连接按钮卡死修复——串口读写全链路看门狗**（仅 QiChargerStationTool，固件版本不变）。根因：USB-CDC 驱动挂起时 `SerialPort.Write/Open/Close` 会无限阻塞（WriteTimeout 对 USB 串口不总是生效），而 `OnConnect` 在 UI 线程同步调用——点击「连接」整个界面冻结，且卡死的 Close/Open 永不返回，拔插盒子后仍需重启软件。修复：①`OnConnect` 全程后台化（Task.Run）+ 限时——旧连接释放 2s 超时弃线程、Open 8s 超时报「拔插 CAN 盒子后重试（无需重启软件）」；②新增 `WriteBytes` 写看门狗（后台写 + 2s 超时抛错），`WriteRaw`/`Send`/`SendBatch` 全部走它，操作中写卡死不再拖死 UI/轮询线程；③`Open` 失败路径归还 TimerResolution 精度引用（try/finally） |
| 2026-11-03 | **fix(can): STB 滞留帧补触发——SF 应答迟到 3s / 状态轮询卡死根因**（依据 station_log_20261103_184021）。机制：`can_txbuf_transmit(STB_*)` 在上一触发位（tsone/tsall/tsa）未清时返回 ERROR 拒绝新触发，`can_try_txbuf` 无视返回值报成功——帧写进 STB FIFO 却无触发信号，滞留到**下一次 `can_driver_send`** 才被带走：SF 应答全部迟到到下一个请求到达时才出线（实测 3.02s，恰好是工具 3s 超时+下一请求），状态轮询 11 个 DID 读全超时→UI 无数据，设置功率的 `50 03` 应答被卡 3s→「门禁失败」。SA 分片正常是因标记帧走 PTB、应答独占 STB 无触发冲突。修复：`can_driver_poll` 每轮检测 STB 非空且无触发挂起即补发 `STB_ALL`（滞留帧一轮主循环内出队）；`can_try_txbuf` STB 改用 `STB_ALL` 并注释触发失败不算入队失败。`SW_VERSION_STR` 1.1.55→1.1.56 |
| 2026-11-03 | **fix(qi): SF 应答静默丢失修复（10 02/0x2133 无应答根因）**。机制：`isotp_message_received` 先发 AA55 标记帧（占邮箱 ~0.5ms），`proto_send_response` 的 SF 直发为 20 次微秒级紧循环——等不到邮箱释放，**每个 SF 应答都落 `g_tx_pend` 单槽**（SF 应答迟到 ~110ms 的原因）；单槽后写覆盖前写，唤醒 burst 的迟到 `7E 00`（3E 00 应答）会盖掉 `10 02` 的 `50 02`、`22 21 33` 应答同理被盖 → 主机「UDS 无应答」（MF 应答走延迟冲刷不受影响，故读版本 F1xx 正常）。修复：SF 直发改 1ms 间隔重试（30ms 预算，正常 ~1ms 出帧，不再落单槽）；单槽保护——keep-alive 应答（7E）不得覆盖在途真实应答；上位机就绪探测 `22 F195` 补 `expectDid`（防吃旧应答误判就绪后抢发请求）。`SW_VERSION_STR` 1.1.54→1.1.55 |
| 2026-11-03 | **fix(qi/tool): 实时功率大端解析修正 + UDS 应答 DID 匹配修正**。①固件（can_protocol.c）：Qi 0x01 上报帧「实时功率」2 字节按**大端**解析（协议表 IAP 大小/地址明写高八位在前，版本字段 1.1.20 实测同为大端；此前按小端拼导致字节颠倒——826mW 读成 14851mW，"偶发大数"根源之一）；docs/4 功率字段注记同步。②上位机（UdsClient）：`TryRequest` 新增 `expectDid` 参数，RDBI/WDBI 应答必须核对回显 DID（d[1..2]）——此前只匹配 SID(0x62/0x6E)，设备应答慢（~110ms）而请求间隔快（~40ms）时每个读都吃掉上一个请求的应答、值整体错位（功率吃到 0x2105 电压/电流字节 = "偶发大数"主因）；TryReadDid/TryWriteDid/TryReadSwVersion/TryReadAsciiDid/TryReadQiFwVersion/ReadSlot 及产线步骤 OtaSteps/SnSteps 全部传 expectDid（新增 ProtoIds.DidChargerEnable 常量）。`SW_VERSION_STR` 1.1.53→1.1.54 |
| 2026-11-03 | **fix(tool): 「Qi 状态检测」按钮门禁与命名调整**（仅 QiChargerStationTool）：按钮文案恒定「Qi 状态检测」，运行态仅橙底区分（不再换字）；未开启充电时按钮禁用并浮窗提示——含**初始态**（`_chargeEnabled` 初始 false，本会话点过「开启充电」成功后才可点），开启充电恢复、关闭充电禁用；`UpdateQiStatusButton` 统一管理文案/底色/浮窗/可用态（操作忙碌中不误恢复） |
| 2026-11-03 | **feat(tool): Qi 状态检查改开关式持续检查**（仅 QiChargerStationTool，固件版本不变）：「Qi 状态查询」按钮改为「开始/停止状态检查」开关——点击进入持续检查，后台每 ~1s 读 Qi 全量状态（0x2132/0x2102/0x2104/…/0x210E/0x2111）静默刷新仪表盘（不刷日志/操作记录，连续失败仅打首条与每第 10 条），再次点击停止（按钮橙底示运行中）；新增 `_udsGate`（SemaphoreSlim）与 RunOpAsync body 互斥，OTA/写 SN 等操作与轮询不交叉占用 CAN，操作期间轮询自动跳拍；关闭充电/断开连接/异常时自动停止检查 |
| 2026-11-03 | **feat(qi): 充电次数统计（DID 0x210E）+ 上位机展示与查询门禁**。①固件：监测 Qi 状态字节2 b0（30s 去抖充电触发标志，放机稳定 30s 置 1/移开稳定 30s 清 0）的 0→1 上升沿，计一次实际充电，`g_qi_charge_count` 写 NVM（`NVM_OFFSET_CHARGE_COUNT` 0x108，uint32 LE）并在 `qi_nvm_load_config` 上电恢复；新增只读 DID 0x210E 返回 uint32 LE；docs/4 状态字节2 位定义更正为充电触发标志。②QiChargerStationTool：Qi 状态仪表盘新增「充电次数」大字与摘要行；`QiChipStatus`/`UdsClient` 增 `ChargeCount`（0x210E u32 LE）读取。③关闭充电成功后禁用「Qi 状态查询」按钮并浮窗提示原因（Qi 芯片 PB2 断电无法查询，`ToolTipService.ShowOnDisabled`）；开启充电恢复。`SW_VERSION_STR` 1.1.52→1.1.53 |
| 2026-11-03 | **feat(qi): 充电使能/功率 NVM 掉电续充 + Qi 版本持久化修复**。①DID 0x2101 充电使能改 NVM 持久化（新增 `NVM_OFFSET_CHARGE_ENABLE` 0x104）：CCU 写入时先置标志→写 flash→再执行门控；上电 `qi_nvm_load_config` 恢复使能，充电是否开启仍由 `board_charge_poll` 霍尔条件（PA0）把关——颠簸断电续充。②功率档位 NVM 加载后补下发 Qi 芯片：`qi_apply_power_cfg`/`qi_power_cfg_poll` 跟踪 PB2 上电（`board_5v_is_on`），探测窗口/充电启动/IAP 完成后各补发一次 `qi_protocol_set_power`。③修复 Qi 版本断电读回旧版本：`can_proto_qi_version_probe` 先监听实时 CMD 0x01 再回退 Flash 缓存（原逻辑预载缓存致监听循环立即退出、新版本永不落盘）；新增 `qi_ver_persist_if_changed`，升级刷新（阻塞/轮询）与版本问询回复后均持久化到 device_info。`SW_VERSION_STR` 1.1.51→1.1.52 |
| 2026-11-01 | **feat(qi): 新增 DID 0x2111 暴露 Qi 状态原始字节**（含保护位 b3=OVP b4=UVP b5=OCP b6=OTP b7=FOD + b0=PING b1=CHARGING b2=FULL），供上位机逐位显示保护状态。`SW_VERSION_STR` 1.1.47→1.1.48 |
| 2026-10-28 | **refactor(diag): E 诊断帧重排为连续序列 E0~E3**（原 E0/E2/E3/E4 缺 E1）。E0 全局快照不变；原 E2→E1（fc/MASK）、E3→E2（ID/DM0-1）、E4→E3（DM2-7），内容与格式不变、仅编号顺移。`SW_VERSION_STR` 1.1.46→1.1.47 |
| 2026-10-28 | **feat(config): E 诊断帧宏开关 `CAN_LP_EFRAME_ENABLE`**（app_config.h，默认 1）。置 0 时 `can_lp_enter_sleep` 的 E0/E2/E3/E4 快照帧整段不编译（0x18FF270D，量产屏蔽用）；UDS 组包标记 AA55 不受控。`SW_VERSION_STR` 1.1.45→1.1.46 |
| 2026-10-28 | **feat(lifecycle): BOOTUP 改由 APP 补发**（Boot 侧已回退）。announce 块在 CAN 上线+100ms 静默窗后发 `01` BOOTUP → `02` OPERATIONAL（FIFO 保序）；时点=APP 启动后 ~100-200ms（Boot 不发帧后「整机上电 100ms」不可达，以 APP 启动计）。`SW_VERSION_STR` 1.1.44→1.1.45 |
| 2026-10-28 | **revert(bootloader): Boot 代码回退至 6110933 之前**（用户指令）。恢复 ABT 心跳帧（`safe_heartbeat`，500ms `01 41 42 54`）、移除 `boot_lifecycle_bootup()` 及 main.c 调用——**注意：0x01 BOOTUP 帧现无发送方**（APP 侧此前已移除），BOOTUP<100ms 要求暂不满足。`BOOTLOADER_VER_STR` 回 1.0.0；`SW_VERSION_STR` 1.1.43→1.1.44 |
| 2026-10-28 | **fix(wuf)!: 唤醒帧改 DLC=8——全链路统一 DLC=8 + 0xCC 填充**。唤醒帧 = `02 3E 00 CC CC CC CC CC`（与普通 UDS SF 同线格式）；WUF 过滤改严格 DLC=8（PNDM=1，fc=0xC8，DM0=0x02/DM1=0x3E），DLC=3 裸帧不再唤醒。C# SendWake/注释、AGENTS/docs/19/16/10 同步。`SW_VERSION_STR` 1.1.42→1.1.43 |
| 2026-10-28 | **fix(lifecycle): 不区分冷启动/唤醒启动——WAKEUP(0x03) 保留词汇位但固件不发送**。启动序列统一 `01` BOOTUP（Bootloader）→`02` OPERATIONAL；announce 块去掉 WAKEUP 分支。帧格式约定澄清：仅唤醒帧为 DLC=3 裸 `02 3E 00`（WUF 严格匹配），其余 UDS 指令/应答一律 DLC=8、尾部 0xCC 填充（全链路一致）。`SW_VERSION_STR` 1.1.41→1.1.42 |
| 2026-10-28 | **feat(protocol)!: CCU 词汇表落地——生命周期字节 0 重构 + WUF 唤醒过滤严格化（破坏性变更）**。①生命周期 0x18FF260D：byte0 仅 4 值 `01` BOOTUP/`02` OPERATIONAL/`03` WAKEUP/`04` SLEEP、[1..7] 全零；序列 冷启动 01→02、CAN 唤醒 01→03→02、休眠 04 确认送达后才睡（失败重试 1 次）；BOOTUP 由 Bootloader 上电 100ms 内发（boot_lifecycle_bootup）；OPERATIONAL 1s 周期（仅该态）、SLEEP 后停；MF UDS 期间 pending FIFO 推迟补发；修正「停在 BOOTUP 不发 OPERATIONAL」（can_lp_send_ident_bus 清 announce 标志短路）；ABT 心跳废弃、0x37 复位静默（删 SHUTDOWN 发送）。②WUF 严格化：`sit1145_wuf_config_uds` PNDM=0→1、DLC=8→3、DM5=0x02/DM6=0x3E/DM7=0x00、fc=0xC3；ID+DLC 严格、数据 (rx&DM)≠0 近似（超集帧误唤醒=硬件限制）；唤醒帧改 raw 3 字节 `02 3E 00`（DLC=8 填充帧不再唤醒）——C#/python 全部唤醒发送点同步（UdsClient.SendWake/IsoTpClient.SendRaw/各 wake_bus）。③主机解码 byte0 四值分类 + legacy `0x41` magic 兼容。`SW_VERSION_STR` 1.1.40→1.1.41，`BOOTLOADER_VER_STR` 1.0.0→1.0.1 |
| 2026-10-26 | **feat(sit1145/can_protocol): 启动时区分「CAN 唤醒启动」与「冷启动」**。硬件供电拓扑确认：SIT1145 进 Sleep→INH 断电=AT32 整板断电，CAN 唤醒→重新供电冷启动——唤醒后 Boot 帧（A1/A2/A4+`01 41 00`）是正常唤醒签名；WK 标记 `01 41 57 4B` 因 RAM 断电清零永远发不出。实现：`sit1145_init` 步骤6.5 在 `wakeup_clear` 前快照残留事件位 0x24/0x63（SIT1145 自身不断电，CW/WUF 锁存保留；整板上电则寄存器复位=全 0），新增 `sit1145_boot_wake_ev24/ev63()` 访问；`can_protocol_init` 据此预置 `g_lp_ever_sleep`+`g_lp_last_wake_src`（ev24 CW/WUF→src=2，ev63 CW→src=3），`enter_normal` 首次执行 wup_count 0→1、sleep_sec=0，BOOTUP 标记以 `01 41 57 4B 01 src 00 00`（WK）发出，与冷启动 `01 41 00` 区分。`SW_VERSION_STR` 1.1.39→1.1.40 |
| 2026-10-26 | **fix(sit1145): 清除 g_sleep_diag 残留引用修复编译错误**——1.1.31 删 E5 时诊断静态变量已移除，1.1.38 误引用导致 Keil 报 undeclared identifier；删除赋值（回读逻辑不变）。`SW_VERSION_STR` 1.1.38→1.1.39 |
| 2026-10-26 | **fix(sit1145): 休眠序列对齐官方 FAE SleepMode_Set——补 0x4C WAKE_enable + MODE 回读验证（可诊断写入被忽略）**。对照仓库官方参考 `code/SIT1145.c` 逐行核对，发现三处偏离：①官方写 `WAKE_enable(0x4C)=双边沿`（回读验证），我们全工程从未写过 0x4C（`sit1145_wake_enable()` 名不副实，实际写的是 0x23）；②官方 CAN_Control=**0x71（CMC=01）**，1.1.37 误改 CMC=00 与官方相悖（1.1.27「CMC=01 阻止入睡」系读识别码 0x74 的误判——官方 Sleep 写后仍可回读 MODE）；③官方写 Sleep 后**回读 MODE 验证**，我们完全信任写入。修复：sleep_enter/sleep_mode_set 补 0x4C 双边沿写；写 MODE=Sleep 后回读三态判定（0x01/0xFF=接受；仍 0x07=被忽略→CMC=00 兜底再写再回读；仍失败 return 0 置 g_lp_sleep_fail）——10s 空闲触发本身已实测正常（SB+E0 帧齐全），卡点在 Sleep 写入被芯片忽略且此前无回读发现。`SW_VERSION_STR` 1.1.37→1.1.38 |
| 2026-10-26 | **fix(sit1145): 补回进 Sleep 前 CMC=00 步——修复 Sleep 写入被芯片忽略（1.1.28 回归）**。E5 诊断证实 CMC=01（主动模式）时写 MODE=Sleep 被芯片静默忽略、从未真正入睡；1.1.27 已加 CMC=00 修复，但 1.1.28 回归官方序列时误删（提交说明称「保留 CMC=00」实际未保留）。修复：`sit1145_sleep_enter()` 步骤 3.9 与 `sit1145_sleep_mode_set()` 在写 MODE=Sleep 前 RMW CAN_CONTROL：`CMC=00` 离线 + 同笔写入带 `PNCOK=1`（写 0x20 会清 PNCOK，必须一笔写全保持锁定）。`SW_VERSION_STR` 1.1.36→1.1.37，README/.agent-notes 同步 |
| 2026-10-26 | **fix(sit1145): WUF 配置锁定期竞态修复——解锁→全量重配→锁定→验证**。实测 E3 诊断帧 WUF ID 寄存器全 0（应为 03 0D DA 18）导致选择性唤醒永不匹配、睡死。根因：`wuf_enable()` 置 PNCOK=1 锁定配置寄存器后，3.5 步验证循环在锁定期补写 FRAME_CTRL/ID_REGx（无效/清 PNCOK），且补写顺序违反官方「FrameCtrl 最后写」；3 次重试全空后仍进 Sleep。修复：重试循环改为「PNCOK=0 解锁 → sit1145_wuf_config_uds 全量重配 → wuf_enable 锁定 → 只读验证（PNCOK+frame_ctrl=0x88+ID）」；3 轮失败退化标准 WUP（CPNC=0 任意帧唤醒）兜底，宁可多醒不可睡死。`SW_VERSION_STR` 1.1.35→1.1.36，README/.agent-notes 同步 |
| 2026-10-25 | **fix(sit1145): Sleep 进入路径修正——Normal→Standby→Sleep + 入睡验证**。硬件电平实测此前从未真正进 Sleep：`sit1145_sleep_enter()` 从 Normal 直写 `MODE_CONTROL=0x01`（Sleep）被模式状态机忽略（Sleep 只能从 Standby=0x04 进入），而 `sit1145_set_mode()` 对 Sleep "直接信任写入"无法回读，失败完全静默。修复：① 先写 `0x04` 进 Standby 并回读验证（`MODE_CONTROL & 0x07 == 0x04`），5ms tMODE 稳定；② 再写 `0x01` 进 Sleep；③ 入睡验证——Sleep 下 SPI 断开，读识别码寄存器 0x7E 仍回 0x70/0x74 即未入睡，置 `g_lp_sleep_fail`（DID 0x2119 bit2）并退回 Normal；`sit1145_sleep_mode_set()` 同步补 Standby 步。`SW_VERSION_STR` 1.1.23→1.1.24 |
| 2026-10-10 | **revert Sleep 模式编译开关**：Sleep 不监听 CAN 总线（无法用 3E 00 等帧唤醒，仅 INH/上电唤醒），保留 Standby 单一低功耗模式。撤销 `CAN_LP_SLEEP_ENABLE`，恢复 `CAN_LP_STANDBY_ENABLE` 单开关 |
| 2026-10-10 | **fix Qi 版本字段字节序 LE→BE 修正**：实测 Qi 芯片 0x01 状态上报帧版本字段为大端序（data[4]=高字节, data[5]=低字节），原代码按小端解析导致 DID 0x2133 读出 0x0100 而非实际 0x0001。修复 `qi_iap_frame_cb` 状态上报解析为 `((data[4]<<8)|data[5])`；同步修正 `qi_ver_parse_value` 非 ASCII 整数路径。log1 `EXPECTED_FW_VERSION` 更正为 0x0001。`SW_VERSION_STR` 1.1.19→1.1.20。log1 + log2 已同步（log2 版本常量待确认） |
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
