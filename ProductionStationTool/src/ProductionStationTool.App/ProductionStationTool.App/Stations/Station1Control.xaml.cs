using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Controls;
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

    /// <summary>占位判限阈值（5V 档）；页面阈值配置接入后替换。</summary>
    private static readonly PowerLimits DefaultPowerLimits = new()
    {
        VoltageMin = 4.75,
        VoltageMax = 5.25,
        CurrentMin = 0.0,
        CurrentMax = 3.0,
    };

    private readonly ITestRecordStore _store = new SqliteTestRecordStore();
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
        => Dispatcher.Invoke(() => LogPanel.Append(msg));

    public Station1Control()
    {
        InitializeComponent();
        _workflow = new Station1Workflow(_store);
        FlowPanel.SetSteps(
            "连接设备（串口/CAN 自动检测）",
            "生成 SN 码",
            "写入 SN 并回读验证",
            "开启无线充电",
            "PowerZ 采样电流/电压",
            "判限并入库");

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
            ConnPanel.SetStatus($"已连接 CAN:{can} PowerZ:{pz}", true);
            LogPanel.Append($"串口连接：CAN={can}，PowerZ={pz}");

            var awake = await _can.WaitReadyAsync();
            LogPanel.Append(awake ? "设备在线（唤醒就绪 OK）" : "唤醒无应答（设备未上电？开始测试时会再唤醒）");
            if (awake)
            {
                var (ok, ver, _) = await _can.TryReadVersionAsync();
                if (ok)
                    LogPanel.Append($"设备版本：{ver}");
            }
        }
        catch (Exception ex)
        {
            _can?.Dispose();
            _can = null;
            _connected = false;
            ConnPanel.SetStatus($"连接失败：{ex.Message}", false);
            LogPanel.Append($"CAN 打开失败：{ex.Message}");
        }
        RaiseAvailability();
    }

    private void OnDisconnectRequested()
    {
        try { _can?.Dispose(); } catch { /* 忽略 */ }
        _can = null;
        _connected = false;
        ConnPanel.SetStatus("未连接", false);
        LogPanel.Append("串口已断开");
        RaiseAvailability();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_running)
            return;

        if (!_connected || _can is null)
        {
            LogPanel.Append("串口未接入（CAN/PowerZ），无法开始测试");
            return;
        }

        _running = true;
        RaiseAvailability();

        FlowPanel.Reset();
        ListAbnormal.Items.Clear();
        LogPanel.Append("开始工位 1 测试流程");

        // ① 连接设备：先唤醒 + 等 UDS 就绪（设备空闲 10s 会真休眠/整板断电，
        //    首帧请求会丢在重新供电的冷启动窗口里——必须先唤醒探活）
        FlowPanel.Mark(0, StepStatus.Running);
        LogPanel.Append("步骤 1：唤醒设备并等待 UDS 就绪…");
        var ready = await _can.WaitReadyAsync(ct);
        if (!ready)
        {
            FlowPanel.Mark(0, StepStatus.Failed);
            ListAbnormal.Items.Add("步骤 1「连接设备」失败：设备无应答（未上电？）");
            SetResultBadge(false);
            LogPanel.Append("设备 8s 内无应答——请检查供电后重试");
            _running = false;
            _flowCompleted = true;
            RaiseAvailability();
            return;
        }
        FlowPanel.Mark(0, StepStatus.Passed);
        LogPanel.Append("设备已就绪");

        // ⑤ 的数据源：HID 打开后交给 Workflow 等首个采样
        HidPowerZSource? powerZ = null;
        bool writeOk = false, readOk = false, chargeOk = false, completed = false;
        Station1Result? result = null;

        try
        {
            powerZ = new HidPowerZSource();
            powerZ.Log += LogSafe;
            FlowPanel.Mark(4, StepStatus.Running);
            LogPanel.Append("步骤 5 准备：打开 PowerZ HID 采样…");
            await powerZ.StartAsync(ct);

            result = await _workflow.RunAsync(
                snWriteAsync: async sn =>
                {
                    LogPanel.Append($"生成 SN：{sn}");
                    FlowPanel.Mark(1, StepStatus.Running);
                    TxtSn.Text = sn;
                    FlowPanel.Mark(1, StepStatus.Passed);

                    FlowPanel.Mark(2, StepStatus.Running);
                    var (ok, detail) = await _can.WriteSnAsync(sn, ct);
                    LogPanel.Append(ok ? $"步骤 3：写 SN 成功 {detail}" : $"步骤 3：写 SN 失败 {detail}");
                    writeOk = ok;
                    return ok;
                },
                snReadAsync: async () =>
                {
                    var back = await _can.TryReadSnAsync(ct);
                    readOk = back is not null;
                    LogPanel.Append($"步骤 3：回读 SN = {back ?? "(空)"}");
                    FlowPanel.Mark(2, readOk ? StepStatus.Passed : StepStatus.Failed);
                    return back;
                },
                chargeStartAsync: async () =>
                {
                    FlowPanel.Mark(3, StepStatus.Running);
                    var (ok, detail) = await _can.EnableChargingAsync(ct);
                    LogPanel.Append(ok ? $"步骤 4：{detail}" : $"步骤 4：开启充电失败 {detail}");
                    chargeOk = ok;
                    FlowPanel.Mark(3, ok ? StepStatus.Passed : StepStatus.Failed);
                    if (!ok)
                        throw new InvalidOperationException(detail);
                },
                powerZ: powerZ,
                limits: DefaultPowerLimits,
                ct);
            completed = true;
        }
        catch (Exception ex)
        {
            LogPanel.Append($"流程异常：{ex.Message}");
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

        // 结果展示与剩余步骤定格
        if (result is not null)
        {
            TxtSn.Text = result.Sn;
            if (result.Power is { } p)
            {
                TxtVoltage.Text = $"{p.Voltage:F3}";
                TxtCurrent.Text = $"{p.Current:F3}";
                TxtPowerJudge.Text = p.IsNormal ? "正常" : "异常";
                TxtPowerJudge.Foreground = p.IsNormal ? SuccessBrush : DangerBrush;
                FlowPanel.Mark(4, p.IsNormal ? StepStatus.Passed : StepStatus.Failed);
            }
            else
            {
                TxtVoltage.Text = "—";
                TxtCurrent.Text = "—";
                TxtPowerJudge.Text = "—";
                if (!writeOk)
                    FlowPanel.Mark(4, StepStatus.Failed);
            }
            foreach (var item in result.AbnormalItems)
                ListAbnormal.Items.Add(item);
        }

        if (!writeOk || !readOk)
            FlowPanel.Mark(2, StepStatus.Failed);
        if (!chargeOk)
            FlowPanel.Mark(3, StepStatus.Failed);

        // ⑥ 判限并入库：Workflow 已在 SN 写入成功后入库（写入失败不入库、不消耗流水号）
        FlowPanel.Mark(5, StepStatus.Running);
        if (completed && writeOk)
        {
            FlowPanel.Mark(5, StepStatus.Passed);
            LogPanel.Append($"步骤 6：已入库（{(result?.Passed == true ? "PASS" : "FAIL")}）");
        }
        else
        {
            FlowPanel.Mark(5, StepStatus.Failed);
            ListAbnormal.Items.Add("步骤 6「判限并入库」未完成（SN 写入失败不入库）");
        }

        SetResultBadge(result?.Passed == true && completed);
        LogPanel.Append(result?.Passed == true
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
        TxtVoltage.Text = "—";
        TxtCurrent.Text = "—";
        TxtPowerJudge.Text = "—";
        TxtResult.Text = "待测试";
        TxtResult.Foreground = MutedBrush;
        BadgeResult.Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
        LogPanel.Clear();
        LogPanel.Append("已切换下一台，串口保持连接，等待开始测试");
        RaiseAvailability();
    }

    /// <summary>把一次测试结果显示到数据展示区（异常项红字）。</summary>
    public void ShowResult(Station1Result result)
    {
        ListAbnormal.Items.Clear();
        TxtSn.Text = string.IsNullOrEmpty(result.Sn) ? "—" : result.Sn;

        SetResultBadge(result.Passed);

        if (result.Power is not null)
        {
            TxtVoltage.Text = $"{result.Power.Voltage:F3}";
            TxtCurrent.Text = $"{result.Power.Current:F3}";
            TxtPowerJudge.Text = result.Power.IsNormal ? "正常" : "异常";
            TxtPowerJudge.Foreground = result.Power.IsNormal ? SuccessBrush : DangerBrush;
        }

        foreach (var item in result.AbnormalItems)
            ListAbnormal.Items.Add(item);

        LogPanel.Append(result.Passed
            ? "全部检测项正常，PASS"
            : $"存在 {result.AbnormalItems.Count} 项异常，FAIL");
    }

    private void SetResultBadge(bool passed)
    {
        TxtResult.Text = passed ? "PASS" : "FAIL";
        TxtResult.Foreground = passed ? SuccessBrush : DangerBrush;
        BadgeResult.Background = passed
            ? new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7))
            : new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
    }
}
