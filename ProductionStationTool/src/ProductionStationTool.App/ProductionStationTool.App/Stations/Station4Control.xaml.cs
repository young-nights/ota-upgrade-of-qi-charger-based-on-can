using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Controls;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Data;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位 4 页面：出货门禁 + 打印 + 版本比对。
/// 流程：连接设备 → 读 SN → 数据库门禁（工位1/2/3 全 PASS 才放行，Fail/未测即停）
///       → SN 输出打印机（接口占位）→ 读软硬件版本与「设置 → 版本预设」比对，匹配才 PASS。
/// 防呆目的：前面工序的 FAIL 产品不放行到后续/出货。
/// </summary>
public partial class Station4Control : UserControl, IStationPage
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly Brush PassBg = new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7));
    private static readonly Brush FailBg = new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
    private static readonly Brush IdleBg = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));

    /// <summary>门禁必查工位：前面所有工序，缺记录=未测、FAIL 即拦。</summary>
    private static readonly int[] GateStations = { 1, 2, 3 };

    private readonly ITestRecordStore _store = new SqliteTestRecordStore();
    private readonly ISnPrinter _printer = new NullSnPrinter();
    private readonly LogBuffer _log = new();
    private LogWindow? _logWin;

    private CanUdsDevice? _can;
    private bool _connected;
    private bool _running;
    private bool _flowCompleted;

    public int StationNo => 4;
    public string Title => "工位4 — 出货门禁 + 版本比对";

    public bool CanStartTest => _connected && !_running;
    public bool CanGoNext => _flowCompleted && !_running;

    public event Action? AvailabilityChanged;

    private void RaiseAvailability() => AvailabilityChanged?.Invoke();

    private void LogSafe(string msg) => _log.Append(msg);

    public Station4Control()
    {
        InitializeComponent();
        ConnPanel.ShowPowerZ = false;   // 本工位只用 CAN（读 SN/版本），不需要 PowerZ
        FlowPanel.SetSteps(
            "连接设备（串口/CAN 自动检测）",
            "读取 SN 码",
            "数据库门禁（工位1/2/3 全 PASS）",
            "SN 输出打印机（接口占位）",
            "读取软硬件版本并与预设比对");

        ConnPanel.ConnectRequested += OnConnectRequested;
        ConnPanel.DisconnectRequested += OnDisconnectRequested;
    }

    private async void OnConnectRequested(string can, string pz)
    {
        try
        {
            _can?.Dispose();
            _can = new CanUdsDevice(can);
            _can.Log += LogSafe;
            await _can.OpenAsync();
            _connected = true;
            ConnPanel.SetStatus($"已连接 CAN:{can}", true);
            _log.Append($"串口连接：CAN={can}");
        }
        catch (Exception ex)
        {
            _can?.Dispose();
            _can = null;
            _connected = false;
            ConnPanel.SetStatus($"连接失败：{ex.Message}", false);
            _log.Append($"CAN 打开失败：{ex.Message}");
        }
        RaiseAvailability();
    }

    private void OnDisconnectRequested()
    {
        try { _can?.Dispose(); } catch { /* 忽略 */ }
        _can = null;
        _connected = false;
        ConnPanel.SetStatus("未连接", false);
        _log.Append("串口已断开");
        RaiseAvailability();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_running)
            return;
        if (!_connected || _can is null)
        {
            _log.Append("串口未接入（CAN），无法开始测试");
            return;
        }

        _running = true;
        RaiseAvailability();
        FlowPanel.Reset();
        ListAbnormal.Items.Clear();
        TxtSn.Text = "—";
        TxtPrint.Text = "—";
        ResetVersionCards();
        ResetGateCards();
        _log.Append("开始工位 4 出货流程");

        // ① 连接设备：唤醒 + 等 UDS 就绪
        FlowPanel.Mark(0, StepStatus.Running);
        _log.Append("步骤 1：唤醒设备并等待 UDS 就绪…");
        if (!await _can.WaitReadyAsync(ct))
        {
            FailEarly(0, "设备 8s 内无应答——请检查供电后重试");
            return;
        }
        FlowPanel.Mark(0, StepStatus.Passed);

        // ② 读取 SN 码
        FlowPanel.Mark(1, StepStatus.Running);
        _log.Append("步骤 2：读取设备 SN 码…");
        var sn = await _can.TryReadSnAsync(ct);
        if (string.IsNullOrEmpty(sn))
        {
            FailEarly(1, "SN 读取失败（设备未写 SN？）");
            return;
        }
        TxtSn.Text = sn;
        _log.Append($"步骤 2：SN={sn}");
        FlowPanel.Mark(1, StepStatus.Passed);

        // ③ 数据库门禁：工位1/2/3 全 PASS 才放行（Fail/未测即停）
        FlowPanel.Mark(2, StepStatus.Running);
        var history = _store.FindAllBySn(sn);
        var gateOk = ShowGate(history, sn);
        if (!gateOk)
        {
            FlowPanel.Mark(2, StepStatus.Failed, "存在 Fail/未测工位，禁止放行");
            FlowPanel.Mark(3, StepStatus.Waiting, "未执行（门禁未过）");
            FlowPanel.Mark(4, StepStatus.Waiting, "未执行（门禁未过）");
            SetResultBadge(false);
            _log.Append("步骤 3：门禁未通过——存在 Fail 或未测工位，停止后续流程（不打印、不比对）");
            _running = false;
            _flowCompleted = true;
            RaiseAvailability();
            return;
        }
        FlowPanel.Mark(2, StepStatus.Passed);
        _log.Append("步骤 3：门禁通过（工位1/2/3 全 PASS）");

        // ④ SN 输出打印机（接口占位）
        FlowPanel.Mark(3, StepStatus.Running);
        var (printOk, printDetail) = await _printer.PrintSnAsync(sn, ct);
        TxtPrint.Text = printDetail;
        _log.Append($"步骤 4：{printDetail}");
        FlowPanel.Mark(3, printOk ? StepStatus.Passed : StepStatus.Failed, printOk ? null : printDetail);
        if (!printOk)
        {
            ListAbnormal.Items.Add($"SN 打印失败：{printDetail}");
            FlowPanel.Mark(4, StepStatus.Waiting, "未执行（前序失败）");
            SetResultBadge(false);
            _running = false;
            _flowCompleted = true;
            RaiseAvailability();
            return;
        }

        // ⑤ 读取软硬件版本并与预设比对
        FlowPanel.Mark(4, StepStatus.Running);
        _log.Append("步骤 5：读取软硬件版本…");
        var vs = await _can.TryReadAllVersionsAsync();
        var (sw, hw) = MachineConfig.LoadVersionPresets();
        var swOk = ShowVersionCard(CardSw, TxtSwVerdict, TxtSwRead, TxtSwPreset, "软件版本", vs.Mcu1App, sw);
        var hwOk = ShowVersionCard(CardHw, TxtHwVerdict, TxtHwRead, TxtHwPreset, "硬件版本", vs.Hardware, hw);
        _log.Append($"步骤 5：软件 {vs.Mcu1App ?? "(读取失败)"} vs 预设 {Show(sw)}" +
                    $" → {(swOk ? "匹配" : "不匹配")}；硬件 {vs.Hardware ?? "(读取失败)"} vs 预设 {Show(hw)}" +
                    $" → {(hwOk ? "匹配" : "不匹配")}");
        var versionOk = swOk && hwOk;
        FlowPanel.Mark(4, versionOk ? StepStatus.Passed : StepStatus.Failed,
            versionOk ? null : "软硬件版本与预设不匹配");
        if (!versionOk)
        {
            if (!swOk)
                ListAbnormal.Items.Add($"软件版本不匹配：读取 {Show(vs.Mcu1App)} / 预设 {Show(sw)}");
            if (!hwOk)
                ListAbnormal.Items.Add($"硬件版本不匹配：读取 {Show(vs.Hardware)} / 预设 {Show(hw)}");
        }

        SetResultBadge(versionOk);
        _log.Append(versionOk ? "门禁+打印+版本比对全部通过，PASS"
                             : $"存在 {ListAbnormal.Items.Count} 项异常，FAIL");
        _running = false;
        _flowCompleted = true;
        RaiseAvailability();
    }

    /// <summary>前序步骤失败：该步 FAIL，其后「未执行」。</summary>
    private void FailEarly(int step, string reason)
    {
        FlowPanel.Mark(step, StepStatus.Failed, reason);
        for (var i = step + 1; i < 5; i++)
            FlowPanel.Mark(i, StepStatus.Waiting, "未执行（前序失败）");
        ListAbnormal.Items.Add($"步骤 {step + 1} 失败：{reason}");
        SetResultBadge(false);
        _log.Append(reason);
        _running = false;
        _flowCompleted = true;
        RaiseAvailability();
    }

    /// <summary>
    /// 门禁：工位1/2/3 各自必须有 PASS 记录。
    /// 无记录=未测（灰）、有 FAIL=未通过（红）、PASS=通过（绿）；返回是否全 PASS。
    /// </summary>
    private bool ShowGate(IReadOnlyList<TestRecord> history, string sn)
    {
        var allPass = true;
        var summary = new List<string>();
        var cards = new[] { CardGate1, CardGate2, CardGate3 };
        for (var i = 0; i < GateStations.Length; i++)
        {
            var stationNo = GateStations[i];
            var rec = history.FirstOrDefault(r => r.StationNo == stationNo);
            var state = rec is null
                ? CoilState.Untested
                : rec.Result == "PASS" ? CoilState.Passed : CoilState.Failed;
            var detail = rec is null
                ? "无记录"
                : $"{rec.Result} {rec.TestTime:MM-dd HH:mm}";
            cards[i].SetState(state, detail);
            summary.Add($"工位{stationNo} {(state == CoilState.Untested ? "未测" : rec!.Result)}");
            if (state != CoilState.Passed)
                allPass = false;
        }
        if (!allPass)
        {
            ListAbnormal.Items.Add($"数据库门禁未通过（SN {sn}）：{string.Join(" · ", summary)}");
            _log.Append($"门禁检查：{string.Join(" · ", summary)}");
        }
        return allPass;
    }

    private void ResetGateCards()
    {
        CardGate1.Reset();
        CardGate2.Reset();
        CardGate3.Reset();
    }

    /// <summary>版本比对卡：预设为空=未预设（未通过）；读取与预设忽略大小写/首尾空白比对。</summary>
    private static bool ShowVersionCard(Border card, TextBlock verdict, TextBlock read, TextBlock preset,
        string label, string? readValue, string presetValue)
    {
        read.Text = $"读取：{Show(readValue)}";
        preset.Text = $"预设：{Show(presetValue)}";

        bool ok;
        if (string.IsNullOrEmpty(presetValue))
        {
            verdict.Text = "未预设";
            verdict.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
            card.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
            ok = false;
        }
        else
        {
            ok = readValue is not null &&
                 string.Equals(readValue.Trim(), presetValue.Trim(), StringComparison.OrdinalIgnoreCase);
            verdict.Text = ok ? "匹配" : "不匹配";
            verdict.Foreground = ok
                ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
                : new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            card.Background = ok
                ? new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7))
                : new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
        }
        return ok;
    }

    private void ResetVersionCards()
    {
        foreach (var (card, verdict, read, preset) in new[]
                 {
                     (CardSw, TxtSwVerdict, TxtSwRead, TxtSwPreset),
                     (CardHw, TxtHwVerdict, TxtHwRead, TxtHwPreset),
                 })
        {
            card.Background = IdleBg;
            verdict.Text = "—";
            verdict.Foreground = MutedBrush;
            read.Text = "读取：—";
            preset.Text = "预设：—";
        }
    }

    private static string Show(string? s) => string.IsNullOrEmpty(s) ? "(空)" : s;

    /// <summary>切换下一台：数据展示/流程/日志复位，串口连接保持。</summary>
    public void ResetForNext()
    {
        _flowCompleted = false;
        FlowPanel.Reset();
        ListAbnormal.Items.Clear();
        TxtSn.Text = "—";
        TxtPrint.Text = "—";
        ResetGateCards();
        ResetVersionCards();
        TxtResult.Text = "待测试";
        TxtResult.Foreground = MutedBrush;
        BadgeResult.Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
        _log.Clear();
        _log.Append("已切换下一台，串口保持连接，等待开始测试");
        RaiseAvailability();
    }

    private void SetResultBadge(bool passed)
    {
        TxtResult.Text = passed ? "PASS" : "FAIL";
        TxtResult.Foreground = passed ? SuccessBrush : DangerBrush;
        BadgeResult.Background = passed ? PassBg : FailBg;
    }

    /// <summary>弹出运行日志窗（无模式，边测边看）；重复点击激活已开窗口。</summary>
    private void BtnLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logWin is not null && _logWin.IsLoaded)
        {
            _logWin.Activate();
            return;
        }
        _logWin = new LogWindow("运行日志 — 工位4", _log) { Owner = Window.GetWindow(this) };
        _logWin.Show();
    }
}
