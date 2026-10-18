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

    /// <summary>光标在输入框时，扫码枪（键盘模拟）自动填入；回车 = 扫码完成。</summary>
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

    /// <summary>聚焦时全选，连续扫码可直接覆盖。</summary>
    private void OnSnGotFocus(object sender, RoutedEventArgs e)
        => TxtSn.SelectAll();

    private void OnSnClear(object sender, RoutedEventArgs e)
    {
        TxtSn.Clear();
        TxtSnHint.Text = "";
        FocusSnBox();
    }

    /// <summary>从设备读 SN → 填入输入框。</summary>
    private async void OnSnRead(object sender, RoutedEventArgs e)
    {
        BtnSnRead.IsEnabled = false;
        BtnSnWrite.IsEnabled = false;
        TxtSnHint.Text = "读取中…";
        try
        {
            await RunOnDeviceAsync(async (uds, ct) =>
            {
                var rx = await Task.Run(() =>
                    uds.Request(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x8C }, timeoutMs: 4000), ct);
                if (rx.Length < 4) throw new InvalidOperationException("F18C 响应过短");
                int len = rx.Length - 3;
                while (len > 0 && (rx[3 + len - 1] is 0x00 or 0x20)) len--;
                var sn = System.Text.Encoding.ASCII.GetString(rx, 3, len);
                await Dispatcher.InvokeAsync(() =>
                {
                    TxtSn.Text = sn;
                    TxtSnHint.Text = $"设备 SN: {sn}";
                    AppendLog($"[SN 读取] {sn}");
                });
            });
        }
        catch (Exception ex)
        {
            TxtSnHint.Text = "读取失败: " + ex.Message;
            AppendLog("[SN 读取] 失败: " + ex.Message);
        }
        finally
        {
            BtnSnRead.IsEnabled = true;
            BtnSnWrite.IsEnabled = true;
        }
    }

    /// <summary>把输入框 SN 写入设备（10 02 + SA + 2E F18C + 读回校验）。</summary>
    private async void OnSnWrite(object sender, RoutedEventArgs e)
    {
        var sn = TxtSn.Text.Trim();
        if (sn.Length == 0)
        {
            TxtSnHint.Text = "请先扫码或输入 SN";
            return;
        }
        if (sn.Length > 32)
        {
            TxtSnHint.Text = $"SN 过长 {sn.Length}>32";
            return;
        }

        BtnSnRead.IsEnabled = false;
        BtnSnWrite.IsEnabled = false;
        TxtSnHint.Text = "写入中…";
        try
        {
            await RunOnDeviceAsync(async (uds, ct) =>
            {
                await Task.Run(() => uds.Request(ProtoIds.SidDsc, new byte[] { 0x02 }), ct);
                var signer = SeedSigner.FromDefaultKey()
                    ?? throw new InvalidOperationException("缺少 keys/private.pem，无法 SA 解锁");
                if (!await SecurityAccess.UnlockAsync(uds, signer, m => AppendLog("[SA] " + m), ct))
                    throw new InvalidOperationException("SA 解锁失败");

                var body = new byte[32];
                for (int i = 0; i < 32; i++) body[i] = 0x20;
                var ascii = System.Text.Encoding.ASCII.GetBytes(sn);
                Array.Copy(ascii, body, Math.Min(ascii.Length, 32));
                var payload = new byte[34];
                payload[0] = 0xF1;
                payload[1] = 0x8C;
                body.CopyTo(payload, 2);
                await Task.Run(() =>
                    uds.Request(ProtoIds.SidWdbi, payload, timeoutMs: 5000), ct);

                var rx = await Task.Run(() =>
                    uds.Request(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x8C }, timeoutMs: 4000), ct);
                int len = rx.Length - 3;
                while (len > 0 && (rx[3 + len - 1] is 0x00 or 0x20)) len--;
                var readBack = System.Text.Encoding.ASCII.GetString(rx, 3, len);
                if (!string.Equals(readBack, sn, StringComparison.Ordinal))
                    throw new InvalidOperationException($"写后读回不符 期望={sn} 实际={readBack}");

                await Dispatcher.InvokeAsync(() =>
                {
                    TxtSnHint.Text = $"写入成功: {readBack}";
                    AppendLog($"[SN 写入] {readBack}（读回校验通过）");
                });
            });
        }
        catch (Exception ex)
        {
            TxtSnHint.Text = "写入失败: " + ex.Message;
            AppendLog("[SN 写入] 失败: " + ex.Message);
        }
        finally
        {
            BtnSnRead.IsEnabled = true;
            BtnSnWrite.IsEnabled = true;
        }
    }

    /// <summary>临时打开 ZQWL 串口 CAN，执行一次操作后关闭。</summary>
    private async Task RunOnDeviceAsync(Func<UdsClient, CancellationToken, Task> action)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
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

    private void AppendLog(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            LstLog.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            LstLog.ScrollIntoView(LstLog.Items[^1]);
        });
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        LstLog.Items.Clear();
        LstResult.Items.Clear();
        TxtState.Text = "测试中…";
        TxtState.Foreground = new SolidColorBrush(Colors.DarkOrange);
        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = true;
        BtnSnRead.IsEnabled = false;
        BtnSnWrite.IsEnabled = false;

        var config = new StationConfig
        {
            ComPort = TxtPort.Text.Trim(),
            Baud = int.TryParse(TxtBaud.Text.Trim(), out var b) ? b : 2_000_000,
            FirmwarePath = TxtFirmware.Text.Trim(),
            SnCode = TxtSn.Text.Trim(),
            DoReadVersion = ChkVersion.IsChecked == true,
            DoReadSn = ChkReadSn.IsChecked == true,
            DoWriteSn = ChkWriteSn.IsChecked == true,
            DoOtaUpgrade = ChkOta.IsChecked == true,
            DoChargeTest = ChkCharge.IsChecked == true,
        };

        _cts = new CancellationTokenSource();
        var runner = new StationRunner();
        runner.OnLog += AppendLog;
        runner.OnStep += r => Dispatcher.Invoke(() =>
            LstResult.Items.Add($"{r.Status}  {r.Name}  {r.Message}"));

        _ = Task.Run(async () =>
        {
            try
            {
                var results = await runner.RunAsync(config, new ZqwlTransportFactory(), _cts.Token);
                bool pass = results.All(r => r.Status is TestStepStatus.Passed or TestStepStatus.Skipped);
                Dispatcher.Invoke(() =>
                {
                    TxtState.Text = pass ? "PASS" : "FAIL";
                    TxtState.Foreground = new SolidColorBrush(pass ? Colors.ForestGreen : Colors.Crimson);
                });
            }
            catch (Exception ex)
            {
                AppendLog("异常: " + ex.Message);
                Dispatcher.Invoke(() =>
                {
                    TxtState.Text = "FAIL";
                    TxtState.Foreground = new SolidColorBrush(Colors.Crimson);
                });
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    BtnStart.IsEnabled = true;
                    BtnStop.IsEnabled = false;
                    BtnSnRead.IsEnabled = true;
                    BtnSnWrite.IsEnabled = true;
                    FocusSnBox();
                });
            }
        });
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        TxtState.Text = "已停止";
        TxtState.Foreground = new SolidColorBrush(Colors.DimGray);
    }
}
