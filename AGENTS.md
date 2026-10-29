# 仓库代理规则

## 唯一改动源规则

- 所有改动（代码、文档、脚本）一律先在 WSL 侧仓库 `\\wsl.localhost\Ubuntu-24.04\home\whites\embedded_item\ota-upgrade-of-qi-charger-based-on-can`（即 `/home/whites/embedded_item/ota-upgrade-of-qi-charger-based-on-can`）中进行。
- 改完必须 `git push` 到 origin/main，其他位置的 clone（如 Windows 端 `C:\Users\18452\Documents\Github-young-nights\...`）只通过 `git pull` 同步，禁止在非 WSL 仓库直接修改后当源使用。
- 背景教训：曾出现 Windows 端 clone 本地改动未推送、与 WSL 仓库分叉，导致修复互相覆盖、无法互相验证（2026-09 OTA 唤醒探测修复事件）。

### 修改范围边界（2026-09-19 起）

- 用户指令原文（2026-09-19 10:35，om_x100b65eb063698a0b2a49fbeb40a75c）：「\\wsl.localhost\Ubuntu-24.04\home\whites\embedded_item\ota-upgrade-of-qi-charger-based-on-can 你只负责WSL2下面的这个工程的修改就行，Windows下的不要去动，写入此路径下AGENTS.md中把这个规则」。
- **修改范围仅限**：WSL2 工程本仓库 `\\wsl.localhost\Ubuntu-24.04\home\whites\embedded_item\ota-upgrade-of-qi-charger-based-on-can`（即 `/home/whites/embedded_item/ota-upgrade-of-qi-charger-based-on-can`）。本规则为上文「唯一改动源规则」的强化版：从「不得以 Windows 为源修改」升级为「Windows 端一律不触碰」。
- **Windows 端一律不触碰**：`I:\GitHub-young-nights`（用户运行端 clone）、`C:\Users\18452\Documents\Github-young-nights`（旧 clone）等一切 Windows 路径——不修改 / 不写入 / 不删除 / 不执行构建；运行端更新一律由用户自行 `git pull`。
- **例外**：Windows 端操作仅在用户对具体操作明确指令时执行（例：2026-09-18 23:04 编译产物清理属单次授权）；未明确指令时默认禁止。
- **代理任务书纪律**：所有 AI 代理（coder / clerk / evaluator 等）的任务书必须内置本约束声明，任务书未声明时以本 AGENTS.md 条目为准。

## 提交推送规则

- 每次代码修改后，强制 `git add -A` 全工程提交，不留残留文件。
- 自动流程：`git pull` → edit → `git add -A` → `git commit` → `git push`。
- 仓库下所有变更（代码、文档、配置）必须一并提交推送。
- **提交说明格式**：commit message 的 summary 简洁概括，但必须在 Description 中详细描述变更内容，按模块/文件分点列出具体改动，包含关键地址、常量、新增函数名等技术细节。示例：
  - 1. Device Info 结构扩展（device_info.h/c）：新增 ecdsa_pubkey[65] 存储 SEC1 未压缩公钥...
  - 2. Bootloader 读公钥逻辑（boot_verify.c）：优先从 Device Info 读取...
  - 3. APP UDS 服务（can_protocol.c/h）：新增 DID 0x2120 读写公钥...

## 版本号联动规则（2026-09-18 起）

- 软件版本号**唯一定义**在 APP 固件编译常量 `SW_VERSION_STR`（`qi_wireless_code_app/mdk_app/Src/can_protocol.c`）。
- 发版只改两处：固件 `SW_VERSION_STR` + 文档（docs/README 中的版本值与说明）。
- 打包产物 XATO 镜像头 version 区（偏移 0x4C，16 字节）打包固定填 `0x00`，**不携带版本号**；打包/OTA/校验脚本不写、不读、不输出该字段。
- 不存在打包脚本 `IMAGE_VERSION` 常量环节（已删除）；UDS DID `0xF195` 应答取固件编译常量，不读镜像头 / OTA metadata。
- 独立版本族，不随 APP 版本联动：`BOOTLOADER_VER_STR`、`HW_VERSION_STR`、IAP log1/log2 的 `EXPECTED_FW_VERSION`（Qi 芯片固件）。
- **代码变更版本递增规则（2026-10-02 用户规定）**：每次修改代码（固件源码）后，同一提交内必须同步递增 `SW_VERSION_STR`（默认 patch 位 +1，如 `QC_JYF_MCU1_FW_1.1.5` → `QC_JYF_MCU1_FW_1.1.6`；重大版本变更由用户指定），并同步 README/docs 中的当前版本值。禁止出现「代码已改、版本串仍旧值」的提交（历史教训：1.1.2~1.1.5 期间版本串仅在 Windows 侧构建时临时改动、仓库源码停留在 QC_JYF_FW_1.1.1，版本无法从仓库 HEAD 追溯）。
- 代理任务书纪律：派发代码修改任务书时须内置本条（同「修改范围边界」的任务书纪律），子代理交付后主管验收版本串已递增方可 push。

## 文档读取规则（2026-09-30 用户规定）

- **修改代码时不要读取 PDF 文档**：完整工程仓库下存在大量 .md 与 .pdf 文档（如 `docs/pdf/原理图.pdf`、`docs/pdf/SIT1145AQT_FD.pdf`），执行代码修改类任务时一律不读 PDF——PDF 解析耗时耗 token，且改代码所需的接口/寄存器/时序事实应以 `.md` 文档、头文件与源码注释为准。
- 例外（仅在用户明确要求时才读 PDF）：需要原理图引脚核对、器件数据手册参数原文等 md/源码覆盖不了的信息时，先向用户说明再读。
- 纯文档任务（如 PDF/docx 内容提炼）不受本规则限制，那是用户显式指定的处理对象。
- 派发代码修改任务书时须内置本约束（同「修改范围边界」的任务书纪律）。

## Qi IAP 路径（2026-10-19 起）

- Qi 芯片 IAP 已统一到 UDS OTA 路径：`2E 20 10 04` 选 MCU2 → `31` 擦除直通 → `34`/`36` UART 转发（延迟应答等 ACK）→ `37` 完成。
- 旧 DID `0x2130`/`0x2131` 写路径已删除，不可再用。
- 状态/版本查询保留：`0x2132`（IAP 状态）、`0x2133`（Qi 固件版本）。

## WSL2 串口 CAN 监听（Boot 用例 M1–M4）

其他智能体测 `docs/10. CAN-UDS OTA 测试用例表.md` 的 Boot 段时，**不要用 python-can slcan / SocketCAN / zcanpro**。当前盒子是智嵌 **ZQWL-CANFD**，设备管理器 **COM9**，VID:PID **`3562:0101`**，USB CDC 私有协议（配置帧 `49 3B … 45 2E`，CAN 帧 `5A … A5`）。WSL 内核没有 `can0`。

### 硬件与互斥

| 设备 | Windows | WSL | 注意 |
|------|---------|-----|------|
| ZQWL-CANFD | COM9 / usbipd BUSID **`7-2`** | attach 后 `/dev/ttyACM0`（或以 `ls` 为准） | **同一时刻只能给一边**：attach 后 Windows 上看不到 COM9 |
| AT-Link-Plus | COM7 / BUSID **`7-4`** | **禁止 attach** | Keil/SWD 会掉 |
| CH340 | 另一 COM | Qi UART 19200（2026-10-06 确认；旧口径 9600 错） | 不是 CAN |

CAN 总线：**250 kbps、Classical、29-bit 扩展帧**。终端电阻 120Ω。CANH/CANL/GND 接充电器。

### 本机环境差异记录（2026-09-26，xuanxuan @ DESKTOP-LMI7A8K）

上表 COM9 / BUSID `7-2` 为旧机器配置，一律以 `usbipd list` 实测为准：

| 项 | 本机实测 |
|----|----------|
| WSL 仓库路径 | `/home/xuanxuan/embedded_item/ota-upgrade-of-qi-charger-based-on-can` |
| ZQWL-CANFD | Windows COM7 / usbipd BUSID **`3-4`**，attach 后 `/dev/ttyACM0` |
| AT-Link-Plus | Windows COM4 / BUSID **`3-2`**，**禁止 attach** |
| 用户组 | `xuanxuan` 已在 `dialout`，开串口命令无需 `sg dialout` 包裹 |

### 把盒子交给 WSL（Windows 管理员 PowerShell）

```powershell
usbipd list
usbipd bind --busid 7-2
# 若 hrdevmon 警告：usbipd bind --force --busid 7-2
usbipd attach --wsl --busid 7-2
# 或持久：usbipd attach --wsl --auto-attach --busid 7-2
```

WSL 确认：

```bash
lsusb | grep 3562
ls -l /dev/ttyACM0
```

还给 Windows：`usbipd detach --busid 7-2`。

USB 读空、盒子哑了：先 `detach` 再 `attach`，然后重新开监听。`usbipd list` 里 7-2 消失则是 USB 掉了，请用户重插盒子（不要拔 MCU 电源当 USB）。

### 脚本已删除（2026-10-28）

`python_tools/wsl/` 与 `python_tools/zcanpro/` 全部脚本已删除（主机侧由
QiChargerStationTool / ProductionStationTool 取代，唤醒/生命周期协议见
`docs/19. CAN验证帧参考.md`）。ZQWL-CANFD 盒子硬件接入方式（上文 usbipd 表）
仍有效；需要临时抓总线时用任意 ZQWL `5A…A5` 私有协议工具自行收帧，
**不要**用 python-can slcan / SocketCAN，配置命令 `0x44` 禁止带系统复位，
收包循环禁止 `sleep`，attach BUSID `7-4` 禁止，Boot 帧必须先开监听再上电。

TC-B001 期望（fw 1.1.41 生命周期词汇表）：

```
A1 00 01 01 01 CC CC CC
A2 00 FF CC CC CC CC CC
A4 00 31 00 08 CC CC CC   # 跳 0x08003100
01 00 00 00 00 00 00 00   # BOOTUP（Bootloader 发，0x18FF260D）
02 00 00 00 00 00 00 00   # OPERATIONAL（APP）
```

B002 改的是 **metadata**（`0x0801C000` / `0x0801C800` 的 MATO=`4D 41 54 4F`），
不是 `0x08010000` 固件备份区。Keil 在 Windows 做，AT-Link 禁止 attach。

## QiChargerStationTool（C#/.NET 8 产线上位机）

### WSL 构建与测试

```bash
# .NET 8 SDK 已装在用户级（无需 sudo）
export PATH="$HOME/.dotnet:$PATH"

# 编译 Core 库（协议层 + StationStep，跨平台）
cd QiChargerStationTool/src/QiChargerStationTool.Core/QiChargerStationTool.Core
dotnet build    # 0 Warning 0 Error

# 运行单元测试
cd QiChargerStationTool/tests/QiChargerStationTool.Tests
dotnet test     # 14/14 Passed
```

- **WPF UI 层**（`QiChargerStationTool.App`）是 `net8.0-windows`，WSL 无法编译，需 Windows Visual Studio / `dotnet build`
- Core 库是 `net8.0`，WSL 可编译可测
- 修改 StationTool 代码后，WSL 侧至少验证 Core 库 `dotnet build` + `dotnet test`

### 唤醒 burst 约定（2026-10-28 修订）

- SIT1145 WUF **严格匹配** ID=`0x18DA0D03` + **DLC=3** + 数据 `02 3E 00`（PNDM=1；数据位 DM5=0x02/DM6=0x3E 近似命中，超集帧如 `02 10 00` 会误唤醒）
- 唤醒帧必须 **raw 3 字节 `02 3E 00`、不填充**——DLC=8 的 `02 3E 00 CC…` 填充帧**不再唤醒**
- `ConnectStep`（BasicSteps.cs）、`WaitUdsReadyAsync`（MainWindow.xaml.cs）为唤醒发送点（python_tools 旧脚本已删除）
- 发 ≥6 帧（常用 8）、间隔 250ms（burst 对抗工具链无 ACK 丢帧）
- 生命周期广播 0x18FF260D 字节 0 仅 4 值：`01` BOOTUP / `02` OPERATIONAL / `03` WAKEUP / `04` SLEEP，`[1..7]` 全零（CCU 词汇表）
