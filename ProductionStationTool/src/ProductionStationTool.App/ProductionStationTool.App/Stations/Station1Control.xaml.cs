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
/// 工位 1 页面：SN 生成/写入/回读 + PowerZ 电流电压检测（构想文档）。
/// 四分区布局：串口接入 / 测试流程 / 数据展示 / 运行日志。
/// CAN/UDS 经 CanUdsDevice（复用 QiChargerStationTool.Core 协议栈），
/// 流程编排用 Station1Workflow（SN 写入失败不入库、不消耗流水号）。
/// </summary>
public partial class Station1Control : UserControl, IStationPage
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    /// <summary>步骤 5 检测参数统一在「设置 → 检测参数」页维护（machine.json），测试开始时读取。</summary>
    private CoilTestOptions BuildCoilOptions() => MachineConfig.LoadCoilOptions();

    private readonly ITestRecordStore _store = new SqliteTestRecordStore();
    /// <summary>运行日志缓冲：主页面不占位，弹出日志窗（LogWindow）实时刷新。</summary>
    private readonly LogBuffer _log = new();
    private LogWindow? _logWin;
    private readonly Station1Workflow _workflow;

    private CanUdsDevice? _can;

    /// <summary>CAN 串口已接入（点「连接」成功、未点「断开」）。</summary>
    private bool _connected;

    /// <summary>测试流程正在执行。</summary>
    private bool _running;

    /// <summary>本台产品已完整跑通一次流程（PASS/FAIL 均算跑通）。</summary>
    private bool _flowCompleted;

    public int StationNo => 1;
    public string Title => "工位1 — SN + 电流电压";

    /// <summary>串口未接入时禁止开始测试。</summary>
    public bool CanStartTest => _connected && !_running;

    /// <summary>未跑完或正在跑时禁止「下一个」；完整跑通后允许。</summary>
    public bool CanGoNext => _flowCompleted && !_running;

    public event Action? AvailabilityChanged;

    private void RaiseAvailability() => AvailabilityChanged?.Invoke();

    private void LogSafe(string msg)
        => _log.Append(msg);

    public Station1Control(StationIdentity identity)
    {
        InitializeComponent();
        _workflow = new Station1Workflow(_store, identity: identity);
        _log.Append($"本机身份：{identity.Code}（{identity.Label}，machine.json 可改）");
        FlowPanel.SetSteps(
            ("1", "连接设备（串口/CAN 自动检测）"),
            ("2", "生成 SN 码"),
            ("3", "写入 SN 并回读验证"),
            ("4", "开启无线充电"),
            ("5", "PowerZ：A 线圈 + B 线圈检测"),
            ("5.1", "A 线圈状态检测"),
            ("5.2", "B 线圈状态检测"),
            ("6", "停充并入库"));

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
        _log.Append("开始工位 1 测试流程");

        // ① 连接设备：先唤醒 + 等 UDS 就绪（设备空闲 10s 会真休眠/整板断电，
        //    首帧请求会丢在重新供电的冷启动窗口里——必须先唤醒探活），
        //    然后读全套版本，再往下走
        FlowPanel.Mark(0, StepStatus.Running);
        _log.Append("步骤 1：唤醒设备并等待 UDS 就绪…");
        var ready = await _can.WaitReadyAsync(ct);
        if (!ready)
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
        _log.Append("设备已就绪，读取版本…");
        var vs = await _can.TryReadAllVersionsAsync();
        _log.Append("设备版本：");
        _log.Append($"  APP  = {vs.Mcu1App}");
        _log.Append($"  BL   = {vs.Bootloader}");
        _log.Append($"  HW   = {vs.Hardware}");
        _log.Append($"  Qi   = {vs.Mcu2Qi}");
        if (vs.Errors.Count > 0)
            _log.Append("版本读取告警：" + string.Join("；", vs.Errors));
        FlowPanel.Mark(0, StepStatus.Passed);
        _log.Append("步骤 1 完成");

        // ⑤ 的数据源：HID 先静默就绪，等流程走到采样步再点亮「进行中」
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
            // PowerZ HID 在⑤真正开始（④开充电完成）时才打开，避免日志时序错乱。
            result = await _workflow.RunAsync(
                snWriteAsync: async sn =>
                {
                    writtenSn = sn;
                    TxtSn.Text = sn;
                    _log.Append($"生成 SN：{sn}");
                    FlowPanel.Mark(1, StepStatus.Passed);      // ② 生成 SN：完成

                    FlowPanel.Mark(2, StepStatus.Running);     // ③ 写入并回读：开始
                    var (ok, detail) = await _can.WriteSnAsync(sn, ct);
                    _log.Append(ok ? $"步骤 3：写 SN 成功 {detail}" : $"步骤 3：写 SN 失败 {detail}");
                    writeOk = ok;
                    if (!ok)
                        FlowPanel.Mark(2, StepStatus.Failed);  // 写失败即定格，不再回读
                    return ok;
                },
                snReadAsync: async () =>
                {
                    var back = await _can.TryReadSnAsync(ct);
                    readOk = back is not null && string.Equals(back, writtenSn, StringComparison.Ordinal);
                    _log.Append($"步骤 3：回读 SN = {back ?? "(空)"}");
                    FlowPanel.Mark(2, readOk ? StepStatus.Passed : StepStatus.Failed);  // ③ 完成定格
                    return back;
                },
                chargeStartAsync: async () =>
                {
                    FlowPanel.Mark(3, StepStatus.Running);     // ④ 开充电：开始
                    var (ok, detail) = await _can.EnableChargingAsync(ct);
                    _log.Append(ok ? $"步骤 4：{detail}" : $"步骤 4：开启充电失败 {detail}");
                    chargeOk = ok;
                    FlowPanel.Mark(3, ok ? StepStatus.Passed : StepStatus.Failed);      // ④ 完成定格
                    if (!ok)
                        throw new InvalidOperationException(detail);
                    FlowPanel.Mark(4, StepStatus.Running);     // ⑤ 采样：开始（充电完成才到）
                    _log.Append("步骤 5：打开 PowerZ HID 采样…");
                    try
                    {
                        await powerZ!.StartAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        // HID 打开失败不抛：交给⑤等采样超时 →「无有效采样」定格
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
            ListAbnormal.Items.Add($"流程异常：{ex.Message}");
        }
        finally
        {
            if (powerZ is not null)
            {
                try { await powerZ.StopAsync(ct); } catch { /* 忽略 */ }
                powerZ.Dispose();
            }
        }

        // ---- ⑤⑥ 定格 + 未执行步骤补齐（fail-fast：任一步出错，其后「未执行」）----
        bool snOk = writeOk && readOk;

        if (snOk && chargeOk)
        {
            if (result?.Coil is { NoValidSample: false } c)
            {
                ShowCoilJudgement(c);

                // ⑤ A/B 线圈检测：含判定（B 超时/A 均值不足 = ⑤ Fail）
                FlowPanel.Mark(4, c.IsNormal ? StepStatus.Passed : StepStatus.Failed,
                    c.IsNormal ? null : string.Join("；", c.AbnormalItems));
                // 5.1 / 5.2 子步分别定格 A、B 判定（B 未测到 = 未执行）
                FlowPanel.Mark(5, c.CoilAPassed ? StepStatus.Passed : StepStatus.Failed);
                if (!c.CoilAPassed)
                    FlowPanel.Mark(6, StepStatus.Waiting, "未执行（前序失败）");
                else
                    FlowPanel.Mark(6, c.CoilBPassed ? StepStatus.Passed : StepStatus.Failed);
                _log.Append($"步骤 5：A 均值 {c.CoilACurrent:F3}A / B 均值 {c.CoilBCurrent:F3}A" +
                                $" → {(c.IsNormal ? "正常" : "异常：" + string.Join("；", c.AbnormalItems))}");

                // ⑥ 停充并入库（线圈判 FAIL 也入库为 FAIL 记录）
                FlowPanel.Mark(7, StepStatus.Running);
                var (offOk, offDetail) = await _can.DisableChargingAsync(ct);
                _log.Append(offOk ? $"步骤 6：停止充电（{offDetail}）" : $"步骤 6：停止充电失败 {offDetail}");
                if (!offOk)
                    ListAbnormal.Items.Add("停止充电指令失败");
                FlowPanel.Mark(7, offOk ? StepStatus.Passed : StepStatus.Failed);
                _log.Append(c.IsNormal
                    ? "步骤 6：已入库（PASS）"
                    : $"步骤 6：已入库（FAIL 记录：{string.Join("；", c.AbnormalItems)}）");
            }
            else
            {
                ShowCoilJudgement(result?.Coil);
                FlowPanel.Mark(4, StepStatus.Failed, "无有效采样");
                FlowPanel.Mark(5, StepStatus.Waiting, "未执行（前序失败）");
                FlowPanel.Mark(6, StepStatus.Waiting, "未执行（前序失败）");
                FlowPanel.Mark(7, StepStatus.Waiting, "未执行（前序失败）");
                _log.Append("步骤 6：未执行（无有效采样），不入库");
            }
        }
        else
        {
            // 前序失败：未到达的步骤全部标未执行（已定格 FAIL 的步骤不覆盖——
            // 开充电失败时步骤 4 已标 FAIL，不得改回「未执行」）
            if (!snOk)
                FlowPanel.Mark(3, StepStatus.Waiting, "未执行（前序失败）");
            ShowCoilJudgement(null);
            FlowPanel.Mark(4, StepStatus.Waiting, "未执行（前序失败）");
            FlowPanel.Mark(5, StepStatus.Waiting, "未执行（前序失败）");
            FlowPanel.Mark(6, StepStatus.Waiting, "未执行（前序失败）");
            FlowPanel.Mark(7, StepStatus.Waiting, "未执行（前序失败）");
            _log.Append("步骤 6：未执行（前序失败），不入库");
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
        _flowCompleted = true;  // 完整跑通一次（PASS/FAIL 均算），允许点「下一个」
        RaiseAvailability();
    }

    /// <summary>切换下一台：数据展示区/流程/日志复位，串口连接保持。</summary>
    public void ResetForNext()
    {
        _flowCompleted = false;
        FlowPanel.Reset();
        ListAbnormal.Items.Clear();
        TxtSn.Text = "—";
        ShowCoilJudgement(null);
        TxtResult.Text = "待测试";
        TxtResult.Foreground = MutedBrush;
        BadgeResult.Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
        _log.Clear();
        _log.Append("已切换下一台，串口保持连接，等待开始测试");
        RaiseAvailability();
    }

    /// <summary>把一次测试结果显示到数据展示区（异常项红字）。</summary>
    public void ShowResult(Station1Result result)
    {
        ListAbnormal.Items.Clear();
        TxtSn.Text = string.IsNullOrEmpty(result.Sn) ? "—" : result.Sn;

        SetResultBadge(result.Passed);

        if (result.Coil is { NoValidSample: false } c)
        {
            ShowCoilJudgement(c);
        }

        foreach (var item in result.AbnormalItems)
            ListAbnormal.Items.Add(item);

        _log.Append(result.Passed
            ? "全部检测项正常，PASS"
            : $"存在 {result.AbnormalItems.Count} 项异常，FAIL");
    }

    /// <summary>
    /// PowerZ 检测实时进度：流程区显示阶段提示/倒计时；A 判定一出即点亮 A 卡片，
    /// 测试员见「通过」即可换接 B 线圈。
    /// </summary>
    private void OnCoilProgress(CoilProgress p)
    {
        FlowPanel.Mark(4, StepStatus.Running, p.Hint);   // 5 总步骤：阶段提示/倒计时
        if (p.CoilADecided)
        {
            CardCoilA.SetState(p.CoilAPassed ? CoilState.Passed : CoilState.Failed, p.CoilACurrent);
            // 5.1 A 定格；5.2 B 随即开始（A 未过则 B 不测）
            FlowPanel.Mark(5, p.CoilAPassed ? StepStatus.Passed : StepStatus.Failed);
            if (!p.CoilAPassed)
                FlowPanel.Mark(6, StepStatus.Waiting, "未执行（前序失败）");
            else
                FlowPanel.Mark(6, StepStatus.Running);
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
        _logWin = new LogWindow("运行日志 — 工位1", _log) { Owner = Window.GetWindow(this) };
        _logWin.Show();
    }
}
