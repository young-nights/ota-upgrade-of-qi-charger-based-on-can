# QiChargerStationTool — 产线测试上位机（C# / WPF / .NET 8）

面向 Qi 无线充电模块 CAN-UDS 产线工位的测试工具。选型见 `docs/17. 产线上位机技术选型与环境配置.md`。

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
\\wsl.localhost\Ubuntu-24.04\home\whites\embedded_item\ota-upgrade-of-qi-charger-based-on-can\QiChargerStationTool\QiChargerStationTool.sln

# 或命令行
dotnet build
dotnet test
dotnet run --project src/QiChargerStationTool.App
```

产线发布（单文件免运行时）：

```powershell
dotnet publish src/QiChargerStationTool.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 测试流程

1. **连接探测** — 3E 00 + 22 2113
2. **读软件版本** — 22 F195
3. **写 SN** — 10 02 + SA + 2E F18C（需私钥）
4. **OTA 升级** — 10 02 → SA → 2E 20 10 01 → 31 擦除 → 34/36/37
5. **充电启停** — 2E 21 01（可选）

密钥：将 `docs/keys/private.pem` 拷到输出目录 `keys/private.pem`。

## 硬件

- ZQWL-CANFD USB-CAN（VID:PID `3562:0101`）→ Windows COM 口
- CAN 250 kbps、29-bit 扩展帧
- UDS：请求 `0x18DA0D03`，应答 `0x18DA030D`
