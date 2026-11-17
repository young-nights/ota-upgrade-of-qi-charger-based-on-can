using System.Runtime.InteropServices;
using System.Text;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.Core.Printing;

/// <summary>
/// 直驱标签打印：按 <see cref="LabelDesign"/> 生成 TSPL / ZPL 指令流，经 winspool
/// 原始打印（RAW）直接发给标签打印机——**不需要 BarTender / 不需要授权**。
/// 语言覆盖：TSPL2（TSC 及兼容机，中文 TTF）与 ZPL（Zebra）。
/// 中文文本按 GB18030 编码下发（TSPL TTF / ZPL ^CI28）。
/// </summary>
public static class DirectLabelScript
{
    /// <summary>按设计生成 TSPL2 指令（坐标全部换算为点阵）。</summary>
    public static string BuildTspl(LabelDesign d, string sn)
    {
        int dpi = d.Dpi is 300 or 600 ? d.Dpi : 203;
        double dots(double mm) => Math.Round(mm / 25.4 * dpi);
        var sb = new StringBuilder();
        sb.AppendLine($"SIZE {d.LabelWidthMm:0.##} mm, {d.LabelHeightMm:0.##} mm");
        if (d.GapMm > 0)
            sb.AppendLine($"GAP {d.GapMm:0.##} mm, 0 mm");
        else
            sb.AppendLine("GAP 0 mm, 0 mm");
        if (d.PrintDarkness > 0)
            sb.AppendLine($"DENSITY {Math.Clamp((int)Math.Round(d.PrintDarkness), 0, 15)}");
        if (d.PrintSpeed > 0)
            sb.AppendLine($"SPEED {Math.Clamp((int)Math.Round(d.PrintSpeed), 1, 14)}");
        sb.AppendLine("DIRECTION 1");
        sb.AppendLine("CLS");

        foreach (var o in d.Objects)
        {
            if (string.IsNullOrEmpty(o.Content))
                continue;
            string text = o.Content.Replace("{SN}", sn, StringComparison.OrdinalIgnoreCase);
            double x = dots(o.XMm ?? 0), y = dots(o.YMm ?? 0);
            // TTF 字号→倍率：TSPL TTF 以 ~12pt 为基准 ×倍率输出（近似，试打后可微调字号）
            int mul = o.FontSizePt > 0 ? Math.Max(1, (int)Math.Round(o.FontSizePt / 12.0)) : 1;
            string font = string.IsNullOrWhiteSpace(o.FontName) ? "TTF" : o.FontName;
            sb.AppendLine($"TEXT {x:0},{y:0},\"{font}\",0,{mul},{mul},\"{text}\"");
        }

        var b = d.Barcode;
        if (b.Enabled && !string.IsNullOrEmpty(b.Content))
        {
            string data = b.Content.Replace("{SN}", sn, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(b.Symbology, "qrcode", StringComparison.OrdinalIgnoreCase))
            {
                // TSPL 二维码：QRCODE x,y,纠错等级,模块宽,模式,旋转,"数据"
                int cell = Math.Max(2, (int)Math.Round(dots(b.ModuleMm)));
                sb.AppendLine($"QRCODE {dots(b.XMm):0},{dots(b.YMm):0},L,{cell},A,0,\"{data}\"");
            }
            else
            {
                int narrow = Math.Max(1, (int)Math.Round(dots(b.ModuleMm)));
                int human = b.HumanReadable ? 1 : 0;
                sb.AppendLine($"BARCODE {dots(b.XMm):0},{dots(b.YMm):0},\"128\",{dots(b.HeightMm):0}," +
                              $"{human},0,{narrow},{narrow * 3},\"{data}\"");
            }
        }

        sb.AppendLine("PRINT 1,1");
        return sb.ToString();
    }

    /// <summary>按设计生成 ZPL 指令。中文需打印机带亚洲字库（^CI28=GBK）。</summary>
    public static string BuildZpl(LabelDesign d, string sn)
    {
        int dpi = d.Dpi is 300 or 600 ? d.Dpi : 203;
        double dots(double mm) => Math.Round(mm / 25.4 * dpi);
        var sb = new StringBuilder();
        sb.AppendLine("^XA");
        sb.AppendLine("^CI28");                                   // GBK 编码（中文）
        sb.AppendLine($"^PW{dots(d.LabelWidthMm):0}");             // 标签宽（点）
        if (d.PrintDarkness > 0)
            sb.AppendLine($"^MD{Math.Clamp((int)Math.Round(d.PrintDarkness), -30, 30)}");
        if (d.PrintSpeed > 0)
            sb.AppendLine($"^PR{Math.Clamp((int)Math.Round(d.PrintSpeed), 2, 14)}");

        foreach (var o in d.Objects)
        {
            if (string.IsNullOrEmpty(o.Content))
                continue;
            string text = o.Content.Replace("{SN}", sn, StringComparison.OrdinalIgnoreCase);
            double h = o.FontSizePt > 0 ? Math.Round(o.FontSizePt * dpi / 72.0) : 32;
            double w = Math.Round(h * 0.6);
            sb.AppendLine($"^FO{dots(o.XMm ?? 0):0},{dots(o.YMm ?? 0):0}" +
                          $"^A0N,{h:0},{w:0}^FD{text}^FS");
        }

        var b = d.Barcode;
        if (b.Enabled && !string.IsNullOrEmpty(b.Content))
        {
            string data = b.Content.Replace("{SN}", sn, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(b.Symbology, "qrcode", StringComparison.OrdinalIgnoreCase))
            {
                int cell = Math.Max(2, (int)Math.Round(dots(b.ModuleMm)));
                sb.AppendLine($"^FO{dots(b.XMm):0},{dots(b.YMm):0}^BQN,2,{cell}^FDLA,{data}^FS");
            }
            else
            {
                int module = Math.Max(1, (int)Math.Round(dots(b.ModuleMm)));
                char human = b.HumanReadable ? 'Y' : 'N';
                sb.AppendLine($"^FO{dots(b.XMm):0},{dots(b.YMm):0}^BY{module},3" +
                              $"^BCN,{dots(b.HeightMm):0},{human},N,N^FD{data}^FS");
            }
        }

        sb.AppendLine("^PQ1");
        sb.AppendLine("^XZ");
        return sb.ToString();
    }
}

/// <summary>直驱打印执行：脚本生成 + RAW 原始打印（winspool）。</summary>
public sealed class DirectLabelPrinter : ISnPrinter
{
    private readonly LabelDesign _design;

    public DirectLabelPrinter(LabelDesign design) => _design = design;

    /// <summary>ISnPrinter：按设计直驱出一张 SN 标签。</summary>
    public Task<(bool Ok, string Detail)> PrintSnAsync(string sn, CancellationToken ct = default)
        => Task.Run(() => Print(sn), ct);

    /// <summary>打印一张（脚本按设计生成后 RAW 发给打印机）。</summary>
    public (bool Ok, string Detail) Print(string sn)
    {
        if (string.IsNullOrWhiteSpace(_design.PrinterName))
            return (false, "未选择打印机");
        bool zpl = string.Equals(_design.Script, "zpl", StringComparison.OrdinalIgnoreCase);
        string script = zpl ? DirectLabelScript.BuildZpl(_design, sn) : DirectLabelScript.BuildTspl(_design, sn);

        // 中文按 GB18030（GBK 超集）下发；ZPL 配 ^CI28
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] payload = Encoding.GetEncoding("GB18030").GetBytes(script);

        if (!RawPrinterHelper.SendToPrinter(_design.PrinterName, payload, out var error))
            return (false, $"直驱打印失败：{error}");
        return (true, $"已直驱打印（{(zpl ? "ZPL" : "TSPL")}，{_design.PrinterName}）SN={sn}");
    }
}

/// <summary>winspool 原始打印（RAW）：字节不经驱动加工直达打印机。</summary>
public static class RawPrinterHelper
{
    /// <summary>本机可用的打印机名（winspool 枚举，默认打印机排前）。</summary>
    public static List<string> ListPrinters()
    {
        var names = new List<string>();
        try
        {
            const uint flags = 0x00000002 | 0x00000004;   // LOCAL | CONNECTIONS
            EnumPrintersW(flags, null, 1, IntPtr.Zero, 0, out var needed, out _);
            if (needed == 0)
                return names;
            var buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!EnumPrintersW(flags, null, 1, buf, needed, out _, out var returned))
                    return names;
                int stride = IntPtr.Size * 4;              // PRINTER_INFO_1W: 4 个指针宽字段
                for (int i = 0; i < returned; i++)
                {
                    var pName = Marshal.ReadIntPtr(buf, i * stride + IntPtr.Size * 2);
                    var name = Marshal.PtrToStringUni(pName);
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }

            var def = GetDefaultPrinter();
            if (!string.IsNullOrEmpty(def) && names.Remove(def!))
                names.Insert(0, def!);
        }
        catch
        {
            // 枚举失败交由手填
        }
        return names;
    }

    private static string? GetDefaultPrinter()
    {
        uint size = 0;
        GetDefaultPrinterW(null, ref size);
        if (size == 0)
            return null;
        var sb = new StringBuilder((int)size);
        return GetDefaultPrinterW(sb, ref size) ? sb.ToString() : null;
    }

    public static bool SendToPrinter(string printerName, byte[] data, out string error)
    {
        error = "";
        IntPtr hPrinter = IntPtr.Zero;
        if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero))
        {
            error = $"无法打开打印机「{printerName}」（名称对不对？打印机是否联机）";
            return false;
        }
        try
        {
            var di = new DOCINFOA
            {
                pDocName = "ProductionStationTool Label",
                pDatatype = "RAW",
                pOutputFile = null,
            };
            if (StartDocPrinter(hPrinter, 1, ref di) == 0)
            {
                error = "StartDocPrinter 失败（打印后台服务未运行？）";
                return false;
            }
            try
            {
                if (!StartPagePrinter(hPrinter))
                {
                    error = "StartPagePrinter 失败";
                    return false;
                }
                int written = 0;
                bool ok = WritePrinter(hPrinter, data, data.Length, out written);
                EndPagePrinter(hPrinter);
                if (!ok || written != data.Length)
                {
                    error = $"WritePrinter 失败（已写 {written}/{data.Length} 字节）";
                    return false;
                }
            }
            finally
            {
                EndDocPrinter(hPrinter);
            }
            return true;
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOCINFOA
    {
        public string? pDocName;
        public string? pOutputFile;
        public string? pDatatype;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrintersW(uint flags, string? name, uint level,
        IntPtr buffer, uint cbNeeded, out uint pcbNeeded, out uint pcReturned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDefaultPrinterW(StringBuilder? pszBuffer, ref uint pcchBuffer);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFOA di);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBuf, int cbBuf, out int pcWritten);
}
