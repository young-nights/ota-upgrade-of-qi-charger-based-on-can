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
- **代码变更版本递增规则（2026-10-02 用户规定）**：每次修改代码（固件源码）后，同一提交内必须同步递增 `SW_VERSION_STR`（默认 patch 位 +1，如 `QC_JYF_FW_1.1.5` → `QC_JYF_FW_1.1.6`；重大版本变更由用户指定），并同步 README/docs 中的当前版本值。禁止出现「代码已改、版本串仍旧值」的提交（历史教训：1.1.2~1.1.5 期间版本串仅在 Windows 侧构建时临时改动、仓库源码停留在 QC_JYF_FW_1.1.1，版本无法从仓库 HEAD 追溯）。
- 代理任务书纪律：派发代码修改任务书时须内置本条（同「修改范围边界」的任务书纪律），子代理交付后主管验收版本串已递增方可 push。

## 文档读取规则（2026-09-30 用户规定）

- **修改代码时不要读取 PDF 文档**：完整工程仓库下存在大量 .md 与 .pdf 文档（如 `docs/pdf/原理图.pdf`、`docs/pdf/SIT1145AQT_FD.pdf`），执行代码修改类任务时一律不读 PDF——PDF 解析耗时耗 token，且改代码所需的接口/寄存器/时序事实应以 `.md` 文档、头文件与源码注释为准。
- 例外（仅在用户明确要求时才读 PDF）：需要原理图引脚核对、器件数据手册参数原文等 md/源码覆盖不了的信息时，先向用户说明再读。
- 纯文档任务（如 PDF/docx 内容提炼）不受本规则限制，那是用户显式指定的处理对象。
- 派发代码修改任务书时须内置本约束（同「修改范围边界」的任务书纪律）。

## log1 / log2 脚本同步规则

- `zcanpro_qi_iap_log1.py` 和 `zcanpro_qi_iap_log2.py` 是同一套 IAP 脚本的不同固件版本副本。
- **除以下 4 项外，两个文件必须完全一致**：
  1. 文件头注释中的固件文件名（log1.BIN / log2.BIN）
  2. `FIRMWARE_NAME` 常量
  3. `EXPECTED_FW_VERSION` 常量
  4. 文件头注释中的互斥说明
- **修改任一脚本的 IAP 逻辑后，必须同步另一个脚本**。检查方式：
  ```bash
  diff python_tools/zcanpro_qi_iap_log1.py python_tools/zcanpro_qi_iap_log2.py
  ```
  diff 输出应仅包含上述 4 项差异，不得有其他不同。
- 提交时在 commit message 中注明 `log1 + log2 已同步`。

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

新增工具：`python_tools/3. wsl script/zqwl_can_send.py`（单帧发送 + 窗口抓包，ISO-TP 首帧自动回 FC），
与 `zqwl_can_listen.py` 互斥（同一串口只能一个进程打开）。

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

### 开监听（必须先于 MCU 上电）

依赖：`pip install pyserial`（本机已装则跳过）。在 **WSL 仓库根**执行：

```bash
cd /home/whites/embedded_item/ota-upgrade-of-qi-charger-based-on-can
python3 "python_tools/3. wsl script/zqwl_can_listen.py" --port /dev/ttyACM0 \
  --log /tmp/can_boot_listen.log --event /tmp/can_boot_events.log
```

脚本会：读设备信息 → 配 CAN0 仲裁 250k → 滤波全收 → 打开 CAN0（**不要**发系统复位，`0x44` 的复位字节会把 USB 打掉）→ 忙等收帧（**禁止**在收包循环里 `sleep`，否则 M2/M4 会被盒子 FIFO 挤掉）。

标准输出只打关注 ID；全量在 `/tmp/can_boot_listen.log`，解码后的 Boot/生命周期在 `/tmp/can_boot_events.log`。

关注 ID：

| ID | 含义 |
|----|------|
| `0x18FF480D` | Boot M1–M4 |
| `0x18FF260D` | 生命周期 / Safe 心跳 `01 41 42 54 …` |
| `0x18DA030D` | UDS 应答 |
| `0x18DA0D03` | UDS 请求 |

M1 载荷（metadata **v4**，已删除 `app_valid`）：`A1 [src] [magic_ok] [ver_ok] [crc_ok] CC CC CC`  
src：`00` 主区 `0x0801C000` / `01` 备区 `0x0801C800` / `02` 默认重建。

TC-B001 期望：

```
A1 00 01 01 01 CC CC CC
A2 00 FF CC CC CC CC CC
A4 00 41 00 08 CC CC CC   # 跳 0x08004100
01 41 00 00 …             # App BOOTUP，ID 0x18FF260D
```

### 代理操作顺序

1. 确认 `/dev/ttyACM0` 在，再启动 `zqwl_can_listen.py`（后台常驻）。
2. 日志出现 `CAN0 250kbps opened, listening` 后，让用户给 **充电器 MCU** 断电上电（**不要拔 CAN 盒子 USB**）。
3. 读 `/tmp/can_boot_events.log` 判 M1/M2/M4；没有帧先查盒子是否还 Attached、脚本是否还活着。
4. B002 改的是 **metadata**（`0x0801C000` / `0x0801C800` 的 MATO=`4D 41 54 4F`），不是 `0x08010000` 固件备份区。Keil 在 Windows 做，AT-Link 禁止 attach。
5. 测完 `detach` 把 COM9 还给 Windows。

### 不要做的

- `interface="slcan"` / `gs_usb` / `can0` 对待这只 `3562:0101` 盒子
- 配置命令 `0x44` 带系统复位
- 收包循环 `time.sleep(0.01)`
- attach BUSID `7-4`
- MCU 已上电再开监听（Boot 标记只有几十毫秒）
