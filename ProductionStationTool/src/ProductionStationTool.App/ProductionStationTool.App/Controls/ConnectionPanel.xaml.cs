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

    /// <summary>互斥刷新防重入（清空对方时会再次触发 SelectionChanged）。</summary>
    private bool _syncing;

    public ConnectionPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshPorts();
        ComboCanPort.SelectionChanged += Combo_SelectionChanged;
        ComboPowerZPort.SelectionChanged += Combo_SelectionChanged;
    }

    /// <summary>两下拉互斥：选了同一串口时清空 PowerZ（CAN 优先）。</summary>
    private void Combo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
            return;
        _syncing = true;
        try
        {
            var can = (ComboCanPort.SelectedItem as PortInfo)?.Name ?? string.Empty;
            var pz = (ComboPowerZPort.SelectedItem as PortInfo)?.Name ?? string.Empty;
            if (can.Length > 0 && can == pz)
                ComboPowerZPort.SelectedItem = PortInfo.Empty;
        }
        finally
        {
            _syncing = false;
        }
    }

    public void RefreshPorts()
    {
        var ports = PortDetector.ListPorts();
        var canSel = (ComboCanPort.SelectedItem as PortInfo)?.Name ?? string.Empty;
        var pzSel = (ComboPowerZPort.SelectedItem as PortInfo)?.Name ?? string.Empty;

        // 顶部留一个空选项「—」（未选择）
        var all = new List<PortInfo> { PortInfo.Empty };
        all.AddRange(ports);

        ComboCanPort.DisplayMemberPath = nameof(PortInfo.Label);
        ComboPowerZPort.DisplayMemberPath = nameof(PortInfo.Label);
        ComboCanPort.ItemsSource = all;
        ComboPowerZPort.ItemsSource = all;

        _syncing = true;
        try
        {
            // CAN：优先 VID/关键词命中 → 保留原选择（不含空）→ 空选项
            var canHit = PortDetector.FindCanPort(ports);
            var canName = canHit
                          ?? (ports.Exists(p => p.Name == canSel) ? canSel : string.Empty);
            ComboCanPort.SelectedItem = all.Find(p => p.Name == canName);

            // PowerZ：VID 命中 → 排除 CAN 口取第一个 → 保留原选择（不与 CAN 冲突）→ 空
            var pzHit = PortDetector.FindPowerZPort(ports, canName);
            var pzName = pzHit
                         ?? (ports.Exists(p => p.Name == pzSel && p.Name != canName) ? pzSel : string.Empty);
            ComboPowerZPort.SelectedItem = all.Find(p => p.Name == pzName);
        }
        finally
        {
            _syncing = false;
        }
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
    {
        // 两个口都选了才允许连接（空选项 = 未选择）
        if (string.IsNullOrEmpty(CanPortName) || string.IsNullOrEmpty(PowerZPortName))
        {
            SetStatus("请先选择 CAN 与 PowerZ 串口", false);
            return;
        }
        ConnectRequested?.Invoke(CanPortName, PowerZPortName);
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        => DisconnectRequested?.Invoke();
}
