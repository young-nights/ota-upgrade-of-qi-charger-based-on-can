using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位 2 页面：带载老化测试（构想文档：30 分钟）。
/// 雏形先做计时+进度；带载控制（充电开关/负载）接入后在 StartAsync 中补。
/// </summary>
public partial class Station2Control : UserControl, IStationPage
{
    /// <summary>老化时长（分钟），构想文档定 30。</summary>
    public const int AgingMinutes = 30;

    public int StationNo => 2;
    public string Title => "工位2 — 老化测试";

    public Station2Control()
    {
        InitializeComponent();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        ListDetails.Items.Clear();
        ListDetails.Items.Add($"开始带载老化（{AgingMinutes} 分钟）…");
        TxtResult.Text = "老化中";
        TxtResult.Foreground = Brushes.DarkOrange;

        var total = TimeSpan.FromMinutes(AgingMinutes);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < total)
        {
            ct.ThrowIfCancellationRequested();
            var remain = total - sw.Elapsed;
            ProgressAging.Value = sw.Elapsed.TotalSeconds / total.TotalSeconds * 100;
            TxtRemaining.Text = $"剩余 {remain:mm\\:ss} / 共 {AgingMinutes} 分钟";
            await Task.Delay(1000, ct);
        }

        ProgressAging.Value = 100;
        TxtRemaining.Text = "老化完成";
        TxtResult.Text = "PASS";
        TxtResult.Foreground = Brushes.Green;
        ListDetails.Items.Add($"带载老化 {AgingMinutes} 分钟完成");
        // 占位：后续在此读老化后电压/电流并判限、入库、置标志
    }

    public Task FactoryResetAsync(CancellationToken ct = default)
    {
        ListDetails.Items.Clear();
        ListDetails.Items.Add("已请求清测试标志（待接入设备）");
        return Task.CompletedTask;
    }
}
