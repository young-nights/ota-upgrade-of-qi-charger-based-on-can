using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ProductionStationTool.App.Utilities;

using ProductionStationTool.Core.Printing;

/// <summary>
/// 标签效果渲染（预览画布与 PDF 导出共用同一套绘制逻辑）：
/// 底纸 + 文字行（X/Y/字号/字体/粗体）。
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
    }
}
