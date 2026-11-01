using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Controls;
using ProductionStationTool.Core.Sn;
using ProductionStationTool.Core.Station;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位 1 页面：SN 生成/写入/回读 + PowerZ 电流电压检测（构想文档）。
/// 四分区布局：串口接入 / 测试流程 / 数据展示 / 运行日志。
/// CAN/PowerZ 接入后替换 StartAsync 占位逻辑。
/// </summary>
public partial class Station1Control : UserControl, IStationPage
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    /// <summary>CAN + PowerZ 串口均已接入（点「连接」成功、未点「断开」）。</summary>
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

    public Station1Control()
    {
        InitializeComponent();
        FlowPanel.SetSteps(
            "连接设备（串口/CAN 自动检测）",
            "生成 SN 码",
            "写入 SN 并回读验证",
            "开启无线充电",
            "PowerZ 采样电流/电压",
            "判限并入库");

        ConnPanel.ConnectRequested += (can, pz) =>
        {
            _connected = true;
            ConnPanel.SetStatus($"已连接 CAN:{can} PowerZ:{pz}", true);
            LogPanel.Append($"串口连接：CAN={can}，PowerZ={pz}");
            RaiseAvailability();
        };
        ConnPanel.DisconnectRequested += () =>
        {
            _connected = false;
            ConnPanel.SetStatus("未连接", false);
            LogPanel.Append("串口已断开");
            RaiseAvailability();
        };
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_running)
            return;

        if (!_connected)
        {
            LogPanel.Append("串口未接入（CAN/PowerZ），无法开始测试");
            return;
        }

        _running = true;
        RaiseAvailability();

        FlowPanel.Reset();
        ListAbnormal.Items.Clear();
        LogPanel.Append("开始工位 1 测试流程（SN 生成与 PowerZ 采样为真实逻辑）");

        // ① 连接设备：入口已校验串口已接入
        FlowPanel.Mark(0, StepStatus.Running);
        FlowPanel.Mark(0, StepStatus.Passed);

        // ② 生成 SN 码：真实生成（Core.SnGenerator），显示到数据展示区
        FlowPanel.Mark(1, StepStatus.Running);
        var sn = SnGenerator.Generate(DateTime.Now, NextSnSequence(), "01");
        TxtSn.Text = sn;
        LogPanel.Append($"生成 SN：{sn}");
        FlowPanel.Mark(1, StepStatus.Passed);

        // ③ 写入 SN 并回读：CAN/UDS 协议未接入
        FlowPanel.Mark(2, StepStatus.Running);
        LogPanel.Append("步骤 3「写入 SN 并回读」：CAN/UDS 协议未接入，未真实执行");
        FlowPanel.Mark(2, StepStatus.Failed);
        ListAbnormal.Items.Add("步骤 3「写入 SN 并回读」未真实执行（CAN/UDS 协议未接入）");

        // ④ 开启无线充电：CAN/UDS 协议未接入
        FlowPanel.Mark(3, StepStatus.Running);
        LogPanel.Append("步骤 4「开启无线充电」：CAN/UDS 协议未接入，未真实执行");
        FlowPanel.Mark(3, StepStatus.Failed);
        ListAbnormal.Items.Add("步骤 4「开启无线充电」未真实执行（CAN/UDS 协议未接入）");

        // ⑤ PowerZ 采样电流/电压：真实采样（HID KM003C 协议）
        FlowPanel.Mark(4, StepStatus.Running);
        LogPanel.Append("步骤 5「PowerZ 采样电流/电压」：打开 HID 采样…");
        var sample = await SamplePowerZAsync(ct);
        if (sample is { } s)
        {
            TxtVoltage.Text = $"{s.Voltage:F3}";
            TxtCurrent.Text = $"{s.Current:F3}";
            var fails = DefaultPowerLimits.Judge(s.Voltage, s.Current);
            if (fails.Count == 0)
            {
                TxtPowerJudge.Text = "正常";
                TxtPowerJudge.Foreground = SuccessBrush;
                LogPanel.Append($"步骤 5：采样成功 {s}，判限正常");
                FlowPanel.Mark(4, StepStatus.Passed);
            }
            else
            {
                TxtPowerJudge.Text = "异常";
                TxtPowerJudge.Foreground = DangerBrush;
                foreach (var f in fails)
                    ListAbnormal.Items.Add(f);
                LogPanel.Append($"步骤 5：采样成功 {s}，判限异常");
                FlowPanel.Mark(4, StepStatus.Failed);
            }
        }
        else
        {
            TxtVoltage.Text = "—";
            TxtCurrent.Text = "—";
            TxtPowerJudge.Text = "—";
            FlowPanel.Mark(4, StepStatus.Failed);
            ListAbnormal.Items.Add("步骤 5「PowerZ 采样」失败：HID 无采样（设备未接/协议不符）");
            LogPanel.Append("步骤 5「PowerZ 采样」：无采样（PowerZ 未接或协议不符，见上方日志）");
        }

        // ⑥ 判限并入库：判限已在步骤 5 执行；入库（SQLite）未接入
        FlowPanel.Mark(5, StepStatus.Running);
        LogPanel.Append("步骤 6「判限并入库」：判限已在步骤 5 执行；入库（SQLite）未接入，未真实执行");
        FlowPanel.Mark(5, StepStatus.Failed);
        ListAbnormal.Items.Add("步骤 6「判限并入库」未真实执行（入库未接入）");

        SetResultBadge(false);
        LogPanel.Append("流程结束：SN 生成与 PowerZ 采样为真实执行，其余待接入协议");

        _running = false;
        _flowCompleted = true;  // 完整跑通一次（PASS/FAIL 均算），允许点「下一个」
        RaiseAvailability();
    }

    /// <summary>占位判限阈值（5V 档）；页面阈值配置接入后替换。</summary>
    private static readonly PowerLimits DefaultPowerLimits = new()
    {
        VoltageMin = 4.75,
        VoltageMax = 5.25,
        CurrentMin = 0.0,
        CurrentMax = 3.0,
    };

    /// <summary>PowerZ HID 采样：取首个样本（3s 超时）；协议不符时日志含原始报文。</summary>
    private async Task<PowerSample?> SamplePowerZAsync(CancellationToken ct)
    {
        try
        {
            using var pz = new HidPowerZSource();
            var tcs = new TaskCompletionSource<PowerSample>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pz.SampleReceived += s => tcs.TrySetResult(s);
            pz.Log += m => Dispatcher.Invoke(() => LogPanel.Append(m));
            await pz.StartAsync(ct);
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(3000, ct));
            PowerSample? result = finished == tcs.Task ? await tcs.Task : null;
            await pz.StopAsync(ct);
            return result;
        }
        catch (Exception ex)
        {
            LogPanel.Append($"[PowerZ] 打开失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>当日流水号（演示用递增；正式接入 SQLite 后取库存水号）。</summary>
    private static int _snSequence;

    private static int NextSnSequence()
    {
        if (_snSequence >= 9999)
            _snSequence = 0;
        return ++_snSequence;
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
