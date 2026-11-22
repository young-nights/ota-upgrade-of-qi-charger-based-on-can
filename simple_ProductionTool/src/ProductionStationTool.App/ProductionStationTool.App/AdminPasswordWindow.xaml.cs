using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ProductionStationTool.App.Utilities;

namespace ProductionStationTool.App;

/// <summary>
/// 管理密码二次验证窗：进入汇总机「良率统计设置」前弹出，
/// 校验的是汇总机管理密码（machine.json 只存 SHA-256 哈希）。
/// </summary>
public partial class AdminPasswordWindow : Window
{
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    public AdminPasswordWindow(string purpose)
    {
        InitializeComponent();
        TxtPurpose.Text = $"进入「{purpose}」需要输入汇总机管理密码进行验证。";
        Loaded += (_, _) => TxtPassword.Focus();
    }

    /// <summary>验证通过返回 true；取消/密码错误返回 false。</summary>
    public static bool Verify(Window? owner, string purpose)
    {
        var win = new AdminPasswordWindow(purpose) { Owner = owner };
        return win.ShowDialog() == true;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (MachineConfig.Sha256Hex(TxtPassword.Password) != MachineConfig.LoadSummaryPasswordHash())
        {
            TxtMsg.Text = "密码错误，请重试";
            TxtMsg.Foreground = DangerBrush;
            TxtPassword.SelectAll();
            TxtPassword.Focus();
            return;
        }
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TxtPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            BtnOk_Click(sender, e);
    }
}
