using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Printing;

namespace ProductionStationTool.App;

/// <summary>
/// 打印机设置：SN 码预设 / 外包箱预设（顶部菜单「打印机设置」进入）。
/// 模板排版在 BarTender Designer 调；此处管模板路径、打印机、命名数据源，
/// 并可逆向查看模板内文本对象的字体/字号/坐标、套用字体、试打印。
/// </summary>
public partial class PrinterPresetWindow : Window
{
    private readonly bool _isSn;

    /// <summary>kind："sn" = SN 码预设，"box" = 外包箱预设。</summary>
    public PrinterPresetWindow(string kind)
    {
        InitializeComponent();
        _isSn = !string.Equals(kind, "box", StringComparison.OrdinalIgnoreCase);
        Title = _isSn ? "打印机设置 — SN 码预设" : "打印机设置 — 外包箱预设";
        TxtTitle.Text = _isSn ? "SN 码预设" : "外包箱预设";

        var (sn, box) = MachineConfig.LoadPrintPresets();
        var preset = _isSn ? sn : box;
        BoxTemplate.Text = preset.TemplatePath;
        BoxPrinter.Text = preset.PrinterName;
        BoxNamed.Text = string.Join("\r\n", preset.NamedValues
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        LoadPreview();
    }

    /// <summary>加载 .btw 内嵌预览 PNG（纯文件解析，无需 BarTender/授权）。</summary>
    private void LoadPreview()
    {
        var path = BoxTemplate.Text.Trim();
        var png = path.Length > 0 ? BtwPreview.ExtractPng(path) : null;
        if (png is null)
        {
            ImgPreview.Source = null;
            TxtPreviewNote.Text = path.Length == 0
                ? "未选择模板"
                : "该 .btw 未内嵌预览图（BarTender 保存时会附带缩略图）";
            return;
        }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            bmp.Freeze();
            ImgPreview.Source = bmp;
            TxtPreviewNote.Text = $"模板：{Path.GetFileName(path)}（内嵌缩略图，无需 BarTender）";
        }
        catch (Exception ex)
        {
            ImgPreview.Source = null;
            TxtPreviewNote.Text = $"预览图解码失败：{ex.Message}";
        }
    }

    private LabelPreset Collect()
    {
        var named = new Dictionary<string, string>();
        foreach (var raw in BoxNamed.Text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            named[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return new LabelPreset
        {
            TemplatePath = BoxTemplate.Text.Trim(),
            PrinterName = BoxPrinter.Text.Trim(),
            NamedValues = named,
        };
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "BarTender 模板 (*.btw)|*.btw|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
        {
            BoxTemplate.Text = dlg.FileName;
            LoadPreview();
        }
    }

    private void BtnReadInfo_Click(object sender, RoutedEventArgs e)
    {
        if (!BarTenderLabelPrinter.Available())
        {
            TxtInfoStatus.Text = "本机未安装 BarTender（Automation 及以上），无法读取模板";
            return;
        }
        var path = BoxTemplate.Text.Trim();
        var info = BarTenderLabelPrinter.ReadTemplateInfo(path);
        if (info is null)
        {
            TxtInfoStatus.Text = $"读取失败：模板不存在或 BarTender 打不开（{path}）";
            return;
        }

        var lines = new List<string> { $"命名数据源（{info.NamedSubstrings.Count}）：" };
        lines.AddRange(info.NamedSubstrings.Select(n => $"  {n}"));
        lines.Add($"文本对象（{info.TextObjects.Count}）：");
        lines.AddRange(info.TextObjects.Select(o =>
            $"  {o.Name}  字体 {o.Font}  {o.Size:F1}pt  X={o.X:F2} Y={o.Y:F2}"));
        TxtInfo.Text = string.Join("\r\n", lines);
        TxtInfoStatus.Text = "读取成功（坐标单位同模板设置）";
    }

    private void BtnApplyFont_Click(object sender, RoutedEventArgs e)
    {
        if (!BarTenderLabelPrinter.Available())
        {
            TxtResult.Text = "本机未安装 BarTender，无法套用字体";
            return;
        }
        double size = 0;
        if (!string.IsNullOrWhiteSpace(BoxFontSize.Text) &&
            (!double.TryParse(BoxFontSize.Text.Trim(), out size) || size <= 0))
        {
            TxtResult.Text = "字号需大于 0（或留空不改）";
            return;
        }
        var (ok, detail) = BarTenderLabelPrinter.ApplyTemplateFont(
            BoxTemplate.Text.Trim(), BoxFontName.Text.Trim(), size);
        TxtResult.Text = detail;
        TxtResult.Foreground = ok ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Firebrick;
    }

    private async void BtnTestPrint_Click(object sender, RoutedEventArgs e)
    {
        var preset = Collect();
        if (string.IsNullOrWhiteSpace(preset.TemplatePath))
        {
            TxtResult.Text = "请先填写模板路径";
            return;
        }
        TxtResult.Text = "试打印中…";
        try
        {
            using var printer = new BarTenderLabelPrinter(preset);
            var (ok, detail) = await printer.PrintSnAsync("TEST0001");
            TxtResult.Text = ok ? $"试打印已发出：{detail}" : $"试打印失败：{detail}";
            TxtResult.Foreground = ok ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Firebrick;
        }
        catch (Exception ex)
        {
            TxtResult.Text = $"试打印异常：{ex.Message}";
            TxtResult.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var (sn, box) = MachineConfig.LoadPrintPresets();
        var updated = Collect();
        MachineConfig.SavePrintPresets(_isSn ? updated : sn, _isSn ? box : updated);
        TxtResult.Text = "已保存（machine.json）";
        TxtResult.Foreground = System.Windows.Media.Brushes.Green;
    }
}
