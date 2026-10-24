using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位 1 页面：SN 生成/写入/回读 + PowerZ 电流电压检测（构想文档）。
/// 流程骨架就绪，CAN/PowerZ 接入后替换 StartAsync 占位逻辑。
/// </summary>
public partial class Station1Control : UserControl, IStationPage
{
    public int StationNo => 1;
    public string Title => "工位1 — SN + 电流电压";

    public Station1Control()
    {
        InitializeComponent();
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        // 占位：后续接 Station1Workflow + 串口/CAN 自动连接 + PowerZ 采样
        ListDetails.Items.Clear();
        ListDetails.Items.Add("流程骨架就绪——待接入 CAN/PowerZ 后执行完整测试");
        TxtResult.Text = "待测试";
        TxtResult.Foreground = Brushes.Black;
        return Task.CompletedTask;
    }

    public Task FactoryResetAsync(CancellationToken ct = default)
    {
        // 占位：后续接 ITestFlagService.ClearAllFlagsAsync
        ListDetails.Items.Clear();
        ListDetails.Items.Add("已请求清测试标志（待接入设备）");
        return Task.CompletedTask;
    }

    /// <summary>把一次测试结果显示到页面（异常项红字）。</summary>
    public void ShowResult(Station1Result result)
    {
        ListDetails.Items.Clear();

        if (result.Skipped)
        {
            TxtResult.Text = "PASS";
            TxtResult.Foreground = Brushes.Green;
            ListDetails.Items.Add("标志位已置位：跳过流程，直接 PASS");
            return;
        }

        TxtResult.Text = result.Passed ? "PASS" : "FAIL";
        TxtResult.Foreground = result.Passed ? Brushes.Green : Brushes.Red;

        if (result.Power is not null)
            ListDetails.Items.Add($"电流电压：{result.Power.Voltage:F3}V / {result.Power.Current:F3}A");

        foreach (var item in result.AbnormalItems)
            ListDetails.Items.Add($"【异常】{item}");

        if (result.Passed && result.AbnormalItems.Count == 0)
            ListDetails.Items.Add("全部检测项正常");
    }
}
