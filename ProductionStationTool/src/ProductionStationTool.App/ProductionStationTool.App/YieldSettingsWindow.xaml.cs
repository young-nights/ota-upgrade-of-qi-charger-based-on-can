using System.Windows;
using System.Windows.Media;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Data;

namespace ProductionStationTool.App;

/// <summary>
/// 汇总机「设置 → 良率统计设置」：维护良率口径（弃用记录是否计入），
/// 并显示按机位聚合的测试/良率概况。保存到 machine.json，重启后沿用。
/// </summary>
public partial class YieldSettingsWindow : Window
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));

    public YieldSettingsWindow()
    {
        InitializeComponent();
        ChkYieldExclude.IsChecked = MachineConfig.LoadYieldExcludeAbandoned();
        RefreshYieldSummary();
    }

    private void RefreshYieldSummary()
    {
        try
        {
            using var store = new SqliteTestRecordStore();
            bool exclude = ChkYieldExclude.IsChecked == true;
            var lines = new List<string> { $"当前口径：{(exclude ? "弃用记录不计入良率" : "全部记录计入良率")}" };
            var rows = store.StatsByMachine(exclude);
            if (rows.Count == 0)
            {
                lines.Add("（暂无测试记录）");
            }
            else
            {
                int total = 0, pass = 0, fail = 0;
                foreach (var r in rows)
                {
                    total += r.Total;
                    pass += r.Pass;
                    fail += r.Fail;
                    double y = r.Total == 0 ? 0 : r.Pass * 100.0 / r.Total;
                    lines.Add($"工位{r.StationNo}·机位{r.MachineId}：测试 {r.Total} · PASS {r.Pass} · FAIL {r.Fail} · 良率 {y:F1}%");
                }
                double all = total == 0 ? 0 : pass * 100.0 / total;
                lines.Insert(1, $"全部机位合计：测试 {total} · PASS {pass} · FAIL {fail} · 良率 {all:F1}%");
            }
            TxtYieldSummary.Text = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            TxtYieldSummary.Text = $"统计读取失败：{ex.Message}";
        }
    }

    private void BtnSaveYield_Click(object sender, RoutedEventArgs e)
    {
        MachineConfig.SaveYieldExcludeAbandoned(ChkYieldExclude.IsChecked == true);
        RefreshYieldSummary();
        TxtYieldResult.Text = "口径已保存（machine.json），重启后沿用";
        TxtYieldResult.Foreground = SuccessBrush;
    }

    private void BtnRefreshYield_Click(object sender, RoutedEventArgs e)
    {
        RefreshYieldSummary();
        TxtYieldResult.Text = "已刷新";
        TxtYieldResult.Foreground = Brushes.Gray;
    }
}
