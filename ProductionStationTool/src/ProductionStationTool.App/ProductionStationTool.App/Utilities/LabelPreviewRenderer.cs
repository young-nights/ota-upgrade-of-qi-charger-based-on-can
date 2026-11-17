using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ProductionStationTool.App.Utilities;

using ProductionStationTool.Core.Printing;

/// <summary>
/// 标签效果渲染（预览画布与 PDF 导出共用同一套绘制逻辑）：
/// 底纸 + 文字行（X/Y/字号/字体/粗体）+ 条码占位（QR 方块定位角 / Code128 竖条）。
/// </summary>
public static class LabelPreviewRenderer
{
    /// <summary>把标签画到目标画布；scale = DIP/mm，offX/offY = 标签左上角在画布上的位置。</summary>
    public static void Draw(Canvas target, LabelDesign d, double scale, double offX, double offY, string snSample)
    {
        double w = d.LabelWidthMm, h = d.LabelHeightMm;
        if (w <= 0 || h <= 0 || scale <= 0)
            return;
        double mm2dip(double mm) => mm * scale;

        var sheet = new Rectangle
        {
            Width = w * scale,
            Height = h * scale,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            StrokeThickness = 1,
        };
        Canvas.SetLeft(sheet, offX);
        Canvas.SetTop(sheet, offY);
        target.Children.Add(sheet);

        // 表格线先画，文字后画，避免线压在字符上。
        foreach (var line in d.Lines)
        {
            if (line.WidthMm <= 0 || line.HeightMm <= 0)
                continue;
            var bar = new Rectangle
            {
                Width = mm2dip(line.WidthMm),
                Height = mm2dip(line.HeightMm),
                Fill = Brushes.Black,
            };
            Canvas.SetLeft(bar, offX + mm2dip(line.XMm));
            Canvas.SetTop(bar, offY + mm2dip(line.YMm));
            target.Children.Add(bar);
        }

        const double ptToMm = 25.4 / 72.0;

        foreach (var o in d.Objects)
        {
            if (string.IsNullOrEmpty(o.Content))
                continue;
            string text = o.Content.Replace("{SN}", snSample, StringComparison.OrdinalIgnoreCase);
            double pt = o.FontSizePt > 0 ? o.FontSizePt : 12;
            var tb = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily(string.IsNullOrWhiteSpace(o.FontName) ? "Segoe UI" : o.FontName),
                FontSize = Math.Max(6, mm2dip(pt * ptToMm)),
                FontWeight = o.Bold == true ? FontWeights.Bold : FontWeights.Normal,
                Foreground = Brushes.Black,
            };
            Canvas.SetLeft(tb, offX + mm2dip(o.XMm ?? 0));
            Canvas.SetTop(tb, offY + mm2dip(o.YMm ?? 0));
            target.Children.Add(tb);
        }

        var b = d.Barcode;
        if (b.Enabled && !string.IsNullOrEmpty(b.Content))
        {
            string data = b.Content.Replace("{SN}", snSample, StringComparison.OrdinalIgnoreCase);
            bool qr = string.Equals(b.Symbology, "qrcode", StringComparison.OrdinalIgnoreCase);
            // 有指定宽高（.btw 导入）就按盒画；未指定才按内容/模块自算
            double bw = b.WidthMm > 0
                ? Math.Min(b.WidthMm, w - b.XMm)
                : (qr ? Math.Min(b.HeightMm, w - b.XMm)
                      : Math.Min(w - b.XMm, (data.Length + 3) * 11 * (b.ModuleMm > 0 ? b.ModuleMm : 0.4)));
            double bh = b.HeightMm > 0 && b.WidthMm > 0
                ? Math.Min(b.HeightMm, h - b.YMm)
                : (qr ? bw : b.HeightMm);
            double module = b.ModuleMm > 0 ? b.ModuleMm : 0.4;
            double bx = offX + mm2dip(b.XMm), by = offY + mm2dip(b.YMm);

            var box = new Rectangle
            {
                Width = mm2dip(bw),
                Height = mm2dip(bh),
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                StrokeThickness = 0.5,
            };
            Canvas.SetLeft(box, bx);
            Canvas.SetTop(box, by);
            target.Children.Add(box);

            if (qr)
            {
                double mark = mm2dip(bw) / 6;
                foreach (var (mx, my) in new[] { (bx, by), (bx + mm2dip(bw) - mark, by), (bx, by + mm2dip(bh) - mark) })
                {
                    var corner = new Rectangle { Width = mark, Height = mark, Fill = Brushes.Black };
                    Canvas.SetLeft(corner, mx);
                    Canvas.SetTop(corner, my);
                    target.Children.Add(corner);
                }
            }
            else
            {
                // 竖条填满盒宽（示意密度与边界；真实条纹由打印机按数据生成）
                double unit = mm2dip(bw) / ((data.Length + 3) * 11.0);
                double x = bx;
                int seed = 7;
                while (x < bx + mm2dip(bw) - unit)
                {
                    seed = (seed * 1103515245 + 12345) & 0x7fffffff;
                    double barW = unit * (1 + (seed % 3));
                    if (x + barW > bx + mm2dip(bw))
                        break;
                    var bar = new Rectangle { Width = barW, Height = mm2dip(bh), Fill = Brushes.Black };
                    Canvas.SetLeft(bar, x);
                    Canvas.SetTop(bar, by);
                    target.Children.Add(bar);
                    x += barW + unit * (1 + (seed >> 3) % 2);
                }
            }

            if (b.HumanReadable && !qr)
            {
                var human = new TextBlock
                {
                    Text = data,
                    FontSize = Math.Max(7, mm2dip(2.2)),
                    Foreground = Brushes.Black,
                };
                Canvas.SetLeft(human, bx + mm2dip(bw) / 2 - data.Length * mm2dip(1.1));
                Canvas.SetTop(human, by + mm2dip(bh) + mm2dip(0.6));
                target.Children.Add(human);
            }
        }
    }
}
