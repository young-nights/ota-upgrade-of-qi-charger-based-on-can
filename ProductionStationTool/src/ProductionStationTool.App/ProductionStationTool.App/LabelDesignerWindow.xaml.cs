using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Printing;

namespace ProductionStationTool.App;

/// <summary>
/// SN 码标签设计器（「打印机设置 → SN 码预设」）：
/// 直接在本 UI 配置标签尺寸、打印机参数与对象排版（文本/字体/位置），
/// 「生成」把参数应用到画布副本产出 .btw——不经手工导入导出。
/// 生成/试打印走 BarTender COM（画布读取、参数写入、打印均本机 BarTender）。
/// </summary>
public partial class LabelDesignerWindow : Window
{
    private sealed class DesignRow
    {
        public string Name { get; init; } = "";
        public string Content { get; set; } = "";
        public string FontName { get; set; } = "";
        public string FontSizePt { get; set; } = "";
        public bool Bold { get; set; }
        public string XMm { get; set; } = "";
        public string YMm { get; set; } = "";
    }

    /// <summary>画布内命名数据源名（{SN} 只能落这些名字上）。</summary>
    private HashSet<string> _namedSources = new(StringComparer.OrdinalIgnoreCase);

    private const string BarTenderMissingText =
        "本机未检测到 BarTender——需安装 Seagull BarTender（Automation 及以上版本，如产线用的 10.1 企业自动化版），安装后 COM 自动注册即可用";

    public LabelDesignerWindow()
    {
        InitializeComponent();
        var d = MachineConfig.LoadLabelDesign();
        BoxCanvas.Text = string.IsNullOrWhiteSpace(d.CanvasTemplatePath)
            ? FindDefaultCanvas()
            : d.CanvasTemplatePath;
        BoxWidthMm.Text = d.LabelWidthMm.ToString("0.##");
        BoxHeightMm.Text = d.LabelHeightMm.ToString("0.##");
        BoxPaperMm.Text = d.PaperHeightMm.ToString("0.##");
        ComboPrinter.Text = d.PrinterName;
        BoxCopies.Text = Math.Max(1, d.Copies).ToString();
        BoxSpeed.Text = d.PrintSpeed.ToString("0.##");
        BoxDarkness.Text = d.PrintDarkness.ToString("0.##");
        GridObjects.ItemsSource = d.Objects.Select(ToRow).ToList();
        RefreshPrinters();
        LoadPreview();
        TxtResult.Text = "配置后点「生成 .btw」产出输出模板；「保存并应用」后工位 4 按此出标签。";
    }

    /// <summary>加载画布 .btw 内嵌预览 PNG（纯文件解析，无需 BarTender/授权）。</summary>
    private void LoadPreview()
    {
        var path = BoxCanvas.Text.Trim();
        var png = path.Length > 0 ? BtwPreview.ExtractPng(path) : null;
        if (png is null)
        {
            ImgPreview.Source = null;
            TxtPreviewNote.Text = path.Length == 0
                ? "未选择画布模板"
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
            TxtPreviewNote.Text = $"画布：{Path.GetFileName(path)}（内嵌缩略图，无需 BarTender）";
        }
        catch (Exception ex)
        {
            ImgPreview.Source = null;
            TxtPreviewNote.Text = $"预览图解码失败：{ex.Message}";
        }
    }

    private static DesignRow ToRow(LabelObjectDesign o) => new()
    {
        Name = o.Name,
        Content = o.Content,
        FontName = o.FontName,
        FontSizePt = o.FontSizePt > 0 ? o.FontSizePt.ToString("0.##") : "",
        Bold = o.Bold ?? false,
        XMm = o.XMm?.ToString("0.##") ?? "",
        YMm = o.YMm?.ToString("0.##") ?? "",
    };

    /// <summary>找内置画布：程序目录 / 仓库 docs/打印预设（开发机）。</summary>
    private static string FindDefaultCanvas()
    {
        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "打印预设", "SN码标签打印预设.btw"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SN码标签打印预设.btw"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "docs", "打印预设", "SN码标签打印预设.btw"),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists) ?? "";
    }

    private void RefreshPrinters()
    {
        var printers = BarTenderTemplateBuilder.ListPrinters();
        // 纯下拉只显示列表内项：已选打印机名不在列表时补进去，保证选中值可见
        var current = ComboPrinter.Text.Trim();
        var list = printers.ToList();
        if (current.Length > 0 && !list.Contains(current, StringComparer.OrdinalIgnoreCase))
            list.Insert(0, current);
        ComboPrinter.ItemsSource = list;
        if (list.Count > 0 && string.IsNullOrWhiteSpace(current))
            ComboPrinter.SelectedIndex = 0;
        else if (current.Length > 0)
            ComboPrinter.Text = current;
    }

    private void BtnRefreshPrinters_Click(object sender, RoutedEventArgs e) => RefreshPrinters();

    private void BtnBrowseCanvas_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "BarTender 模板 (*.btw)|*.btw|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
        {
            BoxCanvas.Text = dlg.FileName;
            _namedSources.Clear();
            GridObjects.ItemsSource = new List<DesignRow>();
            LoadPreview();
        }
    }

    private void BtnReadObjects_Click(object sender, RoutedEventArgs e)
    {
        if (!BarTenderLabelPrinter.Available())
        {
            TxtObjStatus.Text = BarTenderMissingText;
            return;
        }
        var info = BarTenderLabelPrinter.ReadTemplateInfo(BoxCanvas.Text.Trim());
        if (info is null)
        {
            TxtObjStatus.Text = "读取失败：画布不存在或 BarTender 打不开";
            return;
        }

        _namedSources = new HashSet<string>(info.NamedSubstrings, StringComparer.OrdinalIgnoreCase);
        var saved = new Dictionary<string, LabelObjectDesign>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in MachineConfig.LoadLabelDesign().Objects)
            saved[o.Name] = o;

        var rows = new List<DesignRow>();
        foreach (var o in info.TextObjects)
        {
            if (saved.TryGetValue(o.Name, out var s))
            {
                rows.Add(ToRow(s));
                continue;
            }
            rows.Add(new DesignRow
            {
                Name = o.Name,
                FontName = o.Font,
                FontSizePt = o.Size > 0 ? o.Size.ToString("0.##") : "",
                XMm = o.X.ToString("0.##"),
                YMm = o.Y.ToString("0.##"),
            });
        }
        GridObjects.ItemsSource = rows;
        TxtObjStatus.Text = $"对象 {rows.Count} 个；命名数据源（{{SN}} 可用）：" +
                            (_namedSources.Count > 0 ? string.Join("、", _namedSources) : "（无）");
    }

    private LabelDesign Collect()
    {
        var d = new LabelDesign
        {
            CanvasTemplatePath = BoxCanvas.Text.Trim(),
            OutputTemplatePath = MachineConfig.DefaultOutputTemplatePath,
            LabelWidthMm = ParseD(BoxWidthMm.Text, 50),
            LabelHeightMm = ParseD(BoxHeightMm.Text, 30),
            PaperHeightMm = ParseD(BoxPaperMm.Text, 0),
            PrinterName = ComboPrinter.Text.Trim(),
            Copies = (int)Math.Max(1, ParseD(BoxCopies.Text, 1)),
            PrintSpeed = ParseD(BoxSpeed.Text, 0),
            PrintDarkness = ParseD(BoxDarkness.Text, 0),
            Objects = new List<LabelObjectDesign>(),
        };
        foreach (var r in GridObjects.Items.OfType<DesignRow>())
        {
            if (string.IsNullOrWhiteSpace(r.Name))
                continue;
            d.Objects.Add(new LabelObjectDesign
            {
                Name = r.Name.Trim(),
                Content = r.Content?.Trim() ?? "",
                FontName = r.FontName?.Trim() ?? "",
                FontSizePt = ParseD(r.FontSizePt, 0),
                Bold = r.Bold,
                XMm = string.IsNullOrWhiteSpace(r.XMm) ? null : ParseD(r.XMm, 0),
                YMm = string.IsNullOrWhiteSpace(r.YMm) ? null : ParseD(r.YMm, 0),
            });
        }
        return d;
    }

    private static double ParseD(string? text, double fallback)
        => double.TryParse(text?.Trim(), out var v) ? v : fallback;

    /// <summary>设计 → 打印预设（输出模板 + {SN} 命名数据源映射）。</summary>
    private static LabelPreset ToPreset(LabelDesign d)
    {
        var named = new Dictionary<string, string>();
        foreach (var o in d.Objects)
        {
            if (string.IsNullOrEmpty(o.Content))
                continue;
            if (o.Content.Contains("{SN}", StringComparison.OrdinalIgnoreCase))
                named[o.Name] = o.Content;       // 打印时替换 {SN}
        }
        return new LabelPreset
        {
            TemplatePath = d.OutputTemplatePath,
            PrinterName = d.PrinterName,
            NamedValues = named,
        };
    }

    private (bool Ok, string Detail) Generate()
    {
        if (!BarTenderLabelPrinter.Available())
            return (false, BarTenderMissingText);
        return BarTenderTemplateBuilder.Generate(Collect());
    }

    private void BtnGenerate_Click(object sender, RoutedEventArgs e)
    {
        var (ok, detail) = Generate();
        TxtResult.Text = detail;
        TxtResult.Foreground = ok
            ? System.Windows.Media.Brushes.Green
            : System.Windows.Media.Brushes.Firebrick;
    }

    private async void BtnTestPrint_Click(object sender, RoutedEventArgs e)
    {
        var (ok, detail) = Generate();
        if (!ok)
        {
            TxtResult.Text = detail;
            TxtResult.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }
        var design = Collect();
        try
        {
            using var printer = new BarTenderLabelPrinter(ToPreset(design));
            var (printOk, printDetail) = await printer.PrintSnAsync("TEST0001");
            TxtResult.Text = detail + "\n试打印：" + (printOk ? printDetail : "失败 " + printDetail);
            TxtResult.Foreground = printOk
                ? System.Windows.Media.Brushes.Green
                : System.Windows.Media.Brushes.Firebrick;
        }
        catch (Exception ex)
        {
            TxtResult.Text = detail + $"\n试打印异常：{ex.Message}";
            TxtResult.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var design = Collect();
        var (ok, detail) = BarTenderLabelPrinter.Available()
            ? BarTenderTemplateBuilder.Generate(design)
            : (true, "本机未装 BarTender：仅保存设计参数（生成 .btw / 打印需在装了 BarTender 的机器上）");
        if (ok)
        {
            MachineConfig.SaveLabelDesign(design);
            var (_, box) = MachineConfig.LoadPrintPresets();
            MachineConfig.SavePrintPresets(ToPreset(design), box);
        }
        TxtResult.Text = detail + (ok ? "\n已保存设计与打印预设（machine.json），工位 4 按此出标签。" : "");
        TxtResult.Foreground = ok
            ? System.Windows.Media.Brushes.Green
            : System.Windows.Media.Brushes.Firebrick;
    }
}
