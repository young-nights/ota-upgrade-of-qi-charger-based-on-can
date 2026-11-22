using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Controls;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Sn;
using ProductionStationTool.Core.Station;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位 3 页面：读取 SN 并与设备硬件版本比对 + 充电下 A/B 双线圈复测。
/// 流程：连接设备 → 读 SN 并比对版本号 → 开充电 → PowerZ A/B 判定（同工位1）→ 停充结束。
/// </summary>
public partial class Station3Control : UserControl, IStationPage
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    private readonly LogBuffer _log = new();
    private LogWindow? _logWin;

    private CanUdsDevice? _can;
    private bool _connected;

    /// <summary>本次连接内是否已开充电（决定断开时是否需要停充）。</summary>
    private bool _chargingOn;

    /// <summary>异常项收集（异常项栏已撤，明细并入 FAIL 汇总日志）。</summary>
    private readonly List<string> _abnormal = new();
    private bool _running;

    /// <summary>本次测试的取消源：断开串口即取消，释放 UDS/线圈等待。</summary>
    private CancellationTokenSource? _testCts;

    public int StationNo => 3;
    public string Title => "工位3 — SN复测 + 线圈A/B";

    public bool CanStartTest => _connected && !_running;

    public event Action? AvailabilityChanged;

    private void RaiseAvailability() => AvailabilityChanged?.Invoke();

    private static string Fmt(string v) => v.Length == 0 ? "(空)" : v;

    private void LogSafe(string msg) => _log.Append(msg);

    public Station3Control(StationIdentity identity)
    {
        InitializeComponent();
        _log.Append($"本机身份：{identity.Code}（{identity.Label}，machine.json 可改）");
        var coil0 = MachineConfig.LoadCoilOptions();
        var vp0 = MachineConfig.LoadVersionPresets();
        _log.Append($"已载入保存参数（machine.json，重启自动沿用）：A {coil0.CoilALowerA:F2}~{coil0.CoilAUpperA:F2}A / B {coil0.CoilBLowerA:F2}~{coil0.CoilBUpperA:F2}A；" +
                    $"版本预设 APP={Fmt(vp0.App)} Boot={Fmt(vp0.Boot)} Qi={Fmt(vp0.Qi)} HW={Fmt(vp0.Hw)}");
        FlowPanel.SetSteps(
            ("1", "连接设备（串口/CAN 自动检测）"),
            ("2", "读取 SN 并比对版本号"),
            ("3", "开启无线充电"),
            ("4", "PowerZ："),
            ("4.1", "A 线圈状态检测"),
            ("4.2", "B 线圈状态检测"),
            ("5", "停充结束"));
        // 4 汇总行：只滚动倒计时提示，通过/失败由 4.1/4.2 承载——初始空白
        FlowPanel.Mark(3, StepStatus.None);

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
            if (_chargingOn)
            {
                try
                {
                    // 快速停充（裸写优先、整套兜底、3s 预算封顶）：断开不再等整套 UDS
                    var (ok, detail) = await _can.DisableChargingQuickAsync();
                    _log.Append(ok ? $"断开前停止充电：{detail}" : $"断开前停充失败：{detail}");
                }
                catch (Exception ex)
                {
                    _log.Append($"断开前停充异常：{ex.Message}");
                }
            }
            else
            {
                _log.Append("充电未开启，断开免停充");
            }
        }
        // 中止在跑的测试：取消 token 释放 UDS/线圈等待；
        // _running 立即置假解锁「开始测试」（不等僵尸 StartAsync 收尾）
        _testCts?.Cancel();
        _testCts = null;
        _running = false;
        try { _can?.Dispose(); } catch { /* 忽略 */ }
        _can = null;
        _connected = false;
        ConnPanel.SetStatus("未连接", false);
        _log.Append("串口已断开");
        ResetForNext(clearLog: false);   // 断开复位界面状态（结果/SN/版本/线圈/流程），日志保留
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
        _testCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = _testCts.Token;   // 断开串口可中止本次测试的全部等待
        RaiseAvailability();
        FlowPanel.Reset();
        _abnormal.Clear();
        ShowCoilJudgement(null);
        TxtSn.Text = "—";
        ClearVersionPanel();
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

        // ② 读取 SN，并用 SN 中的两位硬件版本与设备 F193 硬件版本比对
        FlowPanel.Mark(1, StepStatus.Running);
        _log.Append("步骤 2：读取设备 SN 码…");
        var sn = await _can.TryReadSnAsync(ct);
        if (string.IsNullOrEmpty(sn))
        {
            await FailEarly(1, "SN 读取失败（设备未写 SN？）");
            return;
        }
        TxtSn.Text = sn;
        if (!SnGenerator.TryParse(sn, out var snFields))
        {
            await FailEarly(1, $"SN 结构不符合：{sn}");
            return;
        }
        var (hwOk, hwVersion, hwError) = await _can.TryReadHardwareVersionAsync(ct);
        if (!hwOk)
        {
            await FailEarly(1, $"硬件版本读取失败：{hwError}");
            return;
        }
        // 四项版本读取 + 数据展示全列出（实测 / 预设 / 结果），无论是否设预设
        var vs = await _can.TryReadAllVersionsAsync(ct);
        var vp = MachineConfig.LoadVersionPresets();
        SetVersionPanel(snFields.HwVersion, hwVersion, vs, vp);

        string expected = ExpectedSnHardwareCode(hwVersion);
        bool versionMatch = expected.Length > 0 &&
                            string.Equals(snFields.HwVersion, expected, StringComparison.Ordinal);
        if (!versionMatch)
        {
            await FailEarly(1,
                $"版本号不匹配：SN={snFields.HwVersion}，设备HW={hwVersion}");
            return;
        }
        _log.Append($"步骤 2：SN={sn}，硬件版本 {snFields.HwVersion} 与设备 {hwVersion} 一致");

        // 版本预设比对（设置 → 版本预设）：填了预设的项逐一核对，留空跳过；
        // 预设与设备版本全串或版本尾缀匹配即一致（VersionCompare）
        if (vp.App.Length + vp.Boot.Length + vp.Qi.Length + vp.Hw.Length > 0)
        {
            var mism = new List<string>();
            void Check(string preset, string? actual, string name)
            {
                if (preset.Length == 0)
                    return;
                if (!VersionCompare.Matches(preset, actual))
                    mism.Add($"{name} 预设「{preset}」实际 {actual ?? "(读取失败)"}");
            }
            Check(vp.App, vs.Mcu1App, "APP");
            Check(vp.Boot, vs.Bootloader, "Boot");
            Check(vp.Qi, vs.Mcu2Qi, "Qi");
            Check(vp.Hw, vs.Hardware, "硬件");
            _log.Append($"步骤 2：版本预设比对 {vs.Summary}");
            if (mism.Count > 0)
            {
                await FailEarly(1, "版本预设不符：" + string.Join("；", mism));
                return;
            }
            _log.Append("步骤 2：版本预设比对全部一致");
        }
        else
        {
            _log.Append("步骤 2：未设置版本预设（设置 → 版本预设），跳过版本比对");
        }

        FlowPanel.Mark(1, StepStatus.Passed);

        // ③ 开启无线充电
        FlowPanel.Mark(2, StepStatus.Running);
        var (chargeOk, chargeDetail) = await _can.EnableChargingAsync(ct);
        _chargingOn = chargeOk;
        _log.Append(chargeOk ? $"步骤 3：{chargeDetail}" : $"步骤 3：开启充电失败 {chargeDetail}");
        if (!chargeOk)
        {
            FlowPanel.Mark(2, StepStatus.Failed);
            FlowPanel.Mark(3, StepStatus.None);
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
            FlowPanel.Mark(3, StepStatus.None);
            FlowPanel.Mark(4, StepStatus.Failed);
            FlowPanel.Mark(5, StepStatus.Waiting, "未执行（无有效采样）");
            FlowPanel.Mark(6, StepStatus.Waiting, "未执行（无有效采样）");
            _abnormal.Add("PowerZ 无有效采样");
            await SafeStopChargingAsync(ct);
            SetResultBadge(false);
            _log.Append("步骤 5：未执行（无有效采样）");
            _running = false;
            RaiseAvailability();
            return;
        }

        ShowCoilJudgement(coil);
        FlowPanel.Mark(3, StepStatus.None);
        // 4.1 / 4.2 子步分别定格 A、B 判定（B 未测到 = 未执行）
        FlowPanel.Mark(4, coil.CoilAPassed ? StepStatus.Passed : StepStatus.Failed);
        if (!coil.CoilAPassed)
            FlowPanel.Mark(5, StepStatus.Waiting, "未执行（前序失败）");
        else
            FlowPanel.Mark(5, coil.CoilBPassed ? StepStatus.Passed : StepStatus.Failed);
        _log.Append($"步骤 4：A 均值 {coil.CoilACurrent:F3}A / B 均值 {coil.CoilBCurrent:F3}A" +
                    $" → {(coil.IsNormal ? "正常" : "异常：" + string.Join("；", coil.AbnormalItems))}");

        // 停充结束
        FlowPanel.Mark(6, StepStatus.Running);
        var (offOk, offDetail) = await _can.DisableChargingAsync(ct);
            if (offOk) _chargingOn = false;
        _log.Append(offOk ? $"步骤 5：停充结束（{offDetail}）" : $"步骤 5：停充失败 {offDetail}");
        if (!offOk)
            _abnormal.Add("停止充电指令失败");

        var passed = coil.IsNormal && offOk;
        foreach (var item in coil.AbnormalItems)
            _abnormal.Add(item);
        FlowPanel.Mark(6, offOk ? StepStatus.Passed : StepStatus.Failed);
        _log.Append(coil.IsNormal
            ? "步骤 5：停充结束（PASS）"
            : $"步骤 5：停充结束（FAIL：{string.Join("；", coil.AbnormalItems)}）");

        SetResultBadge(passed);
        _log.Append(passed ? "全部检测项正常，PASS"
            : $"存在 {_abnormal.Count} 项异常，FAIL：{string.Join("；", _abnormal)}");
        _running = false;
        RaiseAvailability();
    }

    /// <summary>
    /// <summary>数据展示：SN 硬件码 + APP/Boot/Qi/硬件 四项版本（实测 / 预设 / 结果）。</summary>
    private void SetVersionPanel(string snHwCode, string hwVersion,
        QiChargerStationTool.Core.Protocol.VersionSet vs, MachineConfig.VersionPresets vp)
    {
        // SN↔设备 HW 的硬件码比对结果进日志（面板只显示四项版本，硬件不重复展示）
        bool snOk = string.Equals(snHwCode, ExpectedSnHardwareCode(hwVersion), StringComparison.Ordinal);
        _log.Append($"SN 硬件码 {snHwCode} ↔ 设备 HW {hwVersion}：{(snOk ? "一致" : "不一致")}");
        SetVer(TxtVerApp, "APP  ", vs.Mcu1App, vp.App);
        SetVer(TxtVerBoot, "Boot ", vs.Bootloader, vp.Boot);
        SetVer(TxtVerQi, "Qi   ", vs.Mcu2Qi, vp.Qi);
        SetVer(TxtVerHw, "硬件 ", vs.Hardware, vp.Hw);
    }

    /// <summary>版本行：实测 / 预设 / 标记；预设为空=不比（灰）。</summary>
    private static void SetVer(TextBlock tb, string name, string? actual, string preset)
    {
        string mark;
        Brush color;
        if (preset.Length == 0)
        {
            mark = "– 未预设·不比";
            color = MutedBrush;
        }
        else if (VersionCompare.Matches(preset, actual))
        {
            mark = "✓";
            color = SuccessBrush;
        }
        else
        {
            mark = "✗";
            color = DangerBrush;
        }
        // 两行式适配右栏宽度：首行实测、次行预设+标记
        tb.Text = $"{name} 实测 {actual ?? "(读取失败)"}\n" +
                  $"      预设 {(preset.Length == 0 ? "—" : preset)}  {mark}";
        tb.Foreground = color;
    }

    private void ClearVersionPanel()
    {
        TxtVerApp.Text = "APP   —";
        TxtVerBoot.Text = "Boot  —";
        TxtVerQi.Text = "Qi    —";
        TxtVerHw.Text = "硬件  —";
    }

    /// 设备 HW 版本形如 QC_JYF_HW_1.1.5；SN 的两位硬件版本取主版本号（1 → 01）。
    /// </summary>
    private static string ExpectedSnHardwareCode(string hwVersion)
    {
        string tail = hwVersion.Trim().Split('_').LastOrDefault() ?? "";
        string major = tail.Split('.').FirstOrDefault() ?? "";
        return int.TryParse(major, out int value) && value is >= 0 and <= 99
            ? value.ToString("D2")
            : "";
    }

    /// <summary>前序步骤失败：该步 FAIL，其后「未执行」，安全停充。</summary>
    private async Task FailEarly(int step, string reason)
    {
        FlowPanel.Mark(step, StepStatus.Failed, reason);
        for (var i = step + 1; i < 7; i++)
            FlowPanel.Mark(i, StepStatus.Waiting, "未执行（前序失败）");
        _abnormal.Add($"步骤 {step + 1} 失败：{reason}");
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
            if (ok) _chargingOn = false;
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

    /// <summary>切换下一台：数据展示/流程/日志复位，串口连接保持。
    /// clearLog=false 时保留运行日志（断开复位用，停充/断开消息不被抹掉）。</summary>
    public void ResetForNext() => ResetForNext(true);

    public void ResetForNext(bool clearLog)
    {
        FlowPanel.Reset();
        _abnormal.Clear();
        TxtSn.Text = "—";
        ClearVersionPanel();
        ShowCoilJudgement(null);
        TxtResult.Text = "待测试";
        TxtResult.Foreground = MutedBrush;
        BadgeResult.Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
        if (clearLog)
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
