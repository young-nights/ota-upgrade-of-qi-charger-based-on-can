using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Data;

namespace ProductionStationTool.App;

/// <summary>
/// 汇总机「设置 → 良率统计设置」：按机位显示实际统计，可填写修正良率并勾选「应用」，
/// 只覆盖汇总机卡片上的良率百分比显示；不勾选/不填则显示实际值。
/// 弃用记录固定不计入实际良率。修正存本机 machine.json，重启沿用。
/// </summary>
public partial class YieldSettingsWindow : Window
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    /// <summary>表格行：实际统计只读，修正良率/应用可编辑。</summary>
    public sealed class YieldRow
    {
        public string Code { get; init; } = "";
        public string Display { get; init; } = "";
        public string ActualText { get; init; } = "";
        public string ActualYieldText { get; init; } = "";
        public string OverrideText { get; set; } = "";
        public bool Apply { get; set; }
    }

    public YieldSettingsWindow()
    {
        InitializeComponent();
        LoadRows();
    }

    private void LoadRows()
    {
        var overrides = MachineConfig.LoadYieldOverrides()
            .ToDictionary(o => o.Code, StringComparer.OrdinalIgnoreCase);

        var statsByCode = new Dictionary<string, MachineTestStats>(StringComparer.OrdinalIgnoreCase);
        using (var store = new SqliteTestRecordStore())
        {
            // 弃用记录固定不计入实际良率
            foreach (var s in store.StatsByMachine(excludeAbandoned: true))
                statsByCode[CodeOf(s.StationNo, s.MachineId)] = s;
        }

        // 卡片可能来自接入登记（暂无测试记录），这里一并列出
        var registry = StationAccessRegistry.Load();
        var codes = new SortedSet<string>(
            registry.Select(r => r.Code).Concat(statsByCode.Keys), StringComparer.OrdinalIgnoreCase);

        var rows = new List<YieldRow>();
        int total = 0, pass = 0;
        foreach (var code in codes)
        {
            statsByCode.TryGetValue(code, out var st);
            overrides.TryGetValue(code, out var ov);
            if (st is not null)
            {
                total += st.Total;
                pass += st.Pass;
            }
            var actual = st is { Total: > 0 } ? st.Pass * 100.0 / st.Total : 0;
            rows.Add(new YieldRow
            {
                Code = code,
                Display = LabelOf(code),
                ActualText = st is null or { Total: 0 }
                    ? "无测试记录"
                    : $"测试 {st.Total} · PASS {st.Pass} · FAIL {st.Fail}",
                ActualYieldText = st is null or { Total: 0 } ? "—" : $"{actual:F1}%",
                OverrideText = ov is not null && (ov.Enabled || ov.Percent > 0)
                    ? ov.Percent.ToString("0.#")
                    : "",
                Apply = ov?.Enabled ?? false,
            });
        }

        GridYield.ItemsSource = rows;
        TxtYieldSummary.Text = total == 0
            ? "当前无测试记录（弃用记录固定不计入良率）"
            : $"全部机位合计：测试 {total} · PASS {pass} · 良率 {pass * 100.0 / total:F1}%（弃用记录固定不计入）";
    }

    private void BtnRefreshYield_Click(object sender, RoutedEventArgs e)
    {
        LoadRows();
        TxtYieldResult.Text = "已刷新实际统计";
        TxtYieldResult.Foreground = Brushes.Gray;
    }

    private void BtnSaveYield_Click(object sender, RoutedEventArgs e)
    {
        // 把当前正在编辑的单元格/行提交到绑定对象
        GridYield.CommitEdit(DataGridEditingUnit.Cell, true);
        GridYield.CommitEdit(DataGridEditingUnit.Row, true);
        if (GridYield.ItemsSource is not IEnumerable<YieldRow> rows)
            return;

        var overrides = new List<YieldOverride>();
        foreach (var row in rows)
        {
            var text = row.OverrideText.Trim();
            double percent = 0;
            if (text.Length > 0 &&
                (!double.TryParse(text, out percent) || percent < 0 || percent > 100))
            {
                TxtYieldResult.Text = $"{row.Display}：修正良率需为 0~100 的数字";
                TxtYieldResult.Foreground = DangerBrush;
                return;
            }
            if (row.Apply && text.Length == 0)
            {
                TxtYieldResult.Text = $"{row.Display}：勾选「应用」时请填写修正良率";
                TxtYieldResult.Foreground = DangerBrush;
                return;
            }
            if (row.Apply || text.Length > 0)
                overrides.Add(new YieldOverride(row.Code, row.Apply, percent));
        }

        try
        {
            MachineConfig.SaveYieldOverrides(overrides);
        }
        catch (Exception ex)
        {
            TxtYieldResult.Text = $"保存失败：{ex.Message}";
            TxtYieldResult.Foreground = DangerBrush;
            return;
        }

        LoadRows();
        TxtYieldResult.Text = "已保存到本机 machine.json，汇总机卡片按勾选状态显示";
        TxtYieldResult.Foreground = SuccessBrush;
    }

    private static string CodeOf(int stationNo, string machineId) => $"ST-{stationNo:00}-{machineId}";

    private static string LabelOf(string code)
    {
        var m = Regex.Match(code, @"^ST-(\d+)-(.+)$");
        return m.Success ? $"工位{m.Groups[1].Value} · 机位{m.Groups[2].Value}（{code}）" : code;
    }
}
