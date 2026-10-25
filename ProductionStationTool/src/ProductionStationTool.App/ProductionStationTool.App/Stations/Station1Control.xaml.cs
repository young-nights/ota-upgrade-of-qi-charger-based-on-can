using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Controls;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位 1 页面：SN 生成/写入/回读 + PowerZ 电流电压检测（构想文档）。
/// 四分区布局：串口接入 / 测试流程 / 数据展示 / 运行日志。
/// CAN/PowerZ 接入后替换 StartAsync 占位逻辑。
/// </summary>
public partial class Station1Control : UserControl, IStationPage
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    public int StationNo => 1;
    public string Title => "工位1 — SN + 电流电压";

    public Station1Control()
    {
        InitializeComponent();
        FlowPanel.SetSteps(
            "连接设备（串口/CAN 自动检测）",
            "生成 SN 码",
            "写入 SN 并回读验证",
            "开启无线充电",
            "PowerZ 采样电流/电压",
            "判限并入库、置标志");

        ConnPanel.ConnectRequested += (can, pz) =>
        {
            ConnPanel.SetStatus($"已连接 CAN:{can} PowerZ:{pz}", true);
            LogPanel.Append($"串口连接：CAN={can}，PowerZ={pz}");
        };
        ConnPanel.DisconnectRequested += () =>
        {
            ConnPanel.SetStatus("未连接", false);
            LogPanel.Append("串口已断开");
        };
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        FlowPanel.Reset();
        ListAbnormal.Items.Clear();
        LogPanel.Append("开始工位 1 测试流程（骨架演示）");

        // 占位演示：按步骤点亮流程区；接入 CAN/PowerZ 后替换为 Station1Workflow
        for (var i = 0; i < FlowPanel.Steps.Count; i++)
        {
            FlowPanel.Mark(i, StepStatus.Running);
            LogPanel.Append($"步骤 {i + 1} 进行中…");
            FlowPanel.Mark(i, StepStatus.Passed);
        }

        TxtResult.Text = "待测试";
        TxtResult.Foreground = MutedBrush;
        BadgeResult.Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
        LogPanel.Append("流程骨架就绪——待接入 CAN/PowerZ 后执行完整测试");
        return Task.CompletedTask;
    }

    public Task FactoryResetAsync(CancellationToken ct = default)
    {
        // 占位：后续接 ITestFlagService.ClearAllFlagsAsync
        LogPanel.Append("已请求清测试标志 / 恢复出厂设置（待接入设备）");
        return Task.CompletedTask;
    }

    /// <summary>把一次测试结果显示到数据展示区（异常项红字）。</summary>
    public void ShowResult(Station1Result result)
    {
        ListAbnormal.Items.Clear();
        TxtSn.Text = string.IsNullOrEmpty(result.Sn) ? "—" : result.Sn;

        SetResultBadge(result.Passed);

        if (result.Power is not null)
        {
            TxtVoltage.Text = $"{result.Power.Voltage:F3}";
            TxtCurrent.Text = $"{result.Power.Current:F3}";
            TxtPowerJudge.Text = result.Power.IsNormal ? "正常" : "异常";
            TxtPowerJudge.Foreground = result.Power.IsNormal ? SuccessBrush : DangerBrush;
        }

        foreach (var item in result.AbnormalItems)
            ListAbnormal.Items.Add(item);

        LogPanel.Append(result.Passed
            ? "全部检测项正常，PASS"
            : $"存在 {result.AbnormalItems.Count} 项异常，FAIL");
    }

    private void SetResultBadge(bool passed)
    {
        TxtResult.Text = passed ? "PASS" : "FAIL";
        TxtResult.Foreground = passed ? SuccessBrush : DangerBrush;
        BadgeResult.Background = passed
            ? new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7))
            : new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
    }
}
