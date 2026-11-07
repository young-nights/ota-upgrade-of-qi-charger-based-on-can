using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Data;

namespace ProductionStationTool.App;

/// <summary>
/// 汇总机页面（服务器电脑专用，整页接管主窗口）：
/// 外壳与主页面同款——顶部横幅菜单（设置/关于/修改汇总机密码）、左侧「功能选择」
/// 两个按钮（工位机接入管理 / 数据库表格）、右侧操作栏 + 功能页、底部状态栏。
/// 进入需输管理密码（默认 admin123，machine.json 只存 SHA-256 哈希）；
/// 数据库页按机位分清：一机位一卡（绿=最近 PASS、红=最近 FAIL、灰=未测试），
/// 表格按机位分组排序、结果绿/红，点卡片筛选；「退出汇总机」返回工位机工作区。
/// </summary>
public partial class SummaryMachinePage : UserControl
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    /// <summary>数据库表格一次加载的行数上限（产线日产量千级，够看且不卡）。</summary>
    private const int RecordLoadLimit = 1000;

    private List<StationAccessEntry> _stations = new();
    private List<MachineVm> _machines = new();
    private List<RecordRow> _rows = new();

    /// <summary>机位筛选（null = 全部）；点概览卡片切换。</summary>
    private string? _filterCode;

    /// <summary>点「退出汇总机」。</summary>
    public event Action? ExitRequested;

    public SummaryMachinePage()
    {
        InitializeComponent();
    }

    /// <summary>每次进入页面调用：回到密码门（每次进入都需输密码）。</summary>
    public void ResetToLocked()
    {
        LockPanel.Visibility = Visibility.Visible;
        MgmtPanel.Visibility = Visibility.Collapsed;
        TxtPassword.Clear();
        TxtLockMsg.Text = "";
        TxtStatusSummary.Text = "汇总机：请输入管理密码进入管理区";
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    // ================= 顶部横幅菜单（与主页面同款） =================

    private void MenuSettingsNetwork_Click(object sender, RoutedEventArgs e)
        => OpenSettings(0);

    private void MenuSettingsCoil_Click(object sender, RoutedEventArgs e)
        => OpenSettings(1);

    private void OpenSettings(int startTab)
    {
        var win = new SettingsWindow(startTab) { Owner = Window.GetWindow(this) };
        win.ShowDialog();
    }

    private void MenuChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var win = new SummaryPasswordWindow { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true)
            TxtStatusSummary.Text = "汇总机：管理密码已修改，下次进入生效";
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var win = new AboutWindow { Owner = Window.GetWindow(this) };
        win.ShowDialog();
    }

    // ================= 密码门 =================

    private void BtnUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (MachineConfig.Sha256Hex(TxtPassword.Password) == MachineConfig.LoadSummaryPasswordHash())
        {
            TxtPassword.Clear();
            TxtLockMsg.Text = "";
            LockPanel.Visibility = Visibility.Collapsed;
            MgmtPanel.Visibility = Visibility.Visible;
            ShowSection(ListFunctions.SelectedIndex < 0 ? 0 : ListFunctions.SelectedIndex);
            RefreshStations();
            RefreshRecords();
        }
        else
        {
            TxtLockMsg.Text = "密码错误，请重试";
            TxtPassword.SelectAll();
            TxtPassword.Focus();
        }
    }

    private void TxtPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            BtnUnlock_Click(sender, e);
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
        BtnShowAll.Visibility = isRecords ? Visibility.Visible : Visibility.Collapsed;
        TxtSectionTitle.Text = isRecords ? "数据库表格" : "工位机接入管理";
        TxtSectionDesc.Text = isRecords
            ? "station_records.db · test_records：按机位分组，结果绿=PASS、红=FAIL"
            : "维护允许接入服务器的工位机清单（编码如 ST-01-1）";
        TxtStatusSummary.Text = isRecords
            ? "汇总机：数据库表格（按机位分清 PASS/FAIL）"
            : "汇总机：工位机接入管理";
    }

    // ================= 工位机接入管理 =================

    private void RefreshStations()
    {
        _stations = StationAccessRegistry.Load();
        GridStations.ItemsSource = _stations;
        TxtStationMsg.Text = $"共 {_stations.Count} 台登记";
        TxtStationMsg.Foreground = MutedBrush;
    }

    private void SaveStations()
    {
        StationAccessRegistry.Save(_stations);
        GridStations.Items.Refresh();
    }

    private StationAccessEntry? SelectedStation => GridStations.SelectedItem as StationAccessEntry;

    private void BtnAddStation_Click(object sender, RoutedEventArgs e)
    {
        var code = TxtNewCode.Text.Trim().ToUpperInvariant();
        var err = StationAccessRegistry.ValidateCode(code);
        if (err is not null)
        {
            StationMsg(err, false);
            return;
        }
        if (_stations.Any(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            StationMsg($"编码 {code} 已登记", false);
            return;
        }
        _stations.Add(new StationAccessEntry
        {
            Code = code,
            Note = TxtNewNote.Text.Trim(),
            Allowed = true,
        });
        SaveStations();
        TxtNewCode.Clear();
        TxtNewNote.Clear();
        StationMsg($"已添加 {code}（默认允许接入）", true);
    }

    private void BtnRemoveStation_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedStation;
        if (sel is null)
        {
            StationMsg("请先选中要移除的工位机", false);
            return;
        }
        _stations.Remove(sel);
        SaveStations();
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
        sel.Allowed = allowed;
        SaveStations();
        StationMsg($"{sel.Code}：{(allowed ? "允许" : "禁止")}接入", true);
    }

    private void StationMsg(string text, bool ok)
    {
        TxtStationMsg.Text = text;
        TxtStationMsg.Foreground = ok ? SuccessBrush : DangerBrush;
    }

    // ================= 数据库表格（机位概览 + 记录） =================

    private void BtnRefreshDb_Click(object sender, RoutedEventArgs e) => RefreshRecords();

    private void BtnShowAll_Click(object sender, RoutedEventArgs e)
    {
        _filterCode = null;
        RefreshRecords();
    }

    /// <summary>点机位卡片：选中=筛选该机位；再点同一张=取消筛选。</summary>
    private void MachineCard_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string code)
            return;
        _filterCode = string.Equals(_filterCode, code, StringComparison.OrdinalIgnoreCase) ? null : code;
        RefreshRecords();
    }

    private void RefreshRecords()
    {
        try
        {
            using var store = new SqliteTestRecordStore();
            _rows = store.Recent(RecordLoadLimit)
                .Select(r => new RecordRow
                {
                    Id = r.Id,
                    Sn = r.Sn,
                    Code = $"ST-{r.StationNo:00}-{r.MachineId}",
                    Result = r.Result,
                    Voltage = r.Voltage,
                    Current = r.Current,
                    AbnormalItems = r.AbnormalItems,
                    TestTime = r.TestTime,
                })
                // 按机位分组排序：同机位记录排在一起，一眼扫过去看清该机位 PASS/FAIL
                .OrderBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(r => r.Id)
                .ToList();

            _machines = BuildMachineOverview(_rows);
            ItemsMachines.ItemsSource = _machines;

            var view = _filterCode is null
                ? _rows
                : _rows.Where(r => string.Equals(r.Code, _filterCode, StringComparison.OrdinalIgnoreCase)).ToList();
            GridRecords.ItemsSource = view;

            var pass = view.Count(r => r.Result == "PASS");
            TxtDbStats.Text =
                $"最近 {RecordLoadLimit} 条记录 · 共 {view.Count} 条：PASS {pass} · FAIL {view.Count - pass}"
                + (_filterCode is null
                    ? $"（{_machines.Count} 个机位，点卡片筛选）"
                    : $"　|　已筛选：{_filterCode}（点「显示全部机位」或再点卡片取消）");
            TxtDbStats.Foreground = MutedBrush;
            BtnShowAll.IsEnabled = _filterCode is not null;
        }
        catch (Exception ex)
        {
            _rows.Clear();
            _machines.Clear();
            ItemsMachines.ItemsSource = null;
            GridRecords.ItemsSource = null;
            TxtDbStats.Text = $"数据库打开失败：{ex.Message}";
            TxtDbStats.Foreground = DangerBrush;
        }
    }

    /// <summary>
    /// 机位概览：登记表（station_access.json）∪ 记录里出现过的机位，
    /// 一机位一卡——最近一条记录 PASS 绿 / FAIL 红 / 无记录灰，附通过/失败计数。
    /// </summary>
    private List<MachineVm> BuildMachineOverview(List<RecordRow> rows)
    {
        var byCode = new Dictionary<string, List<RecordRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (!byCode.TryGetValue(r.Code, out var list))
                byCode[r.Code] = list = new List<RecordRow>();
            list.Add(r);
        }

        var codes = new SortedSet<string>(byCode.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var s in StationAccessRegistry.Load())
            codes.Add(s.Code);

        var vms = new List<MachineVm>();
        foreach (var code in codes)
        {
            byCode.TryGetValue(code, out var list);
            list ??= new List<RecordRow>();
            var latest = list.OrderByDescending(r => r.Id).FirstOrDefault();
            var pass = list.Count(r => r.Result == "PASS");
            var fail = list.Count(r => r.Result == "FAIL");
            vms.Add(new MachineVm
            {
                Code = code,
                Label = LabelOf(code),
                Status = latest?.Result ?? "未测试",
                Counts = latest is null ? "无记录" : $"✓ {pass} · ✗ {fail}",
                IsSelected = string.Equals(_filterCode, code, StringComparison.OrdinalIgnoreCase),
            });
        }
        return vms;
    }

    /// <summary>ST-01-1 → 工位1 · 机位1；解析不了就用原编码。</summary>
    private static string LabelOf(string code)
    {
        var m = Regex.Match(code, @"^ST-(\d+)-(.+)$");
        return m.Success ? $"工位{int.Parse(m.Groups[1].Value)} · 机位{m.Groups[2].Value}" : code;
    }

    // ================= 展示行（表格绑定） =================

    private sealed class RecordRow
    {
        public long Id { get; init; }
        public string Sn { get; init; } = "";
        public string Code { get; init; } = "";
        public string Result { get; init; } = "";
        public double Voltage { get; init; }
        public double Current { get; init; }
        public string AbnormalItems { get; init; } = "";
        public DateTime TestTime { get; init; }
    }

    private sealed class MachineVm
    {
        public string Code { get; init; } = "";
        public string Label { get; init; } = "";
        public string Status { get; init; } = "未测试";
        public string Counts { get; init; } = "";
        public bool IsSelected { get; init; }
    }
}
