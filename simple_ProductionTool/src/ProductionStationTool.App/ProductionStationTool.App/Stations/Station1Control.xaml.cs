using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Controls;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Station;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位 1 页面（简化）：连接设备 → 开启无线充电 → PowerZ A/B 双线圈 → 直接结束。
/// 无 SN 读取、无数据库关联/入库；流程不含停充步骤（测完即结束）。
/// 无有效采样 = 流程错误不判限；线圈判 FAIL 也在页面显示（不入库）。
/// </summary>
public partial class Station1Control : UserControl, IStationPage
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    // 流程索引（与 SetSteps 一一对应，0 基）：
    // 0 连接 | 1 开充 | 2 PowerZ 汇总（滚动提示） | 3 A 线圈 | 4 B 线圈
    private const int IdxConnect = 0;
    private const int IdxCharge = 1;
    private const int IdxPowerZ = 2;
    private const int IdxCoilA = 3;
    private const int IdxCoilB = 4;

    private readonly LogBuffer _log = new();
    private LogWindow? _logWin;

    private CanUdsDevice? _can;
    private bool _connected;
    private bool _running;
    private bool _flowCompleted;

    public int StationNo => 1;
    public string Title => "工位1 — 充电线圈 A/B";

    public bool CanStartTest => _connected && !_running;

    /// <summary>完整跑通一次流程（PASS/FAIL 均算）后允许「下一个」。</summary>
    public bool CanGoNext => _flowCompleted && !_running;

    public event Action? AvailabilityChanged;

    private void RaiseAvailability() => AvailabilityChanged?.Invoke();

    private void LogSafe(string msg) => _log.Append(msg);

    public Station1Control(StationIdentity identity)
    {
        InitializeComponent();
        _log.Append($"本机身份：{identity.Code}（{identity.Label}，machine.json 可改）");
        var coil0 = MachineConfig.LoadCoilOptions();
        _log.Append($"已载入线圈参数（machine.json，重启自动沿用）：A {coil0.CoilALowerA:F2}~{coil0.CoilAUpperA:F2}A / B {coil0.CoilBLowerA:F2}~{coil0.CoilBUpperA:F2}A");
        FlowPanel.SetSteps(
            ("1", "连接设备（串口/CAN 自动检测）"),
            ("2", "开启无线充电"),
            ("3", "PowerZ："),
            ("3.1", "A 线圈状态检测"),
            ("3.2", "B 线圈状态检测"));
        // PowerZ 汇总行：只滚动倒计时/阶段提示，通过/失败由 3.1/3.2 承载
        FlowPanel.Mark(IdxPowerZ, StepStatus.None);

        ConnPanel.ConnectRequested += OnConnectRequested;
        ConnPanel.DisconnectRequested += OnDisconnectRequested;
    }

    private async void OnConnectRequested(string can, string pz)
    {
        try
        {
            if (!HidPowerZSource.TryProbe(out var pzDetail))
            {
                _can?.Dispose();
                _can = null;
                _connected = false;
                ConnPanel.SetStatus($"PowerZ 不可用：{pzDetail}", false);
                _log.Append($"PowerZ 校验失败：{pzDetail}");
                _log.Append("提示：若设备已 usbipd 挂载到 WSL，请先 usbipd detach 后重试");
                RaiseAvailability();
                return;
            }

            _can?.Dispose();
            _can = new CanUdsDevice(can);
            _can.Log += LogSafe;
            await _can.OpenAsync();
            _connected = true;
            ConnPanel.SetStatus($"已连接 CAN:{can} PowerZ:{pz}", true);
            _log.Append($"串口连接：CAN={can}，PowerZ={pz}（{pzDetail}）");
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

    /// <summary>
    /// 断开：直接释放串口，不等待停充 UDS（原先同步 DisableCharging 会卡数秒）。
    /// 充电若仍开着，由设备侧超时/下次开充覆盖；产线「断开」要求即时响应。
    /// </summary>
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
            _log.Append("串口未接入（CAN/PowerZ），无法开始测试");
            return;
        }

        _running = true;
        RaiseAvailability();
        FlowPanel.Reset();
        FlowPanel.Mark(IdxPowerZ, StepStatus.None);
        ListAbnormal.Items.Clear();
        ShowCoilJudgement(null);
        _flowCompleted = false;
        _log.Append("开始工位 1 测试流程");

        // ① 连接设备：唤醒 + 等 UDS 就绪
        FlowPanel.Mark(IdxConnect, StepStatus.Running);
        _log.Append("步骤 1：唤醒设备并等待 UDS 就绪…");
        if (!await _can.WaitReadyAsync(ct))
        {
            FlowPanel.Mark(IdxConnect, StepStatus.Failed);
            MarkRestWaiting("未执行（前序失败）");
            ListAbnormal.Items.Add("步骤 1「连接设备」失败：设备无应答（未上电？）");
            SetResultBadge(false);
            _log.Append("设备 8s 内无应答——请检查供电后重试");
            _running = false;
            _flowCompleted = true;
            RaiseAvailability();
            return;
        }
        FlowPanel.Mark(IdxConnect, StepStatus.Passed);
        _log.Append("步骤 1 完成");

        // ② 开启无线充电
        FlowPanel.Mark(IdxCharge, StepStatus.Running);
        var (chargeOk, chargeDetail) = await _can.EnableChargingAsync(ct);
        _log.Append(chargeOk ? $"步骤 2：{chargeDetail}" : $"步骤 2：开启充电失败 {chargeDetail}");
        if (!chargeOk)
        {
            FlowPanel.Mark(IdxCharge, StepStatus.Failed);
            MarkRestWaiting("未执行（前序失败）");
            ListAbnormal.Items.Add("开启充电失败：请检查设备放置和充电功能后重试");
            SetResultBadge(false);
            _running = false;
            _flowCompleted = true;
            RaiseAvailability();
            return;
        }
        FlowPanel.Mark(IdxCharge, StepStatus.Passed);

        // ③ PowerZ A/B 双线圈（onProgress → 流程区滚动倒计时/阶段提示）
        FlowPanel.Mark(IdxPowerZ, StepStatus.Running);
        _log.Append("步骤 3：打开 PowerZ HID 采样…");
        CoilJudgement? coil = null;
        HidPowerZSource? powerZ = null;
        try
        {
            var coilOpts = MachineConfig.LoadCoilOptions();
            powerZ = new HidPowerZSource { SampleIntervalMs = coilOpts.SampleIntervalMs };
            powerZ.Log += LogSafe;
            try
            {
                await powerZ.StartAsync(ct);
            }
            catch (Exception ex)
            {
                _log.Append($"[PowerZ] 打开失败：{ex.Message}");
            }
            coil = await CoilCheck.RunAsync(powerZ, coilOpts, LogSafe, ct, OnCoilProgress);
        }
        finally
        {
            if (powerZ is not null)
            {
                try { await powerZ.StopAsync(ct); } catch { /* 忽略 */ }
                powerZ.Dispose();
            }
        }

        if (coil is null || coil.NoValidSample)
        {
            ShowCoilJudgement(null);
            FlowPanel.Mark(IdxPowerZ, StepStatus.Failed, "无有效采样");
            FlowPanel.Mark(IdxCoilA, StepStatus.Failed);
            FlowPanel.Mark(IdxCoilB, StepStatus.Waiting, "未执行（无有效采样）");
            ListAbnormal.Items.Add("PowerZ 无有效采样");
            await SafeStopChargingAsync(ct);
            SetResultBadge(false);
            _log.Append("无有效采样，已安全停充并结束");
            _running = false;
            _flowCompleted = true;
            RaiseAvailability();
            return;
        }

        // 定格 A/B 判定；PowerZ 汇总行回空白（提示已由 onProgress 滚过）
        ShowCoilJudgement(coil);
        FlowPanel.Mark(IdxPowerZ, StepStatus.None);
        FlowPanel.Mark(IdxCoilA, coil.CoilAPassed ? StepStatus.Passed : StepStatus.Failed);
        if (!coil.CoilAPassed)
            FlowPanel.Mark(IdxCoilB, StepStatus.Waiting, "未执行（前序失败）");
        else
            FlowPanel.Mark(IdxCoilB, coil.CoilBPassed ? StepStatus.Passed : StepStatus.Failed);

        _log.Append($"步骤 3：A 均值 {coil.CoilACurrent:F3}A / B 均值 {coil.CoilBCurrent:F3}A" +
                    $" → {(coil.IsNormal ? "正常" : "异常：" + string.Join("；", coil.AbnormalItems))}");
        foreach (var item in coil.AbnormalItems)
            ListAbnormal.Items.Add(item);

        // 直接结束：流程不含停充步骤
        var passed = coil.IsNormal;
        SetResultBadge(passed);
        _log.Append(passed ? "全部检测项正常，PASS（未发停充，流程直接结束）"
                           : $"存在 {ListAbnormal.Items.Count} 项异常，FAIL（未发停充）");
        _running = false;
        _flowCompleted = true;
        RaiseAvailability();
    }

    /// <summary>前序失败时把 PowerZ/A/B 标成未执行。</summary>
    private void MarkRestWaiting(string text)
    {
        FlowPanel.Mark(IdxPowerZ, StepStatus.Waiting, text);
        FlowPanel.Mark(IdxCoilA, StepStatus.Waiting, text);
        FlowPanel.Mark(IdxCoilB, StepStatus.Waiting, text);
    }

    /// <summary>
    /// PowerZ 检测实时进度：流程区「PowerZ：」行滚动阶段提示/倒计时；
    /// A 判定一出即点亮 A 卡片并定格 3.1，3.2 随之进行中/未执行。
    /// </summary>
    private void OnCoilProgress(CoilProgress p)
    {
        FlowPanel.Mark(IdxPowerZ, StepStatus.Running, p.Hint);
        if (p.CoilADecided)
        {
            CardCoilA.SetState(p.CoilAPassed ? CoilState.Passed : CoilState.Failed, p.CoilACurrent);
            FlowPanel.Mark(IdxCoilA, p.CoilAPassed ? StepStatus.Passed : StepStatus.Failed);
            if (!p.CoilAPassed)
                FlowPanel.Mark(IdxCoilB, StepStatus.Waiting, "未执行（前序失败）");
            else
                FlowPanel.Mark(IdxCoilB, StepStatus.Running);
        }
    }

    /// <summary>安全关充：无有效采样等中途出错时充电可能仍开着，补发停充。</summary>
    private async Task SafeStopChargingAsync(CancellationToken ct = default)
    {
        if (_can is null)
            return;
        try
        {
            var (ok, detail) = await _can.DisableChargingAsync(ct);
            _log.Append(ok ? $"安全停充：{detail}" : $"安全停充失败：{detail}");
        }
        catch (Exception ex)
        {
            _log.Append($"安全停充异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 线圈 A/B 两卡片三态：未测灰 / 通过绿 / 未通过红。
    /// B 未执行到（无有效采样、A 未通过提前结束）保持未测。
    /// </summary>
    private void ShowCoilJudgement(CoilJudgement? c)
    {
        if (c is null || c.NoValidSample)
        {
            CardCoilA.Reset();
            CardCoilB.Reset();
            return;
        }
        CardCoilA.SetState(c.CoilAPassed ? CoilState.Passed : CoilState.Failed, c.CoilACurrent);
        if (!c.CoilAPassed)
            CardCoilB.Reset();
        else
            CardCoilB.SetState(c.CoilBPassed ? CoilState.Passed : CoilState.Failed, c.CoilBCurrent);
    }

    /// <summary>切换下一台：数据展示/流程/日志复位，串口连接保持。</summary>
    public void ResetForNext()
    {
        _flowCompleted = false;
        FlowPanel.Reset();
        FlowPanel.Mark(IdxPowerZ, StepStatus.None);
        ListAbnormal.Items.Clear();
        ShowCoilJudgement(null);
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
        BadgeResult.Background = passed
            ? new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7))
            : new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
    }

    /// <summary>弹出运行日志窗（无模式，边测边看）；重复点击激活已开窗口。</summary>
    private void BtnLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logWin is not null && _logWin.IsLoaded)
        {
            _logWin.Activate();
            return;
        }
        _logWin = new LogWindow("运行日志 — 工位1", _log) { Owner = Window.GetWindow(this) };
        _logWin.Show();
    }
}
