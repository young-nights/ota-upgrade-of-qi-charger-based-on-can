using System.Windows;
using System.Windows.Controls;

namespace ProductionStationTool.App.Controls;

/// <summary>
/// 日志视图：绑定 <see cref="LogBuffer"/> 显示（带时间戳、自动滚底）。
/// 同一缓冲可绑多个视图（流程写入 → 弹出的日志窗实时刷新）。
/// </summary>
public partial class LogPanel : UserControl
{
    private LogBuffer _buffer = new();

    public LogPanel()
    {
        InitializeComponent();
        _buffer.Changed += OnBufferChanged;
    }

    /// <summary>绑定共享缓冲（切换绑定时自动解绑旧的并重放内容）。</summary>
    public void Bind(LogBuffer buffer)
    {
        _buffer.Changed -= OnBufferChanged;
        _buffer = buffer;
        _buffer.Changed += OnBufferChanged;
        Refresh();
    }

    public void Append(string message) => _buffer.Append(message);

    public void Clear() => _buffer.Clear();

    private void OnBufferChanged()
    {
        if (Dispatcher.CheckAccess())
            Refresh();
        else
            Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        TxtLog.Text = string.Join(Environment.NewLine, _buffer.Snapshot());
        TxtLog.ScrollToEnd();
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e) => Clear();
}
