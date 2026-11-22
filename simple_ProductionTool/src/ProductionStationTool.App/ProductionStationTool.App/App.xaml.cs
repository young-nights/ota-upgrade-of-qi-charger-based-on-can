using System.Windows;

namespace ProductionStationTool.App;

/// <summary>
/// 启动流程：直接进入工位主窗（简化版无工位机/汇总机登录分流）。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        MainWindow win;
        try
        {
            win = new MainWindow();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"主窗口创建失败：{ex.Message}", "产线测试上位机",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        MainWindow = win;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        win.Show();
    }
}
