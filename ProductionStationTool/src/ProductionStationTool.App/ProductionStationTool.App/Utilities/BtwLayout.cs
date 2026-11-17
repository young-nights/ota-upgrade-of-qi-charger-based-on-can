using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProductionStationTool.App.Utilities;

using ProductionStationTool.Core.Printing;

/// <summary>预览图量出的摆放结果（mm，坐标原点=标签左上）。</summary>
public sealed record BtwPlacement(
    string Kind, string Content, string Font,
    double XMm, double YMm, double FontPt, double WidthMm, double HeightMm);

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
                if (labels[idx] != 0 || !Ink(x, y))
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
                            if (labels[nidx] == 0 && Ink(nx, ny))
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

        // 条码盒 = 高度显著大于文字带且接近方形的盒
        var barcode = boxes.Where(b => b.y1 - b.y0 + 1 > typicalBandH * 2).ToList();
        (int x0, int y0, int x1, int y1)? bcBox = null;
        foreach (var b in barcode)
        {
            double bw = b.x1 - b.x0 + 1, bh = b.y1 - b.y0 + 1;
            if (bw / bh is > 0.7 and < 1.45)
            {
                bcBox = b;
                break;
            }
        }
        var textBoxes = boxes.Where(b => !bcBox.HasValue || b != bcBox.Value).ToList();

        // 字符宽度估算（Arial 近似逐字符宽，em = 行高/0.78）；{SN} 按 20 字符样值估宽
        double em = typicalBandH / 0.78;
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

        // 盒-串匹配：逐盒选未分配串（1~2 个）使估算宽度和最贴盒宽
        var unassigned = texts.Select((t, i) => i).ToList();
        foreach (var box in textBoxes.OrderByDescending(b => b.x1 - b.x0))
        {
            double boxW = box.x1 - box.x0 + 1;
            int pick1 = -1, pick2 = -1;
            double best = double.MaxValue;
            // 先扫双串（同分时对子优先——同宽的「Trade:+Lime」与「Qi Charger」靠此不互换）
            foreach (int i in unassigned)
                foreach (int j in unassigned)
                {
                    if (j == i)
                        continue;
                    double d2 = Math.Abs(Est(texts[i].Content) + Est(texts[j].Content) - boxW);
                    if (d2 <= best) { best = d2; pick1 = i; pick2 = j; }
                }
            foreach (int i in unassigned)
            {
                double d = Math.Abs(Est(texts[i].Content) - boxW);
                if (d < best) { best = d; pick1 = i; pick2 = -1; }
            }
            if (pick1 < 0)
                continue;
            var picks = pick2 >= 0 ? new List<int> { pick1, pick2 } : new List<int> { pick1 };
            // 含 ':' 的串在左（Trade:/SN:/PN: 均为前缀），否则宽的在左
            if (picks.Count == 2)
            {
                bool a = texts[picks[0]].Content.Contains(':');
                bool b = texts[picks[1]].Content.Contains(':');
                if (a != b && !a)
                    picks.Reverse();
                else if (a == b && Est(texts[picks[0]].Content) < Est(texts[picks[1]].Content))
                    picks.Reverse();
            }

            double boxH = box.y1 - box.y0 + 1;
            double fontPt = boxH / 0.78 * scale * 72 / 25.4;
            // 2 串同盒：按串宽占比瓜分盒宽
            double usedX = box.x0;
            foreach (int i in picks)
            {
                double share = picks.Count == 1 ? boxW : boxW * Est(texts[i].Content) / Math.Max(1, picks.Sum(p => Est(texts[p].Content)));
                placements.Add(new BtwPlacement(
                    "text", texts[i].Content, fonts,
                    (usedX - x0) * scale,
                    (box.y0 - y0) * scale,
                    fontPt,
                    share, boxH * scale));
                usedX += share;
                unassigned.Remove(i);
            }
        }

        // 未匹配上的串（估算失配兜底）：放到第一行下方顺排
        foreach (int i in unassigned)
        {
            placements.Add(new BtwPlacement("text", texts[i].Content, fonts,
                labelWidthMm * 0.05, labelWidthMm / aspect * (0.1 + 0.12 * placements.Count),
                typicalBandH / 0.78 * scale * 72 / 25.4, labelWidthMm * 0.4, typicalBandH * scale));
        }

        if (bcBox is { } bb && objects.Any(o => o.IsBarcode))
        {
            double bh = bb.y1 - bb.y0 + 1, bw = bb.x1 - bb.x0 + 1;
            placements.Add(new BtwPlacement(
                "barcode", objects.First(o => o.IsBarcode).Content, "",
                (bb.x0 - x0) * scale, (bb.y0 - y0) * scale, 0,
                bw * scale, bh * scale));
        }

        return (placements, aspect);
    }

    /// <summary>预览图是否接近方形（判条码码制用）。</summary>
    public static bool LooksSquare(double widthMm, double heightMm)
        => widthMm / Math.Max(0.1, heightMm) is > 0.8 and < 1.25;
}
