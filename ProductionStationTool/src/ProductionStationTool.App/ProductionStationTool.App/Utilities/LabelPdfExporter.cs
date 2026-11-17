using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProductionStationTool.App.Utilities;

using ProductionStationTool.Core.Printing;

/// <summary>
/// 标签导出 PDF：把效果预览同一套绘制渲染成 300dpi 图，装进单页 PDF（DCTDecode JPEG）。
/// 纯本地生成、无第三方库——虚拟 PDF 打印机不认 TSPL/ZPL 指令（RAW 会产出坏文件），
/// 本导出才是「打印成 PDF 看效果」的正确通道。
/// </summary>
public static class LabelPdfExporter
{
    /// <summary>导出标签 PDF；返回（是否成功, 说明）。</summary>
    public static (bool Ok, string Detail) Export(LabelDesign d, string path, string snSample = "TEST0001")
    {
        if (d.LabelWidthMm <= 0 || d.LabelHeightMm <= 0)
            return (false, "标签尺寸无效");
        try
        {
            // 画布按 96DIP/inch 布局，位图按 300dpi 渲染
            double dipW = d.LabelWidthMm * 96 / 25.4;
            double dipH = d.LabelHeightMm * 96 / 25.4;
            var canvas = new Canvas { Width = dipW, Height = dipH, Background = Brushes.White };
            LabelPreviewRenderer.Draw(canvas, d, dipW / d.LabelWidthMm, 0, 0, snSample);
            canvas.Measure(new Size(dipW, dipH));
            canvas.Arrange(new Rect(0, 0, dipW, dipH));

            int pxW = Math.Max(1, (int)Math.Round(d.LabelWidthMm / 25.4 * 300));
            int pxH = Math.Max(1, (int)Math.Round(d.LabelHeightMm / 25.4 * 300));
            var rtb = new RenderTargetBitmap(pxW, pxH, 300, 300, PixelFormats.Pbgra32);
            rtb.Render(canvas);

            var jpg = new JpegBitmapEncoder { QualityLevel = 92 };
            jpg.Frames.Add(BitmapFrame.Create(rtb));
            using var img = new MemoryStream();
            jpg.Save(img);
            byte[] jpeg = img.ToArray();

            // 单页 PDF：MediaBox = 标签物理尺寸（mm→pt）
            double ptW = d.LabelWidthMm * 72 / 25.4;
            double ptH = d.LabelHeightMm * 72 / 25.4;
            var pdf = new MemoryStream();
            var offsets = new List<long>();
            void Raw(string s) { var b = Encoding.ASCII.GetBytes(s); pdf.Write(b, 0, b.Length); }

            Raw("%PDF-1.4\n");
            offsets.Add(pdf.Position); Raw("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
            offsets.Add(pdf.Position); Raw("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
            offsets.Add(pdf.Position);
            Raw($"3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 {ptW:0.##} {ptH:0.##}] " +
                "/Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >> endobj\n");
            offsets.Add(pdf.Position);
            Raw($"4 0 obj << /Type /XObject /Subtype /Image /Width {pxW} /Height {pxH} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >> stream\n");
            pdf.Write(jpeg, 0, jpeg.Length);
            Raw("\nendstream endobj\n");
            string content = $"q {ptW:0.##} 0 0 {ptH:0.##} 0 0 cm /Im0 Do Q\n";
            var cb = Encoding.ASCII.GetBytes(content);
            offsets.Add(pdf.Position);
            Raw($"5 0 obj << /Length {cb.Length} >> stream\n");
            pdf.Write(cb, 0, cb.Length);
            Raw("endstream endobj\n");

            long xref = pdf.Position;
            Raw($"xref\n0 6\n0000000000 65535 f \n");
            foreach (var off in offsets)
                Raw($"{off:0000000000} 00000 n \n");
            Raw($"trailer << /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

            File.WriteAllBytes(path, pdf.ToArray());
            return (true, $"已导出 PDF：{path}（{d.LabelWidthMm:0.#}×{d.LabelHeightMm:0.#}mm @300dpi）");
        }
        catch (Exception ex)
        {
            return (false, $"导出失败：{ex.Message}");
        }
    }
}
