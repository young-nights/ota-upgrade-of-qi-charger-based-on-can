namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位页面统一契约：外壳（MainWindow）负责切换页面、
/// 「开始测试」「恢复出厂设置」按钮统一调用当前页面实现。
/// </summary>
public interface IStationPage
{
    /// <summary>工位号 1~4（对应 Flash 4 个测试标志位）。</summary>
    int StationNo { get; }

    /// <summary>页面标题（工位选择栏显示）。</summary>
    string Title { get; }

    /// <summary>一键执行本工位测试流程。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>恢复出厂设置：清本工位测试标志（老设备复测前必须调用）。</summary>
    Task FactoryResetAsync(CancellationToken ct = default);
}
