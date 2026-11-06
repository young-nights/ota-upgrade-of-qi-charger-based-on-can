using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App;

/// <summary>
/// 本机设置页（按类型分页）：
///  「机位与网络」——机位命名（写 machine.json + 留痕）、查看本机 IP、
///    服务器连通检测、跳转系统网络设置改 IP（应用内不直改）；
///  「检测参数」——步骤 5 双线圈检测参数（电流阈值/窗口/超时），保存后下次测试生效。
/// </summary>
public partial class SettingsWindow : Window
{
    private bool _ready;

    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    public SettingsWindow() : this(0) { }

    /// <summary>startTab：0=机位与网络，1=检测参数（顶部菜单直达对应页）。</summary>
    public SettingsWindow(int startTab)
    {
        InitializeComponent();
        Tabs.SelectedIndex = startTab is 0 or 1 ? startTab : 0;
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
        BoxAThreshold.Text = o.CoilAThresholdA.ToString("0.##");
        BoxBThreshold.Text = o.CoilBThresholdA.ToString("0.##");
        BoxAWindowSec.Text = (o.CoilACollectMs / 1000.0).ToString("0.##");
        BoxBSustainSec.Text = (o.CoilBSustainMs / 1000.0).ToString("0.##");
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
        var aThr = ParseD(BoxAThreshold, d.CoilAThresholdA);
        var bThr = ParseD(BoxBThreshold, d.CoilBThresholdA);
        var aWin = ParseD(BoxAWindowSec, d.CoilACollectMs / 1000.0);
        var bSus = ParseD(BoxBSustainSec, d.CoilBSustainMs / 1000.0);
        var tOut = ParseD(BoxTimeoutSec, d.OverallTimeoutMs / 1000.0);

        var err = MachineConfig.ValidateCoil(aThr, bThr, aWin, bSus, tOut);
        if (err is not null)
        {
            TxtCoilSaveResult.Text = err;
            TxtCoilSaveResult.Foreground = DangerBrush;
            return;
        }

        var options = new CoilTestOptions
        {
            CoilAThresholdA = aThr,
            CoilBThresholdA = bThr,
            CoilACollectMs = (int)(aWin * 1000),
            CoilBSustainMs = (int)(bSus * 1000),
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
}
