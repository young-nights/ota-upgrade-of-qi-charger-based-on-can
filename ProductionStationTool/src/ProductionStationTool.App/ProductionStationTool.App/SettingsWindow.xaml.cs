using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Data;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App;

/// <summary>
/// 本机设置页（按类型分页）：
///  「机位与网络」——机位命名（写 machine.json + 留痕）、查看本机 IP、
///    服务器连通检测、跳转系统网络设置改 IP（应用内不直改）；
///  「检测参数」——步骤 5 双线圈检测参数（上下限/检测窗口/超时），保存后下次测试生效；
///  「版本预设」——工位 4 四项版本（MCU1/Qi/Boot/HW），四项全匹配才 PASS；
///  「数据管理」——良率口径与记录级手动修正。
/// </summary>
public partial class SettingsWindow : Window
{
    private bool _ready;

    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    public SettingsWindow() : this(0) { }

    /// <summary>startTab：0=机位与网络，1=检测参数，2=版本预设，3=数据管理（顶部菜单直达对应页）。</summary>
    public SettingsWindow(int startTab)
    {
        InitializeComponent();
        Tabs.SelectedIndex = startTab is 0 or 1 or 2 or 3 ? startTab : 0;
        // 可编辑 ComboBox 的文本变化是内部 TextBox 的路由事件，挂上以便实时预览编码
        CmbStationNo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => Identity_Changed(CmbStationNo, EventArgs.Empty)));
        _ready = true;

        // 页 1：机位与网络
        var settings = MachineConfig.LoadSettings();
        CmbStationNo.Text = settings.StationNo.ToString();
        TxtMachineId.Text = settings.MachineId;
        TxtServerHost.Text = settings.ServerHost;
        TxtCurrentCode.Text = $"当前编码：{settings.Identity.Code}";
        TxtPingResult.Text = "点「检测连通」验证到服务器的链路";
        RefreshIps();
        RefreshLog();

        // 页 2：检测参数
        FillCoilFields(MachineConfig.LoadCoilOptions());

        // 页 3：四项版本预设
        var vp = MachineConfig.LoadVersionPresets();
        BoxPresetMcu1.Text = vp.Mcu1App;
        BoxPresetMcu2.Text = vp.Mcu2Qi;
        BoxPresetBoot.Text = vp.Bootloader;
        BoxPresetHw.Text = vp.Hardware;

        // 页 4：数据管理
        ChkYieldExclude.IsChecked = MachineConfig.LoadYieldExcludeAbandoned();
        RefreshYieldSummary();
        QueryFixRows();
    }

    // ================= 页 4：数据管理 =================

    /// <summary>手动修正表行（弃用/打印标记可改，写回按 SN 生效）。</summary>
    private sealed class FixRow
    {
        public string Sn { get; init; } = "";
        public int StationNo { get; init; }
        public string Result { get; init; } = "";
        public DateTime TestTime { get; init; }
        public bool Abandoned { get; set; }
        public bool Printed { get; set; }
    }

    private void RefreshYieldSummary()
    {
        try
        {
            using var store = new Core.Data.SqliteTestRecordStore();
            bool exclude = ChkYieldExclude.IsChecked == true;
            var lines = new List<string> { $"当前口径：{(exclude ? "弃用记录不计入良率" : "全部记录计入良率")}" };
            var rows = store.StatsByMachine(exclude);
            if (rows.Count == 0)
            {
                lines.Add("（暂无测试记录）");
            }
            else
            {
                int total = 0, pass = 0, fail = 0;
                foreach (var r in rows)
                {
                    total += r.Total;
                    pass += r.Pass;
                    fail += r.Fail;
                    double y = r.Total == 0 ? 0 : r.Pass * 100.0 / r.Total;
                    lines.Add($"工位{r.StationNo}·机位{r.MachineId}：测试 {r.Total} · PASS {r.Pass} · FAIL {r.Fail} · 良率 {y:F1}%");
                }
                double all = total == 0 ? 0 : pass * 100.0 / total;
                lines.Insert(1, $"全部机位合计：测试 {total} · PASS {pass} · FAIL {fail} · 良率 {all:F1}%");
            }
            TxtYieldSummary.Text = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            TxtYieldSummary.Text = $"统计读取失败：{ex.Message}";
        }
    }

    private void BtnSaveYield_Click(object sender, RoutedEventArgs e)
    {
        MachineConfig.SaveYieldExcludeAbandoned(ChkYieldExclude.IsChecked == true);
        RefreshYieldSummary();
        TxtYieldResult.Text = "口径已保存（machine.json）";
    }

    private void BtnRefreshYield_Click(object sender, RoutedEventArgs e)
    {
        RefreshYieldSummary();
        TxtYieldResult.Text = "已刷新";
    }

    private void QueryFixRows()
    {
        try
        {
            using var store = new Core.Data.SqliteTestRecordStore();
            var sn = BoxSnQuery.Text.Trim();
            var records = sn.Length > 0
                ? store.FindAllBySn(sn)
                : store.Query(new Core.Data.TestRecordFilter(Limit: 200));
            GridFix.ItemsSource = records.Select(r => new FixRow
            {
                Sn = r.Sn,
                StationNo = r.StationNo,
                Result = r.Result,
                TestTime = r.TestTime,
                Abandoned = r.Abandoned,
                Printed = r.Printed,
            }).ToList();
        }
        catch (Exception ex)
        {
            TxtYieldResult.Text = $"查询失败：{ex.Message}";
        }
    }

    private void BtnQueryRecords_Click(object sender, RoutedEventArgs e) => QueryFixRows();

    private void BtnApplyFix_Click(object sender, RoutedEventArgs e)
    {
        if (GridFix.ItemsSource is not IEnumerable<FixRow> rows)
            return;
        try
        {
            using var store = new Core.Data.SqliteTestRecordStore();
            int n = 0;
            foreach (var grp in rows.GroupBy(r => r.Sn))
            {
                var row = grp.First();
                store.SetAbandoned(row.Sn, row.Abandoned);
                store.SetPrinted(row.Sn, row.Printed);
                n++;
            }
            RefreshYieldSummary();
            TxtYieldResult.Text = $"已应用 {n} 个 SN 的标记修正";
        }
        catch (Exception ex)
        {
            TxtYieldResult.Text = $"应用失败：{ex.Message}";
        }
    }

    // ================= 页 1：机位与网络 =================

    private void RefreshIps()
    {
        var ips = NetworkInfo.GetLocalIpv4();
        TxtIps.Text = ips.Count == 0
            ? "（未检测到已启用的网卡 IPv4）"
            : string.Join("\n", ips.Select(x => $"{x.Adapter}：{x.Ipv4}"));
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
            MachineConfig.Save(stationNo, machineId, TxtServerHost.Text);
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

    private async void BtnPing_Click(object sender, RoutedEventArgs e)
    {
        var host = TxtServerHost.Text.Trim();
        if (host.Length == 0)
        {
            TxtPingResult.Text = "请先填写服务器地址";
            TxtPingResult.Foreground = DangerBrush;
            return;
        }

        TxtPingResult.Text = $"正在检测 {host} …";
        TxtPingResult.Foreground = MutedBrush;
        var (ok, _, detail) = await NetworkInfo.PingAsync(host);
        TxtPingResult.Text = $"{host}：{detail}";
        TxtPingResult.Foreground = ok ? SuccessBrush : DangerBrush;
    }

    private void BtnOpenNetwork_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ncpa.cpl") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开网络设置失败：{ex.Message}", "本机设置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

        TxtCoilSaveResult.Text = "已保存，下次测试生效";
        TxtCoilSaveResult.Foreground = SuccessBrush;
        RefreshLog();
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

        TxtPresetResult.Text = "已保存，工位 4 下次测试比对生效";
        TxtPresetResult.Foreground = SuccessBrush;
        RefreshLog();
    }
}
