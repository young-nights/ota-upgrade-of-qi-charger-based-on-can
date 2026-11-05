using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Utilities;

namespace ProductionStationTool.App;

/// <summary>
/// 本机设置页：机位命名（写 machine.json + 留痕）、查看本机 IP、
/// 服务器连通检测、跳转系统网络设置改 IP（应用内不直改，防改错网段断服务器）。
/// </summary>
public partial class SettingsWindow : Window
{
    private bool _ready;

    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    public SettingsWindow()
    {
        InitializeComponent();
        // 可编辑 ComboBox 的文本变化是内部 TextBox 的路由事件，挂上以便实时预览编码
        CmbStationNo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => Identity_Changed(CmbStationNo, EventArgs.Empty)));
        _ready = true;

        var settings = MachineConfig.LoadSettings();
        CmbStationNo.Text = settings.StationNo.ToString();
        TxtMachineId.Text = settings.MachineId;
        TxtServerHost.Text = settings.ServerHost;
        TxtCurrentCode.Text = $"当前编码：{settings.Identity.Code}";
        TxtPingResult.Text = "点「检测连通」验证到服务器的链路";
        RefreshIps();
        RefreshLog();
    }

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

        TxtCurrentCode.Text = $"当前编码：{new Core.Station.StationIdentity(stationNo, machineId).Code}";
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
}
