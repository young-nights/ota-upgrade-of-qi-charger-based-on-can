using System.Windows;
using System.Windows.Media;
using ProductionStationTool.App.Utilities;

namespace ProductionStationTool.App;

/// <summary>
/// 修改汇总机管理密码（汇总机页横幅「设置 → 修改汇总机密码…」弹出）：
/// 校验旧密码 → 新密码 ≥6 位且两次一致 → SHA-256 哈希写 machine.json。
/// </summary>
public partial class SummaryPasswordWindow : Window
{
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    public SummaryPasswordWindow()
    {
        InitializeComponent();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (MachineConfig.Sha256Hex(TxtOldPw.Password) != MachineConfig.LoadSummaryPasswordHash())
        {
            Msg("旧密码不正确", false);
            return;
        }
        if (TxtNewPw.Password.Length < 6)
        {
            Msg("新密码至少 6 位", false);
            return;
        }
        if (TxtNewPw.Password != TxtNewPw2.Password)
        {
            Msg("两次输入的新密码不一致", false);
            return;
        }

        MachineConfig.SaveSummaryPasswordHash(MachineConfig.Sha256Hex(TxtNewPw.Password));
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Msg(string text, bool ok)
    {
        TxtMsg.Text = text;
        TxtMsg.Foreground = ok ? SuccessBrush : DangerBrush;
    }
}
