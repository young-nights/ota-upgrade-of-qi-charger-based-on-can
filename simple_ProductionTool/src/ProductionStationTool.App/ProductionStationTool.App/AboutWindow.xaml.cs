using System.Reflection;
using System.Windows;
using ProductionStationTool.App.Utilities;

namespace ProductionStationTool.App;

/// <summary>关于弹窗：版本、本机编码、运行环境。</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "—";
        TxtVersion.Text = $"版本：{version}";
        TxtIdentity.Text = $"本机编码：{MachineConfig.Load().Code}";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
