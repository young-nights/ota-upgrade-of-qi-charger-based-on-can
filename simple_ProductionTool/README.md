# simple_ProductionTool

ProductionStationTool **简化版**（自 main 的 ProductionStationTool 剥离）。

## 与完整版差异

| 项 | 完整版 ProductionStationTool | 本简化版 |
|----|------------------------------|----------|
| 工位2 老化测试 | 有 | **无**（文件与菜单已删） |
| 汇总机页面 / 登录分流 | 有（ModeLogin + Summary*） | **无**（启动直接进工位主窗） |
| 工位 | 1 / 2 / 3 / 4 | **1 / 3 / 4** |

其余（工位1/3/4 流程、设置、打印机标签直驱、PowerZ、SQLite 等）与剥离时的 main 基线一致。

## 构建

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build src/ProductionStationTool.App/ProductionStationTool.App/ProductionStationTool.App.csproj -p:EnableWindowsTargeting=true
```

WPF 真机运行需 Windows。
