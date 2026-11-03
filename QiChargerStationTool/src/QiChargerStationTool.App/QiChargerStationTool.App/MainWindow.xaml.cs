using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QiChargerStationTool.Core.Protocol;
using QiChargerStationTool.Core.Security;
using QiChargerStationTool.Core.Station;
using QiChargerStationTool.Core.Transport;

namespace QiChargerStationTool.App;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    /// <summary>是否有操作在跑。IsBusyGuard 只看这个，不看 _cts（否则结束后永远 true）。</summary>
    private bool _busy;
    /// <summary>充电使能状态。初始 false（本会话尚未点过「开启充电」），
    /// 禁用「Qi 状态检测」；开启充电成功后置 true，关闭充电后回 false。</summary>
    private bool _chargeEnabled = false;
    /// <summary>Qi 状态持续检查开关（按钮切换）。</summary>
    private volatile bool _pollRunning;
    /// <summary>持续检查轮询任务的取消源。</summary>
    private CancellationTokenSource? _pollCts;
    /// <summary>UDS 会话互斥：持续检查轮询与单次操作（RunOpAsync body）不交叉占用总线。</summary>
    private readonly SemaphoreSlim _udsGate = new(1, 1);
    /// <summary>
    /// 固件传输期间置真：过滤逐帧协议噪声（[TX]/[RX]/[SER] 等），只留进度/里程碑/错误，
    /// 方便看百分比进度。错误关键字（超时/失败/NRC…）不过滤。
    /// </summary>
    private volatile bool _quietLogs;
    /// <summary>长连接传输层；连接后各操作复用，直到「断开」。</summary>
    private ZqwlSerialTransport? _transport;
    private readonly object _transportLock = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LoadExtraComMatch();
            ScanPorts(keepSelection: null);
            FocusSnBox();
        };
        Closed += (_, _) => DisconnectTransport();
    }

    /// <summary>从 appsettings.json 读 ExtraComMatch（额外 VID:PID/子串），追加到扫描匹配表。</summary>
    private static void LoadExtraComMatch()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("ExtraComMatch", out var arr)
                || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return;
            foreach (var item in arr.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s) && !ComPortScanner.ExtraMatchIds.Contains(s))
                    ComPortScanner.ExtraMatchIds.Add(s);
            }
        }
        catch { /* 配置可选，失败不阻断扫描 */ }
    }

    /// <summary>扫描串口并填充下拉框；自动选中 ZQWL CAN 盒子。</summary>
    private void ScanPorts(string? keepSelection)
    {
        try
        {
            var ports = ComPortScanner.Scan();
            var pick = ComPortScanner.PickDefault(ports, keepSelection);
            CmbPort.ItemsSource = ports;
            CmbPort.SelectedItem = pick;
            if (pick is null)
                AppendLog("[COM] 未发现串口——请检查 ZQWL 盒子是否已从 WSL detach 到 Windows");
            else if (pick.IsZqwlCan)
                AppendLog($"[COM] 自动选中 ZQWL: {pick.PortName}（{pick.Description}）");
            else
                AppendLog($"[COM] 未识别到 ZQWL（VID_3562 / 描述含 CAN），当前选中 {pick.PortName}（{pick.Description}）");
        }
        catch (Exception ex)
        {
            AppendLog($"[COM] 扫描失败: {ex.Message}");
        }
    }

    private void OnScanPorts(object sender, RoutedEventArgs e)
    {
        var prev = (CmbPort.SelectedItem as ComPortInfo)?.PortName
                   ?? CmbPort.Text?.Trim();
        ScanPorts(prev);
    }

    // ZQWL USB 私有协议固定 2M，与 UI 只读展示一致
    private const int FixedBaud = 2_000_000;

    private bool IsConnected
    {
        get { lock (_transportLock) return _transport is { IsOpen: true }; }
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var port = SelectedPortName();
        if (string.IsNullOrWhiteSpace(port))
        {
            SetState("失败", Colors.Crimson, "请先扫描并选择 COM 口");
            return;
        }
        lock (_transportLock)
        {
            if (_transport is { IsOpen: true })
            {
                AppendLog($"[连接] 已连接 {port}，勿重复连接");
                return;
            }
            try
            {
                _transport?.Dispose();
                _transport = new ZqwlSerialTransport(port, FixedBaud);
                // 原始串口收发全部进运行日志，便于调试超时/无应答
                _transport.OnRawLog += m => AppendLog("[SER] " + m);
                _transport.Open();
            }
            catch (Exception ex)
            {
                _transport?.Dispose();
                _transport = null;
                SetState("失败", Colors.Crimson, $"连接失败: {ex.Message}");
                AppendLog($"[连接] {port} 失败: {ex.Message}");
                UpdateConnectUi();
                return;
            }
        }
        AppendLog($"[连接] CAN 打开 {port} @ {FixedBaud}");
        SetState("已连接", Colors.ForestGreen, port);
        UpdateConnectUi();
    }

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        StopQiPoll("连接断开");
        DisconnectTransport();
        AppendLog("[断开] 已关闭 COM 口");
        SetState("已断开", Colors.DimGray);
        UpdateConnectUi();
    }

    private void DisconnectTransport()
    {
        lock (_transportLock)
        {
            try { _transport?.Dispose(); } catch { /* ignore */ }
            _transport = null;
        }
    }

    private void UpdateConnectUi()
    {
        bool on = IsConnected;
        BtnConnect.IsEnabled = !on;
        BtnDisconnect.IsEnabled = on;
        CmbPort.IsEnabled = !on;
    }

    private string SelectedPortName()
        => (CmbPort.SelectedItem as ComPortInfo)?.PortName
           ?? CmbPort.Text?.Trim()
           ?? "";

    private void FocusSnBox()
    {
        TxtSn.Focus();
        TxtSn.SelectAll();
    }

    /// <summary>操作中统一置灰，防止重复触发；结束/超时恢复。</summary>
    private void SetBusy(bool busy, string? label = null)
    {
        void Apply()
        {
            _busy = busy;
            Button[] disable =
            {
                BtnSnRead, BtnSnWrite, BtnReadVersion,
                BtnOta, BtnIap, BtnQiStatus, BtnQiSetPower, BtnChargeOn, BtnChargeOff,
                BtnLinkProbe,
            };
            foreach (var b in disable)
                b.IsEnabled = !busy;
            // 连接/断开：空闲时按实际连接态恢复，忙碌时强制置灰
            if (busy)
            {
                BtnConnect.IsEnabled = false;
                BtnDisconnect.IsEnabled = false;
            }
            else
            {
                UpdateConnectUi();
                ApplyQiStatusGate();
            }
            BtnStop.IsEnabled = busy;
            if (label is not null && busy)
                SetState(label, Colors.DarkOrange);
            if (!busy) FocusSnBox();
        }
        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.Invoke(Apply);
    }

    /// <summary>
    /// 充电未开启/关闭时禁用「Qi 状态检测」按钮并浮窗（ToolTip）提示原因：
    /// Qi 芯片由 PB2 供电，未开充电即断电，检测无意义。同时停掉持续检查。
    /// </summary>
    private void ApplyQiStatusGate()
    {
        if (!_chargeEnabled)
            StopQiPoll("充电未开启");
        UpdateQiStatusButton();
    }

    private void SetState(string text, Color color, string detail = "")
    {
        TxtState.Text = text;
        TxtState.Foreground = new SolidColorBrush(color);
        TxtStateDetail.Text = detail;
    }

    /// <summary>固件传输期间过滤的协议噪声前缀（逐帧级，量大且与进度无关）。</summary>
    private static readonly string[] NoisePrefixes =
    {
        "[TX]", "[RX]", "[RX?]", "[SER]", "[CAN-TX]", "[CAN-RX]",
    };

    /// <summary>错误关键字：即便带噪声前缀也保留（错误必须可见）。</summary>
    private static readonly string[] ErrorKeywords =
    {
        "超时", "失败", "异常", "错误", "NRC", "无应答", "不匹配",
    };

    private static bool IsNoise(string msg)
    {
        // NRC 0x78 responsePending 是擦除/下载期间的正常等待心跳，不算错误
        if (msg.Contains("responsePending"))
            return true;
        foreach (var kw in ErrorKeywords)
            if (msg.Contains(kw))
                return false;
        foreach (var p in NoisePrefixes)
            if (msg.StartsWith(p, StringComparison.Ordinal))
                return true;
        return false;
    }

    private void AppendLog(string msg)
    {
        if (_quietLogs && IsNoise(msg))
            return;
        Dispatcher.Invoke(() =>
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n";
            // 追加文本而非 Text += line（避免整串重排）；无选中时跟滚到底，
            // 有选中时不打扰用户正在框选的文本
            TxtLog.AppendText(line);
            if (TxtLog.SelectedText.Length == 0)
                TxtLog.ScrollToEnd();
        });
    }

    /// <summary>
    /// 写操作记录。msg 可含换行（如版本四行），块内自动换行显示。
    /// 格式：
    ///   [时间] STATUS  名称
    ///     详情行1
    ///     详情行2
    /// </summary>
    private void AppendResult(string status, string name, string msg)
    {
        Dispatcher.Invoke(() =>
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {status}  {name}");
            if (!string.IsNullOrEmpty(msg))
            {
                foreach (var line in msg.Split('\n'))
                    sb.AppendLine("  " + line.TrimEnd('\r'));
            }
            LstResult.Items.Add(sb.ToString().TrimEnd());
            LstResult.ScrollIntoView(LstResult.Items[^1]);
        });
    }

    /// <summary>多行详情的操作记录（PASS 绿块内容同样换行）。</summary>
    private void Pass(string name, string msg)
    {
        AppendResult("PASS", name, msg);
        SetState("完成", Colors.ForestGreen, FirstLine(msg));
        AppendLog($"[PASS] {name}: {FirstLine(msg)}");
        if (msg.Contains('\n'))
            foreach (var line in msg.Split('\n').Skip(1))
                AppendLog("       " + line.TrimEnd('\r'));
    }

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        return i < 0 ? s : s[..i].TrimEnd('\r');
    }

    /// <summary>
    /// 执行一次设备操作：自动置灰按钮、超时保护、完成后恢复。
    /// body 返回 false 表示已自行提示失败（不再报「完成」）；异常在内部消化为 FAIL。
    /// </summary>
    private async Task RunOpAsync(string name, TimeSpan timeout,
        Func<UdsClient, CancellationToken, Task<bool>> body)
    {
        if (IsBusyGuard()) return;
        SetBusy(true, name + "…");
        AppendLog($"==== 开始 {name}（超时 {timeout.TotalSeconds:F0}s）====");
        _cts = new CancellationTokenSource(timeout);
        var ct = _cts.Token;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ZqwlSerialTransport transport;
        lock (_transportLock)
        {
            if (_transport is not { IsOpen: true })
            {
                SetBusy(false);
                SetState("未连接", Colors.Crimson, "请先点「连接」打开 COM 口");
                AppendLog($"==== 跳过 {name}：未连接 ====");
                return;
            }
            transport = _transport;
        }
        try
        {
            string failMsg = "";
            bool bodyOk = false;
            await Task.Run(async () =>
            {
                try
                {
                    AppendLog("CAN 复用已连接端口");
                    var iso = new IsoTpClient(transport);
                    // 只挂 uds.OnLog：UdsClient 已转发 IsoTp 日志，避免双份
                    var uds = new UdsClient(iso);
                    uds.OnLog += m => AppendLog(m);
                    // 与 Qi 状态持续检查互斥：等轮询空档再执行本次操作
                    await _udsGate.WaitAsync(ct);
                    try
                    {
                        bodyOk = await body(uds, ct);
                    }
                    finally
                    {
                        _udsGate.Release();
                    }
                }
                catch (Exception ex)
                {
                    failMsg = FormatOpError(ex);
                    bodyOk = false;
                }
            }, ct);

            sw.Stop();
            if (bodyOk && failMsg.Length == 0)
            {
                AppendLog($"==== 完成 {name} 耗时 {sw.Elapsed.TotalSeconds:F1}s ====");
            }
            else if (failMsg.Length > 0)
            {
                AppendLog($"==== 失败 {name} 耗时 {sw.Elapsed.TotalSeconds:F1}s: {failMsg} ====");
                AppendResult("FAIL", name, failMsg);
                SetState("失败", Colors.Crimson, failMsg);
            }
            // bodyOk==false 且无 failMsg：body 已自行提示过 FAIL，不重复
        }
        catch (Exception ex)
        {
            sw.Stop();
            var msg = FormatOpError(ex);
            AppendLog($"==== 失败 {name} 耗时 {sw.Elapsed.TotalSeconds:F1}s: {msg} ====");
            AppendResult("FAIL", name, msg);
            SetState("失败", Colors.Crimson, msg);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    /// <summary>把异常整理成用户可读的失败原因（含 NRC 解释）。</summary>
    private static string FormatOpError(Exception ex) => ex switch
    {
        UdsException u => u.Explain(),
        OperationCanceledException => "操作已取消/超时",
        _ => ex.Message,
    };

    /// <summary>全局异常兜底：只记日志，不崩溃、不停监听。</summary>
    public void ReportCrash(Exception ex)
    {
        try
        {
            AppendLog($"[异常已捕获] {ex.GetType().Name}: {ex.Message}");
            AppendLog("[异常] 操作已终止，请重试或检查连接");
            SetState("异常", Colors.Crimson, ex.Message);
            StopQiPoll("异常");
            SetBusy(false);
        }
        catch { /* ignore */ }
    }

    private bool IsBusyGuard()
    {
        // 只认 _busy 标志；_cts 用完即弃，避免「结束后仍占用」导致按钮点了没反应
        return _busy;
    }

    // ---------- SN 扫码 ----------

    private void OnSnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var sn = TxtSn.Text.Trim();
        TxtSnHint.Text = sn.Length == 0
            ? "输入为空，请扫码或手输"
            : $"已录入: {sn}（可点「写入」保存到设备）";
        if (sn.Length > 0) AppendLog($"[扫码] SN = {sn}");
        TxtSn.SelectAll();
    }

    private void OnSnGotFocus(object sender, RoutedEventArgs e) => TxtSn.SelectAll();

    private void OnSnClear(object sender, RoutedEventArgs e)
    {
        TxtSn.Clear();
        TxtSnHint.Text = "";
        FocusSnBox();
    }

    private async void OnSnRead(object sender, RoutedEventArgs e)
        => await RunOpAsync("读 SN", TimeSpan.FromSeconds(20), async (uds, ct) =>
        {
            // 真休眠=整板断电（fw 1.1.40）：单帧请求会唤醒设备但丢在重启窗口里，
            // 先唤醒 burst + 等 UDS 就绪再读
            var (ready, readyErr) = await WaitUdsReadyAsync(uds, ct, waitSec: 8, log: AppendLog);
            if (!ready)
            {
                await UiAsync(() =>
                {
                    AppendResult("FAIL", "读 SN", readyErr);
                    SetState("失败", Colors.Crimson, readyErr);
                });
                return false;
            }
            var r = await Task.Run(() =>
                uds.TryRequest(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x8C }, timeoutMs: 4000), ct);
            if (!r.Ok)
            {
                // NRC 0x31 = SN 未写入（Flash 空），不是设备故障
                bool snEmpty = r.Nrc == 0x31;
                await UiAsync(() =>
                {
                    if (snEmpty)
                    {
                        TxtSnHint.Text = "SN 未写入（Flash 为空），请扫码后点「写入」";
                        AppendResult("EMPTY", "读 SN", "SN 未写入，请先写入");
                        SetState("SN 未写", Colors.DarkOrange, "设备无 SN，请写入");
                        AppendLog("[读 SN] NRC 0x31：SN 未写入（Flash 中为空）");
                    }
                    else
                    {
                        AppendResult("FAIL", "读 SN", r.Error);
                        SetState("失败", Colors.Crimson, r.Error);
                        AppendLog($"[FAIL] 读 SN: {r.Error}");
                    }
                });
                return false;
            }
            var sn = DecodeSn(r.Data);
            await UiAsync(() =>
            {
                TxtSn.Text = sn;
                TxtSnHint.Text = $"设备 SN: {sn}";
                Pass("读 SN", sn);
            });
            return true;
        });

    private async void OnSnWrite(object sender, RoutedEventArgs e)
    {
        var sn = TxtSn.Text.Trim();
        if (sn.Length == 0) { TxtSnHint.Text = "请先扫码或输入 SN"; return; }
        if (sn.Length > 32) { TxtSnHint.Text = $"SN 过长 {sn.Length}>32"; return; }

        await RunOpAsync("写 SN", TimeSpan.FromSeconds(60), async (uds, ct) =>
        {
            // 设备可能刚 BOOTUP，先等 UDS 栈就绪
            var (ready, readyErr) = await WaitUdsReadyAsync(uds, ct, waitSec: 8, log: AppendLog);
            if (!ready)
            {
                await FailAsync("写 SN", readyErr);
                return false;
            }

            UdsResult r = await Task.Run(() => uds.TryRequest(ProtoIds.SidDsc,
                new byte[] { 0x02 }, expectSub: 0x02), ct);
            if (!Must(r)) { await FailAsync("写 SN", r.Error); return false; }
            var signer = RequireSigner();
            if (!await SecurityAccess.UnlockAsync(uds, signer, m => AppendLog("[SA] " + m), ct))
            {
                await FailAsync("写 SN", "SA 解锁失败");
                return false;
            }

            r = await Task.Run(() =>
                uds.TryRequest(ProtoIds.SidWdbi, BuildSnPayload(sn), timeoutMs: 5000,
                    expectSub: 0xF1), ct);
            if (!Must(r)) { await FailAsync("写 SN", r.Error); return false; }
            r = await Task.Run(() =>
                uds.TryRequest(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x8C }, timeoutMs: 4000), ct);
            if (!Must(r)) { await FailAsync("写 SN", r.Error); return false; }
            var readBack = DecodeSn(r.Data);
            if (!string.Equals(readBack, sn, StringComparison.Ordinal))
            {
                await FailAsync("写 SN", $"写后读回不符 期望={sn} 实际={readBack}");
                return false;
            }

            await UiAsync(() =>
            {
                TxtSnHint.Text = $"写入成功: {readBack}";
                Pass("写 SN", readBack);
            });
            return true;
        });
    }

    // ---------- 测试操作按钮 ----------

    private async void OnReadVersion(object sender, RoutedEventArgs e)
        => await RunOpAsync("读版本", TimeSpan.FromSeconds(15), async (uds, ct) =>
        {
            // 若设备刚 BOOTUP，自动多等 1s 再读
            var ver = await Task.Run(() =>
            {
                for (int i = 0; i < 3; i++)
                {
                    var r = uds.TryReadAllVersions(ct);
                    if (r.AllOk || r.Mcu1App is not null) return r;
                    if (i < 2 && r.Errors.Any(e => e.Contains("BOOTUP", StringComparison.OrdinalIgnoreCase)))
                    {
                        AppendLog("[读版本] 检测到 APP 刚 BOOTUP，1s 后自动重试…");
                        Thread.Sleep(1000);
                        continue;
                    }
                    return r;
                }
                return uds.TryReadAllVersions(ct);
            }, ct);

            bool fail = ver.Mcu1App is null && !ver.AllOk;
            await UiAsync(() =>
            {
                foreach (var line in ver.ToDisplayString().Split('\n'))
                    AppendLog("[版本] " + line);
                if (fail)
                {
                    AppendResult("FAIL", "读版本", string.Join("; ", ver.Errors));
                    SetState("失败", Colors.Crimson, "版本读取失败，请确认设备在 APP");
                    return;
                }
                // 全部读到 PASS；部分失败标 PARTIAL
                if (ver.AllOk)
                    Pass("读版本", ver.ToDisplayString());
                else
                {
                    AppendResult("PARTIAL", "读版本", ver.ToDisplayString());
                    SetState("部分完成", Colors.DarkOrange, ver.Summary);
                    AppendLog($"[PARTIAL] 读版本: {ver.Summary}");
                }
            });
            return !fail;
        });

    /// <summary>OTA：MCU1 APP，块 128B，使用 AT32 OTA 固件路径。</summary>
    private async void OnOtaUpgrade(object sender, RoutedEventArgs e)
        => await RunUpgradeAsync("OTA 升级", otaTarget: 0x01, transferBlock: 128,
                                 fwPath: TxtOtaFirmware.Text.Trim(),
                                 timeout: TimeSpan.FromMinutes(20));

    /// <summary>IAP：Qi 芯片 MCU2，块 22B（QI_IAP_MAX_CHUNK），使用 QI IAP 固件路径。</summary>
    private async void OnIapUpgrade(object sender, RoutedEventArgs e)
        => await RunUpgradeAsync("IAP 升级", otaTarget: 0x04, transferBlock: 22,
                                 fwPath: TxtIapFirmware.Text.Trim(),
                                 timeout: TimeSpan.FromMinutes(30));

    private void OnBrowseOtaFirmware(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 AT32 OTA 固件",
            Filter = "固件 (*.bin)|*.bin|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true)
            TxtOtaFirmware.Text = dlg.FileName;
    }

    private void OnBrowseIapFirmware(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 QI IAP 固件",
            Filter = "固件 (*.bin)|*.bin|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true)
            TxtIapFirmware.Text = dlg.FileName;
    }

    /// <summary>
    /// 等 UDS 就绪：设备可能刚 BOOTUP 或在 Sleep，UDS 栈要几百 ms～几秒才起来。
    /// 先发唤醒 burst（≥6 帧 02 3E 00 CC…，DLC=8），再轮询 22 F195，有正响应即就绪。
    /// </summary>
    private static async Task<(bool Ok, string Error)> WaitUdsReadyAsync(
        UdsClient uds, CancellationToken ct, int waitSec = 8, Action<string>? log = null)
    {
        // SIT1145 WUF 唤醒 burst：≥6 帧 02 3E 00 CC…（DLC=8），PNDM=1 严格匹配
        log?.Invoke("[就绪] 发送唤醒 burst (8 帧)…");
        for (int i = 0; i < 8; i++)
        {
            if (ct.IsCancellationRequested) return (false, "已取消");
            uds.SendWake();
            await Task.Delay(250, ct);
        }

        long deadline = Environment.TickCount64 + waitSec * 1000;
        int n = 0;
        while (Environment.TickCount64 < deadline)
        {
            if (ct.IsCancellationRequested) return (false, "已取消");
            n++;
            var r = await Task.Run(() =>
                uds.TryRequest(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x95 },
                    timeoutMs: 1500, ct: ct, expectDid: ProtoIds.DidSwVersion), ct);
            if (r.Ok)
            {
                log?.Invoke($"[就绪] 第 {n} 次探测 UDS 应答，设备就绪");
                return (true, "");
            }
            // 有 NRC 也算栈活着（Boot 可能回 0x31）
            if (r.Nrc != 0)
            {
                log?.Invoke($"[就绪] 第 {n} 次探测有 NRC 0x{r.Nrc:X2}，UDS 栈已活");
                return (true, "");
            }
            // 无应答时再发 2 帧唤醒
            uds.SendWake();
            uds.SendWake();
            log?.Invoke($"[就绪] 第 {n} 次探测无应答，0.5s 后重试…");
            await Task.Delay(500, ct);
        }
        return (false, $"等待 UDS 就绪超时 {waitSec}s（设备可能仍在 Boot 或未上电）");
    }

    private async Task RunUpgradeAsync(string name, byte otaTarget, int transferBlock,
                                       string fwPath, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(fwPath) || !File.Exists(fwPath))
        {
            SetState("失败", Colors.Crimson, $"固件不存在: {fwPath}");
            AppendResult("FAIL", name, $"请先选择 .bin 固件（当前: {fwPath}）");
            return;
        }

        await RunOpAsync(name, timeout, async (uds, ct) =>
        {
            byte[] raw = await Task.Run(() => File.ReadAllBytes(fwPath), ct);
            string verHint = "";
            if (otaTarget == 0x01)
            {
                // AT32 OTA 走 XATO 签名镜像
                var image = QiChargerStationTool.Core.Imaging.XatoImage.Parse(raw);
                verHint = image.ExtractSwVersion() ?? "";
                AppendLog($"[OTA] XATO 镜像 {raw.Length}B ver={verHint} 目标=MCU1/APP");
            }
            else
            {
                // Qi IAP 原始 bin，无需 XATO 头
                AppendLog($"[IAP] 原始固件 {raw.Length}B 目标=MCU2/Qi");
            }

            // 刷写前先等 APP/UDS 就绪，避免踩到 Boot 窗口
            var (ready, readyErr) = await WaitUdsReadyAsync(uds, ct, waitSec: 8, log: AppendLog);
            if (!ready)
            {
                await FailAsync(name, readyErr);
                return false;
            }

            async Task<bool> MustStep(UdsResult r, string step)
            {
                if (r.Ok) return true;
                await FailAsync(name, $"{step}: {r.Error}");
                return false;
            }

            // 10 02 必须校验应答子功能 0x02：只认 SID(0x50) 时，队列里迟到的
            // 50 03（上个 10 03 操作残留）会被误判为成功 → 设备没进编程会话 →
            // 2E 20 10 回 0x22。ZQWL 偶发丢帧 → 未通过则补发一次。
            // 2E 20 10 门禁（编程会话+SA）拿 0x22/0x33 = 会话/安全状态已丢
            // （设备复位/S3 等）：整套 10 02 + SA 重做一次再试。
            async Task<(bool Ok, byte Nrc)> TryGateAsync()
            {
                UdsResult d = UdsResult.Fail("未发送");
                for (int t = 1; t <= 2; t++)
                {
                    d = await Task.Run(() => uds.TryRequest(ProtoIds.SidDsc,
                        new byte[] { 0x02 }, expectSub: 0x02), ct);
                    if (d.Ok) break;
                    AppendLog($"[10 02] 第 {t} 次失败: {d.Error}");
                }
                if (!d.Ok) return (false, d.Nrc);
                if (!await SecurityAccess.UnlockAsync(uds, RequireSigner(),
                        m => AppendLog("[SA] " + m), ct))
                    return (false, (byte)0);
                var w = await Task.Run(() => uds.TryRequest(ProtoIds.SidWdbi,
                    new byte[] { 0x20, 0x10, otaTarget }, timeoutMs: 4000,
                    expectSub: 0x20), ct);
                if (!w.Ok)
                {
                    AppendLog($"[2E 20 10] 失败: {w.Error}");
                    return (false, w.Nrc);
                }
                return (true, (byte)0);
            }

            var (gateOk, gateNrc) = await TryGateAsync();
            if (!gateOk && (gateNrc is 0x22 or 0x33))
            {
                AppendLog($"[恢复] NRC 0x{gateNrc:X2} 会话/安全丢失，重做 10 02 + SA 后重试 2E 20 10…");
                (gateOk, gateNrc) = await TryGateAsync();
            }
            if (!gateOk)
            {
                await FailAsync(name, gateNrc != 0
                    ? $"2E 20 10: NRC 0x{gateNrc:X2}（重试后仍失败）"
                    : "门禁失败（10 02 / SA）");
                return false;
            }
            AppendLog($"2E 20 10 {otaTarget:X2} 选择目标 OK");

            // 擦除+下载+传输期间过滤协议逐帧噪声（[TX]/[RX]/[SER]/NRC 0x78 心跳），
            // 只留进度百分比与错误——方便看运行到百分之多少
            int size = raw.Length;
            _quietLogs = true;
            try
            {
                if (!await MustStep(await Task.Run(() => uds.TryRequest(ProtoIds.SidRc,
                        new byte[] { 0x01, 0xFF, 0x00 }, timeoutMs: 15_000, waitPendingMs: 90_000), ct), "31 擦除"))
                    return false;
                AppendLog("31 擦除完成");

                if (!await MustStep(await Task.Run(() => uds.TryRequest(ProtoIds.SidRd, new byte[]
                {
                    0x00, 0x44, 0x08, 0x01, 0x00, 0x00,
                    (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size,
                }, timeoutMs: 8000), ct), "34 RequestDownload"))
                    return false;
                AppendLog($"34 RequestDownload {size}B @ 0x08010000 block={transferBlock}");

                int off = 0, seq = 1, blk = 0;
                int nblocks = (size + transferBlock - 1) / transferBlock;
                var img = raw;
                int lastPct = -1;
                long sumSend = 0, sumRecv = 0, sumFc = 0;
                var swTr = System.Diagnostics.Stopwatch.StartNew();
                while (off < size)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = Math.Min(transferBlock, size - off);
                    var payload = new byte[1 + n];
                    payload[0] = (byte)(seq & 0xFF);
                    Buffer.BlockCopy(img, off, payload, 1, n);
                    // 直接调用（整个 body 已在 Task.Run 线程池里），去掉每块一次
                    // Task.Run 的调度开销
                    if (!await MustStep(uds.TryRequest(ProtoIds.SidTd, payload,
                            timeoutMs: 8000, waitPendingMs: 30_000), $"36 块 {blk + 1}"))
                        return false;
                    sumSend += uds.LastSendMs;
                    sumRecv += uds.LastRecvMs;
                    sumFc += uds.LastFcTries;
                    off += n;
                    blk++;
                    seq = (seq + 1) & 0xFF;

                    // 每 2% 或首尾报一次进度：百分比一眼可见
                    int pct = size > 0 ? (int)(off * 100L / size) : 100;
                    if (pct != lastPct && (pct % 2 == 0 || off >= size))
                    {
                        lastPct = pct;
                        AppendLog($"[进度] {pct}%  {blk}/{nblocks} 块  {off}/{size} 字节  " +
                                  $"均 {swTr.Elapsed.TotalMilliseconds / blk:F1}ms/块（发 {sumSend / blk} + 等 {sumRecv / blk}，FC重试 {sumFc}/{blk}）");
                        await UiAsync(() => SetState($"升级中 {pct}%", Colors.DarkOrange));
                    }
                }
                AppendLog($"[计时] 传输 {swTr.Elapsed.TotalSeconds:F1}s / {nblocks} 块 = " +
                          $"{swTr.Elapsed.TotalMilliseconds / nblocks:F1}ms/块（组帧发送 {sumSend / nblocks}ms + 等应答 {sumRecv / nblocks}ms，FC重试 {sumFc}/{nblocks}）");

                try
                {
                    await Task.Run(() => uds.Request(ProtoIds.SidRte,
                        Array.Empty<byte>(), timeoutMs: 8000, waitPendingMs: 60_000), ct);
                    AppendLog("37 TransferExit 确认，设备自复位");
                }
                catch (Exception ex)
                {
                    AppendLog($"37 异常（设备可能已自复位）: {ex.Message}");
                }
            }
            finally
            {
                _quietLogs = false;
            }

            await UiAsync(() => Pass(name, $"{size}B 传输完成"));
            return true;
        });
    }

    private async void OnChargeOn(object sender, RoutedEventArgs e)
        => await RunChargeAsync(enable: true);

    private async void OnChargeOff(object sender, RoutedEventArgs e)
        => await RunChargeAsync(enable: false);

    /// <summary>链路自检：3E 00 + 22 F195，把收发都打进日志，区分「无回包 / 协议不匹配」。</summary>
    private async void OnLinkProbe(object sender, RoutedEventArgs e)
        => await RunOpAsync("链路自检", TimeSpan.FromSeconds(20), async (uds, ct) =>
        {
            // 真休眠=整板断电时直接探测会误报「硬件/未上电」：先唤醒再自检
            var (ready, readyErr) = await WaitUdsReadyAsync(uds, ct, waitSec: 8, log: AppendLog);
            if (!ready)
            {
                await UiAsync(() =>
                {
                    AppendLog($"[自检] 唤醒/就绪失败: {readyErr}");
                    AppendLog("[自检] 请查看上方 [CAN-TX]/[CAN-RX]：有 CAN-RX=协议/ID 问题；无 CAN-RX=硬件/盒子/设备未上电");
                    AppendResult("FAIL", "链路自检", readyErr);
                    SetState("失败", Colors.Crimson, readyErr);
                });
                return false;
            }
            await Task.Run(() =>
            {
                try
                {
                    uds.SendWake();
                    AppendLog("[自检] 已发 3E 00");
                }
                catch (Exception ex)
                {
                    AppendLog($"[自检] 3E 00 异常: {ex.Message}");
                }
            }, ct);

            var (ok, ver, err) = await Task.Run(() => uds.TryReadSwVersion(ct), ct);
            if (!ok)
            {
                await UiAsync(() =>
                {
                    AppendLog($"[自检] 22 F195 失败: {err}");
                    AppendLog("[自检] 请查看上方 [CAN-TX]/[CAN-RX]：有 CAN-RX=协议/ID 问题；无 CAN-RX=硬件/盒子/设备未上电");
                    AppendResult("FAIL", "链路自检", err);
                    SetState("失败", Colors.Crimson, err);
                });
                return false;
            }
            await UiAsync(() =>
            {
                AppendLog($"[自检] 应答版本: {ver}");
                Pass("链路自检", $"F195 = {ver}");
            });
            return true;
        });

    /// <summary>
    /// 「Qi 状态检测」开关：点击开始持续检测（Qi 芯片持续上报，后台每 ~1s 刷新
    /// 仪表盘），再次点击停止（运行中橙底示意）。充电未开启时按钮禁用
    /// （见 ApplyQiStatusGate）。
    /// </summary>
    private async void OnQiStatusQuery(object sender, RoutedEventArgs e)
    {
        if (_pollRunning)
        {
            StopQiPoll("用户停止");
            SetState("就绪", Colors.DimGray, "Qi 状态检测已停止");
            return;
        }
        if (!_chargeEnabled)
        {
            AppendLog("[状态检测] 充电未开启，Qi 芯片断电，无法检测（按钮已禁用）");
            return;
        }
        await RunOpAsync("Qi 状态检测启动", TimeSpan.FromSeconds(20), async (uds, ct) =>
        {
            // 真休眠=整板断电：先唤醒 burst + 等 UDS 就绪再进入轮询
            var (ready, readyErr) = await WaitUdsReadyAsync(uds, ct, waitSec: 8, log: AppendLog);
            if (!ready)
            {
                await UiAsync(() =>
                {
                    AppendResult("FAIL", "Qi 状态检测启动", readyErr);
                    SetState("失败", Colors.Crimson, readyErr);
                });
                return false;
            }
            await UiAsync(StartQiPollUi);
            return true;
        });
    }

    /// <summary>进入持续检测态：置开关标志、按钮样式，启动后台轮询任务。</summary>
    private void StartQiPollUi()
    {
        _pollRunning = true;
        _pollCts = new CancellationTokenSource();
        UpdateQiStatusButton();
        AppendLog("[状态检测] 已开始：仪表盘约每 1s 刷新一次；再次点击「Qi 状态检测」结束");
        SetState("状态检测中", Colors.DarkOrange, "持续读取 Qi 状态，再次点击按钮停止");
        _ = QiPollLoopAsync(_pollCts.Token);
    }

    /// <summary>后台轮询：每 ~1s 读一次 Qi 全量状态并刷仪表盘（不打日志/操作记录）。</summary>
    private async Task QiPollLoopAsync(CancellationToken ct)
    {
        int failStreak = 0;
        while (!ct.IsCancellationRequested)
        {
            // 与其它 UDS 操作互斥：占用中（如 OTA/写 SN）本轮直接跳过
            if (await _udsGate.WaitAsync(0, ct))
            {
                try
                {
                    ZqwlSerialTransport transport;
                    lock (_transportLock) { transport = _transport; }
                    if (transport is { IsOpen: true })
                    {
                        var st = await Task.Run(() =>
                        {
                            var iso = new IsoTpClient(transport);
                            var uds = new UdsClient(iso);
                            return uds.ReadQiChipStatus(ct);
                        }, ct);
                        failStreak = 0;
                        await UiAsync(() => UpdateQiPanel(st));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // 连续失败只打首条与每第 10 条，避免刷屏
                    failStreak++;
                    if (failStreak == 1 || failStreak % 10 == 0)
                        await UiAsync(() => AppendLog($"[状态检测] 读取失败（连续 {failStreak} 次）: {FormatOpError(ex)}"));
                }
                finally
                {
                    _udsGate.Release();
                }
            }
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>停止持续检测（幂等）：取消轮询任务并恢复按钮。</summary>
    private void StopQiPoll(string reason)
    {
        if (_pollCts == null && !_pollRunning) return;
        _pollRunning = false;
        try { _pollCts?.Cancel(); } catch { /* ignore */ }
        _pollCts = null;
        UpdateQiStatusButton();
        AppendLog($"[状态检测] 已停止（{reason}）");
    }

    /// <summary>
    /// 刷新「Qi 状态检测」按钮：文案恒定，运行态仅以橙底区分；
    /// 未开启充电时禁用并浮窗提示原因（ToolTipService.ShowOnDisabled）。
    /// </summary>
    private void UpdateQiStatusButton()
    {
        BtnQiStatus.Content = "Qi 状态检测";
        if (!_chargeEnabled)
        {
            BtnQiStatus.IsEnabled = false;
            BtnQiStatus.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
            BtnQiStatus.ToolTip =
                "充电未开启：Qi 芯片未供电（PB2 关闭），无法检测。\n请先点击「开启充电」。";
        }
        else if (_pollRunning)
        {
            BtnQiStatus.IsEnabled = !_busy;
            BtnQiStatus.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x80));
            BtnQiStatus.ToolTip = "正在持续检测（仪表盘约每 1s 刷新）；点击停止";
        }
        else
        {
            BtnQiStatus.IsEnabled = !_busy;
            BtnQiStatus.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
            BtnQiStatus.ToolTip = "点击开始持续检测（Qi 芯片持续上报，仪表盘约每 1s 刷新）；再次点击停止";
        }
    }

    /// <summary>把 Qi 状态刷进左侧仪表盘（充电功率 + 保护位指示灯 + 摘要行）。</summary>
    private void UpdateQiPanel(QiChipStatus st)
    {
        // 充电功率大字（mW 原值 + W 换算）
        if (st.OutputPowerMw is { } mw)
        {
            TxtQiPower.Text = $"{mw} mW";
            TxtQiPowerW.Text = $"（{mw / 1000.0:F2} W）";
        }
        else
        {
            TxtQiPower.Text = "— mW";
            TxtQiPowerW.Text = "（— W）";
        }

        // 保护位指示灯：激活=红底红字，未激活=绿底绿字，未读到=灰底
        var raw = st.StatusRaw;
        SetProtectionChip(BdOvp, "OVP 过压", raw, QiChipStatusCodec.QiStatusOvp);
        SetProtectionChip(BdUvp, "UVP 欠压", raw, QiChipStatusCodec.QiStatusUvp);
        SetProtectionChip(BdOcp, "OCP 过流", raw, QiChipStatusCodec.QiStatusOcp);
        SetProtectionChip(BdOtp, "OTP 过温", raw, QiChipStatusCodec.QiStatusOtp);
        SetProtectionChip(BdFod, "FOD 异物", raw, QiChipStatusCodec.QiStatusFod);

        // 充电次数（DID 0x210E，NVM 累计）
        TxtChargeCount.Text = st.ChargeCount is { } cc ? $"{cc} 次" : "— 次";

        // 摘要行
        var temp = st.PcbTempC is { } t ? $"{t}℃" : "—";
        var limit = st.PowerLimitName;
        var active = st.ActiveProtections;
        var protText = active is null ? "保护位: —" :
            active.Count > 0 ? $"保护触发: {string.Join(" / ", active)}" : "保护位: 无";
        var countText = st.ChargeCount is { } c2 ? $"{c2} 次" : "—";
        TxtQiSummary.Text = $"充电状态: {st.ChargeStateName}   PCB: {temp}   档位: {limit}   充电次数: {countText}\n{protText}";
    }

    /// <summary>单个保护位指示灯着色：null=未知(灰) / 置位=红 / 未置位=绿。</summary>
    private static void SetProtectionChip(Border chip, string label, byte? statusRaw, byte bit)
    {
        var tb = (TextBlock)chip.Child!;
        tb.Text = label;
        if (statusRaw is not { } raw)
        {
            chip.Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
            tb.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        }
        else if ((raw & bit) != 0)
        {
            chip.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xEB, 0xEE));
            tb.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        }
        else
        {
            chip.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9));
            tb.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        }
    }

    /// <summary>
    /// 设置 Qi 功率：10 03 + SA → 2E 210D。UI 5W/10W/15W 映射档位码 0x01/0x02/0x03。
    /// </summary>
    private async void OnQiSetPower(object sender, RoutedEventArgs e)
    {
        if (CmbQiPower.SelectedItem is not ComboBoxItem item ||
            !byte.TryParse(item.Tag?.ToString(), out var powerCode))
        {
            SetState("失败", Colors.Crimson, "请选择功率档位");
            return;
        }
        string label = item.Content?.ToString() ?? powerCode.ToString();

        await RunOpAsync($"设置 Qi 功率 {label}", TimeSpan.FromSeconds(60), async (uds, ct) =>
        {
            var (ready, readyErr) = await WaitUdsReadyAsync(uds, ct, waitSec: 8, log: AppendLog);
            if (!ready)
            {
                await FailAsync($"设置 Qi 功率 {label}", readyErr);
                return false;
            }

            var r = await Task.Run(() => uds.TryRequest(ProtoIds.SidDsc,
                new byte[] { 0x03 }, expectSub: 0x03), ct);
            if (!Must(r)) { await FailAsync($"设置 Qi 功率 {label}", r.Error); return false; }
            AppendLog("进入扩展会话 10 03");
            var signer = RequireSigner();
            if (!await SecurityAccess.UnlockAsync(uds, signer, m => AppendLog("[SA] " + m), ct))
            {
                await FailAsync($"设置 Qi 功率 {label}", "SA 解锁失败");
                return false;
            }

            var wr = await Task.Run(() => uds.TryWriteQiPowerLimit(powerCode), ct);
            AppendLog($"2E 210D 写入 0x{powerCode:X2} ({label})");
            if (!Must(wr)) { await FailAsync($"设置 Qi 功率 {label}", wr.Error); return false; }
            var rd = await Task.Run(() => uds.TryReadDid(ProtoIds.DidPowerLimit), ct);
            if (rd.Data is null)
            {
                await FailAsync($"设置 Qi 功率 {label}", rd.Error);
                return false;
            }
            var readBack = QiChargerStationTool.Core.Protocol.QiChipStatusCodec.ReadU8(rd.Data);
            if (readBack != powerCode)
            {
                await FailAsync($"设置 Qi 功率 {label}", $"写后读回不符 期望=0x{powerCode:X2} 实际=0x{readBack:X2}");
                return false;
            }

            await UiAsync(() => Pass($"设置 Qi 功率 {label}", $"已设置 {label}（0x{powerCode:X2}），读回一致"));
            return true;
        });
    }

    /// <summary>执行 UDS 请求；失败返回 false 并由调用方提示（不抛异常）。</summary>
    private static bool Must(UdsResult r) => r.Ok;

    private async Task RunChargeAsync(bool enable)
    {
        string name = enable ? "开启充电" : "关闭充电";
        await RunOpAsync(name, TimeSpan.FromSeconds(30), async (uds, ct) =>
        {
            var (ready, readyErr) = await WaitUdsReadyAsync(uds, ct, waitSec: 6, log: AppendLog);
            if (!ready)
            {
                await FailAsync(name, readyErr);
                return false;
            }
            var r = await Task.Run(() => uds.TryRequest(ProtoIds.SidDsc,
                new byte[] { 0x03 }, expectSub: 0x03), ct);
            if (!Must(r))
            {
                await FailAsync(name, r.Error);
                return false;
            }
            var signer = RequireSigner();
            if (!await SecurityAccess.UnlockAsync(uds, signer, m => AppendLog("[SA] " + m), ct))
            {
                await FailAsync(name, "SA 解锁失败");
                return false;
            }
            r = await Task.Run(() => uds.TryRequest(ProtoIds.SidWdbi,
                new byte[] { 0x21, 0x01, (byte)(enable ? 0x01 : 0x00) }, timeoutMs: 4000,
                expectSub: 0x21), ct);
            if (!Must(r))
            {
                await FailAsync(name, r.Error);
                return false;
            }
            _chargeEnabled = enable;
            await UiAsync(() =>
            {
                Pass(name, enable ? "已开启" : "已关闭");
                ApplyQiStatusGate();
                if (!enable)
                    AppendLog("[充电] 已关闭：Qi 状态检测按钮已禁用（Qi 芯片断电，检测无意义）");
            });
            return true;
        });
    }

    private Task FailAsync(string name, string err) => UiAsync(() =>
    {
        AppendResult("FAIL", name, err);
        SetState("失败", Colors.Crimson, err);
        AppendLog($"[FAIL] {name}: {err}");
    });

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        AppendLog("[停止] 用户中断当前操作");
        SetState("已停止", Colors.DimGray);
    }

    private void OnClearResult(object sender, RoutedEventArgs e) => LstResult.Items.Clear();

    // ---------- 日志复制 / 导出 ----------

    private string CollectLogText() => TxtLog.Text;

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            // 有选中则复制选中，否则复制全部
            string text = TxtLog.SelectedText.Length > 0 ? TxtLog.SelectedText : TxtLog.Text;
            if (text.Length == 0)
            {
                AppendLog("[日志] 无内容可复制");
                return;
            }
            Clipboard.SetText(text);
            AppendLog($"[日志] 已复制 {TxtLog.LineCount} 行到剪贴板，可直接 Ctrl+V 粘贴");
        }
        catch (Exception ex)
        {
            AppendLog($"[日志] 复制失败: {ex.Message}");
        }
    }

    private void OnExportLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new SaveFileDialog
            {
                Title = "导出运行日志",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                FileName = $"station_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, CollectLogText());
            AppendLog($"[日志] 已导出 {dlg.FileName}");
        }
        catch (Exception ex)
        {
            AppendLog($"[日志] 导出失败: {ex.Message}");
        }
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        TxtLog.Clear();
        AppendLog("[日志] 已清空");
    }

    // ---------- helpers ----------

    private static SeedSigner RequireSigner()
        => SeedSigner.FromDefaultKey()
           ?? throw new InvalidOperationException(
               "缺少私钥 docs/keys/private.pem（仓库 docs/keys），无法 SA 解锁");

    private static byte[] BuildSnPayload(string sn)
    {
        var body = new byte[32];
        for (int i = 0; i < 32; i++) body[i] = 0x20;
        var ascii = System.Text.Encoding.ASCII.GetBytes(sn);
        Array.Copy(ascii, body, Math.Min(ascii.Length, 32));
        var payload = new byte[34];
        payload[0] = 0xF1;
        payload[1] = 0x8C;
        body.CopyTo(payload, 2);
        return payload;
    }

    private static string DecodeSn(byte[] rx)
    {
        if (rx.Length < 4) throw new InvalidOperationException("F18C/F195 响应过短");
        int len = rx.Length - 3;
        while (len > 0 && (rx[3 + len - 1] is 0x00 or 0x20)) len--;
        return System.Text.Encoding.ASCII.GetString(rx, 3, len);
    }

    private async Task UiAsync(Action action) => await Dispatcher.InvokeAsync(action);
}
