using System.Windows;
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
                LogPanel.Append($"PowerZ 校验失败：{pzDetail}");
                LogPanel.Append("提示：若设备已 usbipd 挂载到 WSL，请先 usbipd detach 后重试");
                RaiseAvailability();
                return;
            }

            _can?.Dispose();
            _can = new CanUdsDevice(can);
            _can.Log += LogSafe;
            await _can.OpenAsync();
            _connected = true;
            ConnPanel.SetStatus($"已连接 CAN:{can} PowerZ:{pz}", true);
            LogPanel.Append($"串口连接：CAN={can}，PowerZ={pz}（{pzDetail}）");
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

    private async void OnDisconnectRequested()
    {
        // 断开前发停止充电（2E 21 01 00）——充电开着则设备不休眠，命令可达
        if (_can is not null && _connected)
        {
            try
            {
                var (ok, detail) = await _can.DisableChargingAsync();
                LogPanel.Append(ok ? $"断开前停止充电：{detail}" : $"断开前停充失败：{detail}");
            }
            catch (Exception ex)
            {
                LogPanel.Append($"断开前停充异常：{ex.Message}");
            }
        }
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
        //    首帧请求会丢在重新供电的冷启动窗口里——必须先唤醒探活），
        //    然后读全套版本，再往下走
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
        LogPanel.Append("设备已就绪，读取版本…");
        var vs = await _can.TryReadAllVersionsAsync();
        LogPanel.Append("设备版本：");
        LogPanel.Append($"  APP  = {vs.Mcu1App}");
        LogPanel.Append($"  BL   = {vs.Bootloader}");
        LogPanel.Append($"  HW   = {vs.Hardware}");
        LogPanel.Append($"  Qi   = {vs.Mcu2Qi}");
        if (vs.Errors.Count > 0)
            LogPanel.Append("版本读取告警：" + string.Join("；", vs.Errors));
        FlowPanel.Mark(0, StepStatus.Passed);
        LogPanel.Append("步骤 1 完成");

        // ⑤ 的数据源：HID 先静默就绪，等流程走到采样步再点亮「进行中」
        HidPowerZSource? powerZ = null;
        bool writeOk = false, readOk = false, chargeOk = false;
        string? writtenSn = null;
        Station1Result? result = null;

        try
        {
            powerZ = new HidPowerZSource();
            powerZ.Log += LogSafe;

            // 严格顺序执行：每步完成即定格（Passed/Failed），下一步才亮「进行中」。
            // PowerZ HID 在⑤真正开始（④开充电完成）时才打开，避免日志时序错乱。
            result = await _workflow.RunAsync(
                snWriteAsync: async sn =>
                {
                    writtenSn = sn;
                    TxtSn.Text = sn;
                    LogPanel.Append($"生成 SN：{sn}");
                    FlowPanel.Mark(1, StepStatus.Passed);      // ② 生成 SN：完成

                    FlowPanel.Mark(2, StepStatus.Running);     // ③ 写入并回读：开始
                    var (ok, detail) = await _can.WriteSnAsync(sn, ct);
                    LogPanel.Append(ok ? $"步骤 3：写 SN 成功 {detail}" : $"步骤 3：写 SN 失败 {detail}");
                    writeOk = ok;
                    if (!ok)
                        FlowPanel.Mark(2, StepStatus.Failed);  // 写失败即定格，不再回读
                    return ok;
                },
                snReadAsync: async () =>
                {
                    var back = await _can.TryReadSnAsync(ct);
                    readOk = back is not null && string.Equals(back, writtenSn, StringComparison.Ordinal);
                    LogPanel.Append($"步骤 3：回读 SN = {back ?? "(空)"}");
                    FlowPanel.Mark(2, readOk ? StepStatus.Passed : StepStatus.Failed);  // ③ 完成定格
                    return back;
                },
                chargeStartAsync: async () =>
                {
                    FlowPanel.Mark(3, StepStatus.Running);     // ④ 开充电：开始
                    var (ok, detail) = await _can.EnableChargingAsync(ct);
                    LogPanel.Append(ok ? $"步骤 4：{detail}" : $"步骤 4：开启充电失败 {detail}");
                    chargeOk = ok;
                    FlowPanel.Mark(3, ok ? StepStatus.Passed : StepStatus.Failed);      // ④ 完成定格
                    if (!ok)
                        throw new InvalidOperationException(detail);
                    FlowPanel.Mark(4, StepStatus.Running);     // ⑤ 采样：开始（充电完成才到）
                    LogPanel.Append("步骤 5：打开 PowerZ HID 采样…");
                    try
                    {
                        await powerZ!.StartAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        // HID 打开失败不抛：交给⑤等采样超时 →「无有效采样」定格
                        LogPanel.Append($"[PowerZ] 打开失败：{ex.Message}");
                    }
                },
                powerZ: powerZ,
                limits: DefaultPowerLimits,
                ct);
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

        // ---- ⑤⑥ 定格 + 未执行步骤补齐（fail-fast：任一步出错，其后「未执行」）----
        bool snOk = writeOk && readOk;

        if (snOk && chargeOk)
        {
            // ⑤ PowerZ 采样：拿到采样即本步完成（判限归⑥）
            if (result?.Power is { } p)
            {
                TxtVoltage.Text = $"{p.Voltage:F3}";
                TxtCurrent.Text = $"{p.Current:F3}";
                FlowPanel.Mark(4, StepStatus.Passed);

                // ⑥ 判限并入库：判限入库时发停止充电指令（2E 21 01 00）
                FlowPanel.Mark(5, StepStatus.Running);
                var (offOk, offDetail) = await _can.DisableChargingAsync(ct);
                LogPanel.Append(offOk ? $"步骤 6：停止充电（{offDetail}）" : $"步骤 6：停止充电失败 {offDetail}");

                TxtPowerJudge.Text = p.IsNormal ? "正常" : "异常";
                TxtPowerJudge.Foreground = p.IsNormal ? SuccessBrush : DangerBrush;
                bool step6Ok = p.IsNormal && offOk;
                FlowPanel.Mark(5, step6Ok ? StepStatus.Passed : StepStatus.Failed);
                LogPanel.Append(step6Ok
                    ? "步骤 6：判限正常，已入库（PASS）"
                    : $"步骤 6：判限/停充异常（{string.Join("；", p.AbnormalItems)}{(offOk ? "" : "；停充失败")}），已入库（FAIL）");
                if (!offOk)
                    ListAbnormal.Items.Add("停止充电指令失败");
            }
            else
            {
                TxtVoltage.Text = "—";
                TxtCurrent.Text = "—";
                TxtPowerJudge.Text = "—";
                FlowPanel.Mark(4, StepStatus.Failed, "无有效采样");
                FlowPanel.Mark(5, StepStatus.Waiting, "未执行（前序失败）");
                LogPanel.Append("步骤 6：未执行（无有效采样），不判限不入库");
            }
        }
        else
        {
            // 前序失败：未到达的步骤全部标未执行
            if (!chargeOk)
                FlowPanel.Mark(3, StepStatus.Waiting, "未执行（前序失败）");
            TxtVoltage.Text = "—";
            TxtCurrent.Text = "—";
            TxtPowerJudge.Text = "—";
            FlowPanel.Mark(4, StepStatus.Waiting, "未执行（前序失败）");
            FlowPanel.Mark(5, StepStatus.Waiting, "未执行（前序失败）");
            LogPanel.Append("步骤 6：未执行（前序失败），不判限不入库");
        }

        // 安全关充：流程中途出错（无采样/前序失败）时充电可能仍开着，补发停充
        if (chargeOk && result?.Power is null)
        {
            var (offOk, offDetail) = await _can.DisableChargingAsync(ct);
            LogPanel.Append(offOk ? $"安全停充：{offDetail}" : $"安全停充失败：{offDetail}");
        }

        if (result is not null)
            foreach (var item in result.AbnormalItems)
                ListAbnormal.Items.Add(item);

        SetResultBadge(result?.Passed == true);
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
        BarOta.Value = 0;
        TxtOtaStatus.Text = "待命";
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

    // ---------- 固件升级（OTA，维护动作：不进测试流程/不入库） ----------

    private void OnOtaBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 XATO 签名镜像",
            Filter = "XATO 镜像 (*.bin)|*.bin|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
            BoxOtaFirmware.Text = dlg.FileName;
    }

    /// <summary>
    /// OTA 升级（对齐 QiChargerStationTool 的 RunUpgradeAsync）：
    /// 进度条按块推进；升级中禁止「开始测试/下一个」（_running 复用）。
    /// </summary>
    private async void OnOtaUpgrade(object sender, RoutedEventArgs e)
    {
        if (_running)
            return;
        if (!_connected || _can is null)
        {
            TxtOtaStatus.Text = "串口未接入，无法升级";
            LogPanel.Append("[OTA] 串口未接入（CAN），无法升级");
            return;
        }
        var path = BoxOtaFirmware.Text.Trim();
        if (path.Length == 0 || !System.IO.File.Exists(path))
        {
            TxtOtaStatus.Text = "请先选择固件文件";
            LogPanel.Append("[OTA] 固件文件不存在，请先「浏览…」选择 app_image_vX_Y_Z.bin");
            return;
        }

        _running = true;
        RaiseAvailability();
        BtnOta.IsEnabled = false;
        BarOta.Value = 0;
        TxtOtaStatus.Text = "升级中…";
        LogPanel.Append($"[OTA] 开始升级：{path}");
        var progress = new Progress<OtaProgress>(p =>
        {
            BarOta.Value = p.Percent;
            TxtOtaStatus.Text = $"{p.Percent}%  {p.Phase}";
        });
        try
        {
            var (ok, detail) = await _can.OtaUpgradeAsync(path, 0x01, 0, progress);
            TxtOtaStatus.Text = ok ? $"完成：{detail}" : $"失败：{detail}";
            LogPanel.Append(ok ? $"[OTA] 升级成功：{detail}" : $"[OTA] 升级失败：{detail}");
        }
        catch (Exception ex)
        {
            TxtOtaStatus.Text = $"异常：{ex.Message}";
            LogPanel.Append($"[OTA] 异常：{ex.Message}");
        }
        finally
        {
            _running = false;
            BtnOta.IsEnabled = true;
            RaiseAvailability();
        }
    }
}
