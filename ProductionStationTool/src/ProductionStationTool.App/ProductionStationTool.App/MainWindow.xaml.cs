using System.Windows;
using System.Windows.Controls;
using ProductionStationTool.App.Stations;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App;

/// <summary>
/// 工位外壳：一个 App 集成多个工位页面，左侧列表自主切换。
/// 「开始测试」「下一个」作用于当前选中工位。
/// 本机身份（工位号+机位号）来自 machine.json——每工位机位数随产量动态增减，
/// 增减机位只改配置文件，代码零改动。
/// </summary>
public partial class MainWindow : Window
{
    private readonly IStationPage[] _pages;
    private readonly StationIdentity _identity;
    private IStationPage? _currentPage;
    private bool _isRunning;

    public MainWindow()
    {
        InitializeComponent();
        _identity = MachineConfig.Load();
        Title = $"产线测试上位机 — {_identity.Code}";
        _pages = new IStationPage[]
        {
            new Station1Control(_identity),
            new Station2Control(),
            new Station3Control(_identity),
            new Station4Control(),
        };
        ShowPage(0);
        SummaryView.ExitRequested += OnSummaryExitRequested;
    }

    private void ListStations_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        ShowPage(ListStations.SelectedIndex);
    }

    private void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Length)
            return;
        if (_currentPage is not null)
            _currentPage.AvailabilityChanged -= OnPageAvailabilityChanged;
        _currentPage = _pages[index];
        _currentPage.AvailabilityChanged += OnPageAvailabilityChanged;
        PageHost.Content = _currentPage;
        TxtCurrent.Text = $"当前：{_currentPage.Title}（本机：{_identity.Label}）";
        UpdateButtons();
    }

    private void OnPageAvailabilityChanged() => Dispatcher.Invoke(UpdateButtons);

    /// <summary>
    /// 按当前工位可用性刷新按钮：
    /// 开始测试=串口已接入且未在跑；下一个=完整跑通一次且未在跑。
    /// </summary>
    private void UpdateButtons()
    {
        if (_currentPage is null)
            return;
        BtnStart.IsEnabled = !_isRunning && _currentPage.CanStartTest;
        BtnNext.IsEnabled = !_isRunning && _currentPage.CanGoNext;
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        var page = _currentPage;
        if (page is null || _isRunning || !page.CanStartTest)
        {
            if (page is not null && !page.CanStartTest)
                TxtStatus.Text = $"{page.Title}：串口未接入，无法开始测试";
            return;
        }
        _isRunning = true;
        UpdateButtons();
        TxtStatus.Text = $"{page.Title}：测试进行中…";
        try
        {
            await page.StartAsync();
            TxtStatus.Text = $"{page.Title}：流程结束";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"{page.Title}：异常 {ex.Message}";
        }
        finally
        {
            _isRunning = false;
            UpdateButtons();
        }
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        var page = _currentPage;
        if (page is null || _isRunning || !page.CanGoNext)
            return;
        page.ResetForNext();
        UpdateButtons();
        TxtStatus.Text = $"{page.Title}：已切换下一台，串口保持连接，等待开始测试";
    }

    // ---- 顶部横幅菜单 ----

    private void MenuSettingsNetwork_Click(object sender, RoutedEventArgs e) => OpenSettings(0);

    private void MenuSettingsCoil_Click(object sender, RoutedEventArgs e) => OpenSettings(1);

    private void MenuSettingsPresets_Click(object sender, RoutedEventArgs e) => OpenSettings(2);

    private void OpenSettings(int startTab)
    {
        var win = new SettingsWindow(startTab) { Owner = this };
        win.ShowDialog();
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var win = new AboutWindow { Owner = this };
        win.ShowDialog();
    }

    // ---- 汇总机页面（整页接管工作区，非弹窗；进入前密码验证）----

    private void BtnSummary_Click(object sender, RoutedEventArgs e)
    {
        // 密码输入窗口居中弹出，验证通过才进入汇总机页
        var win = new SummaryLoginWindow { Owner = this };
        if (win.ShowDialog() != true)
            return;
        SummaryView.EnterUnlocked();
        WorkstationView.Visibility = Visibility.Collapsed;
        SummaryView.Visibility = Visibility.Visible;
    }

    private void OnSummaryExitRequested()
    {
        SummaryView.Visibility = Visibility.Collapsed;
        WorkstationView.Visibility = Visibility.Visible;
    }
}
