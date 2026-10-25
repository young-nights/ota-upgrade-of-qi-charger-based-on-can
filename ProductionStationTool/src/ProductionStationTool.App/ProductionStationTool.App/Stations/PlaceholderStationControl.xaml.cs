using System.Windows.Controls;

namespace ProductionStationTool.App.Stations;

/// <summary>工位 3/4 预留页面（构想文档待定义测试项后替换为独立页面）。</summary>
public partial class PlaceholderStationControl : UserControl, IStationPage
{
    private readonly int _stationNo;

    public int StationNo => _stationNo;
    public string Title => $"工位{_stationNo} — 预留";

    public PlaceholderStationControl(int stationNo)
    {
        InitializeComponent();
        _stationNo = stationNo;
        TxtTitle.Text = $"工位{stationNo} — 预留（测试项待定义）";
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        TxtTitle.Text = $"工位{_stationNo} — 测试项待定义";
        return Task.CompletedTask;
    }

    /// <summary>切换下一台：预留页仅复位标题提示，串口连接保持。</summary>
    public void ResetForNext()
    {
        TxtTitle.Text = $"工位{_stationNo} — 预留（测试项待定义）";
    }
}
