using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProductionStationTool.App.Controls;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Sn;
using ProductionStationTool.Core.Station;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位 3 页面：扫码枪扫描 SN → 结构自动判定 → 写入设备 + 回读 →
/// 开充电 → PowerZ A/B 双线圈 → 停充并结束（原先工位1 的扫码逻辑，简化版无数据库不入库）。
/// 扫码超时/SN 判定/整体超时在「设置 → 检测参数 → 扫码参数」维护。
/// </summary>
public partial class Station3Control : UserControl, IStationPage
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    /// <summary>步骤检测参数统一在「设置 → 检测参数」页维护（machine.json），测试开始时读取。</summary>
    private CoilTestOptions BuildCoilOptions() => MachineConfig.LoadCoilOptions();

    /// <summary>运行日志缓冲：主页面不占位，弹出日志窗（LogWindow）实时刷新。</summary>
    private readonly LogBuffer _log = new();
    private LogWindow? _logWin;
    private readonly Station1Workflow _workflow = new();

    // ---- 扫码枪输入状态（原先工位1 逻辑）----
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TaskCompletionSource<string>? _scanTcs;
    private int _scanTimeoutSeconds = 10;
    private int _judgeTimeoutSeconds = 2;
    private int _overallTimeoutSeconds = 15;
    private long _scanDeadlineMs;
    private long _judgeDeadlineMs;
    private long _overallDeadlineMs;
    private bool _waitingForScan;

    private CanUdsDevice? _can;

    /// <summary>CAN 串口已接入（点「连接」成功、未点「断开」）。</summary>
    private bool _connected;

    /// <summary>测试流程正在执行。</summary>
    private bool _running;

    public int StationNo => 3;
    public string Title => "工位3 — 扫码写 SN + 线圈A/B";

    /// <summary>串口未接入时禁止开始测试。</summary>
    public bool CanStartTest => _connected && !_running;

    public event Action? AvailabilityChanged;

    private void RaiseAvailability() => AvailabilityChanged?.Invoke();

    private void LogSafe(string msg)
        => _log.Append(msg);

    public Station3Control(StationIdentity identity)
    {
        InitializeComponent();
        _scanTimer.Tick += (_, _) => OnScanCountdownTick();
        _log.Append($"本机身份：{identity.Code}（{identity.Label}，machine.json 可改）");
        var coil0 = MachineConfig.LoadCoilOptions();
        _log.Append($"已载入线圈参数（machine.json，重启自动沿用）：A {coil0.CoilALowerA:F2}~{coil0.CoilAUpperA:F2}A / B {coil0.CoilBLowerA:F2}~{coil0.CoilBUpperA:F2}A");
        _log.Append("SN 来源：扫码枪输入，本机校验结构后写入设备");
        FlowPanel.SetSteps(
            ("1", "连接设备（串口/CAN 自动检测）"),
            ("2", "扫码并写入 SN（校验/读回）"),
            ("3", "开启无线充电"),
            ("4", "PowerZ："),
            ("4.1", "A 线圈状态检测"),
            ("4.2", "B 线圈状态检测"),
            ("5", "停充并结束"));
        // 4 汇总行：只滚动倒计时提示，通过/失败由 4.1/4.2 承载——初始空白
        FlowPanel.Mark(3, StepStatus.None);

        ConnPanel.ConnectRequested += OnConnectRequested;
        ConnPanel.DisconnectRequested += OnDisconnectRequested;
    }

    private async void OnConnectRequested(string can, string pz)
    {
        // 「连接」只做串口接入，不发任何 UDS 指令（唤醒/读版本归「开始测试」）
        try
        {
            // PowerZ 可达性先验：HID 被 usbipd 挂到 WSL/被占用时打开失败，
            // 不能只开 CAN 就报「已连接」（设备残留时会误判成功）
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
        // 断开前发停止充电（2E 21 01 00）——充电开着则设备不休眠，命令可达
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

    // ================= 扫码枪输入（原先工位1 逻辑） =================

    /// <summary>等待扫码枪输入；每次文本变化重新计时，回车或倒计时结束提交当前内容。</summary>
    private async Task<string?> WaitForScanAsync(CancellationToken ct)
    {
        TxtSn.Clear();
        TxtSn.IsReadOnly = false;
        TxtSn.Focus();
        TxtScanHint.Text = "等待扫描…";
        TxtScanHint.Foreground = MutedBrush;
        _scanTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timing = MachineConfig.LoadScanTiming();
        _scanTimeoutSeconds = timing.ScanSeconds;
        _judgeTimeoutSeconds = timing.JudgeSeconds;
        _overallTimeoutSeconds = timing.OverallSeconds;
        _waitingForScan = true;
        StartScanDeadlines();

        using var reg = ct.Register(() => Dispatcher.Invoke(() => CompleteScan("")));
        try
        {
            return await _scanTcs.Task;
        }
        finally
        {
            _waitingForScan = false;
            _scanTimer.Stop();
            TxtSn.IsReadOnly = true;
        }
    }

    private void StartScanDeadlines()
    {
        long now = Environment.TickCount64;
        _scanDeadlineMs = now + _scanTimeoutSeconds * 1000L;
        _judgeDeadlineMs = 0;
        _overallDeadlineMs = now + _overallTimeoutSeconds * 1000L;
        UpdateScanCountdownText(now);
        _scanTimer.Stop();
        _scanTimer.Start();
    }

    private void OnScanCountdownTick()
    {
        if (!_waitingForScan)
        {
            _scanTimer.Stop();
            return;
        }
        long now = Environment.TickCount64;
        string current = TxtSn.Text.Trim();
        if (now >= _overallDeadlineMs)
        {
            TxtScanCountdown.Text = "整体超时：按当前输入判定";
            CompleteScan(current);
            return;
        }
        if (current.Length == 0 && now >= _scanDeadlineMs)
        {
            TxtScanCountdown.Text = "扫码超时：未输入 SN";
            CompleteScan("");
            return;
        }
        if (current.Length > 0 && _judgeDeadlineMs > 0 && now >= _judgeDeadlineMs)
        {
            TxtScanCountdown.Text = "SN 判定超时：校验当前输入";
            CompleteScan(current);
            return;
        }
        UpdateScanCountdownText(now);
    }

    private void UpdateScanCountdownText(long now)
    {
        long scanLeft = Math.Max(0, (_scanDeadlineMs - now + 999) / 1000);
        long judgeLeft = _judgeDeadlineMs > 0
            ? Math.Max(0, (_judgeDeadlineMs - now + 999) / 1000)
            : -1;
        long overallLeft = Math.Max(0, (_overallDeadlineMs - now + 999) / 1000);
        string scanText = TxtSn.Text.Trim().Length == 0 ? $"扫码 {scanLeft}s" : "扫码已输入";
        string judgeText = judgeLeft >= 0 ? $"SN判定 {judgeLeft}s" : "SN判定 --";
        TxtScanCountdown.Text = $"{scanText} · {judgeText} · 整体 {overallLeft}s";
    }

    private void CompleteScan(string sn)
    {
        if (!_waitingForScan)
            return;
        _waitingForScan = false;
        _scanTimer.Stop();
        TxtSn.IsReadOnly = true;
        _scanTcs?.TrySetResult(sn);
    }

    private void OnScanTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_waitingForScan)
            return;
        _judgeDeadlineMs = TxtSn.Text.Trim().Length > 0
            ? Environment.TickCount64 + _judgeTimeoutSeconds * 1000L
            : 0;
        UpdateScanCountdownText(Environment.TickCount64);
    }

    private void OnScanKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_waitingForScan)
            return;
        e.Handled = true;
        CompleteScan(TxtSn.Text.Trim());
    }

    /// <summary>扫码门禁失败：立即停止本次测试，后续设备步骤全部不执行。</summary>
    private void FailScanGate(string flowReason, string hint, string logMessage)
    {
        FlowPanel.Mark(1, StepStatus.Failed, flowReason);
        for (int i = 2; i <= 6; i++)
            FlowPanel.Mark(i, StepStatus.Waiting, "未执行（SN校验未通过）");
        ListAbnormal.Items.Add(hint);
        TxtScanHint.Text = hint;
        TxtScanHint.Foreground = DangerBrush;
        SetResultBadge(false);
        _log.Append(logMessage);
        _running = false;
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

        // 「下一个」并入「开始测试」：开测即复位上一台显示，连续测试免手点
        ResetForNext();
        FlowPanel.Mark(3, StepStatus.None);   // PowerZ 汇总行回空白
        _log.Append("开始工位 3 测试流程");

        // ① 连接设备：先唤醒 + 等 UDS 就绪（设备空闲 10s 会真休眠/整板断电，
        //    首帧请求会丢在重新供电的冷启动窗口里——必须先唤醒探活）
        FlowPanel.Mark(0, StepStatus.Running);
        _log.Append("步骤 1：唤醒设备并等待 UDS 就绪…");
        var ready = await _can.WaitReadyAsync(ct);
        if (!ready)
        {
            FlowPanel.Mark(0, StepStatus.Failed);
            ListAbnormal.Items.Add("设备没有响应：请检查设备是否上电、CAN 线是否接好");
            SetResultBadge(false);
            _log.Append("设备 8s 内无应答——请检查供电和 CAN 连接后重试");
            _running = false;
            RaiseAvailability();
            return;
        }
        FlowPanel.Mark(0, StepStatus.Passed);
        _log.Append("步骤 1 完成");

        // ② 扫码：等待扫码枪输入，倒计时结束校验 SN 结构
        FlowPanel.Mark(1, StepStatus.Running);
        var scannedSn = await WaitForScanAsync(ct);
        if (string.IsNullOrWhiteSpace(scannedSn))
        {
            FailScanGate("扫码超时/未输入", "扫码超时：未输入 SN",
                "步骤 2：扫码/SN判定超时，SN 为空；后续流程不执行");
            return;
        }
        if (!SnGenerator.TryParse(scannedSn, out _))
        {
            FailScanGate("SN 结构不符合",
                "SN 结构不符合：LSCH42JY + 两位数字硬件版本 + YYMMDD + 0001~9999",
                $"步骤 2：SN 结构不符合：{scannedSn}；后续流程不执行");
            return;
        }
        TxtSn.Text = scannedSn;
        TxtScanHint.Text = "扫码通过，正在写入设备并读回验证…";
        TxtScanHint.Foreground = SuccessBrush;
        TxtScanCountdown.Text = "";
        _log.Append($"[扫码] SN = {scannedSn}，结构校验通过");

        // ③ 写入 + 回读 + 开充电 + 线圈检测（编排在 workflow；无数据库不入库）
        HidPowerZSource? powerZ = null;
        bool writeOk = false, readOk = false, chargeOk = false;
        string? writtenSn = null;
        Station1Result? result = null;

        try
        {
            var coilOpts = BuildCoilOptions();
            powerZ = new HidPowerZSource { SampleIntervalMs = coilOpts.SampleIntervalMs };
            powerZ.Log += LogSafe;

            // 严格顺序执行：每步完成即定格（Passed/Failed），下一步才亮「进行中」。
            // PowerZ HID 在开充电完成时才打开，避免日志时序错乱。
            result = await _workflow.RunAsync(
                scannedSn,
                snWriteAsync: async sn =>
                {
                    writtenSn = sn;
                    TxtSn.Text = sn;
                    _log.Append($"扫码 SN：{sn}");

                    var (ok, detail) = await _can.WriteSnAsync(sn, ct);
                    _log.Append(ok ? $"步骤 2：写 SN 成功 {detail}" : $"步骤 2：写 SN 失败 {detail}");
                    writeOk = ok;
                    if (!ok)
                    {
                        FlowPanel.Mark(1, StepStatus.Failed, "SN 写入失败");
                        TxtScanHint.Text = "SN 写入设备失败";
                        TxtScanHint.Foreground = DangerBrush;
                    }
                    return ok;
                },
                snReadAsync: async () =>
                {
                    var back = await _can.TryReadSnAsync(ct);
                    readOk = back is not null && string.Equals(back, writtenSn, StringComparison.Ordinal);
                    _log.Append($"步骤 2：回读 SN = {back ?? "(空)"}");
                    FlowPanel.Mark(1, readOk ? StepStatus.Passed : StepStatus.Failed,
                        readOk ? "" : "SN 回读不一致");
                    TxtScanHint.Text = readOk ? "扫码、写入、回读均通过" : "SN 回读不一致";
                    TxtScanHint.Foreground = readOk ? SuccessBrush : DangerBrush;
                    return back;
                },
                chargeStartAsync: async () =>
                {
                    FlowPanel.Mark(2, StepStatus.Running);     // 开充电：开始
                    var (ok, detail) = await _can.EnableChargingAsync(ct);
                    _log.Append(ok ? $"步骤 3：{detail}" : $"步骤 3：开启充电失败 {detail}");
                    chargeOk = ok;
                    FlowPanel.Mark(2, ok ? StepStatus.Passed : StepStatus.Failed);      // 开充电完成定格
                    if (!ok)
                        throw new InvalidOperationException("开启充电失败，请检查设备放置和充电功能后重试");
                    FlowPanel.Mark(3, StepStatus.Running);     // 采样：开始（充电完成才到）
                    _log.Append("步骤 4：打开 PowerZ HID 采样…");
                    try
                    {
                        await powerZ!.StartAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        // HID 打开失败不抛：交给采样超时 →「无有效采样」定格
                        _log.Append($"[PowerZ] 打开失败：{ex.Message}");
                    }
                },
                powerZ: powerZ,
                coils: coilOpts,
                log: LogSafe,
                ct,
                onProgress: OnCoilProgress);
        }
        catch (Exception ex)
        {
            _log.Append($"流程异常：{ex.Message}");
            ListAbnormal.Items.Add($"测试过程中出错：{ex.Message}");
        }
        finally
        {
            if (powerZ is not null)
            {
                try { await powerZ.StopAsync(ct); } catch { /* 忽略 */ }
                powerZ.Dispose();
            }
        }

        // ---- 定格 + 未执行步骤补齐（fail-fast：任一步出错，其后「未执行」）----
        bool snOk = writeOk && readOk;

        if (snOk && chargeOk)
        {
            if (result?.Coil is { NoValidSample: false } c)
            {
                ShowCoilJudgement(c);

                // PowerZ 汇总行回空白；通过/失败由 4.1/4.2 承载
                FlowPanel.Mark(3, StepStatus.None);
                FlowPanel.Mark(4, c.CoilAPassed ? StepStatus.Passed : StepStatus.Failed);
                if (!c.CoilAPassed)
                    FlowPanel.Mark(5, StepStatus.Waiting, "未执行（前序失败）");
                else
                    FlowPanel.Mark(5, c.CoilBPassed ? StepStatus.Passed : StepStatus.Failed);
                _log.Append($"步骤 4：A 均值 {c.CoilACurrent:F3}A / B 均值 {c.CoilBCurrent:F3}A" +
                                $" → {(c.IsNormal ? "正常" : "异常：" + string.Join("；", c.AbnormalItems))}");

                // ⑤ 停充并结束（无入库）
                FlowPanel.Mark(6, StepStatus.Running);
                var (offOk, offDetail) = await _can.DisableChargingAsync(ct);
                _log.Append(offOk ? $"步骤 5：停止充电（{offDetail}）" : $"步骤 5：停止充电失败 {offDetail}");
                if (!offOk)
                    ListAbnormal.Items.Add("关闭充电失败：请检查设备连接后重试");
                FlowPanel.Mark(6, offOk ? StepStatus.Passed : StepStatus.Failed);
                _log.Append("步骤 5：停充结束（简化版无数据库，不入库）");
            }
            else
            {
                ShowCoilJudgement(result?.Coil);
                FlowPanel.Mark(3, StepStatus.None);
                FlowPanel.Mark(4, StepStatus.Failed, "无有效采样");
                FlowPanel.Mark(5, StepStatus.Waiting, "未执行（前序失败）");
                FlowPanel.Mark(6, StepStatus.Waiting, "未执行（无有效采样）");
                _log.Append("步骤 5：未执行（无有效采样）");
            }
        }
        else
        {
            // 前序失败：未到达的步骤全部标未执行（已定格 FAIL 的步骤不覆盖）
            if (!snOk)
                FlowPanel.Mark(2, StepStatus.Waiting, "未执行（前序失败）");
            ShowCoilJudgement(null);
            FlowPanel.Mark(3, StepStatus.Waiting, "未执行（前序失败）");
            FlowPanel.Mark(4, StepStatus.Waiting, "未执行（前序失败）");
            FlowPanel.Mark(5, StepStatus.Waiting, "未执行（前序失败）");
            FlowPanel.Mark(6, StepStatus.Waiting, "未执行（前序失败）");
            _log.Append("步骤 5：未执行（前序失败）");
        }

        // 安全关充：流程中途出错（无采样/前序失败）时充电可能仍开着，补发停充
        if (chargeOk && (result?.Coil is null || result.Coil.NoValidSample))
        {
            var (offOk, offDetail) = await _can.DisableChargingAsync(ct);
            _log.Append(offOk ? $"安全停充：{offDetail}" : $"安全停充失败：{offDetail}");
        }

        if (result is not null)
            foreach (var item in result.AbnormalItems)
                ListAbnormal.Items.Add(item);

        SetResultBadge(result?.Passed == true);
        _log.Append(result?.Passed == true
            ? "全部检测项正常，PASS"
            : $"存在 {ListAbnormal.Items.Count} 项异常，FAIL");

        _running = false;
        RaiseAvailability();
    }

    /// <summary>切换下一台：数据展示区/流程/日志复位，串口连接保持。</summary>
    public void ResetForNext()
    {
        FlowPanel.Reset();
        ListAbnormal.Items.Clear();
        TxtSn.Text = "";
        TxtScanHint.Text = "点击「开始测试」后等待扫描";
        TxtScanHint.Foreground = MutedBrush;
        TxtScanCountdown.Text = "";
        ShowCoilJudgement(null);
        TxtResult.Text = "待测试";
        TxtResult.Foreground = MutedBrush;
        BadgeResult.Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
        _log.Clear();
        RaiseAvailability();
    }

    /// <summary>
    /// PowerZ 检测实时进度：流程区显示阶段提示/倒计时；A 判定一出即点亮 A 卡片，
    /// 测试员见「通过」即可换接 B 线圈。
    /// </summary>
    private void OnCoilProgress(CoilProgress p)
    {
        FlowPanel.Mark(3, StepStatus.Running, p.Hint);   // PowerZ 汇总行：阶段提示/倒计时
        if (p.CoilADecided)
        {
            CardCoilA.SetState(p.CoilAPassed ? CoilState.Passed : CoilState.Failed, p.CoilACurrent);
            FlowPanel.Mark(4, p.CoilAPassed ? StepStatus.Passed : StepStatus.Failed);
            if (!p.CoilAPassed)
                FlowPanel.Mark(5, StepStatus.Waiting, "未执行（前序失败）");
            else
                FlowPanel.Mark(5, StepStatus.Running);
        }
    }

    /// <summary>
    /// 线圈 A/B 两卡片三态：未测灰 / 通过绿 / 未通过红。
    /// B 未执行到（无有效采样、A 未通过提前结束）保持未测；
    /// A 通过后 B 超时/未达标 = B 未通过。
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
