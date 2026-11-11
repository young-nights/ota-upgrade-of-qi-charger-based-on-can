using System.Windows;
using System.Windows.Input;
using ProductionStationTool.App.Utilities;

namespace ProductionStationTool.App;

/// <summary>启动登录身份：工位机（工位测试）或汇总机（服务器管理）。</summary>
public enum MachineMode
{
    Workstation,
    Summary,
}

/// <summary>
/// 启动登录窗口：选择工位机/汇总机后点「登录」进入对应页面。
/// 工位机免密码；汇总机需管理密码（SHA-256 比对 machine.json）。
/// 汇总机不计入机位和工位——不发工位接入心跳、不加载工位身份。
/// 身份仅在此选定，工位机页面无汇总机入口、不可中途切换。
/// </summary>
public partial class ModeLoginWindow : Window
{
    public MachineMode SelectedMode { get; private set; } = MachineMode.Workstation;

    public ModeLoginWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (RdoSummary.IsChecked == true) TxtPassword.Focus();
            else RdoStation.Focus();
        };
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        // 选汇总机才可输密码（登录时校验）
        if (TxtPassword is not null)
            TxtPassword.IsEnabled = RdoSummary.IsChecked == true;
    }

    private void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        if (RdoSummary.IsChecked == true)
        {
            if (MachineConfig.Sha256Hex(TxtPassword.Password) != MachineConfig.LoadSummaryPasswordHash())
            {
                TxtMsg.Text = "密码错误，请重试";
                TxtPassword.SelectAll();
                TxtPassword.Focus();
                return;
            }
            SelectedMode = MachineMode.Summary;
        }
        else
        {
            SelectedMode = MachineMode.Workstation;
        }
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TxtPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            BtnLogin_Click(sender, e);
    }
}
