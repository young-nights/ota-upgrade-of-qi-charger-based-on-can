using System.IO.Compression;
using System.Text;
using ProductionStationTool.Core.Printing;

namespace ProductionStationTool.Tests;

/// <summary>.btw 参考导入解析测试（合成样例，无需 BarTender）。</summary>
public class BtwImportTests
{
    /// <summary>构造合成 .btw：文本头 + zlib 参数流（FF FE FF 记录）+ 最小 PNG。</summary>
    private static string BuildFakeBtw()
    {
        static byte[] Rec(string s)
        {
            var chars = Encoding.Unicode.GetBytes(s);
            var buf = new byte[3 + 1 + chars.Length];
            buf[0] = 0xFF; buf[1] = 0xFE; buf[2] = 0xFF;
            buf[3] = (byte)(s.Length);
            Array.Copy(chars, 0, buf, 4, chars.Length);
            return buf;
        }

        using var ms = new MemoryStream();
        ms.Write(Rec("BarTender"));
        ms.Write(Rec("ZDesigner ZT610-600dpi ZPL"));
        ms.Write(Rec("Trade:"));            // 显示文本
        ms.Write(Rec("方框 1"));
        ms.Write(Rec("文本 2"));
        ms.Write(Rec("LS"));                // 数据源样值（成对 + s0x）
        ms.Write(Rec("LS"));
        ms.Write(Rec("s01"));
        ms.Write(Rec("输入数据"));           // 界面样板
        var param = ms.ToArray();

        using var zip = new MemoryStream();
        using (var z = new ZLibStream(zip, CompressionMode.Compress, leaveOpen: true))
            z.Write(param);
        var zlib = zip.ToArray();

        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        var file = new MemoryStream();
        var head = Encoding.ASCII.GetBytes("\r\nBar Tender Format test");
        file.Write(head);
        file.Write(zlib);
        file.Write(png);
        string path = Path.Combine(Path.GetTempPath(), $"btwtest_{Guid.NewGuid():N}.btw");
        File.WriteAllBytes(path, file.ToArray());
        return path;
    }

    [Fact]
    public void Extract_FindsTextsPrinterAndPng()
    {
        var path = BuildFakeBtw();
        try
        {
            var r = BtwImport.Extract(path);

            Assert.NotNull(r.PreviewPng);                          // 内嵌 PNG
            Assert.Contains("ZT610", r.PrinterName ?? "");          // 打印机
            Assert.Equal("zpl", BtwImport.GuessScript(r.PrinterName));
            Assert.Contains(r.Texts, t => t.Content == "Trade:");   // 显示文本留下
            Assert.DoesNotContain(r.Texts, t => t.Content == "LS"); // 数据源样值过滤
            Assert.DoesNotContain(r.Texts, t => t.Content == "输入数据"); // 界面样板过滤
        }
        finally
        {
            File.Delete(path);
        }
    }
}
