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

    /// <summary>
    /// 「开始测试」可用性：默认放行（未细化的工位）。
    /// 工位 1 实现为「CAN+PowerZ 串口已接入」。
    /// </summary>
    bool CanStartTest => true;

    /// <summary>
    /// 「下一个」可用性：默认放行（未细化的工位）。
    /// 工位 1 实现为「完整跑通一次流程后」为 true。
    /// </summary>
    bool CanGoNext => true;

    /// <summary>
    /// 可用性变化通知（串口接入/断开、测试开始/结束、切换下一台），
    /// 外壳收到后刷新「开始测试」「下一个」按钮。默认空实现。
    /// </summary>
    event Action? AvailabilityChanged
    {
        add { }
        remove { }
    }
}
