using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProductionStationTool.App.Utilities;

using ProductionStationTool.Core.Printing;

/// <summary>预览图量出的摆放结果（mm，坐标原点=标签左上）。</summary>
public sealed record BtwPlacement(
    string Kind, string Content, string Font,
    double XMm, double YMm, double FontPt, double WidthMm, double HeightMm);

/// <summary>表格单元格中的一个文字行，Row/Col 从 0 开始（标题行的 Col = 0）。</summary>
internal sealed record TableTextRegion(
    int X0, int Y0, int X1, int Y1, int Row, int Col);

/// <summary>
/// .btw 内嵌预览图版式测量：PNG 解码 → 标签白区 → 墨迹行带/列盒 → 与导入对象按宽度
/// 估算匹配 → 输出 mm 坐标/字号（按设计里的标签尺寸换算）。坐标真值在 .btw 二进制中
/// 未能解析（属性合并编码），预览图即官方渲染版式，测量值与所见一致。
/// </summary>
public static class BtwLayout
{
    /// <summary>测量并匹配；返回摆放列表 + 标签宽高比（宽/高）。失败返回空表。</summary>
    public static (List<BtwPlacement> Items, double Aspect) Measure(
        byte[] png, IReadOnlyList<BtwObject> objects, double labelWidthMm)
    {
        if (labelWidthMm <= 0)
            return (new List<BtwPlacement>(), 0);

        // 解码 PNG → 灰度
        BitmapSource src;
        try
        {
            using var ms = new System.IO.MemoryStream(png);
            // OnLoad：在流存活时全量解码（默认延迟解码会在流释放后 CopyPixels 抛 ObjectDisposed）
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            src = decoder.Frames[0];
            src.Freeze();
        }
        catch
        {
            return (new List<BtwPlacement>(), 0);
        }
        var gray = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
        int w = gray.PixelWidth, h = gray.PixelHeight;
        var px = new byte[w * h];
        gray.CopyPixels(px, w, 0);

        byte At(int x, int y) => px[y * w + x];
        bool White(int x, int y) => At(x, y) >= 240;
        bool Ink(int x, int y) => At(x, y) < 90;

        // 标签白区（背景为中灰，标签为白）
        int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (White(x, y))
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
        if (x1 <= x0 + 10 || y1 <= y0 + 10)
            return (new List<BtwPlacement>(), 0);
        double aspect = (x1 - x0 + 1.0) / (y1 - y0 + 1.0);
        double scale = labelWidthMm / (x1 - x0 + 1.0);

        // 表格模板先识别并剥离长横/竖线。若把整张网格当成一个连通域，
        // 后续文字分行会退化为一个大盒，所有文本挤到左侧。
        var gridLines = DetectTableGrid(px, w, x0, y0, x1, y1);
        var gridMask = new bool[w * h];
        foreach (var line in gridLines)
            for (int gy = Math.Max(y0, line.y0 - 1); gy <= Math.Min(y1, line.y1 + 1); gy++)
                for (int gx = Math.Max(x0, line.x0 - 1); gx <= Math.Min(x1, line.x1 + 1); gx++)
                    gridMask[gy * w + gx] = true;
        bool TextInk(int x, int y) => Ink(x, y) && !gridMask[y * w + x];

        // 连通域提取（逐字符/QR 模块），再按邻近合并成元素盒——
        // 行投影会把竖向重叠的 QR 与右侧文字粘成一个大盒，导致摆放重叠，故不用
        var labels = new int[w * h];
        var comps = new List<(int x0, int y0, int x1, int y1)>();
        var stack = new Stack<(int x, int y)>();
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int idx = y * w + x;
                if (labels[idx] != 0 || !TextInk(x, y))
                    continue;
                int id = comps.Count + 1;
                int cx0 = x, cx1 = x, cy0 = y, cy1 = y, area = 0;
                stack.Push((x, y));
                labels[idx] = id;
                while (stack.Count > 0)
                {
                    var (fx, fy) = stack.Pop();
                    area++;
                    if (fx < cx0) cx0 = fx;
                    if (fx > cx1) cx1 = fx;
                    if (fy < cy0) cy0 = fy;
                    if (fy > cy1) cy1 = fy;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = fx + dx, ny = fy + dy;
                            if (nx < x0 || nx > x1 || ny < y0 || ny > y1)
                                continue;
                            int nidx = ny * w + nx;
                            if (labels[nidx] == 0 && TextInk(nx, ny))
                            {
                                labels[nidx] = id;
                                stack.Push((nx, ny));
                            }
                        }
                }
                if (area >= 6)                              // 滤碎点
                    comps.Add((cx0, cy0, cx1, cy1));
                else
                    for (int yy = cy0; yy <= cy1; yy++)
                        for (int xx = cx0; xx <= cx1; xx++)
                            if (labels[yy * w + xx] == id)
                                labels[yy * w + xx] = -1;
            }
        }

        // 合并阈值：0.35×文字高（字母间隙 2~4px、词间隙 ~10px、QR 模块间隙 ~10px 均并；
        // 元素行间隙 >50px 不并）
        int textH = comps.Count > 0
            ? comps.Select(c => c.y1 - c.y0 + 1).OrderBy(v => v).ElementAt(comps.Count * 4 / 5)
            : 30;
        int mergeGap = Math.Max(6, (int)(textH * 0.35));

        int n = comps.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int a) => parent[a] == a ? a : parent[a] = Find(parent[a]);
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                var A = comps[i]; var B = comps[j];
                int gx = Math.Max(0, Math.Max(A.x0 - B.x1, B.x0 - A.x1));
                int gy = Math.Max(0, Math.Max(A.y0 - B.y1, B.y0 - A.y1));
                bool near = gx <= mergeGap && gy <= 2;
                if (near)
                {
                    int ra = Find(i), rb = Find(j);
                    if (ra != rb)
                        parent[ra] = rb;
                }
            }

        var boxes = new List<(int x0, int y0, int x1, int y1)>();
        var groups = new Dictionary<int, (int x0, int y0, int x1, int y1)>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            var c = comps[i];
            if (groups.TryGetValue(r, out var g))
                groups[r] = (Math.Min(g.x0, c.x0), Math.Min(g.y0, c.y0), Math.Max(g.x1, c.x1), Math.Max(g.y1, c.y1));
            else
                groups[r] = c;
        }
        boxes.AddRange(groups.Values);
        if (boxes.Count == 0)
            return (new List<BtwPlacement>(), aspect);

        // 文字行高（用于字号）：取盒高的中位数（QR 大盒会被拉高，取中位数稳）
        var heights = boxes.Select(b => b.y1 - b.y0 + 1).OrderBy(v => v).ToList();
        int typicalBandH = heights[heights.Count / 2];

        // 条码盒 = 高度显著大于文字带且接近方形的盒；同类候选取面积最大者
        var barcode = boxes.Where(b => b.y1 - b.y0 + 1 > typicalBandH * 2).ToList();
        (int x0, int y0, int x1, int y1)? bcBox = null;
        foreach (var b in barcode.OrderByDescending(b =>
                     (b.x1 - b.x0 + 1) * (b.y1 - b.y0 + 1)))
        {
            double bw = b.x1 - b.x0 + 1, bh = b.y1 - b.y0 + 1;
            if (bw / bh is > 0.7 and < 1.45)
            {
                bcBox = b;
                break;
            }
        }

        // 从连通域中剔除条码内部的模块。只剔“主体落在条码盒内”的连通域，
        // 避免二维码旁边、上下方紧邻的文字被一并删除。
        bool MostlyInsideBarcode((int x0, int y0, int x1, int y1) c,
                                 (int x0, int y0, int x1, int y1) box)
        {
            int ow = Math.Min(c.x1, box.x1) - Math.Max(c.x0, box.x0) + 1;
            int oh = Math.Min(c.y1, box.y1) - Math.Max(c.y0, box.y0) + 1;
            return ow > 0 && oh > 0
                && ow >= (c.x1 - c.x0 + 1) * 0.5
                && oh >= (c.y1 - c.y0 + 1) * 0.5;
        }
        var textComponents = bcBox is { } codeBox
            ? comps.Where(c => !MostlyInsideBarcode(c, codeBox)).ToList()
            : comps;

        // 字号只按所有字符的典型墨迹高度估算一次。若逐行/逐盒用最大高度，
        // 带 g/Q/中文字形的行会被当成更大字号，效果预览会明显放大。
        var characterHeights = textComponents
            .Select(c => c.y1 - c.y0 + 1)
            .OrderBy(v => v)
            .ToList();
        int fontBandH = characterHeights.Count > 0
            ? characterHeights[Math.Min(characterHeights.Count - 1,
                (int)(characterHeights.Count * 0.75))]
            : typicalBandH;
        double em = fontBandH / 0.78;

        // 字符宽度估算（Arial 近似逐字符宽，em = 墨迹高/0.78）；{SN} 按 20 字符样值估宽
        double CharW(char c)
        {
            if (c > 0x2E80)
                return em;                                   // CJK 全宽
            if ("iljtfrI.,:!'|\"".Contains(c))
                return em * 0.30;                            // 窄
            if ("mwMWQGODCN@".Contains(c))
                return em * 0.80;                            // 宽
            if (char.IsUpper(c))
                return em * 0.68;
            if (c == ' ')
                return em * 0.28;
            return em * 0.55;
        }
        double Est(string s)
        {
            string sample = s.Replace("{SN}", "LSCH42JY012611140142", StringComparison.OrdinalIgnoreCase);
            return sample.Sum(CharW);
        }

        var texts = objects.Where(o => !o.IsBarcode).ToList();
        var fonts = texts.Select(t => t.Font).FirstOrDefault(f => !string.IsNullOrEmpty(f)) ?? "";
        var placements = new List<BtwPlacement>();
        var tableRegions = gridLines.Count > 0
            ? BuildTableTextRegions(gridLines, textComponents, x0, y0, x1, y1)
            : new List<TableTextRegion>();

        int textCount = texts.Count;
        var matched = new bool[textCount];
        bool tableSolved = false;
        if (tableRegions.Count > 0 && tableRegions.Count <= textCount)
        {
            var assignment = AssignTableTexts(
                texts.Select(t => t.Content).ToList(), tableRegions, fonts);
            if (assignment.All(ri => ri >= 0))
            {
                var regionUseCounts = assignment
                    .GroupBy(ri => ri)
                    .ToDictionary(g => g.Key, g => g.Count());
                var regionUsed = new Dictionary<int, int>();
                foreach (var (ti, ri) in assignment.Select((ri, ti) => (ti, ri)))
                {
                    var region = tableRegions[ri];
                    int slot = regionUsed.GetValueOrDefault(ri);
                    regionUsed[ri] = slot + 1;
                    int slots = regionUseCounts[ri];
                    double regionH = region.Y1 - region.Y0 + 1;
                    double slotWidth = (region.X1 - region.X0 + 1.0) / slots;
                    double fontPt = regionH / 0.78 * scale * 72 / 25.4;
                    matched[ti] = true;
                    placements.Add(new BtwPlacement(
                        "text", texts[ti].Content, fonts,
                        (region.X0 - x0 + slot * slotWidth) * scale,
                        (region.Y0 - y0) * scale,
                        fontPt,
                        slotWidth * scale,
                        regionH * scale));
                }
                tableSolved = true;
            }
        }

        // 先按纵向重叠归并成文本行，而不是横向拼字符盒。模板中同一对象可能
        // 被较大字距拆成多个盒（如 SN: + 长 SN），逐盒匹配会把整行拆错。
        var lineBands = new List<(int x0, int y0, int x1, int y1)>();
        foreach (var c in textComponents
                     .OrderBy(v => v.y0)
                     .ThenBy(v => v.x0))
        {
            int bestLine = -1;
            double bestOverlap = 0;
            for (int li = 0; li < lineBands.Count; li++)
            {
                var line = lineBands[li];
                int overlap = Math.Max(0,
                    Math.Min(c.y1, line.y1) - Math.Max(c.y0, line.y0) + 1);
                int shorter = Math.Min(c.y1 - c.y0 + 1, line.y1 - line.y0 + 1);
                double ratio = shorter > 0 ? overlap / (double)shorter : 0;
                if (ratio > bestOverlap)
                {
                    bestOverlap = ratio;
                    bestLine = li;
                }
            }

            if (bestLine >= 0 && bestOverlap >= 0.33)
            {
                var line = lineBands[bestLine];
                lineBands[bestLine] = (
                    Math.Min(line.x0, c.x0), Math.Min(line.y0, c.y0),
                    Math.Max(line.x1, c.x1), Math.Max(line.y1, c.y1));
            }
            else
            {
                lineBands.Add(c);
            }
        }
        lineBands = lineBands
            .OrderBy(v => v.y0)
            .ThenBy(v => v.x0)
            .ToList();

        // 模板记录顺序不一定等于画面顺序（条码对象可能夹在两组文本记录之间）。
        // 因此为每条文本行搜索一组不重复的文本，使“估算字宽之和”最接近行宽。
        int lineCount = lineBands.Count;
        var bestLineMasks = new ulong[lineCount];
        var currentLineMasks = new List<ulong>(lineCount);
        bool solved = !tableSolved
            && textCount is > 0 and <= 16 && lineCount > 0 && lineCount <= textCount;
        if (solved)
        {
            ulong allMask = textCount == 64 ? ulong.MaxValue : (1UL << textCount) - 1;
            var lineCandidates = new List<(ulong Mask, double Cost)>[lineCount];
            for (int li = 0; li < lineCount; li++)
            {
                var line = lineBands[li];
                double lineWidth = line.x1 - line.x0 + 1;
                var candidates = new List<(ulong Mask, double Cost)>();
                for (ulong mask = allMask; mask != 0; mask = (mask - 1) & allMask)
                {
                    int count = System.Numerics.BitOperations.PopCount(mask);
                    if (count > 4)
                        continue;
                    double sum = 0;
                    for (int i = 0; i < textCount; i++)
                        if ((mask & (1UL << i)) != 0)
                            sum += Est(texts[i].Content);
                    double mismatch = Math.Abs(sum - lineWidth) / Math.Max(1.0, lineWidth);
                    candidates.Add((mask, mismatch + 0.01 * (count - 1)));
                }
                // 只保留最贴合的少量组合，避免对象较多时组合搜索指数膨胀。
                lineCandidates[li] = candidates
                    .OrderBy(v => v.Cost)
                    .Take(24)
                    .ToList();
            }
            double bestCost = double.PositiveInfinity;

            void SearchLines(int lineIndex, ulong usedMask, double cost)
            {
                if (cost >= bestCost)
                    return;
                if (lineIndex == lineCount)
                {
                    if (usedMask == allMask)
                    {
                        bestCost = cost;
                        currentLineMasks.CopyTo(bestLineMasks);
                    }
                    return;
                }

                int usedCount = System.Numerics.BitOperations.PopCount(usedMask);
                int remainingLines = lineCount - lineIndex - 1;
                int maxTake = textCount - usedCount - remainingLines;
                foreach (var candidate in lineCandidates[lineIndex])
                {
                    int count = System.Numerics.BitOperations.PopCount(candidate.Mask);
                    if (count > maxTake)
                        continue;
                    if ((candidate.Mask & usedMask) != 0)
                        continue;
                    currentLineMasks.Add(candidate.Mask);
                    SearchLines(lineIndex + 1, usedMask | candidate.Mask, cost + candidate.Cost);
                    currentLineMasks.RemoveAt(currentLineMasks.Count - 1);
                }
            }

            SearchLines(0, 0, 0);
            solved = !double.IsPositiveInfinity(bestCost);
        }

        if (solved)
        {
            double fontPt = fontBandH / 0.78 * scale * 72 / 25.4;
            for (int li = 0; li < lineCount; li++)
            {
                var line = lineBands[li];
                double lineWidth = line.x1 - line.x0 + 1;
                ulong mask = bestLineMasks[li];
                var lineTexts = Enumerable.Range(0, textCount)
                    .Where(i => (mask & (1UL << i)) != 0)
                    .ToList();
                double sumEst = 0;
                foreach (int ti in lineTexts)
                    sumEst += Est(texts[ti].Content);

                double fit = sumEst > lineWidth ? lineWidth / Math.Max(1, sumEst) : 1;
                double gap = lineTexts.Count > 1 && sumEst <= lineWidth
                    ? (lineWidth - sumEst) / (lineTexts.Count - 1)
                    : 0;
                double usedX = line.x0;
                foreach (int ti in lineTexts)
                {
                    matched[ti] = true;
                    double width = Est(texts[ti].Content) * fit;
                    placements.Add(new BtwPlacement(
                        "text", texts[ti].Content, fonts,
                        (usedX - x0) * scale,
                        (line.y0 - y0) * scale,
                        fontPt,
                        width * scale,
                        (line.y1 - line.y0 + 1) * scale));
                    usedX += width + gap;
                }
            }
        }

        // 无法分行时的兜底：放在标签左侧顺排，字号仍使用全局典型字高，
        // 避免某个大图形盒导致文字字号异常放大。
        for (int i = 0; i < textCount; i++)
        {
            if (matched[i])
                continue;
            placements.Add(new BtwPlacement("text", texts[i].Content, fonts,
                labelWidthMm * 0.05,
                labelWidthMm / aspect * (0.1 + 0.12 * placements.Count),
                fontBandH / 0.78 * scale * 72 / 25.4,
                Math.Min(labelWidthMm * 0.9, Est(texts[i].Content) * scale * 0.9),
                fontBandH * scale));
        }

        if (bcBox is { } bb && objects.Any(o => o.IsBarcode))
        {
            double bh = bb.y1 - bb.y0 + 1, bw = bb.x1 - bb.x0 + 1;
            placements.Add(new BtwPlacement(
                "barcode", objects.First(o => o.IsBarcode).Content, "",
                (bb.x0 - x0) * scale, (bb.y0 - y0) * scale, 0,
                bw * scale, bh * scale));
        }

        foreach (var line in gridLines)
        {
            placements.Add(new BtwPlacement(
                "line", "", "",
                (line.x0 - x0) * scale,
                (line.y0 - y0) * scale,
                0,
                (line.x1 - line.x0 + 1) * scale,
                (line.y1 - line.y0 + 1) * scale));
        }

        return (placements, aspect);
    }

    /// <summary>
    /// 检测表格长线：先找连续深色横/竖线段，再按方向合并相邻像素为一条实线。
    /// 只有存在内部竖线/横线时才认定为表格，普通标签的单层外框仍按普通版式处理。
    /// </summary>
    private static List<(int x0, int y0, int x1, int y1)> DetectTableGrid(
        byte[] px, int width, int x0, int y0, int x1, int y1)
    {
        int labelW = x1 - x0 + 1, labelH = y1 - y0 + 1;
        bool Dark(int x, int y) => px[y * width + x] < 128;
        var runs = new List<(int x0, int y0, int x1, int y1)>();

        for (int y = y0; y <= y1; y++)
        {
            int start = -1;
            for (int x = x0; x <= x1 + 1; x++)
            {
                bool dark = x <= x1 && Dark(x, y);
                if (dark && start < 0)
                    start = x;
                else if (!dark && start >= 0)
                {
                    if (x - start >= labelW * 0.12)
                        runs.Add((start, y, x - 1, y));
                    start = -1;
                }
            }
        }
        for (int x = x0; x <= x1; x++)
        {
            int start = -1;
            for (int y = y0; y <= y1 + 1; y++)
            {
                bool dark = y <= y1 && Dark(x, y);
                if (dark && start < 0)
                    start = y;
                else if (!dark && start >= 0)
                {
                    if (y - start >= labelH * 0.2)
                        runs.Add((x, start, x, y - 1));
                    start = -1;
                }
            }
        }

        var merged = new List<(int x0, int y0, int x1, int y1)>();
        foreach (var run in runs.OrderBy(v => v.y0).ThenBy(v => v.x0))
        {
            bool horizontal = run.x1 - run.x0 >= run.y1 - run.y0;
            bool joined = false;
            for (int i = 0; i < merged.Count; i++)
            {
                var m = merged[i];
                bool mHorizontal = m.x1 - m.x0 >= m.y1 - m.y0;
                if (horizontal != mHorizontal)
                    continue;
                bool adjacent = horizontal
                    ? Math.Abs(run.x0 - m.x0) <= 2 && Math.Abs(run.x1 - m.x1) <= 2
                      && run.y0 <= m.y1 + 2 && run.y1 >= m.y0 - 2
                    : Math.Abs(run.y0 - m.y0) <= 2 && Math.Abs(run.y1 - m.y1) <= 2
                      && run.x0 <= m.x1 + 2 && run.x1 >= m.x0 - 2;
                if (!adjacent)
                    continue;
                merged[i] = (
                    Math.Min(run.x0, m.x0), Math.Min(run.y0, m.y0),
                    Math.Max(run.x1, m.x1), Math.Max(run.y1, m.y1));
                joined = true;
                break;
            }
            if (!joined)
                merged.Add(run);
        }

        var horizontalLines = merged
            .Where(v => v.x1 - v.x0 >= labelW * 0.45 && v.x1 - v.x0 > (v.y1 - v.y0) * 3)
            .ToList();
        var verticalLines = merged
            .Where(v => v.y1 - v.y0 >= labelH * 0.45 && v.y1 - v.y0 > (v.x1 - v.x0) * 3)
            .ToList();
        bool innerVertical = verticalLines.Any(v =>
            (v.x0 + v.x1) / 2 > x0 + labelW * 0.05
            && (v.x0 + v.x1) / 2 < x1 - labelW * 0.05);
        bool innerHorizontal = horizontalLines.Any(v =>
            (v.y0 + v.y1) / 2 > y0 + labelH * 0.05
            && (v.y0 + v.y1) / 2 < y1 - labelH * 0.05);
        return horizontalLines.Count >= 2 && verticalLines.Count >= 2
               && innerVertical && innerHorizontal
            ? horizontalLines.Concat(verticalLines).ToList()
            : new List<(int x0, int y0, int x1, int y1)>();
    }

    /// <summary>按表格网格切单元格，再取每个单元格里实际存在的文字行盒。</summary>
    private static List<TableTextRegion> BuildTableTextRegions(
        IReadOnlyList<(int x0, int y0, int x1, int y1)> gridLines,
        IReadOnlyList<(int x0, int y0, int x1, int y1)> components,
        int labelX0, int labelY0, int labelX1, int labelY1)
    {
        var horizontal = gridLines
            .Where(v => v.x1 - v.x0 > (v.y1 - v.y0) * 3)
            .OrderBy(v => (v.y0 + v.y1) / 2.0)
            .ToList();
        var vertical = gridLines
            .Where(v => v.y1 - v.y0 > (v.x1 - v.x0) * 3)
            .ToList();
        var rowBounds = horizontal
            .Select(v => (v.y0 + v.y1) / 2)
            .Distinct()
            .OrderBy(v => v)
            .ToList();
        if (rowBounds.Count < 2)
            return new List<TableTextRegion>();

        var cells = new List<(int x0, int y0, int x1, int y1, int row, int col)>();
        for (int ri = 0; ri < rowBounds.Count - 1; ri++)
        {
            int top = rowBounds[ri], bottom = rowBounds[ri + 1];
            var colBounds = new List<int> { labelX0, labelX1 };
            colBounds.AddRange(vertical
                .Where(v => v.y0 <= top + 2 && v.y1 >= bottom - 2)
                .Select(v => (v.x0 + v.x1) / 2));
            colBounds = colBounds.Distinct().OrderBy(v => v).ToList();

            for (int ci = 0; ci < colBounds.Count - 1; ci++)
            {
                int left = colBounds[ci], right = colBounds[ci + 1];
                var inside = components.Where(c =>
                {
                    double cx = (c.x0 + c.x1) / 2.0;
                    double cy = (c.y0 + c.y1) / 2.0;
                    return cx > left && cx < right && cy > top && cy < bottom;
                }).ToList();
                var cellLines = GroupTextComponentsByLine(inside);
                // 同一单元格中可能因抗锯齿把一个字符拆成上下两段；间距很小且横向重叠时合回一行。
                for (int i = 0; i < cellLines.Count; i++)
                    for (int j = i + 1; j < cellLines.Count; j++)
                    {
                        var a = cellLines[i];
                        var b = cellLines[j];
                        int verticalGap = Math.Max(0, Math.Max(a.y0 - b.y1, b.y0 - a.y1));
                        int overlapX = Math.Max(0,
                            Math.Min(a.x1, b.x1) - Math.Max(a.x0, b.x0) + 1);
                        int minWidth = Math.Max(1, Math.Min(a.x1 - a.x0 + 1, b.x1 - b.x0 + 1));
                        if (verticalGap <= 6 && overlapX >= minWidth * 0.6)
                        {
                            cellLines[i] = (
                                Math.Min(a.x0, b.x0), Math.Min(a.y0, b.y0),
                                Math.Max(a.x1, b.x1), Math.Max(a.y1, b.y1));
                            cellLines.RemoveAt(j--);
                        }
                    }
                foreach (var line in cellLines)
                    cells.Add((line.x0, line.y0, line.x1, line.y1, ri, ci));
            }
        }

        // 非空单元格的列索引只保留实际有文字的列，标题行统一为 Col=0。
        var bodyCols = cells
            .Where(v => v.row > 0)
            .Select(v => v.col)
            .Distinct()
            .OrderBy(v => v)
            .Select((v, i) => (v, i))
            .ToDictionary(v => v.v, v => v.i);
        return cells
            .Select(v => new TableTextRegion(
                v.x0, v.y0, v.x1, v.y1, v.row,
                v.row == 0 ? 0 : bodyCols.GetValueOrDefault(v.col)))
            .OrderBy(v => v.Row)
            .ThenBy(v => v.Col)
            .ThenBy(v => v.Y0)
            .ToList();
    }

    private static List<(int x0, int y0, int x1, int y1)> GroupTextComponentsByLine(
        IReadOnlyList<(int x0, int y0, int x1, int y1)> components)
    {
        var lines = new List<(int x0, int y0, int x1, int y1)>();
        foreach (var c in components.OrderBy(v => v.y0).ThenBy(v => v.x0))
        {
            int best = -1;
            double bestRatio = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                int overlap = Math.Max(0,
                    Math.Min(c.y1, line.y1) - Math.Max(c.y0, line.y0) + 1);
                int shorter = Math.Min(c.y1 - c.y0 + 1, line.y1 - line.y0 + 1);
                double ratio = shorter > 0 ? overlap / (double)shorter : 0;
                if (ratio > bestRatio)
                {
                    bestRatio = ratio;
                    best = i;
                }
            }
            if (best >= 0 && bestRatio >= 0.33)
            {
                var line = lines[best];
                lines[best] = (
                    Math.Min(c.x0, line.x0), Math.Min(c.y0, line.y0),
                    Math.Max(c.x1, line.x1), Math.Max(c.y1, line.y1));
            }
            else
            {
                lines.Add(c);
            }
        }
        return lines;
    }

    /// <summary>
    /// 表格文字与文字盒一对一匹配。宽度用 WPF 实际字形边界按目标高度缩放后比较，
    /// 比固定字符宽度表更适合中英文混排；再用匈牙利算法做全局最优分配。
    /// </summary>
    private static int[] AssignTableTexts(
        IReadOnlyList<string> texts,
        IReadOnlyList<TableTextRegion> regions,
        string preferredFont)
    {
        int n = texts.Count;
        var assignment = Enumerable.Repeat(-1, n).ToArray();
        if (n == 0 || regions.Count == 0 || regions.Count > n)
            return assignment;

        // 富士达唛头这一组模板的对象保存顺序固定：
        // 标题 → 首行4格 → 左列7个文字行 → 中列5个值 → 右侧D/E列按行配对。
        // 几何宽度足够识别结构时按该顺序直接落位，比仅靠字宽匹配稳定得多。
        if (texts[0].Contains("Made in China", StringComparison.OrdinalIgnoreCase))
        {
            int RegionIndex(TableTextRegion region)
                => Enumerable.Range(0, regions.Count).First(i => regions[i] == region);
            var title = regions.Where(v => v.Row == 0).OrderBy(v => v.Y0).ThenBy(v => v.X0).ToList();
            var second = regions.Where(v => v.Row == 1).OrderBy(v => v.Col).ToList();
            var left = regions.Where(v => v.Row > 1 && v.Col == 0)
                .OrderBy(v => v.Row).ThenBy(v => v.Y0).ToList();
            var middle = regions.Where(v => v.Row > 1 && v.Col == 1)
                .OrderBy(v => v.Row).ToList();
            var right = regions.Where(v => v.Row > 1 && v.Col >= 2)
                .OrderBy(v => v.Row).ThenBy(v => v.Col).ToList();
            var purchaseLabel = regions.Where(v => v.Row == 2 && v.Col == 2).ToList();
            var purchaseValues = regions.Where(v => v.Row == 2 && v.Col == 3)
                .OrderBy(v => v.Y0).ToList();
            var rightRest = right
                .Where(v => !purchaseLabel.Contains(v) && !purchaseValues.Contains(v))
                .ToList();
            int extraPurchaseValues = Math.Max(0, n - 26);
            if (n >= 26 && title.Count == 1 && second.Count == 4 && left.Count == 7
                && middle.Count == 5 && purchaseLabel.Count == 1 && purchaseValues.Count >= 1
                && rightRest.Count == 7)
            {
                assignment[0] = RegionIndex(title[0]);
                assignment[1] = RegionIndex(second[0]);
                assignment[2] = RegionIndex(second[2]);
                assignment[3] = RegionIndex(second[1]);
                assignment[4] = RegionIndex(second[3]);
                for (int i = 0; i < 7; i++)
                    assignment[5 + i] = RegionIndex(left[i]);
                for (int i = 0; i < 5; i++)
                    assignment[12 + i] = RegionIndex(middle[i]);
                assignment[17] = RegionIndex(purchaseLabel[0]);
                assignment[18] = RegionIndex(purchaseValues[0]);
                for (int i = 0; i < 7; i++)
                    assignment[19 + i] = RegionIndex(rightRest[i]);
                int perLine = Math.Max(1,
                    (int)Math.Ceiling((extraPurchaseValues + 1.0) / purchaseValues.Count));
                for (int i = 0; i < extraPurchaseValues; i++)
                {
                    int visualSlot = i + 1;
                    int regionAt = Math.Min(purchaseValues.Count - 1, visualSlot / perLine);
                    assignment[26 + i] = RegionIndex(purchaseValues[regionAt]);
                }
                return assignment;
            }
        }

        if (regions.Count != n)
            return assignment;

        // 表格标题通常独占合并单元格，字高显著大于正文。先锁定标题，
        // 避免“字高更大的文字盒”被其他同宽高比的正文抢占后引发整列错位。
        var heights = regions.Select(v => v.Y1 - v.Y0 + 1).OrderBy(v => v).ToList();
        int titleText = -1, titleRegion = -1;
        if (heights.Count >= 3)
        {
            int maxRegion = Enumerable.Range(0, n)
                .OrderByDescending(i => regions[i].Y1 - regions[i].Y0)
                .ThenBy(i => i)
                .First();
            if (regions[maxRegion].Y1 - regions[maxRegion].Y0 + 1 >= heights[heights.Count / 2] * 1.5)
            {
                titleText = 0;
                titleRegion = maxRegion;
                assignment[titleText] = titleRegion;
            }
        }

        var textIndexes = Enumerable.Range(0, n).Where(i => i != titleText).ToList();
        var regionIndexes = Enumerable.Range(0, n).Where(i => i != titleRegion).ToList();
        int reduced = textIndexes.Count;
        var costs = new double[reduced, reduced];
        for (int ti = 0; ti < reduced; ti++)
            for (int ri = 0; ri < reduced; ri++)
            {
                int sourceText = textIndexes[ti], sourceRegion = regionIndexes[ri];
                double width = regions[sourceRegion].X1 - regions[sourceRegion].X0 + 1;
                double height = regions[sourceRegion].Y1 - regions[sourceRegion].Y0 + 1;
                costs[ti, ri] = MeasureTextAspectCost(texts[sourceText], width, height, preferredFont)
                    + 0.0005 * Math.Abs(sourceText - sourceRegion) / n;
            }
        var reducedAssignment = SolveAssignment(costs);
        for (int ti = 0; ti < reduced; ti++)
            if (reducedAssignment[ti] >= 0)
                assignment[textIndexes[ti]] = regionIndexes[reducedAssignment[ti]];
        return assignment;
    }

    private static double MeasureTextAspectCost(
        string text, double targetWidth, double targetHeight, string preferredFont)
    {
        if (string.IsNullOrWhiteSpace(text) || targetWidth <= 0 || targetHeight <= 0)
            return 10;
        var fontNames = new[] { preferredFont, "Times New Roman", "SimSun", "Microsoft YaHei", "Arial" }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var weights = new[] { FontWeights.Normal, FontWeights.SemiBold, FontWeights.Bold };
        double best = 10;
        foreach (string fontName in fontNames)
            foreach (var weight in weights)
            {
                try
                {
                    var formatted = new FormattedText(
                        text,
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        new Typeface(new FontFamily(fontName), FontStyles.Normal,
                            weight, FontStretches.Normal),
                        100,
                        Brushes.Black,
                        1.0);
                    var bounds = formatted.BuildGeometry(new Point(0, 0)).Bounds;
                    if (bounds.Width <= 0 || bounds.Height <= 0)
                        continue;
                    double predictedWidth = bounds.Width * targetHeight / bounds.Height;
                    double cost = Math.Abs(predictedWidth - targetWidth)
                                  / Math.Max(1, targetWidth);
                    if (cost < best)
                        best = cost;
                }
                catch
                {
                    // 字体不存在/字形不可用时换下一种候选。
                }
            }
        return best;
    }

    /// <summary>匈牙利算法：返回每行文本分配到的列（文字盒）下标。</summary>
    private static int[] SolveAssignment(double[,] cost)
    {
        int n = cost.GetLength(0);
        if (n == 0)
            return Array.Empty<int>();
        var u = new double[n + 1];
        var v = new double[n + 1];
        var p = new int[n + 1];
        var way = new int[n + 1];
        for (int i = 1; i <= n; i++)
        {
            p[0] = i;
            int j0 = 0;
            var minv = Enumerable.Repeat(double.PositiveInfinity, n + 1).ToArray();
            var used = new bool[n + 1];
            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = 0;
                double delta = double.PositiveInfinity;
                for (int j = 1; j <= n; j++)
                {
                    if (used[j])
                        continue;
                    double cur = cost[i0 - 1, j - 1] - u[i0] - v[j];
                    if (cur < minv[j])
                    {
                        minv[j] = cur;
                        way[j] = j0;
                    }
                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }
                for (int j = 0; j <= n; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }
                j0 = j1;
            } while (p[j0] != 0);
            do
            {
                int j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            } while (j0 != 0);
        }

        var assignment = Enumerable.Repeat(-1, n).ToArray();
        for (int j = 1; j <= n; j++)
            if (p[j] > 0)
                assignment[p[j] - 1] = j - 1;
        return assignment;
    }

    /// <summary>预览图是否接近方形（判条码码制用）。</summary>
    public static bool LooksSquare(double widthMm, double heightMm)
        => widthMm / Math.Max(0.1, heightMm) is > 0.8 and < 1.25;
}
