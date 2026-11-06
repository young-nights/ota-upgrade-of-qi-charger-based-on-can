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

    /// <summary>已连接（CAN/PowerZ 已接入、未断开）。</summary>
    private bool _connected;

    /// <summary>连接/断开进行中（异步接入未回报前锁按钮防重入）。</summary>
    private bool _busy;

    public ConnectionPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshPorts();
        ComboCanPort.SelectionChanged += Combo_SelectionChanged;
        ComboPowerZPort.SelectionChanged += Combo_SelectionChanged;
        ApplyButtonState();
    }

    /// <summary>
    /// 两下拉互斥 + 设备身份约束：
    /// ① 选到与对方相同串口时清空 PowerZ（CAN 优先）；
    /// ② PowerZ 不允许选 CAN 盒子设备（VID/关键词身份识别，自动清空）。
    /// </summary>
    private void Combo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
            return;
        _syncing = true;
        try
        {
            var can = (ComboCanPort.SelectedItem as PortInfo)?.Name ?? string.Empty;
            var pzSel = ComboPowerZPort.SelectedItem as PortInfo;
            var pz = pzSel?.Name ?? string.Empty;
            if (pzSel is { IsEmpty: false } && PortDetector.IsCanDevice(pzSel))
            {
                ComboPowerZPort.SelectedItem = PortInfo.Empty;
                return;
            }
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
        var hid = PortDetector.ListPowerZDevices();   // PowerZ = HID 设备（0483:5750）
        var canSel = (ComboCanPort.SelectedItem as PortInfo)?.Name ?? string.Empty;
        var pzSel = (ComboPowerZPort.SelectedItem as PortInfo)?.Name ?? string.Empty;

        // CAN 候选：串口列表（HID 设备无 COM 口，不进 CAN 下拉）
        var all = new List<PortInfo> { PortInfo.Empty };
        all.AddRange(ports);

        // PowerZ 候选：HID 设备 + 普通串口（CAN 盒子设备不进列表）
        var pzList = new List<PortInfo> { PortInfo.Empty };
        pzList.AddRange(hid);
        foreach (var p in ports)
            if (!PortDetector.IsCanDevice(p))
                pzList.Add(p);

        ComboCanPort.DisplayMemberPath = nameof(PortInfo.Label);
        ComboPowerZPort.DisplayMemberPath = nameof(PortInfo.Label);
        ComboCanPort.ItemsSource = all;
        ComboPowerZPort.ItemsSource = pzList;

        _syncing = true;
        try
        {
            // CAN：优先 VID/关键词命中 → 保留原选择（不含空）→ 空选项
            var canHit = PortDetector.FindCanPort(ports);
            var canName = canHit
                          ?? (ports.Exists(p => p.Name == canSel) ? canSel : string.Empty);
            ComboCanPort.SelectedItem = all.Find(p => p.Name == canName);

            // PowerZ：HID VID 精确命中（含在 pzList 里）→ 保留原选择 → 空
            var pzHit = PortDetector.FindPowerZPort(pzList, canName);
            var pzKeep = pzList.Find(p => p.Name == pzSel
                                          && p.Name != canName
                                          && !PortDetector.IsCanDevice(p));
            var pzName = pzHit ?? pzKeep?.Name ?? string.Empty;
            ComboPowerZPort.SelectedItem = pzList.Find(p => p.Name == pzName);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 刷新状态徽章并驱动连接/断开按钮高亮反转：
    /// ok=true = 已连接 → 「连接」禁用（暗）、「断开」高亮可按；
    /// ok=false = 未连接 → 「连接」高亮可按、「断开」禁用（暗）。
    /// </summary>
    public void SetStatus(string text, bool ok)
    {
        TxtConnStatus.Text = text;
        TxtConnStatus.Foreground = ok ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        BadgeConn.Background = ok
            ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
            : new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
        _connected = ok;
        _busy = false;
        ApplyButtonState();
    }

    /// <summary>连接/断开按钮高亮反转：当前可执行的动作高亮（Primary），对侧禁用（Ghost+暗）。</summary>
    private void ApplyButtonState()
    {
        if (_busy)
        {
            BtnConnect.IsEnabled = false;
            BtnDisconnect.IsEnabled = false;
            return;
        }
        BtnConnect.IsEnabled = !_connected;
        BtnDisconnect.IsEnabled = _connected;
        BtnConnect.Style = (Style)FindResource(_connected ? "GhostButton" : "PrimaryButton");
        BtnDisconnect.Style = (Style)FindResource(_connected ? "PrimaryButton" : "GhostButton");
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
        _busy = true;
        ApplyButtonState();
        ConnectRequested?.Invoke(CanPortName, PowerZPortName);
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        ApplyButtonState();
        DisconnectRequested?.Invoke();
    }
}
