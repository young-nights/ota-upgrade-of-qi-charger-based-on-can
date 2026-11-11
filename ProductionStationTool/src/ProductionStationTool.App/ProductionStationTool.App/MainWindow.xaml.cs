using System.Windows;
using System.Windows.Controls;
using ProductionStationTool.App.Stations;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App;

/// <summary>
/// 工位外壳：一个 App 集成多个工位页面，菜单栏「工位选择」下拉切换（默认工位1）。
/// 「开始测试」「下一个」作用于当前选中工位。
/// 本机身份（工位号+机位号）来自 machine.json——每工位机位数随产量动态增减，
/// 增减机位只改配置文件，代码零改动。
/// </summary>
public partial class MainWindow : Window
{
    private readonly IStationPage[] _pages;
    private readonly StationIdentity? _identity;
    private readonly StationPresenceReporter? _presence;

    /// <summary>汇总机模式：整页接管为汇总机页，不计入机位/工位。</summary>
    private readonly bool _summaryMode;
    private IStationPage? _currentPage;
    private bool _isRunning;

    public MainWindow(MachineMode mode)
    {
        InitializeComponent();

        // 小屏防截断：窗口不超出工作区（默认 1280x780 在 768p/高缩放下会被屏幕裁掉）
        var wa = SystemParameters.WorkArea;
        MaxWidth = wa.Width;
        MaxHeight = wa.Height;
        Width = Math.Min(Width, wa.Width);
        Height = Math.Min(Height, wa.Height);

        _summaryMode = mode == MachineMode.Summary;
        SummaryView.ExitRequested += OnSummaryExitRequested;

        if (_summaryMode)
        {
            // 汇总机：直接进管理页；不加载工位身份、不发工位接入心跳（不计入机位和工位）
            Title = "产线测试上位机 — 汇总机";
            _pages = Array.Empty<IStationPage>();
            SummaryView.EnterUnlocked();
            WorkstationView.Visibility = Visibility.Collapsed;
            SummaryView.Visibility = Visibility.Visible;
        }
        else
        {
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

            // 工位机接入心跳：周期上报身份到汇总机（serverHost），汇总机按远端 IP 自动登记
            _presence = new StationPresenceReporter(_identity, MachineConfig.LoadServerHost());
            _presence.Start();
        }

        Closed += (_, _) => _presence?.Dispose();
    }

    private void MenuStation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out var index))
            return;
        for (var i = 0; i < MenuStations.Items.Count; i++)
        {
            if (MenuStations.Items[i] is MenuItem item)
                item.IsChecked = i == index;
        }
        ShowPage(index);
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
        TxtCurrent.Text = $"当前：{_currentPage.Title}（本机：{_identity?.Label}）";
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

    // ---- 汇总机页面（整页接管工作区，非弹窗；仅启动登录选汇总机身份才进入）----

    private void OnSummaryExitRequested()
    {
        // 工位机页没有汇总机入口，退出只可能发生在汇总机模式：关窗结束（重开再选身份）
        Close();
    }
}
