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

### 3.1 编译环境

| 工具 | 版本要求 |
|------|----------|
| 2026-11-10 | **fix(can): S3 会话超时判定下溢误杀会话（fw 1.1.79）**。14:07 完整 OTA 日志定案：2E 20 10 报 NRC 0x22 的根因=**S3 检查（can_protocol_poll:3568）用无符号减法**，而它在 proto_flush_pending_tx（3505）之后——MF 应答冲刷阻塞数秒，期间 isotp_wait_cts 内联处理的踢帧把 last_tester_present_tick 刷成 now+数秒，回到 S3 检查时 now−tick 下溢成巨大无符号值 ≥30s → session_reset_to_default() 无条件误杀会话（AA55 标记会话字节 02→01 恰发生在种子 MF 滴流窗口，两轮 SA 均复现）。修复=改有符号比较（同上方睡眠判定风格——那个坑 2026-11-10 早已修过并留注释，S3 侧漏改）。SW_VERSION_STR=1.1.79 |
| 2026-11-10 | **fix(tool/can): 0x34 应答静默丢弃根治（fw 1.1.78）**。10:54 日志「有 FC 无 74」定案：STB 楔子让 74 的 SF 直发 30 连败→落 g_tx_pend→冲刷 3 连败→**「3 次即丢弃」策略把应答静默扔掉**（主机只见无应答）。修复：①工具 0x34 加 requestRetries=2（首包未下载数据，重发=重开下载，幂等无害）；②冲刷丢弃预算 3→10（10 次仍败才丢，彼时主机已重发）。注：10:54 日志的 2s 派发延迟说明当时固件仍非 1.1.75——STB 强制重触发（1.1.75）+ 本版叠刷才是完整解。SW_VERSION_STR=1.1.78 |
| 2026-11-10 | **fix(can): E0~E3 快照节流 + 入睡失败退避（fw 1.1.77）**。答用户之问「快照为何多组/减少可否判断」：多组=「sit1145_sleep_config 失败→下一轮 poll 立刻重试」的无退避风暴 × 每次尝试发 4 帧（每组含 5 次 SPI 回读+确认发送 ~1s）；同一风暴里配置值冗余重复，判断只需 **E2（WUF ID/掩码真值）+ E0 fl（sleep_fail/ever_sleep）**。修复：①快照 10s 节流（每入睡风暴最多一组，判断力保留）；②入睡失败 1s 退避（刷屏源头，旧逻辑每轮 poll 重试）。CAN_LP_EFRAME_ENABLE 恢复 1（1.1.76 曾整体关闭）。SW_VERSION_STR=1.1.77 |
| 2026-11-10 | **fix(can): 关闭 E0~E3 入睡诊断帧刷屏（fw 1.1.76）**。实测入睡重试环连发十几组 E0~E3 快照刷屏总线/日志（用户「帧太多」）。app_config.h 的 CAN_LP_EFRAME_ENABLE 置 0（设计时即留的屏蔽口，注释原话「量产不需要时省 4 帧/次」）；AA55 组包标记不受影响，唤醒快照 E1 保留（仅唤醒决策发一次）。排睡死问题时改回 1 即可。SW_VERSION_STR=1.1.76 |
| 2026-11-10 | **fix(can): STB 队列强制重触发——上位机通信 2s 延迟总根治（fw 1.1.75）**。WSL 直连 CAN 盒子帧级实测定案：请求→应答恒 ~2s（AA55 派发同），盒子/设备 TX 实为 1~2ms（FC→CF1 实测 1.6ms），空闲生命周期 1028ms 稳定；管道实验（两请求隔 2.5s）证实 STB 队列滞留到下一次新帧触发才整队喷出（REQ1 积压 5 帧在 REQ2 到达瞬间同发）。根因=can_txbuf_transmit 软件守卫（tsall/tsone/tsa 置位即拒触发）让 pump 补触发永远被拒。修复=绕过守卫每轮直接重写 STB_ALL 触发位（纯重触发不丢帧），队列一轮主循环内必出。SW_VERSION_STR=1.1.75 |
| 2026-11-10 | **revert+fix(can): 撤回 TX 路径破坏性实验，修复 FC 兜底丢帧（fw 1.1.74）**。09:29 日志「越改越糟糕」：34 RequestDownload 有 FC（Send 成功）却无 74 应答=帧被丢。丢帧源两个均为 1.1.71~73 引入：①pump 50ms 强制 abort 杀在途帧（1.1.73 让它在确认链高频触发）；②RETLIM=6 截断重传。另拆一颗更早的雷：isotp_send_fc 兜底路径的 bus-off 恢复 offline→online **清 RX FIFO**，把主机已发来的 CF 抹掉（有 FC 无应答的直接根因）——改 can_busoff_reset 温和恢复。回退内容：pump 恢复纯 STB 补触发、撤 RETLIM 配置、撤 PTB abort；保留：等待环内跑 pump（1.1.73）、工具单 FF+6s 耐心窗。SW_VERSION_STR=1.1.74 |
| 2026-11-10 | **fix(can/tool): 等待环内跑 pump + 单 FF 耐心窗（fw 1.1.73）**。08:43 日志：发 2438→2511ms 逐块增长（队列积压，"时间越来越久"）、等恒 ~3953ms。①pump 此前只在 can_driver_poll 跑——发送确认链（SF 30×15ms、FC 5×55ms）自己把主循环占死，楔子在确认链期间永远没机会解锁：现在 wait_tx_frame/wait_tx_idle 等待环内也跑 pump；②工具 FF 重试 2→1 + FC 窗 1500×2→6000×1（FF 重试诱发设备每个 FF 回一个 FC=队列饲料，砍半；FC 真丢由 UDS requestRetries 整块兜底）。SW_VERSION_STR=1.1.73 |
| 2026-11-10 | **fix(can/tool): pump 补 PTB 盲区 + RETLIM 6 + FC 耐心窗（fw 1.1.72）**。1.1.71 后派发延迟仍在（1.6s）且 34 RequestDownload FC 超时：pump 卡死解锁只盯 STB 触发位且要求 STB 非空——**PTB 的 tpe 挂起位在盲区**，TSTAT 报 ABORTED 的帧 tpe/写锁不释放=PTB 卡死，后续帧全挤 STB、STB 满则整队卡 1~2s。修复：①pump 挂起判定并入 tpe（PTB/STB 全覆盖），滞留 >50ms abort 解锁后补触发；②RETLIM/REALIM 3→6（3 太激进，丢 ACK 场景把慢变丢）；③工具 FC 窗 800×2→1500×2（0x34 的 FC 在擦除后 >1.6s 才出，短窗踩空）。`SW_VERSION_STR`→**1.1.72** |
| 2026-11-09 | **fix(can): TX 发送路径三处根治——重传上限/PTB 孤儿 abort/触发位卡死解锁（fw 1.1.71）**。station_log_20261109_233945 实证请求全程恒 ~2s（AA55 派发 +1.98s ±10ms）+ MF 组包 CF4 卡 ~1s，全为 500/1000/2000ms 超时粒度=发送缓冲排队。根因链：`can_retransmission_limit_set` 全工程无人调用（复位默认不限次）→ 丢 ACK 的帧无限重传咬死触发位 → PTB/STB 后续帧全锁 1~2s；且 `can_try_txbuf` PTB 触发失败回退 STB 后孤儿帧会被后续 PTB 触发带上线=同一帧发 2~3 份（FF×3）；`can_driver_tx_pump` 补触发守卫在卡死时永不放行。修复：①init 设 RETLIM/REALIM=3（~2ms 出 ABORTED 终态，上层补发恢复）；②PTB 触发失败先 `can_transmit_abort(PTB)` 杀孤儿再回退 STB；③pump 触发位滞留 >50ms 强制 abort 解锁重触发。`SW_VERSION_STR`→**1.1.71** |
| 2026-11-09 | **fix(isotp): 叠加 FF 不重发 + fast 补触发到 5687add 基线（fw 1.1.70，用户指令版本号）**。station_log_20261109_204150 实证同一链条：MF 应答 FF×3（3ms 间隔=确认报 ABORTED 即软件重发，帧实际已上线）→ 邮箱被重复 FF 堵满 → CF 1s 级卡顿 + E0~E3 诊断帧 1s/帧 → **请求派发晚 2s**（AA55 实证，主循环被阻塞 MF 发送卡住）→ 15s 预算被吃光 → Boot/HW 版本「读取失败」。修复=1.1.67 的两处 isotp 改动叠上 5687add 基线（其余固件不动、上位机不动）：①FF 确认失败一律不重发（TSTAT ABORTED 常=已上线，硬件自动重传所致；真丢失由主机重发/g_tx_pend 冲刷恢复）；②fast 模式（CF/SF）重试环补 `can_driver_tx_pump()`——邮箱滞留补触发腾位。`SW_VERSION_STR`→**1.1.70** |
| 2026-11-05 | **revert(fw): 固件基线定档 5687add（fw 1.1.66），上位机不动（用户指令）**。固件 `qi_wireless_code_app/` 整树取 5687add 状态（=1.1.66：NVM 乒乓+遗留迁移、0x2111 2B、充电次数加载校验；isotp 为 1.1.60 FF 确认重发形态）。用户实证该版固件可用，此前问题定位在上位机侧（08cb5fc 已修读版本取消重发等）。上位机代码保持现状不再改动；docs/3 0x2111 行同步回 2B 口径。`SW_VERSION_STR`=1.1.66 |
| 2026-11-05 | **revert(fw)+fix(tool): 固件回退 1.1.65 基线（用户指令）+ 读版本重复发送修复**。**固件**：按用户指令 `qi_wireless_code_app/` 整树回退至 1.1.65 状态（撤销 1.1.66：0x2111 2B/计数校验/NVM 遗留迁移；撤销 1.1.67：FF 确认不重发/fast tx_pump），`SW_VERSION_STR` 回 **1.1.65**（用户指定版本号回退，覆盖递增规则）。⚠️ 风险注记：1.1.65 仍含 1.1.60 的 FF「确认失败即重发」——15:43 SA 应答 FF×3 风暴的固件端来源；若回退后复现，可单独重打 1.1.67 的 isotp 两处（FF 不重发 + fast 补触发）。**工具**（20:21 读版本日志实锤）：①`TryAsciiDidRetry` 失败即重试不看取消——15s 截止瞬间 `22 F1 80`/`22 F1 93` 被「已取消」触发重试各发两遍（重复 TX 实锤）；改为取消不重试。②`TryReadAllVersions` 取消后仍继续读后续 DID——改为逐段检查 ct 提前收束。③上位机兼容 1.1.65 的 0x2111 1B（≥2B 才取触发位，缺失按功率/电流证据判定）。④WakeUdsReadyAsync 唤醒帧注释同步 3E 80。WSL 验证：Core 0W/0E，tests 21/21 |
| 2026-11-05 | **fix(isotp): FF 重发风暴与 CF 卡顿根治——SA「无应答」失败链条（fw 1.1.67）**。15:43 关闭充电 FAIL 实证链条：SA seed 应答 FF 被工具收到却显示 3 份（3ms 间隔）→ FF×3 堵满 PTB+STB 邮箱 →CF 发送找不到空位、fast 重试环无补触发 → CF 1s 级卡顿 → 34B seed 应答拖过 5s 超时 → 27 01 无应答 → SA 解锁失败。根因两层：①TSTAT 报 ABORTED 不可信——ACK 位被车辆流量撞掉时硬件自动重传，帧已上线而发送方报中止（工具实收 3 份 FF 为证）；②我 1.1.60 的「确认失败→重发 FF」在此现象上叠加软件重复。修复：①isotp_can_send full 模式确认失败**一律不重发**（真丢失由主机请求重发/g_tx_pend 冲刷恢复）；②fast 模式（CF/SF）重试环补 `can_driver_tx_pump()`——邮箱滞留时靠补触发腾位，CF 连发不再卡 500ms。归因更正：此前判为纯上位机问题的「FF 重复/FC 风暴」实为固件侧（1.1.60 改动 + fast 路径缺补触发）。`SW_VERSION_STR` 1.1.66→1.1.67 |
| 2026-11-05 | **fix(tool): 15:26 日志三问题修复（纯上位机，无固件改动）**。①唤醒帧改 `02 3E 80`（suppressPosRsp 形态，WUF byte1=0x3E 照匹配）——不再产生 7E 应答/组包标记噪声（旧 02 3E 00 双重身份=唤醒帧+TesterPresent，每个唤醒都触发派发+7E+AA55）；②`IsoTpClient.DrainStale()`：新请求前排空接收队列并复位 ISO-TP 半包——迟到/串台旧应答不带入当前窗口（根治「SF 不匹配 67 03 06」类噪声与错配）；③AA55 超时提示带 SID 并区分归因（sid 一致=本请求已派发·应答丢失；不一致=保活/唤醒帧标记）——旧提示把唤醒帧的 AA55 误判成本请求组包标记。WSL 验证：Core 0W/0E，tests 21/21 |
| 2026-11-05 | **fix(tool): 无应答不再抛异常/中断——调试期设备不应答软处理**。现象：设备挂调试器/不应答时 `UdsException:"UDS 无应答…"` 被抛出，中断调用流程与 VS 调试会话。修复：①`UdsClient.ReadDid` 失败（含无应答）返回 null、**不抛**；`ReadQiChipStatus` 返回 `QiChipStatus?`（首读无应答=null）；②「Qi 状态检测」轮询收到 null 静默跳过并节流记「无应答，继续轮询」——不打红字不停轮询；③`RunOpAsync` 对 `UdsException`(Nrc=0 无应答) 改软状态「无应答」（灰）：不 FAIL 红字、不打断后续；④Station 步骤 QiStatusQueryStep 空值防护（非 Critical 不阻断产线）。NRC（设备明确答复）仍按原 FAIL 语义。WSL 验证：两 Core 0W/0E；tests 21/21 |
| 2026-11-05 | **feat(tool): 真充判定 2s 滑动采样去抖——空载功率小幅跳动不再误判充电中**。背景：手机未放上时功率值会小幅跳动，单点采样误判「充电中」；充电时电流显著>1A（raw 显著非 0）。按用户要求**不做单位换算**，直接用 Qi 传回原值判定：单采样证据=电流 raw>0 或 功率>0 或 充电触发位(0x2111[1] b0)=1，经 `DebounceFilter`（2s 时间窗，双向）平滑后才出结论——单点尖峰/瞬时回落均不翻转状态，持续 2s 才切换。Core 增 `DebounceFilter`/`ChargingEvidenceFrom`/`ChargingActiveFrom`（时刻注入、可单测），「开启充电」读 0x2118/0x2111/0x2104/**0x2105** 四信号并清滤波器喂首采样，「Qi 状态检测」轮询逐拍喂样。单测 21/21（新增去抖双向滤波 + 原值证据用例） |
| 2026-11-05 | **feat(fw/tool): 充电中/未充电真充判据区分 + 充电次数持久化补强（fw 1.1.66）**。**问题**：`0x2118=0x01`（磁铁不存在）同时覆盖「放手机充电中」与「空载未充电」，原显示不区分；充电次数断电归零（与功率同根因：≤1.1.64 NVM magic 缺陷从不加载，1.1.65 已修）。**固件**：①`0x2111` 扩展 2 字节——[0]=状态1（保护位）、[1]=状态2（**b0=充电触发位**，30s 去抖、充电中持续 1，即充电次数计数用的状态位）；②充电次数加载校验（0xFFFFFFFF/0 不采用）；③NVM 旧数据迁移——1.1.65 前无头布局的已存字段（功率/次数等）按遗留数据接管、首次写入迁移成新镜像，升级不丢。`SW_VERSION_STR` 1.1.65→1.1.66。**工具**：真充判据=`功率>0 或 触发位=1`（任一为真=充电中，都假=未充电，证据不全按未充电）；磁铁不存在时据此分「磁铁不存在·充电中」（绿）/「磁铁不存在·未充电」（橙），磁铁存在恒「未充电」（红），未使能「充电失能」（蓝）；开启充电后即读 0x2118/0x2111/0x2104 三信号即时判定；图例/单测同步 |
| 2026-11-05 | **fix(fw/tool): 功率档位断电丢失根治（NVM 乒乓双扇区，fw 1.1.65）+ 充电状态显示修正**。**固件**：①根因实锤——`NVM_VALIDITY_MAGIC` 全工程无写入者（`nvm_drv_set_valid` 零调用）→ `nvm_drv_init` 校验永不过 → `qi_nvm_load_config` **从未加载过数据**，每次重启功率/使能/次数全回默认（15W→10W 即此）；读回 15W 只是 RAM 值≠落盘。②结构性弱点：`nvm_drv_write` 整扇区擦除+回写，断电落在窗口（如充电使能保存中）抹掉整扇区含 magic。修复：nvm_drv 重写为**乒乓双扇区**（A/B 各存完整镜像：magic+seq+crc32+字段，写入=整镜像写非活动扇区后切换并回读校验）——魔数随镜像写入（根因①）、擦写窗口断电旧副本完好（②）；无有效镜像时以扇区 A 原始内容为种子，升级不丢已存字段；删除无调用者的 nvm_drv_erase/set_valid。上电使能充电后照旧补发 NVM 档位给 Qi 芯片。**工具**：③「开启/关闭充电」结果文案改**充电使能/充电失能**；④磁铁状态极性反转修正（实测 0x00=磁铁不存在·充电中、0x01=磁铁存在·未充电，与固件 board_charge_poll 门禁同极性），面板四态文案/图例/Tooltip/单测同步。`SW_VERSION_STR` 1.1.64→1.1.65 |
| 2026-11-05 | **fix(tool/fw): IAP 块偶发 8s 超时——76 按 BSC 匹配 + 重发诊断 + 双 76 撤销（fw 1.1.64）**。实测（QiChargerStationTool MCU2 IAP）：某块应答超时 8s（0 帧回包）后重发 **10ms** 即完成——24B 多帧完整往返至少 20ms+，10ms 只能命中固件 BSC 幂等去重即时 76 = **首帧已被设备处理、76 应答丢失**；且幂等立即回 76 时首包延迟应答（等 Qi ACK）还会再回一个 **76 双发**，错配后续块应答窗。修复：①工具 0x36 应答按 BSC 匹配（`expectSub=块序号`，三发送点统配）——迟到/重复 76 不再错配后续块；②`TryRequest/Request` 增 `onRetry` 诊断钩子，0x36 重发前读 0x2132 IAP 计数并记日志（sent 含本块=首帧已处理·应答丢失；未含=请求未完成·组包丢失）——下次故障自证根因；③固件 `can_proto_qi_iap_resp_cancel()`：重复 0x36 幂等回 76 前撤掉首包挂起的延迟应答，杀双 76。`SW_VERSION_STR` 1.1.63→1.1.64 |
| 2026-11-05 | **docs(tool): 磁铁/实际充电显示加图例说明**：仪表盘磁铁大字下方常显四态说明（绿=磁铁存在·实际开启（充电使能+磁铁在位）／红=磁铁不存在·未开启充电／蓝=磁铁存在·充电未使能／灰=磁铁状态未知（读取失败）），与 `UpdateMagnetDisplay` 四种着色一一对应 |
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
