using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ProductionStationTool.App.Utilities;
using ProductionStationTool.Core.Printing;

namespace ProductionStationTool.App;

/// <summary>
/// SN 码标签设计器（「打印机设置」）：直接配置标签尺寸、打印机参数与文字排版，
/// 打印时按 TSPL/ZPL 指令经 RAW 原始打印直发打印机——无需 BarTender / 无授权限制。
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

    public LabelDesignerWindow()
    {
        InitializeComponent();
        var d = MachineConfig.LoadLabelDesign();
        BoxWidthMm.Text = d.LabelWidthMm.ToString("0.##");
        BoxHeightMm.Text = d.LabelHeightMm.ToString("0.##");
        BoxPaperMm.Text = d.PaperHeightMm.ToString("0.##");
        ComboPrinter.Text = d.PrinterName;
        BoxCopies.Text = Math.Max(1, d.Copies).ToString();
        BoxSpeed.Text = d.PrintSpeed.ToString("0.##");
        BoxDarkness.Text = d.PrintDarkness.ToString("0.##");
        ComboScript.SelectedIndex = string.Equals(d.Script, "zpl", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ComboDpi.SelectedIndex = d.Dpi == 300 ? 1 : 0;
        BoxGapMm.Text = d.GapMm.ToString("0.##");
        GridObjects.ItemsSource = d.Objects.Select(ToRow).ToList();
        _importedLines = d.Lines
            .Select(v => new LabelLineDesign
            {
                XMm = v.XMm, YMm = v.YMm,
                WidthMm = v.WidthMm, HeightMm = v.HeightMm,
            })
            .ToList();
        RefreshPrinters();
        TxtResult.Text = "配置后「试打印一张」核对版式，「保存」后工位 4 按此出标签。";

        // 任意输入变化 → 防抖重绘效果预览（路由事件统一挂窗口级，含表格单元格编辑）
        _redrawTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _redrawTimer.Tick += (_, _) =>
        {
            _redrawTimer.Stop();
            RedrawPreview();
        };
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ScheduleRedraw()));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => ScheduleRedraw()));
        AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler((_, _) => ScheduleRedraw()));
        AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler((_, _) => ScheduleRedraw()));
        Loaded += (_, _) => RedrawPreview();
    }

    private DispatcherTimer? _redrawTimer;
    private List<LabelLineDesign> _importedLines = new();

    private void ScheduleRedraw()
    {
        _redrawTimer?.Stop();
        _redrawTimer?.Start();
    }

    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleRedraw();

    /// <summary>按设计参数渲染标签效果预览（与 PDF 导出共用 LabelPreviewRenderer）。</summary>
    private void RedrawPreview()
    {
        if (PreviewCanvas.ActualWidth <= 0 || PreviewCanvas.ActualHeight <= 0)
            return;
        var d = Collect();
        PreviewCanvas.Children.Clear();
        double w = d.LabelWidthMm, h = d.LabelHeightMm;
        if (w <= 0 || h <= 0)
            return;
        double scale = Math.Min(PreviewCanvas.ActualWidth / w, PreviewCanvas.ActualHeight / h) * 0.92;
        double offX = (PreviewCanvas.ActualWidth - w * scale) / 2;
        double offY = (PreviewCanvas.ActualHeight - h * scale) / 2;
        LabelPreviewRenderer.Draw(PreviewCanvas, d, scale, offX, offY, "TEST0001");
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

    private void RefreshPrinters()
    {
        var printers = RawPrinterHelper.ListPrinters();
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

    private void BtnBrowseBtw_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "BarTender 模板 (*.btw)|*.btw|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
            BoxBtwPath.Text = dlg.FileName;
    }

    /// <summary>从 .btw 提取预览图/文本行/打印机，填入当前设计（坐标需在效果预览里手动摆位）。</summary>
    private void BtnImportBtw_Click(object sender, RoutedEventArgs e)
    {
        var path = BoxBtwPath.Text.Trim();
        if (path.Length == 0 || !File.Exists(path))
        {
            TxtImportNote.Text = "请先选择 .btw 文件";
            return;
        }
        var r = BtwImport.Extract(path);

        // 内嵌预览图（左栏）
        if (r.PreviewPng is not null)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(r.PreviewPng);
                bmp.EndInit();
                bmp.Freeze();
                ImgBtwPreview.Source = bmp;
            }
            catch { ImgBtwPreview.Source = null; }
        }
        else
        {
            ImgBtwPreview.Source = null;
        }

        // 预览图量版式 → 自动摆位（坐标/字号），无需手摆；条码对象忽略（不进设计）
        double widthMm = ParseD(BoxWidthMm.Text, 50);
        var (items, aspect) = BtwLayout.Measure(r.PreviewPng ?? Array.Empty<byte>(), r.Objects, widthMm);
        if (aspect > 0.5)
        {
            // 标签高按 .btw 版式比例自适配（宽沿用当前设置）
            BoxHeightMm.Text = (widthMm / aspect).ToString("0.##");
        }

        int i = 0;
        var rows = new List<DesignRow>();
        foreach (var it in items.Where(p => p.Kind == "text"))
        {
            i++;
            rows.Add(new DesignRow
            {
                Name = $"Text{i}",
                Content = it.Content,
                FontName = it.Font,
                FontSizePt = it.FontPt > 0 ? it.FontPt.ToString("0.#") : "12",
                XMm = it.XMm.ToString("0.##"),
                YMm = it.YMm.ToString("0.##"),
            });
        }
        if (rows.Count > 0)
            GridObjects.ItemsSource = rows;

        _importedLines = items
            .Where(p => p.Kind == "line")
            .Select(p => new LabelLineDesign
            {
                XMm = p.XMm, YMm = p.YMm,
                WidthMm = p.WidthMm, HeightMm = p.HeightMm,
            })
            .ToList();

        // 打印机 + 指令语言推断
        if (!string.IsNullOrEmpty(r.PrinterName))
        {
            ComboPrinter.Text = r.PrinterName;
            RefreshPrinters();
            ComboScript.SelectedIndex = BtwImport.GuessScript(r.PrinterName) == "zpl" ? 1 : 0;
        }

        TxtImportNote.Text = r.Note + (items.Count > 0
            ? $"——已识别 {_importedLines.Count} 条表格/框线，坐标/字号自动摆位，看右侧效果预览核对。"
            : "——未能测量版式（预览图缺失？），请手动摆位。");
        ScheduleRedraw();
    }

    private LabelDesign Collect()
    {
        var d = new LabelDesign
        {
            LabelWidthMm = ParseD(BoxWidthMm.Text, 50),
            LabelHeightMm = ParseD(BoxHeightMm.Text, 30),
            PaperHeightMm = ParseD(BoxPaperMm.Text, 0),
            PrinterName = ComboPrinter.Text.Trim(),
            Copies = (int)Math.Max(1, ParseD(BoxCopies.Text, 1)),
            PrintSpeed = ParseD(BoxSpeed.Text, 0),
            PrintDarkness = ParseD(BoxDarkness.Text, 0),
            Script = ComboScript.SelectedIndex == 1 ? "zpl" : "tspl",
            Dpi = ComboDpi.SelectedIndex == 1 ? 300 : 203,
            GapMm = ParseD(BoxGapMm.Text, 2),
            Objects = new List<LabelObjectDesign>(),
            Lines = _importedLines.Select(v => new LabelLineDesign
            {
                XMm = v.XMm, YMm = v.YMm,
                WidthMm = v.WidthMm, HeightMm = v.HeightMm,
            }).ToList(),
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

    private void BtnAddRow_Click(object sender, RoutedEventArgs e)
    {
        var rows = GridObjects.Items.OfType<DesignRow>().ToList();
        rows.Add(new DesignRow { Name = $"Text{rows.Count + 1}", FontName = "SimSun", FontSizePt = "12", XMm = "5", YMm = "5" });
        GridObjects.ItemsSource = rows;
    }

    private void BtnDelRow_Click(object sender, RoutedEventArgs e)
    {
        if (GridObjects.SelectedItem is not DesignRow row)
            return;
        var rows = GridObjects.Items.OfType<DesignRow>().ToList();
        rows.Remove(row);
        GridObjects.ItemsSource = rows;
    }

    private void BtnShowScript_Click(object sender, RoutedEventArgs e)
    {
        var d = Collect();
        bool zpl = d.Script == "zpl";
        string script = zpl ? DirectLabelScript.BuildZpl(d, "TEST0001") : DirectLabelScript.BuildTspl(d, "TEST0001");
        BoxScript.Text = script;
        BoxScript.Visibility = Visibility.Visible;
        TxtResult.Text = $"直驱打印指令（{(zpl ? "ZPL" : "TSPL")}，SN 代入 TEST0001）如下——不需要打印机即可核对：";
    }

    private async void BtnTestPrint_Click(object sender, RoutedEventArgs e)
    {
        var design = PrintGuard(Collect());   // 必须在 UI 线程收集（后台线程访问控件会抛 InvalidOperationException）
        if (design is null)
            return;
        var (ok, detail) = await Task.Run(() => new DirectLabelPrinter(design).Print("TEST0001"));
        TxtResult.Text = detail;
        TxtResult.Foreground = ok
            ? System.Windows.Media.Brushes.Green
            : System.Windows.Media.Brushes.Firebrick;
    }

    /// <summary>虚拟打印机（PDF/XPS 等）不认 TSPL/ZPL 指令——拦截并引导用「导出 PDF」。返回 null 表示已拦截。</summary>
    private LabelDesign? PrintGuard(LabelDesign design)
    {
        var name = design.PrinterName;
        if (name.Contains("PDF", StringComparison.OrdinalIgnoreCase)
            || name.Contains("XPS", StringComparison.OrdinalIgnoreCase)
            || name.Contains("OneNote", StringComparison.OrdinalIgnoreCase)
            || name.Contains("文档", StringComparison.Ordinal))
        {
            TxtResult.Text = $"「{name}」是虚拟打印机，不接受 TSPL/ZPL 指令（RAW 输出会生成坏文件）。请点「导出 PDF」看效果，或选真实标签打印机试打。";
            TxtResult.Foreground = System.Windows.Media.Brushes.Firebrick;
            return null;
        }
        return design;
    }

    private void BtnExportPdf_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            FileName = "标签预览.pdf",
        };
        if (dlg.ShowDialog(this) != true)
            return;
        var (ok, detail) = LabelPdfExporter.Export(Collect(), dlg.FileName);
        TxtResult.Text = detail;
        TxtResult.Foreground = ok
            ? System.Windows.Media.Brushes.Green
            : System.Windows.Media.Brushes.Firebrick;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        MachineConfig.SaveLabelDesign(Collect());
        TxtResult.Text = "已保存设计（machine.json），工位 4 打印按 TSPL/ZPL 直驱出标签。";
        TxtResult.Foreground = System.Windows.Media.Brushes.Green;
    }
}
