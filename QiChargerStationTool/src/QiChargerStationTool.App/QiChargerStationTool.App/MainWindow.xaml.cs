using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QiChargerStationTool.Core.Protocol;
using QiChargerStationTool.Core.Security;
using QiChargerStationTool.Core.Station;

namespace QiChargerStationTool.App;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusSnBox();
    }

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
            var disable = new[]
            {
                BtnSnRead, BtnSnWrite, BtnReadVersion,
                BtnOta, BtnIap, BtnChargeOn, BtnChargeOff,
            };
            foreach (var b in disable)
            {
                b.IsEnabled = !busy;
                if (busy)
                {
                    b.Tag = b.Background;
                    b.Background = new SolidColorBrush(Color.FromRgb(0xBD, 0xBD, 0xBD));
                    b.Foreground = new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61));
                }
                else if (b.Tag is Brush restore)
                {
                    b.Background = restore;
                    b.Foreground = new SolidColorBrush(Colors.White);
                    b.Tag = null;
                }
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

    private void AppendResult(string status, string name, string msg)
    {
        Dispatcher.Invoke(() =>
            LstResult.Items.Add($"[{DateTime.Now:HH:mm:ss}] {status}  {name}  {msg}"));
    }

    /// <summary>
    /// 执行一次设备操作：自动置灰按钮、超时保护、完成后恢复。
    /// </summary>
    private async Task RunOpAsync(string name, TimeSpan timeout, Func<UdsClient, CancellationToken, Task> body)
    {
        if (IsBusyGuard()) return;
        SetBusy(true, name + "…");
        AppendLog($"==== 开始 {name}（超时 {timeout.TotalSeconds:F0}s）====");
        _cts = new CancellationTokenSource(timeout);
        var ct = _cts.Token;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Task.Run(async () =>
            {
                var port = TxtPort.Text.Trim();
                var baud = int.TryParse(TxtBaud.Text.Trim(), out var b) ? b : 2_000_000;
                using var transport =
                    new QiChargerStationTool.Core.Transport.ZqwlSerialTransport(port, baud);
                transport.Open();
                AppendLog($"CAN 打开 {port}");
                var iso = new IsoTpClient(transport);
                iso.OnLog += m => AppendLog(m);
                var uds = new UdsClient(iso);
                uds.OnLog += m => AppendLog(m);
                await body(uds, ct);
            }, ct);

            sw.Stop();
            AppendLog($"==== 完成 {name} 耗时 {sw.Elapsed.TotalSeconds:F1}s ====");
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            AppendLog($"==== 超时/取消 {name} 耗时 {sw.Elapsed.TotalSeconds:F1}s ====");
            AppendResult("TIMEOUT", name, $"超过 {timeout.TotalSeconds:F0}s 或被停止");
            SetState("超时", Colors.Crimson, $"{name} 超时");
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppendLog($"==== 失败 {name} 耗时 {sw.Elapsed.TotalSeconds:F1}s: {ex.Message} ====");
            AppendResult("FAIL", name, ex.Message);
            SetState("失败", Colors.Crimson, ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool IsBusyGuard()
    {
        if (_cts is { IsCancellationRequested: false })
        {
            // 仍可运行说明上一次未结束——按钮本已置灰，这里兜底
            return true;
        }
        return false;
    }

    private void Pass(string name, string msg)
    {
        AppendResult("PASS", name, msg);
        SetState("完成", Colors.ForestGreen, msg);
        AppendLog($"[PASS] {name}: {msg}");
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
            var rx = await Task.Run(() =>
                uds.Request(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x8C }, timeoutMs: 4000), ct);
            var sn = DecodeSn(rx);
            await UiAsync(() =>
            {
                TxtSn.Text = sn;
                TxtSnHint.Text = $"设备 SN: {sn}";
                Pass("读 SN", sn);
            });
        });

    private async void OnSnWrite(object sender, RoutedEventArgs e)
    {
        var sn = TxtSn.Text.Trim();
        if (sn.Length == 0) { TxtSnHint.Text = "请先扫码或输入 SN"; return; }
        if (sn.Length > 32) { TxtSnHint.Text = $"SN 过长 {sn.Length}>32"; return; }

        await RunOpAsync("写 SN", TimeSpan.FromSeconds(60), async (uds, ct) =>
        {
            await Task.Run(() => uds.Request(ProtoIds.SidDsc, new byte[] { 0x02 }), ct);
            var signer = RequireSigner();
            if (!await SecurityAccess.UnlockAsync(uds, signer, m => AppendLog("[SA] " + m), ct))
                throw new InvalidOperationException("SA 解锁失败");

            await Task.Run(() =>
                uds.Request(ProtoIds.SidWdbi, BuildSnPayload(sn), timeoutMs: 5000), ct);
            var rx = await Task.Run(() =>
                uds.Request(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x8C }, timeoutMs: 4000), ct);
            var readBack = DecodeSn(rx);
            if (!string.Equals(readBack, sn, StringComparison.Ordinal))
                throw new InvalidOperationException($"写后读回不符 期望={sn} 实际={readBack}");

            await UiAsync(() =>
            {
                TxtSnHint.Text = $"写入成功: {readBack}";
                Pass("写 SN", readBack);
            });
        });
    }

    // ---------- 测试操作按钮 ----------

    private async void OnReadVersion(object sender, RoutedEventArgs e)
        => await RunOpAsync("读软件版本", TimeSpan.FromSeconds(15), async (uds, ct) =>
        {
            var ver = await Task.Run(() =>
            {
                var rx = uds.Request(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x95 }, timeoutMs: 5000);
                return DecodeSn(rx);
            }, ct);
            await UiAsync(() => Pass("读软件版本", ver));
        });

    /// <summary>OTA：MCU1 APP，块 128B。</summary>
    private async void OnOtaUpgrade(object sender, RoutedEventArgs e)
        => await RunUpgradeAsync("OTA 升级", otaTarget: 0x01, transferBlock: 128,
                                 timeout: TimeSpan.FromMinutes(20));

    /// <summary>IAP：Qi 芯片 MCU2，块 22B（QI_IAP_MAX_CHUNK）。</summary>
    private async void OnIapUpgrade(object sender, RoutedEventArgs e)
        => await RunUpgradeAsync("IAP 升级", otaTarget: 0x04, transferBlock: 22,
                                 timeout: TimeSpan.FromMinutes(30));

    private async Task RunUpgradeAsync(string name, byte otaTarget, int transferBlock,
                                       TimeSpan timeout)
    {
        var fw = TxtFirmware.Text.Trim();
        if (!File.Exists(fw))
        {
            SetState("失败", Colors.Crimson, $"固件不存在: {fw}");
            AppendResult("FAIL", name, $"固件不存在: {fw}");
            return;
        }

        await RunOpAsync(name, timeout, async (uds, ct) =>
        {
            var image = QiChargerStationTool.Core.Imaging.XatoImage.Load(fw);
            AppendLog($"镜像 {image.Raw.Length}B ver={image.ExtractSwVersion()} "
                      + (otaTarget == 0x04 ? "目标=MCU2/Qi" : "目标=MCU1/APP"));

            await Task.Run(() => uds.Request(ProtoIds.SidDsc, new byte[] { 0x02 }), ct);
            var signer = RequireSigner();
            if (!await SecurityAccess.UnlockAsync(uds, signer, m => AppendLog("[SA] " + m), ct))
                throw new InvalidOperationException("SA 解锁失败");

            await Task.Run(() => uds.Request(ProtoIds.SidWdbi,
                new byte[] { 0x20, 0x10, otaTarget }, timeoutMs: 4000), ct);
            AppendLog($"2E 20 10 {otaTarget:X2} 选择目标 OK");

            await Task.Run(() => uds.Request(ProtoIds.SidRc,
                new byte[] { 0x01, 0xFF, 0x00 }, timeoutMs: 15_000, waitPendingMs: 90_000), ct);
            AppendLog("31 擦除完成");

            int size = image.Raw.Length;
            await Task.Run(() => uds.Request(ProtoIds.SidRd, new byte[]
            {
                0x00, 0x44, 0x08, 0x01, 0x00, 0x00,
                (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size,
            }, timeoutMs: 8000), ct);
            AppendLog($"34 RequestDownload {size}B @ 0x08010000 block={transferBlock}");

            int off = 0, seq = 1, blk = 0;
            int nblocks = (size + transferBlock - 1) / transferBlock;
            var img = image.Raw;
            while (off < size)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(transferBlock, size - off);
                var payload = new byte[1 + n];
                payload[0] = (byte)(seq & 0xFF);
                Buffer.BlockCopy(img, off, payload, 1, n);
                await Task.Run(() => uds.Request(ProtoIds.SidTd, payload,
                    timeoutMs: 8000, waitPendingMs: 30_000), ct);
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
        });
    }

    private async void OnChargeOn(object sender, RoutedEventArgs e)
        => await RunChargeAsync(enable: true);

    private async void OnChargeOff(object sender, RoutedEventArgs e)
        => await RunChargeAsync(enable: false);

    private async Task RunChargeAsync(bool enable)
    {
        string name = enable ? "开启充电" : "关闭充电";
        await RunOpAsync(name, TimeSpan.FromSeconds(30), async (uds, ct) =>
        {
            await Task.Run(() => uds.Request(ProtoIds.SidDsc, new byte[] { 0x03 }), ct);
            var signer = RequireSigner();
            if (!await SecurityAccess.UnlockAsync(uds, signer, m => AppendLog("[SA] " + m), ct))
                throw new InvalidOperationException("SA 解锁失败");
            await Task.Run(() => uds.Request(ProtoIds.SidWdbi,
                new byte[] { 0x21, 0x01, (byte)(enable ? 0x01 : 0x00) }, timeoutMs: 4000), ct);
            await UiAsync(() => Pass(name, enable ? "已开启" : "已关闭"));
        });
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        AppendLog("[停止] 用户中断当前操作");
        SetState("已停止", Colors.DimGray);
    }

    private void OnClearResult(object sender, RoutedEventArgs e) => LstResult.Items.Clear();

    // ---------- helpers ----------

    private static SeedSigner RequireSigner()
        => SeedSigner.FromDefaultKey()
           ?? throw new InvalidOperationException("缺少 keys/private.pem，无法 SA 解锁");

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
