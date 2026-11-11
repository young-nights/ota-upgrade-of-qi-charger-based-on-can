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

        var win = new MainWindow(login.SelectedMode);
        MainWindow = win;
        win.Show();
    }
}
