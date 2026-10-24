using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProductionStationTool.App.Controls;

/// <summary>
/// 串口接入区：CAN 盒子 / PowerZ 串口选择、连接/断开、状态徽章。
/// 「连接/断开」通过事件抛给工位页面，实际接入逻辑由页面接线。
/// </summary>
public partial class ConnectionPanel : UserControl
{
    /// <summary>点「连接」。参数：CAN 串口、PowerZ 串口。</summary>
    public event Action<string, string>? ConnectRequested;

    /// <summary>点「断开」。</summary>
    public event Action? DisconnectRequested;

    public string CanPortName => ComboCanPort.Text;
    public string PowerZPortName => ComboPowerZPort.Text;

    public ConnectionPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshPorts();
    }

    public void RefreshPorts()
    {
        var ports = SerialPort.GetPortNames();
        var can = ComboCanPort.Text;
        var pz = ComboPowerZPort.Text;

        ComboCanPort.ItemsSource = ports;
        ComboPowerZPort.ItemsSource = ports;

        if (ports.Contains(can))
            ComboCanPort.Text = can;
        else if (ports.Length > 0)
            ComboCanPort.SelectedIndex = 0;

        if (ports.Contains(pz))
            ComboPowerZPort.Text = pz;
        else if (ports.Length > 1)
            ComboPowerZPort.SelectedIndex = 1;
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
