# simple_ProductionTool

ProductionStationTool **简化版（产线现场版）**：与现场 `win-x64` 发布版对齐，并加回老化工位。

## 与完整版差异

| 项 | 完整版 ProductionStationTool | 本简化版 |
|----|------------------------------|----------|
| 工位 | 1 / 2 / 3 / 4 | **1 / 2 / 3**（工位2 老化测试） |
| 汇总机页面 / 登录分流 | 有 | **无**（启动直接进工位主窗） |
| 标签设计器 / 良率设置 / 管理密码窗 | 有 | **无** |
| SN 流程 | 生成 SN | **扫码枪录入 + SN 结构校验 + 版本预设比对** |
| 线圈判定 | 单阈值 | **电流上下限区间判定**（A/B 区间 + 跌落确认 + 采样间隔） |

工位流程：工位1（扫码写 SN + 线圈 A/B）、工位2（带载老化）、工位3（读 SN 关联数据库 + 线圈 A/B）。

## 构建

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build src/ProductionStationTool.App/ProductionStationTool.App/ProductionStationTool.App.csproj -p:EnableWindowsTargeting=true
```

WPF 真机运行需 Windows。
