namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位页面统一契约：外壳（MainWindow）负责切换页面、
/// 「开始测试」统一调用当前页面实现（「下一个」已并入开始：开测即复位上一台）。
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
    /// 复位上一台显示（结果/数据/流程/日志），**串口连接保持不动**。
    /// 「下一个」按钮已并入「开始测试」——本方法由 StartAsync 开测前置调用。
    /// </summary>
    void ResetForNext();

    /// <summary>
    /// 「开始测试」可用性：默认放行（未细化的工位）。
    /// 工位 1 实现为「CAN+PowerZ 串口已接入」。
    /// </summary>
    bool CanStartTest => true;

    /// <summary>
    /// 可用性变化通知（串口接入/断开、测试开始/结束），
    /// 外壳收到后刷新「开始测试」按钮。默认空实现。
    /// </summary>
    event Action? AvailabilityChanged
    {
        add { }
        remove { }
    }
}
