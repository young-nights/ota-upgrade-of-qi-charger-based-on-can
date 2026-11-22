using System.Windows;
using ProductionStationTool.App.Controls;

namespace ProductionStationTool.App;

/// <summary>
/// 运行日志弹窗（无模式）：绑定页面的 <see cref="LogBuffer"/>，
/// 流程写日志实时刷新，可与主页面并存（边测边看），不占主页面布局。
/// </summary>
public partial class LogWindow : Window
{
    public LogWindow(string title, LogBuffer buffer)
    {
        InitializeComponent();
        Title = title;
        LogPanel.Bind(buffer);
    }
}
