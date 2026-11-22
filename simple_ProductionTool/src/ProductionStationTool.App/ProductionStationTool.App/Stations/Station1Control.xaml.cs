using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Controls;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Station;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位 1 页面（简化）：连接设备 → 开启无线充电 → PowerZ A/B 双线圈 → 停充并结束。
/// 无 SN 读取、无数据库关联/入库（简化版无数据库）。
/// 无有效采样 = 流程错误不判限；线圈判 FAIL 也在页面显示（不入库）。
/// </summary>
public partial class Station1Control : UserControl, IStationPage
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

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
            ("3.2", "B 线圈状态检测"),
            ("4", "停充并结束"));
        // 3 汇总行：只滚动倒计时提示，通过/失败由 3.1/3.2 承载——初始空白
        FlowPanel.Mark(2, StepStatus.None);

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

    private async void OnDisconnectRequested()
    {
        if (_can is not null && _connected)
        {
            try
            {
                var (ok, detail) = await _can.DisableChargingAsync();
                _log.Append(ok ? $"断开前停止充电：{detail}" : $"断开前停充失败：{detail}");
            }
            catch (Exception ex)
            {
                _log.Append($"断开前停充异常：{ex.Message}");
            }
        }
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
        ListAbnormal.Items.Clear();
        ShowCoilJudgement(null);
        _flowCompleted = false;
        _log.Append("开始工位 1 测试流程");

        // ① 连接设备：唤醒 + 等 UDS 就绪
        FlowPanel.Mark(0, StepStatus.Running);
        _log.Append("步骤 1：唤醒设备并等待 UDS 就绪…");
        if (!await _can.WaitReadyAsync(ct))
        {
            FlowPanel.Mark(0, StepStatus.Failed);
            ListAbnormal.Items.Add("步骤 1「连接设备」失败：设备无应答（未上电？）");
            SetResultBadge(false);
            _log.Append("设备 8s 内无应答——请检查供电后重试");
            _running = false;
            _flowCompleted = true;
            RaiseAvailability();
            return;
        }
        FlowPanel.Mark(0, StepStatus.Passed);
        _log.Append("步骤 1 完成");

        // ② 开启无线充电
        FlowPanel.Mark(1, StepStatus.Running);
        var (chargeOk, chargeDetail) = await _can.EnableChargingAsync(ct);
        _log.Append(chargeOk ? $"步骤 2：{chargeDetail}" : $"步骤 2：开启充电失败 {chargeDetail}");
        if (!chargeOk)
        {
            FlowPanel.Mark(1, StepStatus.Failed);
            FlowPanel.Mark(2, StepStatus.None);
            FlowPanel.Mark(3, StepStatus.Waiting, "未执行（前序失败）");
            FlowPanel.Mark(4, StepStatus.Waiting, "未执行（前序失败）");
            ListAbnormal.Items.Add("开启充电失败：请检查设备放置和充电功能后重试");
            SetResultBadge(false);
            _running = false;
            _flowCompleted = true;
            RaiseAvailability();
            return;
        }
        FlowPanel.Mark(1, StepStatus.Passed);

        // ③ PowerZ A/B 双线圈判定
        FlowPanel.Mark(2, StepStatus.Running);
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
            coil = await CoilCheck.RunAsync(powerZ, coilOpts, LogSafe, ct);
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
            FlowPanel.Mark(2, StepStatus.Failed, "无有效采样");
            FlowPanel.Mark(3, StepStatus.Waiting, "未执行（无有效采样）");
            FlowPanel.Mark(4, StepStatus.Waiting, "未执行（无有效采样）");
            ListAbnormal.Items.Add("PowerZ 无有效采样");
            await SafeStopChargingAsync(ct);
            SetResultBadge(false);
            _log.Append("步骤 4：未执行（无有效采样）");
            _running = false;
            _flowCompleted = true;
            RaiseAvailability();
            return;
        }

        ShowCoilJudgement(coil);
        FlowPanel.Mark(2, coil.IsNormal ? StepStatus.Passed : StepStatus.Failed,
            coil.IsNormal ? null : string.Join("；", coil.AbnormalItems));
        _log.Append($"步骤 3：A 均值 {coil.CoilACurrent:F3}A / B 均值 {coil.CoilBCurrent:F3}A" +
                    $" → {(coil.IsNormal ? "正常" : "异常：" + string.Join("；", coil.AbnormalItems))}");

        // ④ 停充并结束（无入库）
        FlowPanel.Mark(3, StepStatus.Running);
        var (offOk, offDetail) = await _can.DisableChargingAsync(ct);
        _log.Append(offOk ? $"步骤 4：停止充电（{offDetail}）" : $"步骤 4：停止充电失败 {offDetail}");
        if (!offOk)
            ListAbnormal.Items.Add("停止充电指令失败");
        foreach (var item in coil.AbnormalItems)
            ListAbnormal.Items.Add(item);
        FlowPanel.Mark(3, offOk ? StepStatus.Passed : StepStatus.Failed);

        var passed = coil.IsNormal && offOk;
        SetResultBadge(passed);
        _log.Append(passed ? "全部检测项正常，PASS" : $"存在 {ListAbnormal.Items.Count} 项异常，FAIL");
        _running = false;
        _flowCompleted = true;
        RaiseAvailability();
    }

    /// <summary>安全关充：流程中途出错时充电可能仍开着，补发停充。</summary>
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
