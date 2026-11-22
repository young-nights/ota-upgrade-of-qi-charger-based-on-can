using ProductionStationTool.Core.Printing;

namespace ProductionStationTool.Tests;

/// <summary>直驱脚本生成测试（TSPL/ZPL——无需打印机/BarTender，纯文本断言）。</summary>
public class DirectLabelScriptTests
{
    private static LabelDesign Sample() => new()
    {
        LabelWidthMm = 50,
        LabelHeightMm = 30,
        GapMm = 2,
        Dpi = 203,
        Script = "tspl",
        Objects =
        {
            new LabelObjectDesign { Name = "Line1", Content = "SN:{SN}", FontName = "SimSun", FontSizePt = 12, XMm = 5, YMm = 5 },
        },
        Lines =
        {
            new LabelLineDesign { XMm = 0, YMm = 0, WidthMm = 50, HeightMm = 0.2 },
            new LabelLineDesign { XMm = 20, YMm = 0, WidthMm = 0.2, HeightMm = 30 },
        },
    };

    [Fact]
    public void Tspl_SubstitutesSn_AndHasTextAndLines()
    {
        var script = DirectLabelScript.BuildTspl(Sample(), "LSCH42JY012601010001");

        Assert.Contains("SIZE 50 mm, 30 mm", script);
        Assert.Contains("GAP 2 mm, 0 mm", script);
        Assert.Contains("SN:LSCH42JY012601010001", script);       // {SN} 已替换
        Assert.DoesNotContain("{SN}", script);
        Assert.Contains("BAR 0,0,400,2", script);                    // 表格横线
        Assert.Contains("BAR 160,0,2,240", script);                   // 表格竖线
        Assert.Contains("PRINT 1,1", script);
        // 条码功能已移除：不得出现 BARCODE / QRCODE 指令
        Assert.DoesNotContain("BARCODE", script);
        Assert.DoesNotContain("QRCODE", script);
    }

    [Fact]
    public void Zpl_SubstitutesSn_AndHasTextAndLines()
    {
        var script = DirectLabelScript.BuildZpl(Sample(), "TEST0001");

        Assert.StartsWith("^XA", script);
        Assert.Contains("^CI28", script);                          // GBK 中文编码
        Assert.Contains("SN:TEST0001", script);
        Assert.DoesNotContain("{SN}", script);
        Assert.Contains("^GB400,2,2^FS", script);                    // 表格横线
        Assert.Contains("^GB2,240,2^FS", script);                    // 表格竖线
        Assert.EndsWith("^XZ", script.TrimEnd());
        // 条码功能已移除：不得出现 ^BC / ^BQ 指令
        Assert.DoesNotContain("^BC", script);
        Assert.DoesNotContain("^BQ", script);
    }
}
