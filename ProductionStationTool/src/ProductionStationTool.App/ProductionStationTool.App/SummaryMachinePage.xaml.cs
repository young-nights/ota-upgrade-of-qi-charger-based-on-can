using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Data;

namespace ProductionStationTool.App;

/// <summary>
/// 汇总机页面（服务器电脑专用，整页接管主窗口）：
/// 进入需输管理密码（默认 admin123，可在页内修改，machine.json 只存 SHA-256 哈希）；
/// 解锁后管理「工位机接入清单」（station_access.json）与「数据库表格」
/// （station_records.db · test_records）；「退出汇总机」返回工位机工作区。
/// </summary>
public partial class SummaryMachinePage : UserControl
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

    /// <summary>数据库表格一次加载的行数上限（产线日产量千级，够看且不卡）。</summary>
    private const int RecordLoadLimit = 1000;

    private List<StationAccessEntry> _stations = new();

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
        PwPanel.Visibility = Visibility.Collapsed;
        TxtPassword.Clear();
        TxtLockMsg.Text = "";
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    // ================= 密码门 =================

    private void BtnUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (MachineConfig.Sha256Hex(TxtPassword.Password) == MachineConfig.LoadSummaryPasswordHash())
        {
            TxtPassword.Clear();
            TxtLockMsg.Text = "";
            LockPanel.Visibility = Visibility.Collapsed;
            MgmtPanel.Visibility = Visibility.Visible;
            PwPanel.Visibility = Visibility.Visible;
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

    // ================= 数据库表格 =================

    private void BtnRefreshDb_Click(object sender, RoutedEventArgs e) => RefreshRecords();

    private void RefreshRecords()
    {
        try
        {
            using var store = new SqliteTestRecordStore();
            var rows = store.Recent(RecordLoadLimit);
            GridRecords.ItemsSource = rows;
            var pass = rows.Count(r => r.Result == "PASS");
            TxtDbStats.Text = $"最近 {rows.Count} 条：PASS {pass} · FAIL {rows.Count - pass}（上限 {RecordLoadLimit} 条）";
            TxtDbStats.Foreground = MutedBrush;
        }
        catch (Exception ex)
        {
            GridRecords.ItemsSource = null;
            TxtDbStats.Text = $"数据库打开失败：{ex.Message}";
            TxtDbStats.Foreground = DangerBrush;
        }
    }

    // ================= 修改密码 =================

    private void BtnSavePw_Click(object sender, RoutedEventArgs e)
    {
        var oldPw = TxtOldPw.Password;
        var newPw = TxtNewPw.Password;
        var newPw2 = TxtNewPw2.Password;

        if (MachineConfig.Sha256Hex(oldPw) != MachineConfig.LoadSummaryPasswordHash())
        {
            PwMsg("旧密码不正确", false);
            return;
        }
        if (newPw.Length < 6)
        {
            PwMsg("新密码至少 6 位", false);
            return;
        }
        if (newPw != newPw2)
        {
            PwMsg("两次输入的新密码不一致", false);
            return;
        }

        MachineConfig.SaveSummaryPasswordHash(MachineConfig.Sha256Hex(newPw));
        TxtOldPw.Clear();
        TxtNewPw.Clear();
        TxtNewPw2.Clear();
        PwMsg("新密码已保存，下次进入汇总机生效", true);
    }

    private void PwMsg(string text, bool ok)
    {
        TxtPwMsg.Text = text;
        TxtPwMsg.Foreground = ok ? SuccessBrush : DangerBrush;
    }
}
