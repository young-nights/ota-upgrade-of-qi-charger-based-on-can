using System.Windows;
using System.Windows.Threading;

namespace QiChargerStationTool.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 任何未处理异常：记日志、不弹崩溃框，保持工装继续可用
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            System.Diagnostics.Trace.WriteLine($"[Unobserved] {args.Exception?.GetBaseException().Message}");
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true; // 吞掉，不让 WPF 崩溃退出
        var ex = e.Exception.GetBaseException();
        System.Diagnostics.Trace.WriteLine($"[Unhandled] {ex.GetType().Name}: {ex.Message}");

        if (Current.MainWindow is MainWindow mw)
            mw.ReportCrash(ex);
    }
}
