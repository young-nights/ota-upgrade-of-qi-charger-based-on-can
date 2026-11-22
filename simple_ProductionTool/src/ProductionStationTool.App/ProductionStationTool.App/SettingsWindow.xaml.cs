using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App;

/// <summary>
/// 本机设置页（按类型分页，参考版写法；简版无网络/无数据库）：
///  「机位设置」——机位命名（写 machine.json + 留痕）；
///  「检测参数」——双线圈上下限/检测窗口/超时 + 扫码参数（SN 判定时间），保存后下次测试生效；
///  「版本预设」——四项版本（MCU1/Qi/Boot/HW），四项全匹配才 PASS。
/// 各页保存均写本机 machine.json，重启后自动沿用。
/// </summary>
public partial class SettingsWindow : Window
{
    private bool _ready;

    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    public SettingsWindow() : this(0) { }

    /// <summary>startTab：0=机位设置，1=检测参数，2=版本预设（顶部菜单直达对应页）。</summary>
    public SettingsWindow(int startTab)
    {
        InitializeComponent();
        Tabs.SelectedIndex = startTab is 0 or 1 or 2 ? startTab : 0;
        // 可编辑 ComboBox 的文本变化是内部 TextBox 的路由事件，挂上以便实时预览编码
        CmbStationNo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => Identity_Changed(CmbStationNo, EventArgs.Empty)));
        _ready = true;

        // 页 1：机位设置
        var settings = MachineConfig.LoadSettings();
        CmbStationNo.Text = settings.StationNo.ToString();
        TxtMachineId.Text = settings.MachineId;
        TxtCurrentCode.Text = $"当前编码：{settings.Identity.Code}";

        // 页 2：检测参数
        FillCoilFields(MachineConfig.LoadCoilOptions());

        // 扫码参数（工位3 扫码枪）
        var scan = MachineConfig.LoadScanTiming();
        BoxScanTimeoutSec.Text = scan.ScanSeconds.ToString();
        BoxSnJudgeTimeoutSec.Text = scan.JudgeSeconds.ToString();
        BoxOverallTimeoutSec.Text = scan.OverallSeconds.ToString();

        // 页 3：四项版本预设
        var vp = MachineConfig.LoadVersionPresets();
        BoxPresetMcu1.Text = vp.Mcu1App;
        BoxPresetMcu2.Text = vp.Mcu2Qi;
        BoxPresetBoot.Text = vp.Bootloader;
        BoxPresetHw.Text = vp.Hardware;
    }

    // ================= 页 1：机位设置 =================

    private void Identity_Changed(object sender, EventArgs e)
    {
        if (!_ready)
            return;
        var code = $"ST-{ParseStation():00}-{TxtMachineId.Text.Trim()}";
        TxtCurrentCode.Text = $"预览：{code}（未保存）";
        TxtCurrentCode.Foreground = MutedBrush;
    }

    private int ParseStation()
        => int.TryParse(CmbStationNo.Text.Trim(), out var n) ? n : 0;

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var stationNo = ParseStation();
        var machineId = TxtMachineId.Text.Trim();
        var err = MachineConfig.Validate(stationNo, machineId);
        if (err is not null)
        {
            TxtSaveResult.Text = err;
            TxtSaveResult.Foreground = DangerBrush;
            return;
        }

        try
        {
            MachineConfig.Save(stationNo, machineId);
        }
        catch (Exception ex)
        {
            TxtSaveResult.Text = $"保存失败：{ex.Message}";
            TxtSaveResult.Foreground = DangerBrush;
            return;
        }

        TxtCurrentCode.Text = $"当前编码：{new StationIdentity(stationNo, machineId).Code}";
        TxtCurrentCode.Foreground = (Brush)FindResource("AccentBrush");
        TxtSaveResult.Text = "已保存到本机 machine.json，重启软件后生效";
        TxtSaveResult.Foreground = SuccessBrush;
    }

    // ================= 页 2：检测参数 =================

    private void FillCoilFields(CoilTestOptions o)
    {
        BoxALower.Text = o.CoilALowerA.ToString("0.##");
        BoxAUpper.Text = o.CoilAUpperA.ToString("0.##");
        BoxBLower.Text = o.CoilBLowerA.ToString("0.##");
        BoxBUpper.Text = o.CoilBUpperA.ToString("0.##");
        BoxDetectionWindowSec.Text = (o.DetectionWindowMs / 1000.0).ToString("0.##");
        BoxSampleIntervalMs.Text = o.SampleIntervalMs.ToString("0");
        BoxTimeoutSec.Text = (o.OverallTimeoutMs / 1000.0).ToString("0.##");
    }

    private void BtnCoilDefaults_Click(object sender, RoutedEventArgs e)
    {
        FillCoilFields(new CoilTestOptions());
        TxtCoilSaveResult.Text = "已填入默认值（未保存）";
        TxtCoilSaveResult.Foreground = MutedBrush;
    }

    private void BtnSaveCoil_Click(object sender, RoutedEventArgs e)
    {
        double ParseD(TextBox box, double fallback)
            => double.TryParse(box.Text.Trim(), out var v) ? v : fallback;

        var d = new CoilTestOptions();
        var aLow = ParseD(BoxALower, d.CoilALowerA);
        var aHigh = ParseD(BoxAUpper, d.CoilAUpperA);
        var bLow = ParseD(BoxBLower, d.CoilBLowerA);
        var bHigh = ParseD(BoxBUpper, d.CoilBUpperA);
        var detWin = ParseD(BoxDetectionWindowSec, d.DetectionWindowMs / 1000.0);
        var samp = ParseD(BoxSampleIntervalMs, d.SampleIntervalMs);
        var tOut = ParseD(BoxTimeoutSec, d.OverallTimeoutMs / 1000.0);

        var err = MachineConfig.ValidateCoil(aLow, aHigh, bLow, bHigh, detWin, tOut, samp);
        if (err is not null)
        {
            TxtCoilSaveResult.Text = err;
            TxtCoilSaveResult.Foreground = DangerBrush;
            return;
        }

        var options = new CoilTestOptions
        {
            CoilALowerA = aLow,
            CoilAUpperA = aHigh,
            CoilBLowerA = bLow,
            CoilBUpperA = bHigh,
            DetectionWindowMs = (int)(detWin * 1000),
            SampleIntervalMs = (int)samp,
            OverallTimeoutMs = (int)(tOut * 1000),
        };

        try
        {
            MachineConfig.SaveCoilOptions(options);
        }
        catch (Exception ex)
        {
            TxtCoilSaveResult.Text = $"保存失败：{ex.Message}";
            TxtCoilSaveResult.Foreground = DangerBrush;
            return;
        }

        TxtCoilSaveResult.Text = "已保存到本机 machine.json，重启后沿用，下次测试生效";
        TxtCoilSaveResult.Foreground = SuccessBrush;
    }

    // ================= 页 3：版本预设 =================

    private void BtnSavePresets_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MachineConfig.SaveVersionPresets(
                BoxPresetMcu1.Text, BoxPresetMcu2.Text, BoxPresetBoot.Text, BoxPresetHw.Text);
        }
        catch (Exception ex)
        {
            TxtPresetResult.Text = $"保存失败：{ex.Message}";
            TxtPresetResult.Foreground = DangerBrush;
            return;
        }

        TxtPresetResult.Text = "已保存到本机 machine.json，重启后沿用，工位 4 下次测试比对生效";
        TxtPresetResult.Foreground = SuccessBrush;
    }

    private void BtnSaveScan_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(BoxScanTimeoutSec.Text.Trim(), out var scan) ||
            !int.TryParse(BoxSnJudgeTimeoutSec.Text.Trim(), out var judge) ||
            !int.TryParse(BoxOverallTimeoutSec.Text.Trim(), out var overall))
        {
            TxtScanSaveResult.Text = "请输入有效整数秒数";
            TxtScanSaveResult.Foreground = DangerBrush;
            return;
        }
        var err = MachineConfig.ValidateScanTiming(scan, judge, overall);
        if (err is not null)
        {
            TxtScanSaveResult.Text = err;
            TxtScanSaveResult.Foreground = DangerBrush;
            return;
        }
        MachineConfig.SaveScanTiming(scan, judge, overall);
        TxtScanSaveResult.Text = "已保存，下次测试生效";
        TxtScanSaveResult.Foreground = SuccessBrush;
    }
}
