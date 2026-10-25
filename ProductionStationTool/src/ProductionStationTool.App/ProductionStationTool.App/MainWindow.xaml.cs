using System.Windows;
using System.Windows.Controls;
using ProductionStationTool.App.Stations;

namespace ProductionStationTool.App;

/// <summary>
/// 工位外壳：一个 App 集成多个工位页面，左侧列表自主切换。
/// 「开始测试」「下一个」作用于当前选中工位。
/// </summary>
public partial class MainWindow : Window
{
    private readonly IStationPage[] _pages;

    public MainWindow()
    {
        InitializeComponent();
        _pages = new IStationPage[]
        {
            new Station1Control(),
            new Station2Control(),
            new PlaceholderStationControl(3),
            new PlaceholderStationControl(4),
        };
        ShowPage(0);
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
        PageHost.Content = _pages[index];
        TxtCurrent.Text = $"当前：{_pages[index].Title}";
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        var page = _pages[ListStations.SelectedIndex];
        BtnStart.IsEnabled = false;
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
            BtnStart.IsEnabled = true;
        }
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        var page = _pages[ListStations.SelectedIndex];
        page.ResetForNext();
        TxtStatus.Text = $"{page.Title}：已切换下一台，串口保持连接，等待开始测试";
    }
}
