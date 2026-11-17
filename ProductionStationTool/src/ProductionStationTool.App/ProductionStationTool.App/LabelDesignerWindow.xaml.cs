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
/// SN 码标签设计器（「打印机设置」）：直接配置标签尺寸、打印机参数、条码与文字排版，
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
        ChkBarcode.IsChecked = d.Barcode.Enabled;
        ComboBcSymbology.SelectedIndex = string.Equals(d.Barcode.Symbology, "qrcode", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        BoxBcContent.Text = d.Barcode.Content;
        BoxBcHeight.Text = d.Barcode.HeightMm.ToString("0.##");
        BoxBcModule.Text = d.Barcode.ModuleMm.ToString("0.##");
        ComboBcHuman.SelectedIndex = d.Barcode.HumanReadable ? 0 : 1;
        BoxBcX.Text = d.Barcode.XMm.ToString("0.##");
        BoxBcY.Text = d.Barcode.YMm.ToString("0.##");
        GridObjects.ItemsSource = d.Objects.Select(ToRow).ToList();
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

    private void ScheduleRedraw()
    {
        _redrawTimer?.Stop();
        _redrawTimer?.Start();
    }

    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleRedraw();

    /// <summary>按设计参数渲染标签效果预览（mm→DIP 等比映射，{SN} 代入 TEST0001）。</summary>
    private void RedrawPreview()
    {
        if (PreviewCanvas.ActualWidth <= 0 || PreviewCanvas.ActualHeight <= 0)
            return;
        var d = Collect();
        PreviewCanvas.Children.Clear();

        double w = d.LabelWidthMm, h = d.LabelHeightMm;
        if (w <= 0 || h <= 0)
            return;
        double scale = Math.Min(PreviewCanvas.ActualWidth / w, PreviewCanvas.ActualHeight / h);

        // 标签底纸
        var sheet = new System.Windows.Shapes.Rectangle
        {
            Width = w * scale,
            Height = h * scale,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            StrokeThickness = 1,
        };
        PreviewCanvas.Children.Add(sheet);

        double mm2dip(double mm) => mm * scale;
        const double ptToMm = 25.4 / 72.0;

        // 文字行
        foreach (var o in d.Objects)
        {
            if (string.IsNullOrEmpty(o.Content))
                continue;
            string text = o.Content.Replace("{SN}", "TEST0001", StringComparison.OrdinalIgnoreCase);
            double pt = o.FontSizePt > 0 ? o.FontSizePt : 12;
            var tb = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily(string.IsNullOrWhiteSpace(o.FontName) ? "Segoe UI" : o.FontName),
                FontSize = Math.Max(6, mm2dip(pt * ptToMm)),
                FontWeight = o.Bold == true ? FontWeights.Bold : FontWeights.Normal,
                Foreground = Brushes.Black,
            };
            Canvas.SetLeft(tb, mm2dip(o.XMm ?? 0));
            Canvas.SetTop(tb, mm2dip(o.YMm ?? 0));
            PreviewCanvas.Children.Add(tb);
        }

        // 条码占位：QR 画方块（含定位角），Code128 画竖条纹（真实图形由打印机生成）
        var b = d.Barcode;
        if (b.Enabled && !string.IsNullOrEmpty(b.Content))
        {
            string data = b.Content.Replace("{SN}", "TEST0001", StringComparison.OrdinalIgnoreCase);
            double module = b.ModuleMm > 0 ? b.ModuleMm : 0.4;
            bool qr = string.Equals(b.Symbology, "qrcode", StringComparison.OrdinalIgnoreCase);
            double bw = qr
                ? Math.Min(b.HeightMm, w - b.XMm)                                  // QR 方形
                : Math.Min(w - b.XMm, (data.Length + 3) * 11 * module);           // Code128 ≈11 模块/字符
            double bh = qr ? bw : b.HeightMm;
            double bx = mm2dip(b.XMm), by = mm2dip(b.YMm);

            var box = new System.Windows.Shapes.Rectangle
            {
                Width = mm2dip(bw),
                Height = mm2dip(bh),
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                StrokeThickness = 0.5,
            };
            Canvas.SetLeft(box, bx);
            Canvas.SetTop(box, by);
            PreviewCanvas.Children.Add(box);

            if (qr)
            {
                // QR 定位角 + 稀疏模块示意
                double mark = mm2dip(bw) / 6;
                foreach (var (mx, my) in new[] { (bx, by), (bx + mm2dip(bw) - mark, by), (bx, by + mm2dip(bh) - mark) })
                {
                    var corner = new System.Windows.Shapes.Rectangle
                    {
                        Width = mark, Height = mark,
                        Fill = Brushes.Black,
                    };
                    Canvas.SetLeft(corner, mx);
                    Canvas.SetTop(corner, my);
                    PreviewCanvas.Children.Add(corner);
                }
            }

            // 伪条纹（确定性图案，仅示意密度/位置；QR 跳过）
            double x = bx + mm2dip(module);
            if (qr) x = bx + mm2dip(bw);
            int seed = 7;
            while (x < bx + mm2dip(bw) - mm2dip(module))
            {
                seed = (seed * 1103515245 + 12345) & 0x7fffffff;
                double barW = mm2dip(module) * (1 + (seed % 3));
                if (x + barW > bx + mm2dip(bw))
                    break;
                var bar = new System.Windows.Shapes.Rectangle
                {
                    Width = barW,
                    Height = mm2dip(bh),
                    Fill = Brushes.Black,
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, by);
                PreviewCanvas.Children.Add(bar);
                x += barW + mm2dip(module) * (1 + (seed >> 3) % 2);
            }

            if (b.HumanReadable)
            {
                var human = new TextBlock
                {
                    Text = data,
                    FontSize = Math.Max(7, mm2dip(2.2)),
                    Foreground = Brushes.Black,
                };
                Canvas.SetLeft(human, bx + mm2dip(bw) / 2 - data.Length * mm2dip(1.1));
                Canvas.SetTop(human, by + mm2dip(bh) + mm2dip(0.6));
                PreviewCanvas.Children.Add(human);
            }
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

        // 预览图量版式 → 自动摆位（坐标/字号/条码），无需手摆
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

        var bc = items.FirstOrDefault(p => p.Kind == "barcode");
        if (bc is not null)
        {
            ChkBarcode.IsChecked = true;
            BoxBcX.Text = bc.XMm.ToString("0.##");
            BoxBcY.Text = bc.YMm.ToString("0.##");
            BoxBcHeight.Text = bc.HeightMm.ToString("0.##");
            ComboBcSymbology.SelectedIndex = BtwLayout.LooksSquare(bc.HeightMm * 1.0, bc.HeightMm) && bc.HeightMm > 8 ? 1 : 0;
        }

        // 打印机 + 指令语言推断
        if (!string.IsNullOrEmpty(r.PrinterName))
        {
            ComboPrinter.Text = r.PrinterName;
            RefreshPrinters();
            ComboScript.SelectedIndex = BtwImport.GuessScript(r.PrinterName) == "zpl" ? 1 : 0;
        }

        TxtImportNote.Text = r.Note + (items.Count > 0 ? "——坐标/字号已自动摆位，看右侧效果预览核对。" : "——未能测量版式（预览图缺失？），请手动摆位。");
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
            Barcode = new LabelBarcodeDesign
            {
                Enabled = ChkBarcode.IsChecked == true,
                Symbology = ComboBcSymbology.SelectedIndex == 1 ? "qrcode" : "code128",
                Content = BoxBcContent.Text.Trim(),
                HeightMm = ParseD(BoxBcHeight.Text, 8),
                ModuleMm = ParseD(BoxBcModule.Text, 0.4),
                HumanReadable = ComboBcHuman.SelectedIndex != 1,
                XMm = ParseD(BoxBcX.Text, 5),
                YMm = ParseD(BoxBcY.Text, 15),
            },
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
        var design = Collect();      // 必须在 UI 线程收集（后台线程访问控件会抛 InvalidOperationException）
        var (ok, detail) = await Task.Run(() => new DirectLabelPrinter(design).Print("TEST0001"));
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
