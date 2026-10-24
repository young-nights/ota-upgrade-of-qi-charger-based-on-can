using System.Windows.Controls;

namespace ProductionStationTool.App.Controls;

/// <summary>日志区：带时间戳追加，自动滚到底。</summary>
public partial class LogPanel : UserControl
{
    public LogPanel()
    {
        InitializeComponent();
    }

    public void Append(string message)
    {
        TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        TxtLog.ScrollToEnd();
    }

    public void Clear() => TxtLog.Clear();
}
