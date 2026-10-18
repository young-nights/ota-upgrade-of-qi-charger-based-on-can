using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>扫码枪通常以 CR/LF 结尾；回车 = 扫码完成。</summary>
    private void OnSnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var sn = TxtSn.Text.Trim();
            if (sn.Length == 0)
            {
                TxtSnHint.Text = "请先扫码";
                return;
            }
            TxtSnHint.Text = $"已扫码: {sn}";
            AppendLog($"[扫码] SN = {sn}");
            // 选中内容，方便下一件扫码直接覆盖
            TxtSn.SelectAll();
        }
    }

    /// <summary>获得焦点时全选，扫码枪/键盘可直接覆盖。</summary>
    private void OnSnGotFocus(object sender, RoutedEventArgs e)
    {
        TxtSn.SelectAll();
    }

    private void OnSnClear(object sender, RoutedEventArgs e)
    {
        TxtSn.Clear();
        TxtSnHint.Text = "";
        FocusSnBox();
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
        {
            LstResult.Items.Add($"{r.Status}  {r.Name}  {r.Message}");
        });

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
                    FocusSnBox(); // 测完回到扫码框，准备下一件
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
