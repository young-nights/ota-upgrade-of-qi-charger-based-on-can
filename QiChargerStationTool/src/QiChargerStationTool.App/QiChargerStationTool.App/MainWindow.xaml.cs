using System.Windows;
using System.Windows.Media;
using QiChargerStationTool.Core.Station;

namespace QiChargerStationTool.App;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
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
            DoWriteSn = ChkSn.IsChecked == true,
            DoOtaUpgrade = ChkOta.IsChecked == true,
            DoChargeTest = ChkCharge.IsChecked == true,
        };

        _cts = new CancellationTokenSource();
        var runner = new StationRunner();
        runner.OnLog += AppendLog;
        runner.OnStep += r => Dispatcher.Invoke(() =>
        {
            var item = $"{r.Status}  {r.Name}  {r.Message}";
            LstResult.Items.Add(item);
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
                    TxtState.Foreground = new SolidColorBrush(pass ? Colors.ForestGreen : Colors.DarkOrangeRed);
                });
            }
            catch (Exception ex)
            {
                AppendLog("异常: " + ex.Message);
                Dispatcher.Invoke(() =>
                {
                    TxtState.Text = "FAIL";
                    TxtState.Foreground = new SolidColorBrush(Colors.DarkOrangeRed);
                });
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    BtnStart.IsEnabled = true;
                    BtnStop.IsEnabled = false;
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
