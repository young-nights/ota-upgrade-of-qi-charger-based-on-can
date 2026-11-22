using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App;

/// <summary>
/// 本机设置页：
///  「机位设置」——机位命名（写 machine.json + 留痕）；
///  「检测参数」——步骤 5 双线圈检测参数（电流阈值/窗口/超时）。
/// </summary>
public partial class SettingsWindow : Window
{
    private bool _ready;

    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    public SettingsWindow() : this(0) { }

    /// <summary>startTab：0=机位设置，1=检测参数。</summary>
    public SettingsWindow(int startTab)
    {
        InitializeComponent();
        Tabs.SelectedIndex = startTab is 0 or 1 or 2 ? startTab : 0;
        CmbStationNo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => Identity_Changed(CmbStationNo, EventArgs.Empty)));
        _ready = true;

        var settings = MachineConfig.LoadSettings();
        CmbStationNo.Text = settings.StationNo.ToString();
        TxtMachineId.Text = settings.MachineId;
        TxtCurrentCode.Text = $"当前编码：{settings.Identity.Code}";
        FillCoilFields(MachineConfig.LoadCoilOptions());

        // 页 3：版本预设
        var vp = MachineConfig.LoadVersionPresets();
        BoxPresetApp.Text = vp.App;
        BoxPresetBoot.Text = vp.Boot;
        BoxPresetQi.Text = vp.Qi;
        BoxPresetHw.Text = vp.Hw;
        var scanTiming = MachineConfig.LoadScanTiming();
        BoxScanTimeoutSec.Text = scanTiming.ScanSeconds.ToString();
        BoxSnJudgeTimeoutSec.Text = scanTiming.JudgeSeconds.ToString();
        BoxOverallTimeoutSec.Text = scanTiming.OverallSeconds.ToString();
        RefreshLog();
    }

    private void RefreshLog()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "machine_change.log");
            TxtLog.Text = File.Exists(path)
                ? string.Join("\n", File.ReadAllLines(path).TakeLast(8))
                : "（暂无变更记录）";
        }
        catch (Exception ex)
        {
            TxtLog.Text = $"（记录读取失败：{ex.Message}）";
        }
    }

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
        TxtSaveResult.Text = "已保存，重启软件后生效";
        TxtSaveResult.Foreground = SuccessBrush;
        RefreshLog();
    }

    private void BtnSavePresets_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MachineConfig.SaveVersionPresets(new MachineConfig.VersionPresets(
                BoxPresetApp.Text, BoxPresetBoot.Text, BoxPresetQi.Text, BoxPresetHw.Text));
            TxtPresetResult.Text = "已保存，下次测试生效";
            TxtPresetResult.Foreground = SuccessBrush;
        }
        catch (Exception ex)
        {
            TxtPresetResult.Text = $"保存失败：{ex.Message}";
            TxtPresetResult.Foreground = DangerBrush;
        }
    }

    private void FillCoilFields(CoilTestOptions o)
    {
        BoxALower.Text = o.CoilALowerA.ToString("0.##");
        BoxAUpper.Text = o.CoilAUpperA.ToString("0.##");
        BoxBLower.Text = o.CoilBLowerA.ToString("0.##");
        BoxBUpper.Text = o.CoilBUpperA.ToString("0.##");
        BoxAWindowSec.Text = (o.CoilACollectMs / 1000.0).ToString("0.##");
        BoxBSustainSec.Text = (o.CoilBSustainMs / 1000.0).ToString("0.##");
        BoxTransitionSec.Text = (o.TransitionMs / 1000.0).ToString("0.##");
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
        var aWin = ParseD(BoxAWindowSec, d.CoilACollectMs / 1000.0);
        var bSus = ParseD(BoxBSustainSec, d.CoilBSustainMs / 1000.0);
        var trans = ParseD(BoxTransitionSec, d.TransitionMs / 1000.0);
        var samp = ParseD(BoxSampleIntervalMs, d.SampleIntervalMs);
        var tOut = ParseD(BoxTimeoutSec, d.OverallTimeoutMs / 1000.0);

        var err = MachineConfig.ValidateCoil(aLow, aHigh, bLow, bHigh, aWin, bSus, tOut, trans, samp);
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
            CoilACollectMs = (int)(aWin * 1000),
            CoilBSustainMs = (int)(bSus * 1000),
            TransitionMs = (int)(trans * 1000),
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

        TxtCoilSaveResult.Text = "已保存，下次测试生效";
        TxtCoilSaveResult.Foreground = SuccessBrush;
        RefreshLog();
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
        RefreshLog();
    }
}
