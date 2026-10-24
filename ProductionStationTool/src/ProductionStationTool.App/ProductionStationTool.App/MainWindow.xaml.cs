using System.Windows;
using System.Windows.Media;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App;

/// <summary>
/// 工位主窗口（雏形）：
///  - 一键执行：开始测试（后续接自动检测串口/CAN + 工位流程）；
///  - 恢复出厂设置：清 4 个测试标志（老设备复测前必须点）；
///  - PASS/FAIL 大字 + 异常项红字列表。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        // 雏形占位：后续接 Station1Workflow + 串口/CAN 自动连接
        TxtStatus.Text = "流程骨架就绪——待接入 CAN/PowerZ 后执行完整测试";
    }

    private void BtnFactoryReset_Click(object sender, RoutedEventArgs e)
    {
        // 雏形占位：后续接 ITestFlagService.ClearAllFlagsAsync
        TxtStatus.Text = "已请求清测试标志（待接入设备）";
    }

    /// <summary>把一次测试结果显示到 UI（异常项红字）。</summary>
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
