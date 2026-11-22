using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Controls;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App.Stations;

/// <summary>
/// 工位 2 页面（简化版老化位）：点击「开始测试」后以 500ms 周期持续发送开充指令
/// （2E 21 01）——产线接新设备无需重新点击；不倒计时、不读电流电压、不判限、不入库。
/// 「断开」停止循环；循环跨「下一台」持续（接新设备不用重击）。
/// </summary>
public partial class Station2Control : UserControl, IStationPage
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly Brush IdleBg = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
    private static readonly Brush RunBg = new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7));
    private static readonly Brush FailBg = new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));

    /// <summary>开充指令发送周期。</summary>
    private static readonly TimeSpan SendPeriod = TimeSpan.FromMilliseconds(500);

    private readonly LogBuffer _log = new();
    private LogWindow? _logWin;

    private CanUdsDevice? _can;
    private bool _connected;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private long _sentCount;

    public int StationNo => 2;
    public string Title => "工位2 — 老化测试";

    /// <summary>已接入 CAN 且循环未在跑时可点「开始测试」。</summary>
    public bool CanStartTest => _connected && _loopTask is null;

    /// <summary>简化老化位无「下一台」复位语义（循环跨设备持续）。</summary>
    public bool CanGoNext => false;

    public event Action? AvailabilityChanged;

    private void RaiseAvailability() => AvailabilityChanged?.Invoke();

    private void LogSafe(string msg) => _log.Append(msg);

    public Station2Control()
    {
        InitializeComponent();
        ConnPanel.ShowPowerZ = false;   // 本工位只用 CAN（开充指令），不接 PowerZ
        _log.Append("工位2 简化老化位：开始测试后以 500ms 周期持续发送开充指令");
        ConnPanel.ConnectRequested += OnConnectRequested;
        ConnPanel.DisconnectRequested += OnDisconnectRequested;
    }

    private async void OnConnectRequested(string can, string pz)
    {
        try
        {
            _can?.Dispose();
            _can = new CanUdsDevice(can);
            _can.Log += LogSafe;
            await _can.OpenAsync();
            _connected = true;
            ConnPanel.SetStatus($"已连接 CAN:{can}", true);
            _log.Append($"串口连接：CAN={can}（开充循环未启动，点「开始测试」启动）");
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
        await StopLoopAsync();
        try { _can?.Dispose(); } catch { /* 忽略 */ }
        _can = null;
        _connected = false;
        ConnPanel.SetStatus("未连接", false);
        _log.Append("串口已断开（开充循环已停止）");
        RaiseAvailability();
    }

    /// <summary>
    /// 启动开充循环（已在跑则不重复），随即返回——循环在后台按 500ms 周期执行，
    /// MainWindow 的「开始测试」按钮随即恢复，循环期间该按钮禁用。
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_loopTask is not null || !_connected || _can is null)
            return Task.CompletedTask;

        _sentCount = 0;
        Ui(() =>
        {
            TxtCount.Text = "0 次";
            TxtLast.Text = "建链中（10 03 + SA）…";
            SetState("发送中", SuccessBrush, RunBg);
        });
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _loopTask = Task.Run(() => LoopAsync(token), token);
        _log.Append("开充循环启动：500ms 周期发送 2E 21 01（开充）");
        RaiseAvailability();
        return Task.CompletedTask;
    }

    /// <summary>开充循环：建链（会话+SA，缓存）→ 每周期轻量写 2E 21 01；失败自动重建链。</summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var can = _can;
                if (can is null)
                    break;

                var (eok, edet) = await can.EnsureExtendedUnlockedAsync(force: false, ct);
                if (!eok)
                {
                    // 设备可能刚接入/休眠：状态提示 + 补唤醒 burst，下轮重建链
                    Ui(() =>
                    {
                        SetState("等待设备", MutedBrush, IdleBg);
                        TxtLast.Text = edet;
                    });
                    _log.Append($"建链未就绪：{edet}，补唤醒后重试");
                    await can.TryWakeAsync(ct);
                    await Task.Delay(SendPeriod, ct);
                    continue;
                }

                var (wok, wdet) = await can.WriteChargerEnableAsync(true, ct);
                if (wok)
                {
                    _sentCount++;
                    var n = _sentCount;
                    Ui(() =>
                    {
                        TxtCount.Text = $"{n} 次";
                        TxtLast.Text = $"{wdet} · {DateTime.Now:HH:mm:ss}";
                        SetState("发送中", SuccessBrush, RunBg);
                    });
                    if (n % 20 == 0)   // 每 ~10s 落一条日志，防刷屏
                        _log.Append($"开充循环：已发送 {n} 次（最近 {wdet}）");
                }
                else
                {
                    Ui(() =>
                    {
                        SetState("写失败，重试中", DangerBrush, FailBg);
                        TxtLast.Text = wdet;
                    });
                    _log.Append($"2E 21 01 失败：{wdet}（下轮重建链）");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Ui(() => TxtLast.Text = $"异常：{ex.Message}");
                _log.Append($"开充循环异常：{ex.Message}");
            }

            try
            {
                var remain = SendPeriod - sw.Elapsed;
                if (remain > TimeSpan.Zero)
                    await Task.Delay(remain, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task StopLoopAsync()
    {
        if (_loopCts is null)
            return;
        _loopCts.Cancel();
        try
        {
            if (_loopTask is not null)
                await _loopTask;
        }
        catch
        {
            /* 取消/异常已吞 */
        }
        _loopCts.Dispose();
        _loopCts = null;
        _loopTask = null;
        Ui(() => SetState("未启动", MutedBrush, IdleBg));
        _log.Append("开充循环已停止");
    }

    /// <summary>切换下一台不停止循环（产线接新设备无需重新点击）。</summary>
    public void ResetForNext()
    {
        _log.Append("切换下一台（开充循环继续运行）");
    }

    private void SetState(string text, Brush fg, Brush bg)
    {
        TxtState.Text = text;
        TxtState.Foreground = fg;
        BadgeState.Background = bg;
    }

    private void Ui(Action action) => Dispatcher.Invoke(action);

    /// <summary>弹出运行日志窗（无模式，边测边看）；重复点击激活已开窗口。</summary>
    private void BtnLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logWin is not null && _logWin.IsLoaded)
        {
            _logWin.Activate();
            return;
        }
        _logWin = new LogWindow("运行日志 — 工位2", _log) { Owner = Window.GetWindow(this) };
        _logWin.Show();
    }
}
