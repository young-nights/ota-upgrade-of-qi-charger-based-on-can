using System.Windows;

namespace ProductionStationTool.App;

/// <summary>
/// 启动流程：先弹登录窗选择工位机/汇总机（工位机免密码、汇总机需密码），
/// 「登录」后进入对应页面；取消则退出。汇总机不计入机位和工位。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 登录窗是当前唯一窗口：默认 ShutdownMode=OnLastWindowClose 会在它关闭瞬间
        // （点「登录」置 DialogResult）就判定应用退出，主窗来不及挂上——必须先改
        // 显式关停，等主窗建立后再恢复 OnMainWindowClose。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var login = new ModeLoginWindow();
        bool? ok;
        try
        {
            ok = login.ShowDialog();
        }
        catch
        {
            ok = false;
        }
        if (ok != true)
        {
            Shutdown();
            return;
        }

        MainWindow win;
        try
        {
            win = new MainWindow(login.SelectedMode);
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
