using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Data;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App;

/// <summary>
/// 汇总机页面（服务器电脑专用，整页接管主窗口，启动登录选汇总机身份后进入）：
/// 外壳与主页面同款——顶部横幅「设置」（汇总机独立），左侧「功能选择」
/// 两按钮，右侧操作栏 + 功能页，底部状态栏。身份仅启动登录时选定，无退出按钮，
/// 关闭窗口即退出应用。
/// 「工位机接入管理」全自动：工位机心跳上报（<see cref="StationPresenceReporter"/>），
/// 本页监听端口按连接远端 IP 自动登记（station_access.json），检测到接入自动生成
/// 状态卡片（在线/离线、最近接入、测试概况、允许/禁止接入），无需手动录入。
/// 「数据库表格」按时间/工位/机位/结果筛选 test_records，结果绿 PASS 红 FAIL。
/// </summary>
public partial class SummaryMachinePage : UserControl
{
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    /// <summary>在线判定窗口：心跳 15s × 3 无上报视为离线。</summary>
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(45);

    /// <summary>数据库表格一次查询的行数上限（产线日产量千级，够看且不卡）。</summary>
    private const int RecordLoadLimit = 1000;

    private const string AllText = "全部";

    private StationPresenceListener? _listener;
    private DispatcherTimer? _refreshTimer;

    public SummaryMachinePage()
    {
        InitializeComponent();
        InitDateCombos();
    }

    /// <summary>密码验证通过后进入页面调用：启动接入监听，载入两页数据。</summary>
    public void EnterUnlocked()
    {
        ListFunctions.SelectedIndex = 0;
        ShowSection(0);
        EnsureListener();
        RefreshCards();
        RefreshFilterOptions();
        RefreshRecords();
        TxtStatusSummary.Text = "汇总机：工位机自动接入监控与数据库管理";
    }

    // ================= 顶部横幅菜单（汇总机独立设置） =================

    private void MenuSetPassword_Click(object sender, RoutedEventArgs e)
    {
        var win = new SummaryPasswordWindow { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true)
            TxtStatusSummary.Text = "汇总机：管理密码已设置，下次进入生效";
    }

    // ================= 功能切换（左「功能选择」两个按钮） =================

    private void ListFunctions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        ShowSection(ListFunctions.SelectedIndex);
    }

    private void ShowSection(int index)
    {
        var isRecords = index == 1;
        PanelAccess.Visibility = isRecords ? Visibility.Collapsed : Visibility.Visible;
        PanelRecords.Visibility = isRecords ? Visibility.Visible : Visibility.Collapsed;
        BtnRefreshDb.Visibility = isRecords ? Visibility.Visible : Visibility.Collapsed;
        TxtSectionTitle.Text = isRecords ? "数据库表格" : "工位机接入管理";
        TxtSectionDesc.Text = isRecords
            ? "station_records.db · test_records：按时间/工位/机位/结果筛选，结果绿=PASS、红=FAIL"
            : "工位机接入后按 IP 自动登记并生成状态卡片（在线/离线、最近接入、测试概况）";
    }

    // ================= 工位机自动接入（监听 + 自动登记 + 卡片） =================

    /// <summary>启动接入监听（端口 5000，同 doc 18 QI-SERVER）与卡片定时刷新。</summary>
    private void EnsureListener()
    {
        if (_listener is not null)
            return;
        var listener = new StationPresenceListener();
        listener.MachineSeen += OnMachineSeen;
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            listener.Dispose();
            TxtStationSummary.Text =
                $"接入监听启动失败（端口 {StationPresenceListener.DefaultPort} 被占用？）：{ex.Message}";
            return;
        }
        _listener = listener;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => RefreshCards();
        _refreshTimer.Start();
    }

    /// <summary>工位机上报：按编码覆盖更新（IP/最近接入时间自动记录），卡片立即刷新。</summary>
    private void OnMachineSeen(StationPresence p)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var registry = StationAccessRegistry.Load();
                var entry = registry.FirstOrDefault(
                    r => string.Equals(r.Code, p.Code, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    entry = new StationAccessEntry { Code = p.Code, Allowed = true };
                    registry.Add(entry);
                }
                entry.Ip = p.Ip;
                entry.LastSeen = p.SeenAt;
                StationAccessRegistry.Save(registry);
                RefreshCards();
            }
            catch
            {
                // 单次记录失败不影响监听
            }
        });
    }

    /// <summary>自动生成状态卡片：已接入登记 ∪ 有测试记录的机位，一机位一卡。</summary>
    private void RefreshCards()
    {
        var registry = StationAccessRegistry.Load();
        var statsByCode = new Dictionary<string, MachineTestStats>(StringComparer.OrdinalIgnoreCase);
        using (var store = new SqliteTestRecordStore())
        {
            // 良率口径：「设置 → 数据管理」可调（弃用记录是否计入）
            foreach (var s in store.StatsByMachine(MachineConfig.LoadYieldExcludeAbandoned()))
                statsByCode[CodeOf(s.StationNo, s.MachineId)] = s;
        }

        var codes = new SortedSet<string>(
            registry.Select(r => r.Code).Concat(statsByCode.Keys), StringComparer.OrdinalIgnoreCase);
        var now = DateTime.Now;
        var vms = new List<MachineCardVm>();
        foreach (var code in codes)
        {
            var entry = registry.FirstOrDefault(
                r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
            statsByCode.TryGetValue(code, out var st);
            var online = entry?.LastSeen is { } t && now - t < OnlineWindow;
            var total = st?.Total ?? 0;
            var pass = st?.Pass ?? 0;
            vms.Add(new MachineCardVm
            {
                Code = code,
                Label = LabelOf(code),
                Ip = string.IsNullOrEmpty(entry?.Ip) ? "—" : entry!.Ip,
                Online = online,
                StatusText = online ? "● 在线" : "● 离线",
                LastSeenText = entry?.LastSeen is not { } last
                    ? "从未接入"
                    : online
                        ? $"{Math.Max(0, (int)(now - last).TotalSeconds)} 秒前"
                        : $"最近 {last:MM-dd HH:mm}",
                StatsText = total == 0
                    ? "无测试记录"
                    : $"测试 {total} · PASS {pass} · FAIL {st!.Fail} · 良率 {pass * 100.0 / total:F1}%",
                Allowed = entry?.Allowed ?? false,
            });
        }

        ItemsMachines.ItemsSource = vms;
        var onlineCount = vms.Count(v => v.Online);
        TxtStationSummary.Text =
            $"已发现 {vms.Count} 台工位机：在线 {onlineCount} · 离线 {vms.Count - onlineCount}"
            + $" · 允许 {vms.Count(v => v.Allowed)} · 禁止 {vms.Count(v => !v.Allowed)}"
            + "　（工位机接入后按 IP 自动登记，无需手动录入）";
    }

    /// <summary>卡片按钮：允许 ⇄ 禁止接入切换。</summary>
    private void CardToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string code)
            return;
        var registry = StationAccessRegistry.Load();
        var entry = registry.FirstOrDefault(
            r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return;
        entry.Allowed = !entry.Allowed;
        StationAccessRegistry.Save(registry);
        RefreshCards();
    }

    /// <summary>卡片按钮：移除记录（再次接入会自动重建）。</summary>
    private void CardRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string code)
            return;
        var registry = StationAccessRegistry.Load();
        registry.RemoveAll(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
        StationAccessRegistry.Save(registry);
        RefreshCards();
    }

    private static string CodeOf(int stationNo, string machineId) => $"ST-{stationNo:00}-{machineId}";

    /// <summary>ST-01-1 → (1, "1")；解析不了返回 (0, 原串)。</summary>
    private static (int StationNo, string MachineId) SplitCode(string code)
    {
        var m = Regex.Match(code, @"^ST-(\d+)-(.+)$");
        return m.Success ? (int.Parse(m.Groups[1].Value), m.Groups[2].Value) : (0, code);
    }

    /// <summary>ST-01-1 → 工位1 · 机位1（机位段只留数字）；解析不了就用原编码。</summary>
    private static string LabelOf(string code)
    {
        var (st, mid) = SplitCode(code);
        return st > 0 ? $"工位{st} · 机位{Regex.Match(mid, @"\d+$").Value}" : code;
    }

    // ================= 数据库表格（筛选 + 记录） =================

    /// <summary>工位/机位下拉：库中出现过的取值 +「全部」，项为纯阿拉伯数字。</summary>
    private void RefreshFilterOptions()
    {
        IReadOnlyList<(int StationNo, string MachineId)> sources;
        using (var store = new SqliteTestRecordStore())
            sources = store.DistinctSources();

        FillCombo(ComboMachine,
            sources.Select(s => s.MachineId).Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        FillCombo(ComboStation,
            sources.Select(s => s.StationNo).Distinct().OrderBy(x => x).Select(n => n.ToString()));
        FillCombo(ComboResult, new[] { "PASS", "FAIL" });
    }

    private static void FillCombo(ComboBox combo, IEnumerable<string> values)
    {
        combo.ItemsSource = new[] { AllText }.Concat(values).ToList();
        combo.SelectedIndex = 0;
    }

    private void BtnQuery_Click(object sender, RoutedEventArgs e) => RefreshRecords();

    private void BtnResetFilter_Click(object sender, RoutedEventArgs e)
    {
        ClearDateCombos();
        ComboStation.SelectedIndex = 0;
        ComboMachine.SelectedIndex = 0;
        ComboResult.SelectedIndex = 0;
        RefreshRecords();
    }

    // ---- 年/月/日 三下拉（可滚动）：-- 该项不限；只选年=整年，年+月=整月 ----

    private const string AnyText = "不限";

    private void InitDateCombos()
    {
        int yearNow = DateTime.Today.Year;
        var years = new List<string> { AnyText };
        for (int y = yearNow + 1; y >= yearNow - 3; y--)
            years.Add($"{y} 年");
        var months = new List<string> { AnyText };
        for (int m = 1; m <= 12; m++)
            months.Add($"{m} 月");
        var days = new List<string> { AnyText };
        for (int d = 1; d <= 31; d++)
            days.Add($"{d} 日");
        foreach (var cmb in new[] { CmbFromYear, CmbToYear })
        {
            cmb.ItemsSource = years;
            cmb.SelectedIndex = 0;
        }
        foreach (var cmb in new[] { CmbFromMonth, CmbToMonth })
        {
            cmb.ItemsSource = months;
            cmb.SelectedIndex = 0;
        }
        foreach (var cmb in new[] { CmbFromDay, CmbToDay })
        {
            cmb.ItemsSource = days;
            cmb.SelectedIndex = 0;
        }
    }

    private void ClearDateCombos()
    {
        foreach (var cmb in new[] { CmbFromYear, CmbFromMonth, CmbFromDay, CmbToYear, CmbToMonth, CmbToDay })
            cmb.SelectedIndex = 0;
    }

    private void SetDateCombos(DateTime from, DateTime to)
    {
        CmbFromYear.Text = $"{from.Year} 年";
        CmbFromMonth.Text = $"{from.Month} 月";
        CmbFromDay.Text = $"{from.Day} 日";
        CmbToYear.Text = $"{to.Year} 年";
        CmbToMonth.Text = $"{to.Month} 月";
        CmbToDay.Text = $"{to.Day} 日";
    }

    /// <summary>取下拉项里的数字（「2026 年」→2026）；「不限」→0。</summary>
    private static int ComboVal(ComboBox cmb)
    {
        if (cmb.SelectedItem is not string s)
            return 0;
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var v) ? v : 0;
    }

    /// <summary>三下拉合成日期段。year=0 → 该端不限；只选年 → 整年；年+月 → 整月。</summary>
    private static DateTime? ParseDateCombo(ComboBox year, ComboBox month, ComboBox day,
        bool endOfDay, out bool bad)
    {
        bad = false;
        int y = ComboVal(year), m = ComboVal(month), d = ComboVal(day);
        if (y == 0)
            return null;                       // 年未选 = 该端不限
        if (m == 0)
            return endOfDay ? new DateTime(y, 12, 31, 23, 59, 59) : new DateTime(y, 1, 1);
        if (d == 0)
        {
            var first = new DateTime(y, m, 1);
            return endOfDay
                ? first.AddMonths(1).AddSeconds(-1)
                : first;
        }
        if (!DateTime.TryParse($"{y}-{m}-{d}", out var dayVal))
        {
            bad = true;                        // 如 2 月 30 日
            return null;
        }
        dayVal = dayVal.Date;
        return endOfDay ? dayVal.AddDays(1).AddSeconds(-1) : dayVal;
    }

    private void BtnRefreshDb_Click(object sender, RoutedEventArgs e) => RefreshRecords();

    // 快捷区间：今天 / 近 7 天 / 近 30 天 / 本月 / 全部（一键填入三下拉并查询）
    private void ApplyRange(DateTime from, DateTime to)
    {
        SetDateCombos(from, to);
        RefreshRecords();
    }

    private void BtnRangeToday_Click(object sender, RoutedEventArgs e)
        => ApplyRange(DateTime.Today, DateTime.Today);

    private void BtnRange7d_Click(object sender, RoutedEventArgs e)
        => ApplyRange(DateTime.Today.AddDays(-6), DateTime.Today);

    private void BtnRange30d_Click(object sender, RoutedEventArgs e)
        => ApplyRange(DateTime.Today.AddDays(-29), DateTime.Today);

    private void BtnRangeMonth_Click(object sender, RoutedEventArgs e)
        => ApplyRange(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today);

    private void BtnRangeAll_Click(object sender, RoutedEventArgs e)
    {
        ClearDateCombos();
        RefreshRecords();
    }

    private bool _dateInputBad;

    private TestRecordFilter BuildFilter()
    {
        // 三下拉日期：起 00:00:00 / 止 23:59:59；未选年 = 该端不限
        bool badFrom, badTo;
        DateTime? from = ParseDateCombo(CmbFromYear, CmbFromMonth, CmbFromDay, endOfDay: false, out badFrom);
        DateTime? to = ParseDateCombo(CmbToYear, CmbToMonth, CmbToDay, endOfDay: true, out badTo);
        _dateInputBad = badFrom || badTo;
        var station = ComboStation.SelectedItem as string;
        var machine = ComboMachine.SelectedItem as string;
        return new TestRecordFilter(
            From: from,
            To: to,
            StationNo: station is not null && station != AllText && int.TryParse(station, out var st) ? st : null,
            MachineId: machine is not null && machine != AllText ? machine : null,
            Result: ComboResult.SelectedItem is string r && r != AllText ? r : null,
            Limit: RecordLoadLimit);
    }

    private void RefreshRecords()
    {
        try
        {
            var filter = BuildFilter();
            List<RecordRow> rows;
            using (var store = new SqliteTestRecordStore())
            {
                rows = store.Query(filter)
                    .Select(r => new RecordRow
                    {
                        Id = r.Id,
                        Sn = r.Sn,
                        StationNo = r.StationNo,
                        MachineId = r.MachineId,
                        Result = r.Result,
                        Voltage = r.Voltage,
                        Current = r.Current,
                        AbnormalItems = r.AbnormalItems,
                        TestTime = r.TestTime,
                        Abandoned = r.Abandoned,
                        Printed = r.Printed,
                    })
                    // 按机位分组排序：同机位记录排在一起，一眼扫过去看清该机位 PASS/FAIL
                    .OrderBy(r => r.StationNo)
                    .ThenBy(r => r.MachineId, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(r => r.Id)
                    .ToList();
            }
            GridRecords.ItemsSource = rows;

            var pass = rows.Count(r => r.Result == "PASS");
            var printed = rows.Count(r => r.Printed);
            double yield = rows.Count == 0 ? 0 : pass * 100.0 / rows.Count;
            TxtDbStats.Text =
                $"筛选结果 {rows.Count} 条：PASS {pass} · FAIL {rows.Count - pass} · 良率 {yield:F1}% · 已打印 {printed}（单次上限 {RecordLoadLimit} 条）"
                + (FilterActive(filter) ? "　|　已按筛选条件查询" : "")
                + (_dateInputBad ? "　|　日期无效（如 2 月 30 日），该项已忽略" : "");
            TxtDbStats.Foreground = MutedBrush;
        }
        catch (Exception ex)
        {
            GridRecords.ItemsSource = null;
            TxtDbStats.Text = $"数据库打开失败：{ex.Message}";
            TxtDbStats.Foreground = DangerBrush;
        }
    }

    private static bool FilterActive(TestRecordFilter f)
        => f.From is not null || f.To is not null || f.StationNo is not null
           || !string.IsNullOrEmpty(f.MachineId) || !string.IsNullOrEmpty(f.Result);

    // ================= 展示行（表格绑定） =================

    private sealed class RecordRow
    {
        public long Id { get; init; }
        public string Sn { get; init; } = "";
        public int StationNo { get; init; }
        public string MachineId { get; init; } = "";
        public string Result { get; init; } = "";
        public double Voltage { get; init; }
        public double Current { get; init; }
        public string AbnormalItems { get; init; } = "";
        public DateTime TestTime { get; init; }
        /// <summary>弃用标记（不流入打印；良率口径可排除）。</summary>
        public bool Abandoned { get; init; }
        /// <summary>已打印出库。</summary>
        public bool Printed { get; init; }
    }

    private sealed class MachineCardVm
    {
        public string Code { get; init; } = "";
        public string Label { get; init; } = "";
        public string Ip { get; init; } = "—";
        public bool Online { get; init; }
        public string StatusText { get; init; } = "● 离线";
        public string LastSeenText { get; init; } = "从未接入";
        public string StatsText { get; init; } = "无测试记录";
        public bool Allowed { get; init; }
        public string AllowedText => Allowed ? "✓ 允许接入" : "✗ 禁止接入";
    }
}
