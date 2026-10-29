# QiChargerStationTool — 维修上位机（C# / WPF / .NET 8）

面向 Qi 无线充电模块 CAN-UDS **维修/返修工位**的诊断与刷写工具。使用说明见 `docs/17. 维修上位机使用与开发说明.md`。

## 结构

```
QiChargerStationTool/
├── QiChargerStationTool.sln
├── config/station.appsettings.json     # 产线配置模板
├── src/
│   ├── QiChargerStationTool.App/       # WPF 工位界面
│   └── QiChargerStationTool.Core/      # 协议 + 测试流程（无 UI）
│       ├── Transport/                  # ICanTransport / ZqwlSerialTransport
│       ├── Protocol/                   # IsoTpClient / UdsClient
│       ├── Imaging/                    # XATO 镜像解析
│       ├── Security/                   # ECDSA seed 签名 + SA 解锁
│       └── Station/                    # 测试步骤编排
└── tests/QiChargerStationTool.Tests/   # xUnit
```

## 开发 / 运行（Windows）

```powershell
# VS2022 打开
\\wsl.localhost\Ubuntu-24.04\home\xuanxuan\embedded_item\ota-upgrade-of-qi-charger-based-on-can\QiChargerStationTool\QiChargerStationTool.sln

# 或命令行
dotnet build
dotnet test
dotnet run --project src/QiChargerStationTool.App
```

## 打包发布（产线）

### 方式 A：一键脚本（推荐）

在 **Windows PowerShell**、`QiChargerStationTool` 目录下：

```powershell
# 绿色版 + 单文件版（自带 .NET，拷到工控机即用）
.\publish.ps1

# 额外打 zip
.\publish.ps1 -Zip

# 小体积（目标机需装 .NET 8 Desktop Runtime）
.\publish.ps1 -SelfContained:$false
```

输出：

| 目录 | 说明 |
|------|------|
| `publish\QiChargerStationTool-Portable\` | **绿色文件夹**，含 exe + 依赖 + `keys\private.pem` + 使用说明 |
| `publish\QiChargerStationTool-SingleFile\` | **单文件** `QiChargerStationTool.exe`，双击即用 |
| `publish\*.zip` | 加 `-Zip` 时生成 |

### 方式 B：安装包（Inno Setup）

1. 先跑 `.\publish.ps1`
2. 安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)
3. 用 Compiler 打开 `installer\QiChargerStationTool.iss` → **Compile**
4. 得到 `publish\QiChargerStationTool-Setup.exe`（带桌面图标、开始菜单）

### 方式 C：手动命令

```powershell
dotnet publish src/QiChargerStationTool.App -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o publish\app
```

**产线机注意：**
- 用 `self-contained`（脚本默认）则**不用装 .NET**
- 把 `docs\keys\private.pem` 拷到 exe 旁 `keys\private.pem`（脚本已自动做）
- ZQWL-CANFD 插 Windows（usbipd detach），不要被 WSL 占用

## 测试流程

1. **连接探测** — 3E 00 + 22 2113
2. **读版本** — 22 F195 / 2133 / F180 / F193（MCU1 APP + MCU2 Qi + Boot + HW 一次读全）
3. **写 SN** — 10 02 + SA + 2E F18C（需私钥）
4. **OTA 升级** — 10 02 → SA → 2E 20 10 01 → 31 擦除 → 34/36/37
5. **Qi 状态查询** — 22 2132/2102/2104/2105/2108/2109/210B/210C/210D（任意会话）
6. **设置 Qi 功率** — 10 03 + SA + 2E 210D（档位码 0x01=5W / 0x02=10W / 0x03=15W）
7. **充电启停** — 2E 21 01（可选）

密钥：自动从仓库 `docs/keys/private.pem` 查找（向上探测 16 级目录）；产线也可拷到输出目录 `keys/private.pem`。

### Qi 状态全量查询

一次读取并解码（原对照脚本 zcanpro_qi_read_status.py 已删除 2026-10-28）：

| DID | 含义 |
|-----|------|
| `0x2132` | IAP 状态 9B：state / progress / Qi版本3B / 已发 LE / 总长 LE |
| `0x2102` | 充电状态机 |
| `0x2104` | 输出功率 u16 LE（mW） |
| `0x2105` | 输入电压/电流 raw |
| `0x2108` | PCB 温度 ℃ |
| `0x2109` | FOD 状态 |
| `0x210B` | 故障码 |
| `0x210C` | 热降额 %（100=无降额） |
| `0x210D` | 当前功率档位 |

### 设置 Qi 功率

UI 档位 **5W / 10W / 15W** → 映射 Qi 芯片档位码 **0x01 / 0x02 / 0x03**（`QI_POWER_5W/10W/15W`），写 DID `0x210D`（1 字节）。需要扩展会话 `10 03` + SA 解锁，写后读回校验。

| UI | 线上档位码 | Qi 芯片 |
|----|-----------|---------|
| 5W | `0x01` | QI_POWER_5W |
| 10W | `0x02` | QI_POWER_10W |
| 15W | `0x03` | QI_POWER_15W |

## 硬件

- ZQWL-CANFD USB-CAN（VID:PID `3562:0101`）→ Windows COM 口
- CAN 250 kbps、29-bit 扩展帧
- UDS：请求 `0x18DA0D03`，应答 `0x18DA030D`

### COM 口自动扫描

顶部「COM 口」下拉框启动时自动扫描系统串口并自动选中 ZQWL 盒子（标 ★）。点「扫描」可重扫（例如盒子刚从 WSL `usbipd detach` 回来）。

**波特率固定 2000000**（ZQWL USB 私有协议），界面上为只读展示，不可修改。

**连接 / 断开**：点「连接」打开 COM 口并初始化 CAN（长连接，后续操作复用）；点「断开」关闭。未连接时执行测试会提示先连接。

**识别规则（按优先级）：**

| 规则 | 说明 |
|------|------|
| 厂商 VID `3562` | 智嵌 ZQWL 全系（CAN / CANFD，PID 可不同）都能中 |
| `ExtraComMatch` | `appsettings.json` 追加 VID:PID 或任意子串，兼容其它厂商盒子 |
| 描述关键字 | `ZQWL` / `智嵌` / `CANFD` / `USB-CAN` |
| 多个 ZQWL 时 | 优先 PID `0101`（本仓库 CANFD）或描述含 CANFD |

```json
"ExtraComMatch": [ "VID_1A86&PID_7523", "CH340" ]
```

实现：`ComPortScanner`（WMI `Win32_PnPEntity` + `SerialPort.GetPortNames()` 兜底）。
