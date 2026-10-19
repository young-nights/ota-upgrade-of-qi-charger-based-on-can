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
            }
            BtnStop.IsEnabled = busy;
            Cursor = busy ? Cursors.Wait : Cursors.Arrow;
            if (label is not null && busy)
                SetState(label, Colors.DarkOrange);
            if (!busy) FocusSnBox();
        }
        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.Invoke(Apply);
    }

    private void SetState(string text, Color color, string detail = "")
    {
        TxtState.Text = text;
        TxtState.Foreground = new SolidColorBrush(color);
        TxtStateDetail.Text = detail;
    }

    private void AppendLog(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            LstLog.Items.Add($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
            LstLog.ScrollIntoView(LstLog.Items[^1]);
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
                    bodyOk = await body(uds, ct);
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
        => await RunOpAsync("读 SN", TimeSpan.FromSeconds(15), async (uds, ct) =>
        {
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

            UdsResult r = await Task.Run(() => uds.TryRequest(ProtoIds.SidDsc, new byte[] { 0x02 }), ct);
            if (!Must(r)) { await FailAsync("写 SN", r.Error); return false; }
            var signer = RequireSigner();
            if (!await SecurityAccess.UnlockAsync(uds, signer, m => AppendLog("[SA] " + m), ct))
            {
                await FailAsync("写 SN", "SA 解锁失败");
                return false;
            }

            r = await Task.Run(() =>
                uds.TryRequest(ProtoIds.SidWdbi, BuildSnPayload(sn), timeoutMs: 5000), ct);
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
    /// 等 UDS 就绪：设备可能刚 BOOTUP，UDS 栈要几百 ms～几秒才起来。
    /// 轮询 3E 00 / 22 F195，有正响应即就绪。最多 waitSec 秒。
    /// </summary>
    private static async Task<(bool Ok, string Error)> WaitUdsReadyAsync(
        UdsClient uds, CancellationToken ct, int waitSec = 8, Action<string>? log = null)
    {
        long deadline = Environment.TickCount64 + waitSec * 1000;
        int n = 0;
        while (Environment.TickCount64 < deadline)
        {
            if (ct.IsCancellationRequested) return (false, "已取消");
            n++;
            var r = await Task.Run(() =>
                uds.TryRequest(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x95 },
                    timeoutMs: 1500, ct: ct), ct);
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

            if (!await MustStep(await Task.Run(() =>
                    uds.TryRequest(ProtoIds.SidDsc, new byte[] { 0x02 }), ct), "10 02"))
                return false;
            var signer = RequireSigner();
            if (!await SecurityAccess.UnlockAsync(uds, signer, m => AppendLog("[SA] " + m), ct))
            {
                await FailAsync(name, "SA 解锁失败");
                return false;
            }

            if (!await MustStep(await Task.Run(() => uds.TryRequest(ProtoIds.SidWdbi,
                    new byte[] { 0x20, 0x10, otaTarget }, timeoutMs: 4000), ct), "2E 20 10"))
                return false;
            AppendLog($"2E 20 10 {otaTarget:X2} 选择目标 OK");

            if (!await MustStep(await Task.Run(() => uds.TryRequest(ProtoIds.SidRc,
                    new byte[] { 0x01, 0xFF, 0x00 }, timeoutMs: 15_000, waitPendingMs: 90_000), ct), "31 擦除"))
                return false;
            AppendLog("31 擦除完成");

            int size = raw.Length;
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
            while (off < size)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(transferBlock, size - off);
                var payload = new byte[1 + n];
                payload[0] = (byte)(seq & 0xFF);
                Buffer.BlockCopy(img, off, payload, 1, n);
                if (!await MustStep(await Task.Run(() => uds.TryRequest(ProtoIds.SidTd, payload,
                        timeoutMs: 8000, waitPendingMs: 30_000), ct), $"36 块 {blk + 1}"))
                    return false;
                off += n;
                blk++;
                seq = (seq + 1) & 0xFF;
                if (blk % 16 == 0 || off >= size)
                    AppendLog($"[进度] {blk}/{nblocks}  {off}/{size} ({off * 100 / size}%)");
            }

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
        => await RunOpAsync("链路自检", TimeSpan.FromSeconds(12), async (uds, ct) =>
        {
            await Task.Run(() =>
            {
                try
                {
                    uds.RequestSuppress(ProtoIds.SidTp, new byte[] { 0x00 });
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

    /// <summary>Qi 芯片状态全量查询：IAP + 充电 + 传感器 + 功率档位，任意会话可读。</summary>
    private async void OnQiStatusQuery(object sender, RoutedEventArgs e)
        => await RunOpAsync("Qi 状态查询", TimeSpan.FromSeconds(20), async (uds, ct) =>
        {
            QiChargerStationTool.Core.Protocol.QiChipStatus st;
            try
            {
                st = await Task.Run(() => uds.ReadQiChipStatus(ct), ct);
            }
            catch (Exception ex)
            {
                var msg = FormatOpError(ex);
                await UiAsync(() =>
                {
                    AppendResult("FAIL", "Qi 状态查询", msg);
                    SetState("失败", Colors.Crimson, msg);
                });
                return false;
            }
            bool fail = false;
            await UiAsync(() =>
            {
                foreach (var line in st.ToDisplayString().Split('\n'))
                    AppendLog("[Qi] " + line);
                if (st.IapState == 0x03)
                {
                    AppendResult("FAIL", "Qi 状态查询", "IAP 失败态");
                    SetState("失败", Colors.Crimson, "IAP 状态=失败，请检查 Qi 芯片固件");
                    fail = true;
                    return;
                }
                var summary =
                    $"Qi {st.QiVerMajor}.{st.QiVerMinor}.{st.QiVerPatch} · {st.ChargeStateName} · " +
                    $"{st.OutputPowerMw}mW · {st.FaultName} · 档位 {st.PowerLimitName}";
                Pass("Qi 状态查询", summary);
            });
            return !fail;
        });

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

            var r = await Task.Run(() => uds.TryRequest(ProtoIds.SidDsc, new byte[] { 0x03 }), ct);
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
            var r = await Task.Run(() => uds.TryRequest(ProtoIds.SidDsc, new byte[] { 0x03 }), ct);
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
                new byte[] { 0x21, 0x01, (byte)(enable ? 0x01 : 0x00) }, timeoutMs: 4000), ct);
            if (!Must(r))
            {
                await FailAsync(name, r.Error);
                return false;
            }
            await UiAsync(() => Pass(name, enable ? "已开启" : "已关闭"));
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

    private string CollectLogText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var item in LstLog.Items)
            sb.AppendLine(item?.ToString());
        return sb.ToString();
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            // 有选中则复制选中，否则复制全部
            string text;
            if (LstLog.SelectedItems.Count > 1)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var item in LstLog.SelectedItems)
                    sb.AppendLine(item?.ToString());
                text = sb.ToString();
            }
            else
            {
                text = CollectLogText();
            }
            if (text.Length == 0)
            {
                AppendLog("[日志] 无内容可复制");
                return;
            }
            Clipboard.SetText(text);
            AppendLog($"[日志] 已复制 {LstLog.Items.Count} 行到剪贴板，可直接 Ctrl+V 粘贴");
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
        LstLog.Items.Clear();
        AppendLog("[日志] 已清空");
    }

    /// <summary>Ctrl+A 全选，Ctrl+C 复制选中。</summary>
    private void OnLogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            LstLog.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (LstLog.SelectedItems.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var item in LstLog.SelectedItems)
                    sb.AppendLine(item?.ToString());
                try { Clipboard.SetText(sb.ToString()); } catch { /* ignore */ }
                e.Handled = true;
            }
        }
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
