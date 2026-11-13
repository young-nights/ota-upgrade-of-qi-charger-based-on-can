using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Controls;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Data;
using ProductionStationTool.Core.Station;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位 3 页面：读取 SN（关联数据库）+ 充电下 A/B 双线圈复测 + 入库。
/// 流程：连接设备 → 读 SN 并关联数据库 → 开充电 → PowerZ A/B 判定（同工位1）→ 停充并入库。
/// 无有效采样不入库（PowerZ 流程错误）；线圈判 FAIL 也入库为 FAIL 记录。
/// </summary>
public partial class Station3Control : UserControl, IStationPage
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    private readonly ITestRecordStore _store = new SqliteTestRecordStore();
    private readonly LogBuffer _log = new();
    private LogWindow? _logWin;
    private readonly StationIdentity _identity;

    private CanUdsDevice? _can;
    private bool _connected;
    private bool _running;

    public int StationNo => 3;
    public string Title => "工位3 — SN复测 + 线圈A/B";

    public bool CanStartTest => _connected && !_running;

    public event Action? AvailabilityChanged;

    private void RaiseAvailability() => AvailabilityChanged?.Invoke();

    private void LogSafe(string msg) => _log.Append(msg);

    public Station3Control(StationIdentity identity)
    {
        InitializeComponent();
        _identity = identity;
        _log.Append($"本机身份：{identity.Code}（{identity.Label}，machine.json 可改）");
        FlowPanel.SetSteps(
            ("1", "连接设备（串口/CAN 自动检测）"),
            ("2", "读取 SN 并关联数据库"),
            ("3", "开启无线充电"),
            ("4", "PowerZ：A 线圈 + B 线圈检测"),
            ("4.1", "A 线圈状态检测"),
            ("4.2", "B 线圈状态检测"),
            ("5", "停充并入库"));

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
        TxtSn.Text = "—";
        TxtDbLink.Text = "—";
        _log.Append("开始工位 3 测试流程");

        // ① 连接设备：唤醒 + 等 UDS 就绪
        FlowPanel.Mark(0, StepStatus.Running);
        _log.Append("步骤 1：唤醒设备并等待 UDS 就绪…");
        if (!await _can.WaitReadyAsync(ct))
        {
            await FailEarly(0, "设备 8s 内无应答——请检查供电后重试");
            return;
        }
        FlowPanel.Mark(0, StepStatus.Passed);
        _log.Append("步骤 1 完成");

        // ② 读取 SN 并关联数据库
        FlowPanel.Mark(1, StepStatus.Running);
        _log.Append("步骤 2：读取设备 SN 码…");
        var sn = await _can.TryReadSnAsync(ct);
        if (string.IsNullOrEmpty(sn))
        {
            await FailEarly(1, "SN 读取失败（设备未写 SN？），无法关联数据库");
            return;
        }
        TxtSn.Text = sn;
        var history = _store.FindAllBySn(sn);
        TxtDbLink.Text = history.Count == 0
            ? "无历史记录（首次入库）"
            : string.Join(" · ", history.Select(r => $"工位{r.StationNo} {r.Result}"));
        _log.Append($"步骤 2：SN={sn}，数据库关联 {history.Count} 条记录" +
                    (history.Count > 0 ? $"（{TxtDbLink.Text}）" : ""));
        FlowPanel.Mark(1, StepStatus.Passed);

        // ③ 开启无线充电
        FlowPanel.Mark(2, StepStatus.Running);
        var (chargeOk, chargeDetail) = await _can.EnableChargingAsync(ct);
        _log.Append(chargeOk ? $"步骤 3：{chargeDetail}" : $"步骤 3：开启充电失败 {chargeDetail}");
        if (!chargeOk)
        {
            FlowPanel.Mark(2, StepStatus.Failed);
            FlowPanel.Mark(3, StepStatus.Waiting, "未执行（前序失败）");
            FlowPanel.Mark(4, StepStatus.Waiting, "未执行（前序失败）");
            FlowPanel.Mark(5, StepStatus.Waiting, "未执行（前序失败）");
            FlowPanel.Mark(6, StepStatus.Waiting, "未执行（前序失败）");
            SetResultBadge(false);
            _running = false;
            RaiseAvailability();
            return;
        }
        FlowPanel.Mark(2, StepStatus.Passed);

        // ④ PowerZ A/B 双线圈判定（同工位1）
        FlowPanel.Mark(3, StepStatus.Running);
        _log.Append("步骤 4：打开 PowerZ HID 采样…");
        CoilJudgement? coil = null;
        HidPowerZSource? powerZ = null;
        try
        {
            var coilOpts = MachineConfig.LoadCoilOptions();
            powerZ = new HidPowerZSource { SampleIntervalMs = coilOpts.SampleIntervalMs };
            powerZ.Log += LogSafe;
            try { await powerZ.StartAsync(ct); }
            catch (Exception ex) { _log.Append($"[PowerZ] 打开失败：{ex.Message}"); }
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
            FlowPanel.Mark(3, StepStatus.Failed, "无有效采样");
            FlowPanel.Mark(4, StepStatus.Waiting, "未执行（无有效采样）");
            FlowPanel.Mark(5, StepStatus.Waiting, "未执行（无有效采样）");
            FlowPanel.Mark(6, StepStatus.Waiting, "未执行（无有效采样），不入库");
            ListAbnormal.Items.Add("PowerZ 无有效采样");
            await SafeStopChargingAsync(ct);
            SetResultBadge(false);
            _log.Append("步骤 5：未执行（无有效采样），不入库");
            _running = false;
            RaiseAvailability();
            return;
        }

        ShowCoilJudgement(coil);
        FlowPanel.Mark(3, coil.IsNormal ? StepStatus.Passed : StepStatus.Failed,
            coil.IsNormal ? null : string.Join("；", coil.AbnormalItems));
        // 4.1 / 4.2 子步分别定格 A、B 判定（B 未测到 = 未执行）
        FlowPanel.Mark(4, coil.CoilAPassed ? StepStatus.Passed : StepStatus.Failed);
        if (!coil.CoilAPassed)
            FlowPanel.Mark(5, StepStatus.Waiting, "未执行（前序失败）");
        else
            FlowPanel.Mark(5, coil.CoilBPassed ? StepStatus.Passed : StepStatus.Failed);
        _log.Append($"步骤 4：A 均值 {coil.CoilACurrent:F3}A / B 均值 {coil.CoilBCurrent:F3}A" +
                    $" → {(coil.IsNormal ? "正常" : "异常：" + string.Join("；", coil.AbnormalItems))}");

        // ⑤ 停充并入库（线圈判 FAIL 也入库为 FAIL 记录）
        FlowPanel.Mark(6, StepStatus.Running);
        var (offOk, offDetail) = await _can.DisableChargingAsync(ct);
        _log.Append(offOk ? $"步骤 5：停止充电（{offDetail}）" : $"步骤 5：停止充电失败 {offDetail}");
        if (!offOk)
            ListAbnormal.Items.Add("停止充电指令失败");

        var passed = coil.IsNormal && offOk;
        _store.Add(new TestRecord
        {
            Sn = sn,
            StationNo = StationNo,
            MachineId = _identity.MachineId,
            Result = coil.IsNormal ? "PASS" : "FAIL",
            Voltage = coil.CoilAVoltage,
            Current = coil.CoilACurrent,
            AbnormalItems = string.Join("；", coil.AbnormalItems),
            TestTime = DateTime.Now,
        });
        foreach (var item in coil.AbnormalItems)
            ListAbnormal.Items.Add(item);
        FlowPanel.Mark(6, offOk ? StepStatus.Passed : StepStatus.Failed);
        _log.Append(coil.IsNormal
            ? "步骤 5：已入库（PASS）"
            : $"步骤 5：已入库（FAIL 记录：{string.Join("；", coil.AbnormalItems)}）");

        SetResultBadge(passed);
        _log.Append(passed ? "全部检测项正常，PASS" : $"存在 {ListAbnormal.Items.Count} 项异常，FAIL");
        _running = false;
        RaiseAvailability();
    }

    /// <summary>前序步骤失败：该步 FAIL，其后「未执行」，安全停充。</summary>
    private async Task FailEarly(int step, string reason)
    {
        FlowPanel.Mark(step, StepStatus.Failed, reason);
        for (var i = step + 1; i < 7; i++)
            FlowPanel.Mark(i, StepStatus.Waiting, "未执行（前序失败）");
        ListAbnormal.Items.Add($"步骤 {step + 1} 失败：{reason}");
        SetResultBadge(false);
        _log.Append(reason);
        await SafeStopChargingAsync();
        _running = false;
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
    /// <summary>
    /// PowerZ 检测实时进度：流程区显示阶段提示/倒计时；A 判定一出即点亮 A 卡片。
    /// </summary>
    private void OnCoilProgress(CoilProgress p)
    {
        FlowPanel.Mark(3, StepStatus.Running, p.Hint);   // 4 总步骤：阶段提示/倒计时
        if (p.CoilADecided)
        {
            CardCoilA.SetState(p.CoilAPassed ? CoilState.Passed : CoilState.Failed, p.CoilACurrent);
            // 4.1 A 定格；4.2 B 随即开始（A 未过则 B 不测）
            FlowPanel.Mark(4, p.CoilAPassed ? StepStatus.Passed : StepStatus.Failed);
            if (!p.CoilAPassed)
                FlowPanel.Mark(5, StepStatus.Waiting, "未执行（前序失败）");
            else
                FlowPanel.Mark(5, StepStatus.Running);
        }
    }

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
        FlowPanel.Reset();
        ListAbnormal.Items.Clear();
        TxtSn.Text = "—";
        TxtDbLink.Text = "—";
        ShowCoilJudgement(null);
        TxtResult.Text = "待测试";
        TxtResult.Foreground = MutedBrush;
        BadgeResult.Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
        _log.Clear();
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
        _logWin = new LogWindow("运行日志 — 工位3", _log) { Owner = Window.GetWindow(this) };
        _logWin.Show();
    }
}
