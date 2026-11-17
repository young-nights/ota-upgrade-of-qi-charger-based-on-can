using System.Windows;
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
        BoxBcContent.Text = d.Barcode.Content;
        BoxBcHeight.Text = d.Barcode.HeightMm.ToString("0.##");
        BoxBcModule.Text = d.Barcode.ModuleMm.ToString("0.##");
        ComboBcHuman.SelectedIndex = d.Barcode.HumanReadable ? 0 : 1;
        BoxBcX.Text = d.Barcode.XMm.ToString("0.##");
        BoxBcY.Text = d.Barcode.YMm.ToString("0.##");
        GridObjects.ItemsSource = d.Objects.Select(ToRow).ToList();
        RefreshPrinters();
        TxtResult.Text = "配置后「试打印一张」核对版式，「保存」后工位 4 按此出标签。";
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
        var (ok, detail) = await Task.Run(() => new DirectLabelPrinter(Collect()).Print("TEST0001"));
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
