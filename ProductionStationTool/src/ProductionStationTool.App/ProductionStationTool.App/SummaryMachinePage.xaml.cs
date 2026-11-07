using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Data;

namespace ProductionStationTool.App;

/// <summary>
/// 汇总机页面（服务器电脑专用，整页接管主窗口，密码验证后进入）：
/// 外壳与主页面同款——顶部横幅「设置」（汇总机独立：仅设置汇总机密码）+「退出汇总机」，
/// 左侧「功能选择」两按钮（工位机接入管理 / 数据库表格），右侧操作栏 + 功能页，底部状态栏。
/// 接入管理：工位号+机位号结构化登记/编辑组件（实时编码预览 ST-xx-x），点行载入编辑；
/// 「工位机」列 = 编码 + 工位·机位 两行组件。数据库页按时间/工位/机位/结果筛选。
/// </summary>
public partial class SummaryMachinePage : UserControl
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    /// <summary>数据库表格一次查询的行数上限（产线日产量千级，够看且不卡）。</summary>
    private const int RecordLoadLimit = 1000;

    private const string AllText = "全部";
    private const string UnregisteredNote = "（未登记）";

    /// <summary>正在编辑已有登记时为原编码；null = 新增。</summary>
    private string? _editingCode;

    /// <summary>编辑组件回填防重入（SelectionChanged/TextChanged 互刷）。</summary>
    private bool _loadingEditor;

    /// <summary>点「退出汇总机」。</summary>
    public event Action? ExitRequested;

    public SummaryMachinePage()
    {
        InitializeComponent();
        // 可编辑 ComboBox 的文本变化是内部 TextBox 的路由事件，挂上以便实时编码预览
        CmbStationNo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => Editor_Changed(CmbStationNo, EventArgs.Empty)));
    }

    /// <summary>密码验证通过后进入页面调用：复位编辑组件并载入两个功能页的数据。</summary>
    public void EnterUnlocked()
    {
        CancelEdit();
        ListFunctions.SelectedIndex = 0;
        ShowSection(0);
        RefreshStations();
        RefreshFilterOptions();
        RefreshRecords();
        TxtStatusSummary.Text = "汇总机：管理工位机接入与数据库表格";
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

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
            : "登记工位机的编码、IP 与接入权限，并汇总各机位测试概况";
    }

    // ================= 工位机登记/编辑组件 =================

    private int ParseStation()
        => int.TryParse(CmbStationNo.Text.Trim(), out var n) ? n : 0;

    private static string CodeOf(int stationNo, string machineId) => $"ST-{stationNo:00}-{machineId}";

    /// <summary>ST-01-1 → (1, "1")；解析不了返回 (0, 原串)。</summary>
    private static (int StationNo, string MachineId) SplitCode(string code)
    {
        var m = Regex.Match(code, @"^ST-(\d+)-(.+)$");
        return m.Success ? (int.Parse(m.Groups[1].Value), m.Groups[2].Value) : (0, code);
    }

    private void Editor_Changed(object sender, EventArgs e)
    {
        // 机位号只收纯阿拉伯数字：混入的 "ST-01-" 等编码串即时过滤掉
        if (sender == TxtMachineId)
        {
            var clean = DigitsOnly(TxtMachineId.Text);
            if (clean != TxtMachineId.Text)
            {
                _loadingEditor = true;
                TxtMachineId.Text = clean;
                TxtMachineId.SelectionStart = clean.Length;
                _loadingEditor = false;
            }
        }
        if (_loadingEditor)
            return;
        UpdatePreview();
    }

    /// <summary>只保留阿拉伯数字（机位号输入过滤）。</summary>
    private static string DigitsOnly(string s) => new(s.Where(char.IsAsciiDigit).ToArray());

    /// <summary>取尾部数字（清洗脏数据："ST-01-1"→"1"、"A1"→"1"）。</summary>
    private static string TrailingDigits(string s) => Regex.Match(s, @"\d+$").Value;

    /// <summary>IPv4 点分四段、每段 0~255（登记可空，填了才校验）。</summary>
    private static bool IsValidIpv4(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4)
            return false;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var n) || n is < 0 or > 255)
                return false;
        }
        return true;
    }

    private void UpdatePreview()
    {
        var st = ParseStation();
        var mid = DigitsOnly(TxtMachineId.Text);
        TxtCodePreview.Text = st >= 1 && mid.Length > 0
            ? $"编码预览：{CodeOf(st, mid)}（工位{st} · 机位{mid}）"
            : "编码预览：—";
    }

    /// <summary>点表格行：回填编辑组件（改备注/工位机位，保存即改）。</summary>
    private void GridStations_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridStations.SelectedItem is not StationRowVm row)
            return;
        var (st, mid) = SplitCode(row.Code);
        _loadingEditor = true;
        CmbStationNo.Text = st > 0 ? st.ToString() : "";
        TxtMachineId.Text = TrailingDigits(mid);   // 脏数据（如 ST-01-1）只留尾部数字
        TxtIp.Text = row.Ip;
        TxtNewNote.Text = row.Note == UnregisteredNote ? "" : row.Note;
        _loadingEditor = false;
        _editingCode = row.Code;
        BtnSaveStation.Content = "保存修改";
        UpdatePreview();
    }

    private void BtnSaveStation_Click(object sender, RoutedEventArgs e)
    {
        var st = ParseStation();
        var mid = DigitsOnly(TxtMachineId.Text);
        if (mid.Length is < 1 or > 8)
        {
            StationMsg("机位号需 1~8 位纯阿拉伯数字", false);
            return;
        }
        var ip = TxtIp.Text.Trim();
        if (ip.Length > 0 && !IsValidIpv4(ip))
        {
            StationMsg("IP 地址格式不对（如 192.168.10.11）", false);
            return;
        }
        var err = MachineConfig.Validate(st, mid);
        if (err is not null)
        {
            StationMsg(err, false);
            return;
        }
        var code = CodeOf(st, mid);
        var registry = StationAccessRegistry.Load();
        var dup = registry.FirstOrDefault(r =>
            !string.Equals(r.Code, _editingCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
        if (dup is not null)
        {
            StationMsg($"编码 {code} 已登记", false);
            return;
        }

        var note = TxtNewNote.Text.Trim();
        var entry = _editingCode is null
            ? null
            : registry.FirstOrDefault(r => string.Equals(r.Code, _editingCode, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            registry.Add(new StationAccessEntry { Code = code, Ip = ip, Note = note, Allowed = true });
            StationAccessRegistry.Save(registry);
            RefreshStations();
            CancelEdit();
            StationMsg($"已添加 {code}（默认允许接入）", true);
            return;
        }

        entry.Code = code;
        entry.Ip = ip;
        entry.Note = note;
        StationAccessRegistry.Save(registry);
        RefreshStations();
        CancelEdit();
        StationMsg($"已保存 {code}", true);
    }

    private void BtnCancelEdit_Click(object sender, RoutedEventArgs e) => CancelEdit();

    /// <summary>退出编辑态：清空组件、按钮回到「添加」。</summary>
    private void CancelEdit()
    {
        _editingCode = null;
        _loadingEditor = true;
        CmbStationNo.Text = "";
        TxtMachineId.Text = "";
        TxtIp.Text = "";
        TxtNewNote.Text = "";
        _loadingEditor = false;
        GridStations.SelectedItem = null;
        BtnSaveStation.Content = "添加";
        UpdatePreview();
    }

    // ================= 工位机接入管理 =================

    /// <summary>登记清单 + 测试概况（统计来自 test_records 按机位聚合）。</summary>
    private void RefreshStations()
    {
        var registry = StationAccessRegistry.Load();
        var statsByCode = new Dictionary<string, MachineTestStats>(StringComparer.OrdinalIgnoreCase);
        using (var store = new SqliteTestRecordStore())
        {
            foreach (var s in store.StatsByMachine())
                statsByCode[CodeOf(s.StationNo, s.MachineId)] = s;
        }

        // 登记表 ∪ 有测试记录的机位：没登记却在测的机位也要暴露出来
        var codes = new SortedSet<string>(
            registry.Select(r => r.Code).Concat(statsByCode.Keys), StringComparer.OrdinalIgnoreCase);
        var rows = new List<StationRowVm>();
        foreach (var code in codes)
        {
            var entry = registry.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
            statsByCode.TryGetValue(code, out var st);
            var total = st?.Total ?? 0;
            var pass = st?.Pass ?? 0;
            rows.Add(new StationRowVm
            {
                Code = code,
                Label = LabelOf(code),
                Ip = entry?.Ip ?? "",
                Note = entry?.Note ?? UnregisteredNote,
                Allowed = entry?.Allowed ?? false,
                Total = total,
                Pass = pass,
                Fail = st?.Fail ?? 0,
                YieldText = total == 0 ? "—" : $"{pass * 100.0 / total:F1}%",
                LastTestText = st?.LastTestTime is { } t ? t.ToString("yyyy-MM-dd HH:mm") : "—",
            });
        }

        GridStations.ItemsSource = rows;
        TxtStationSummary.Text =
            $"共 {rows.Count} 台：允许 {rows.Count(r => r.Allowed)} · 禁止 {rows.Count(r => !r.Allowed)}"
            + $" · 有测试记录 {rows.Count(r => r.Total > 0)} 台 · 未登记 {rows.Count(r => r.Note == UnregisteredNote)} 台";
    }

    private StationAccessEntry? SelectedStation
    {
        get
        {
            var row = GridStations.SelectedItem as StationRowVm;
            return row is null
                ? null
                : new StationAccessEntry { Code = row.Code, Ip = row.Ip, Note = row.Note, Allowed = row.Allowed };
        }
    }

    private void BtnRemoveStation_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedStation;
        if (sel is null)
        {
            StationMsg("请先选中要移除的工位机", false);
            return;
        }
        var registry = StationAccessRegistry.Load();
        var removed = registry.RemoveAll(r => string.Equals(r.Code, sel.Code, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            StationMsg($"{sel.Code} 未登记，无可移除项", false);
            return;
        }
        StationAccessRegistry.Save(registry);
        RefreshStations();
        CancelEdit();
        StationMsg($"已移除 {sel.Code}", true);
    }

    private void BtnAllow_Click(object sender, RoutedEventArgs e) => SetAllowed(true);

    private void BtnDeny_Click(object sender, RoutedEventArgs e) => SetAllowed(false);

    private void SetAllowed(bool allowed)
    {
        var sel = SelectedStation;
        if (sel is null)
        {
            StationMsg("请先选中工位机", false);
            return;
        }
        var registry = StationAccessRegistry.Load();
        var entry = registry.FirstOrDefault(r => string.Equals(r.Code, sel.Code, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            // 有测试记录但未登记的机位：允许/禁止即登记一条
            entry = new StationAccessEntry
            {
                Code = sel.Code,
                Ip = sel.Ip,
                Note = sel.Note == UnregisteredNote ? "" : sel.Note,
            };
            registry.Add(entry);
        }
        entry.Allowed = allowed;
        StationAccessRegistry.Save(registry);
        RefreshStations();
        StationMsg($"{sel.Code}：{(allowed ? "允许" : "禁止")}接入", true);
    }

    private void StationMsg(string text, bool ok)
    {
        TxtStationMsg.Text = text;
        TxtStationMsg.Foreground = ok ? SuccessBrush : DangerBrush;
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
        PickFrom.SelectedDate = null;
        PickTo.SelectedDate = null;
        ComboStation.SelectedIndex = 0;
        ComboMachine.SelectedIndex = 0;
        ComboResult.SelectedIndex = 0;
        RefreshRecords();
    }

    private void BtnRefreshDb_Click(object sender, RoutedEventArgs e) => RefreshRecords();

    private TestRecordFilter BuildFilter()
    {
        // 选中日期不含时刻：起点取 00:00:00，止点取当天 23:59:59
        DateTime? from = PickFrom.SelectedDate is { } f ? f.Date : null;
        DateTime? to = PickTo.SelectedDate is { } t ? t.Date.AddDays(1).AddSeconds(-1) : null;
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
                    })
                    // 按机位分组排序：同机位记录排在一起，一眼扫过去看清该机位 PASS/FAIL
                    .OrderBy(r => r.StationNo)
                    .ThenBy(r => r.MachineId, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(r => r.Id)
                    .ToList();
            }
            GridRecords.ItemsSource = rows;

            var pass = rows.Count(r => r.Result == "PASS");
            TxtDbStats.Text =
                $"筛选结果 {rows.Count} 条：PASS {pass} · FAIL {rows.Count - pass}（单次上限 {RecordLoadLimit} 条）"
                + (FilterActive(filter) ? "　|　已按筛选条件查询" : "");
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

    /// <summary>ST-01-1 → 工位1 · 机位1（机位段只留数字）；解析不了就用原编码。</summary>
    private static string LabelOf(string code)
    {
        var (st, mid) = SplitCode(code);
        return st > 0 ? $"工位{st} · 机位{TrailingDigits(mid)}" : code;
    }

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
    }

    private sealed class StationRowVm
    {
        public string Code { get; init; } = "";
        public string Label { get; init; } = "";
        public string Ip { get; init; } = "";
        public string Note { get; init; } = "";
        public bool Allowed { get; init; }
        public int Total { get; init; }
        public int Pass { get; init; }
        public int Fail { get; init; }
        public string YieldText { get; init; } = "—";
        public string LastTestText { get; init; } = "—";
    }
}
