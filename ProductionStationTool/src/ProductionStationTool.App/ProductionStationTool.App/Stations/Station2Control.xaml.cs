using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Controls;

namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位 2 页面：带载老化测试（构想文档：30 分钟）。
/// 四分区布局：串口接入 / 老化流程 / 数据展示 / 运行日志。
/// 带载控制与老化后判限/入库接入后替换 StartAsync 占位逻辑。
/// </summary>
public partial class Station2Control : UserControl, IStationPage
{
    /// <summary>老化时长（分钟），构想文档定 30。</summary>
    public const int AgingMinutes = 30;

    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    public int StationNo => 2;
    public string Title => "工位2 — 老化测试";

    public Station2Control()
    {
        InitializeComponent();
        FlowPanel.SetSteps(
            "连接设备（串口/CAN 自动检测）",
            "开启带载（启动充电）",
            $"带载老化 {AgingMinutes} 分钟",
            "老化后读取电压/电流",
            "判限并入库");

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

    public async Task StartAsync(CancellationToken ct = default)
    {
        FlowPanel.Reset();
        ListDetails.Items.Clear();
        LogPanel.Append($"开始带载老化（{AgingMinutes} 分钟）");

        SetBadge("老化中", WarningBrush, Color.FromRgb(0xFE, 0xF3, 0xC7));

        FlowPanel.Mark(0, StepStatus.Passed);
        FlowPanel.Mark(1, StepStatus.Passed);
        FlowPanel.Mark(2, StepStatus.Running);

        var total = TimeSpan.FromMinutes(AgingMinutes);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < total)
        {
            ct.ThrowIfCancellationRequested();
            var remain = total - sw.Elapsed;
            var pct = sw.Elapsed.TotalSeconds / total.TotalSeconds * 100;
            ProgressAging.Value = pct;
            TxtProgressPct.Text = $"{pct:F0}%";
            TxtRemaining.Text = $"{remain:mm\\:ss}";
            await Task.Delay(1000, ct);
        }

        ProgressAging.Value = 100;
        TxtProgressPct.Text = "100%";
        TxtRemaining.Text = "00:00";
        FlowPanel.Mark(2, StepStatus.Passed);
        FlowPanel.Mark(3, StepStatus.Passed);
        FlowPanel.Mark(4, StepStatus.Passed);

        SetBadge("PASS", SuccessBrush, Color.FromRgb(0xDC, 0xFC, 0xE7));
        ListDetails.Items.Add($"带载老化 {AgingMinutes} 分钟完成");
        LogPanel.Append("老化完成，PASS");
        // 占位：后续在此读老化后电压/电流并判限、入库
    }

    /// <summary>切换下一台：老化进度/结果/流程/日志复位，串口连接保持。</summary>
    public void ResetForNext()
    {
        FlowPanel.Reset();
        ListDetails.Items.Clear();
        ProgressAging.Value = 0;
        TxtProgressPct.Text = "0%";
        TxtRemaining.Text = $"{AgingMinutes:00}:00";
        SetBadge("待老化", MutedBrush, Color.FromRgb(0xF1, 0xF5, 0xF9));
        LogPanel.Clear();
        LogPanel.Append("已切换下一台，串口保持连接，等待开始测试");
    }

    private void SetBadge(string text, Brush fg, Color bg)
    {
        TxtResult.Text = text;
        TxtResult.Foreground = fg;
        BadgeResult.Background = new SolidColorBrush(bg);
    }
}
