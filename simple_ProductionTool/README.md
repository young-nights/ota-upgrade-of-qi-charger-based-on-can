# simple_ProductionTool

ProductionStationTool **简化版（产线现场版）**：基于 main 最新代码，剥离汇总机/工位4/标签打印等，
工位流程按现场分工重排。

## 与完整版差异

| 项 | 完整版 ProductionStationTool | 本简化版 |
|----|------------------------------|----------|
| 工位 | 1 / 2 / 3 / 4 | **1 / 2 / 3** |
| 汇总机页面 / 启动登录分流 | 有 | **无**（启动直接进工位主窗） |
| 标签设计器 / 良率设置 / 管理密码窗 | 有 | **无** |
| 工位1 | 生成 SN 写入 | **SN 复测**（读设备 SN + 关联数据库 + 线圈 A/B） |
| 工位2 | 带载老化 30min（倒计时/测流/入库） | **开充循环**：500ms 周期持续发开充指令，无倒计时/测流/入库 |
| 工位3 | 读 SN 复测 | **SN 写入**（生成/联机取号 → 写入回读 + 线圈 A/B） |

工位流程：
- **工位1 SN复测**：读设备 SN → 关联数据库（除本工位外历史）→ 开充电 → 线圈 A/B → 入库
- **工位2 开充循环**：连接后点「开始测试」→ 500ms 周期持续发送开充指令（接新设备无需重击，「断开」停止）
- **工位3 SN写入**：生成/联机取号 SN → 写入设备 + 回读 → 开充电 → 线圈 A/B → 入库

## 构建

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build src/ProductionStationTool.App/ProductionStationTool.App/ProductionStationTool.App.csproj -p:EnableWindowsTargeting=true
```

WPF 真机运行需 Windows。
