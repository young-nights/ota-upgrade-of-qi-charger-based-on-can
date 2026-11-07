using System.Windows;
using System.Windows.Input;
using ProductionStationTool.App.Utilities;

namespace ProductionStationTool.App;

/// <summary>
/// 进入汇总机的密码输入窗口（居中弹出）：
/// SHA-256 比对 machine.json 里的管理密码哈希，正确则放行进入汇总机页面。
/// </summary>
public partial class SummaryLoginWindow : Window
{
    public SummaryLoginWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => TxtPassword.Focus();
    }

    private void BtnEnter_Click(object sender, RoutedEventArgs e)
    {
        if (MachineConfig.Sha256Hex(TxtPassword.Password) == MachineConfig.LoadSummaryPasswordHash())
        {
            DialogResult = true;
            return;
        }
        TxtMsg.Text = "密码错误，请重试";
        TxtPassword.SelectAll();
        TxtPassword.Focus();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TxtPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            BtnEnter_Click(sender, e);
    }
}
