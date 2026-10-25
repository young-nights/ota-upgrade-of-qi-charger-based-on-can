namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位页面统一契约：外壳（MainWindow）负责切换页面、
/// 「开始测试」「下一个」按钮统一调用当前页面实现。
/// </summary>
public interface IStationPage
{
    /// <summary>工位号 1~4。</summary>
    int StationNo { get; }

    /// <summary>页面标题（工位选择栏显示）。</summary>
    string Title { get; }

    /// <summary>一键执行本工位测试流程。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// 切换下一台产品：刷新上位机页面状态（结果/数据/流程/日志复位），
    /// **串口连接保持不动**，便于产线连续测试。
    /// </summary>
    void ResetForNext();
}
