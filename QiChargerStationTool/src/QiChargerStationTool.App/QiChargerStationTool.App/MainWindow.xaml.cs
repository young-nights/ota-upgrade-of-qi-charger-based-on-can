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
    private bool _busy;

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

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnSnRead.IsEnabled = !busy;
        BtnSnWrite.IsEnabled = !busy;
        BtnReadVersion.IsEnabled = !busy;
        BtnOta.IsEnabled = !busy;
        BtnIap.IsEnabled = !busy;
        BtnChargeOn.IsEnabled = !busy;
        BtnChargeOff.IsEnabled = !busy;
        BtnStop.IsEnabled = busy;
        if (!busy) FocusSnBox();
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
            LstLog.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            LstLog.ScrollIntoView(LstLog.Items[^1]);
        });
    }

    private void AppendResult(string status, string name, string msg)
    {
        Dispatcher.Invoke(() =>
            LstResult.Items.Add($"[{DateTime.Now:HH:mm:ss}] {status}  {name}  {msg}"));
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
    {
        SetBusy(true);
        SetState("读取 SN…", Colors.DarkOrange);
        try
        {
            await RunOnDeviceAsync(async (uds, ct) =>
            {
                var rx = await Task.Run(() =>
                    uds.Request(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x8C }, timeoutMs: 4000), ct);
                var sn = DecodeSn(rx);
                await UiAsync(() =>
                {
                    TxtSn.Text = sn;
                    TxtSnHint.Text = $"设备 SN: {sn}";
                    AppendLog($"[SN 读取] {sn}");
                    AppendResult("PASS", "读 SN", sn);
                    SetState("完成", Colors.ForestGreen, "SN 已读出");
                });
            });
        }
        catch (Exception ex)
        {
            Fail(ex, "读 SN");
        }
        finally { SetBusy(false); }
    }

    private async void OnSnWrite(object sender, RoutedEventArgs e)
    {
        var sn = TxtSn.Text.Trim();
        if (sn.Length == 0) { TxtSnHint.Text = "请先扫码或输入 SN"; return; }
        if (sn.Length > 32) { TxtSnHint.Text = $"SN 过长 {sn.Length}>32"; return; }

        SetBusy(true);
        SetState("写入 SN…", Colors.DarkOrange);
        try
        {
            await RunOnDeviceAsync(async (uds, ct) =>
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
                    AppendLog($"[SN 写入] {readBack}");
                    AppendResult("PASS", "写 SN", readBack);
                    SetState("完成", Colors.ForestGreen, "SN 写入并校验通过");
                });
            });
        }
        catch (Exception ex) { Fail(ex, "写 SN"); }
        finally { SetBusy(false); }
    }

    // ---------- 测试操作按钮 ----------

    private async void OnReadVersion(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        SetState("读版本…", Colors.DarkOrange);
        try
        {
            await RunOnDeviceAsync(async (uds, ct) =>
            {
                var ver = await Task.Run(() =>
                {
                    var rx = uds.Request(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x95 }, timeoutMs: 5000);
                    return DecodeSn(rx); // same 32B ASCII padding
                }, ct);
                await UiAsync(() =>
                {
                    AppendLog($"[版本] {ver}");
                    AppendResult("PASS", "读软件版本", ver);
                    SetState("完成", Colors.ForestGreen, ver);
                });
            });
        }
        catch (Exception ex) { Fail(ex, "读软件版本"); }
        finally { SetBusy(false); }
    }

    private async void OnOtaUpgrade(object sender, RoutedEventArgs e)
        => await RunUpgradeAsync("OTA 升级", otaTarget: 0x01);

    private async void OnIapUpgrade(object sender, RoutedEventArgs e)
        => await RunUpgradeAsync("IAP 升级", otaTarget: 0x04);

    private async Task RunUpgradeAsync(string name, byte otaTarget)
    {
        var fw = TxtFirmware.Text.Trim();
        if (!File.Exists(fw))
        {
            SetState("失败", Colors.Crimson, $"固件不存在: {fw}");
            AppendResult("FAIL", name, $"固件不存在: {fw}");
            return;
        }

        SetBusy(true);
        SetState($"{name}…", Colors.DarkOrange, fw);
        try
        {
            await RunOnDeviceAsync(async (uds, ct) =>
            {
                var image = QiChargerStationTool.Core.Imaging.XatoImage.Load(fw);
                AppendLog($"镜像 {image.Raw.Length}B ver={image.ExtractSwVersion()}");

                await Task.Run(() => uds.Request(ProtoIds.SidDsc, new byte[] { 0x02 }), ct);
                var signer = RequireSigner();
                if (!await SecurityAccess.UnlockAsync(uds, signer, m => AppendLog("[SA] " + m), ct))
                    throw new InvalidOperationException("SA 解锁失败");

                await Task.Run(() => uds.Request(ProtoIds.SidWdbi,
                    new byte[] { 0x20, 0x10, otaTarget }, timeoutMs: 4000), ct);
                AppendLog(otaTarget == 0x04 ? "目标 MCU2 (IAP)" : "目标 MCU1 (APP)");

                // 31 erase
                await Task.Run(() => uds.Request(ProtoIds.SidRc,
                    new byte[] { 0x01, 0xFF, 0x00 }, timeoutMs: 15_000, waitPendingMs: 90_000), ct);
                AppendLog("擦除完成");

                // 34
                int size = image.Raw.Length;
                await Task.Run(() => uds.Request(ProtoIds.SidRd, new byte[]
                {
                    0x00, 0x44, 0x08, 0x01, 0x00, 0x00,
                    (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size,
                }, timeoutMs: 8000), ct);
                AppendLog($"RequestDownload {size}B");

                // 36
                int block = 128, off = 0, seq = 1, blk = 0;
                int nblocks = (size + block - 1) / block;
                var img = image.Raw;
                while (off < size)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = Math.Min(block, size - off);
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

                // 37
                try
                {
                    await Task.Run(() => uds.Request(ProtoIds.SidRte,
                        Array.Empty<byte>(), timeoutMs: 8000, waitPendingMs: 60_000), ct);
                    AppendLog("0x37 确认，设备自复位");
                }
                catch (Exception ex)
                {
                    AppendLog($"0x37 异常（可能已自复位）: {ex.Message}");
                }

                await UiAsync(() =>
                {
                    AppendResult("PASS", name, $"{size}B 传输完成");
                    SetState("完成", Colors.ForestGreen, $"{name} {size}B");
                });
            });
        }
        catch (Exception ex) { Fail(ex, name); }
        finally { SetBusy(false); }
    }

    private async void OnChargeOn(object sender, RoutedEventArgs e)
        => await RunChargeAsync(enable: true);

    private async void OnChargeOff(object sender, RoutedEventArgs e)
        => await RunChargeAsync(enable: false);

    private async Task RunChargeAsync(bool enable)
    {
        string name = enable ? "开启充电" : "关闭充电";
        SetBusy(true);
        SetState(name + "…", Colors.DarkOrange);
        try
        {
            await RunOnDeviceAsync(async (uds, ct) =>
            {
                await Task.Run(() => uds.Request(ProtoIds.SidDsc, new byte[] { 0x03 }), ct);
                var signer = RequireSigner();
                if (!await SecurityAccess.UnlockAsync(uds, signer, m => AppendLog("[SA] " + m), ct))
                    throw new InvalidOperationException("SA 解锁失败");
                await Task.Run(() => uds.Request(ProtoIds.SidWdbi,
                    new byte[] { 0x21, 0x01, (byte)(enable ? 0x01 : 0x00) }, timeoutMs: 4000), ct);
                await UiAsync(() =>
                {
                    AppendLog($"[充电] {(enable ? "ON" : "OFF")}");
                    AppendResult("PASS", name, enable ? "已开启" : "已关闭");
                    SetState("完成", Colors.ForestGreen, name + "成功");
                });
            });
        }
        catch (Exception ex) { Fail(ex, name); }
        finally { SetBusy(false); }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
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

    private void Fail(Exception ex, string name)
    {
        AppendLog($"[{name}] 失败: {ex.Message}");
        AppendResult("FAIL", name, ex.Message);
        SetState("失败", Colors.Crimson, ex.Message);
    }

    private async Task RunOnDeviceAsync(Func<UdsClient, CancellationToken, Task> action)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var port = TxtPort.Text.Trim();
        var baud = int.TryParse(TxtBaud.Text.Trim(), out var b) ? b : 2_000_000;
        await Task.Run(async () =>
        {
            using var transport = new QiChargerStationTool.Core.Transport.ZqwlSerialTransport(port, baud);
            transport.Open();
            var iso = new IsoTpClient(transport);
            iso.OnLog += m => AppendLog(m);
            var uds = new UdsClient(iso);
            uds.OnLog += m => AppendLog(m);
            await action(uds, ct);
        }, ct);
    }

    private async Task UiAsync(Action action) => await Dispatcher.InvokeAsync(action);
}
