using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Utilities;

namespace ProductionStationTool.App.Controls;

/// <summary>
/// 串口接入区：CAN 盒子 / PowerZ 串口选择、连接/断开、状态徽章。
/// 串口按 USB VID:PID 自动识别（CAN=ZQWL 3562:0101），避免与 PowerZ 搞混。
/// 「连接/断开」通过事件抛给工位页面，实际接入逻辑由页面接线。
/// </summary>
public partial class ConnectionPanel : UserControl
{
    /// <summary>点「连接」。参数：CAN 串口、PowerZ 串口。</summary>
    public event Action<string, string>? ConnectRequested;

    /// <summary>点「断开」。</summary>
    public event Action? DisconnectRequested;

    public string CanPortName =>
        (ComboCanPort.SelectedItem as PortInfo)?.Name ?? ComboCanPort.Text;

    public string PowerZPortName =>
        (ComboPowerZPort.SelectedItem as PortInfo)?.Name ?? ComboPowerZPort.Text;

    public ConnectionPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshPorts();
    }

    public void RefreshPorts()
    {
        var ports = PortDetector.ListPorts();
        var canSel = ComboCanPort.SelectedItem as PortInfo;
        var pzSel = ComboPowerZPort.SelectedItem as PortInfo;

        ComboCanPort.DisplayMemberPath = nameof(PortInfo.Label);
        ComboPowerZPort.DisplayMemberPath = nameof(PortInfo.Label);
        ComboCanPort.ItemsSource = ports;
        ComboPowerZPort.ItemsSource = ports;

        // CAN：优先按 VID:PID=3562:0101 精确识别 ZQWL；否则保留原选择
        var can = PortDetector.FindCanPort(ports);
        var canPick = (can != null ? ports.Find(p => p.Name == can) : null)
                      ?? (canSel != null ? ports.Find(p => p.Name == canSel.Name) : null);
        ComboCanPort.SelectedItem = canPick;

        // PowerZ：VID 已知精确匹配；未知则排除 CAN 口取其余第一个；否则保留原选择
        var pz = PortDetector.FindPowerZPort(ports, canPick?.Name);
        var pzPick = (pz != null ? ports.Find(p => p.Name == pz) : null)
                     ?? (pzSel != null ? ports.Find(p => p.Name == pzSel.Name) : null);
        ComboPowerZPort.SelectedItem = pzPick;
    }

    public void SetStatus(string text, bool ok)
    {
        TxtConnStatus.Text = text;
        TxtConnStatus.Foreground = ok ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        BadgeConn.Background = ok
            ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
            : new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void BtnConnect_Click(object sender, RoutedEventArgs e)
        => ConnectRequested?.Invoke(CanPortName, PowerZPortName);

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        => DisconnectRequested?.Invoke();
}
