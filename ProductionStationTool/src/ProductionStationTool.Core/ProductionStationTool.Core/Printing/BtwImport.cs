using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ProductionStationTool.Core.Printing;

/// <summary>.btw 导入对象：显示文本或条码。</summary>
public sealed record BtwObject(string Kind, string Content, string Font)
{
    public bool IsBarcode => Kind == "barcode";
}

/// <summary>.btw 导入结果（尽力提取；坐标/字号由预览图测量补齐，见 App 层 BtwLayout）。</summary>
public sealed record BtwImportResult(
    byte[]? PreviewPng,
    string? PrinterName,
    IReadOnlyList<BtwObject> Objects,
    string Note);

/// <summary>
/// .btw 参考导入：提取——
///   ① 内嵌 PNG 标签预览（无需 BarTender）；
///   ② 参数 zlib 流里的对象块（文本/条形码：内容与字体；命名数据源拼接的对象 → {SN}）；
///   ③ 目标打印机名（顺带推断指令语言：含 Zebra/ZPL → ZPL，否则 TSPL）。
/// 结构依据（SN码标签打印预设.btw 实证）：文件 = 文本头 + zlib 压缩段；
/// 参数段内对象块以名称记录（"文本 N"/"条形码 N"/"方框 N"）分界，记录形如
/// FF FE FF &lt;u8 长度&gt; &lt;UTF-16LE 字符&gt;；命名数据源样值成组出现（s01…s07）。
/// </summary>
public static class BtwImport
{
    private static readonly byte[] PngMagic = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>解析 .btw，返回预览图/打印机/对象列表。</summary>
    public static BtwImportResult Extract(string btwPath)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(btwPath);
        }
        catch (Exception ex)
        {
            return new BtwImportResult(null, null, Array.Empty<BtwObject>(), $"读取失败：{ex.Message}");
        }

        var png = ExtractPng(data);

        byte[]? param = null;
        foreach (var raw in InflateStreams(data))
        {
            // 参数流判据 = 解压后含 UTF-16 "BarTender"
            if (IndexOf(raw, Encoding.Unicode.GetBytes("BarTender"), 0) >= 0)
            {
                param = raw;
                break;
            }
        }
        if (param is null)
            return new BtwImportResult(png, null, Array.Empty<BtwObject>(),
                "已提取预览图；未找到参数流（文件版本可能不同），无法导入文本/打印机");

        var records = ParseRecords(param);
        string? printer = records.Select(r => r.Text).FirstOrDefault(
            t => ContainsAny(t, "ZDesigner", "Zebra", "ZPL", "TSPL", "TSC", "Toshiba", "SATO", "Honeywell", "Datamax", "Godex"));
        var objects = ExtractObjects(records, param);
        int barcode = objects.Count(o => o.IsBarcode);

        var note = $"已导入文本 {objects.Count(o => !o.IsBarcode)} 行"
            + (barcode > 0 ? $"、条码 {barcode} 个" : "")
            + (printer is not null ? $"、打印机「{printer}」" : "")
            + "；坐标/字号按预览图自动测量";
        return new BtwImportResult(png, printer, objects, note);
    }

    /// <summary>推断指令语言：打印机名含 Zebra/ZPL → "zpl"，否则 "tspl"。</summary>
    public static string GuessScript(string? printerName)
    {
        if (printerName is null)
            return "tspl";
        return ContainsAny(printerName, "Zebra", "ZDesigner", "ZPL") ? "zpl" : "tspl";
    }

    // ---- 内嵌 PNG ----

    private static byte[]? ExtractPng(byte[] data)
    {
        var candidates = new List<byte[]>();
        int start = 0;
        while (start < data.Length)
        {
            int begin = IndexOf(data, PngMagic, start);
            if (begin < 0)
                break;
            int end = PngEnd(data, begin);
            if (end < 0)
                break;
            var png = new byte[end - begin];
            Array.Copy(data, begin, png, 0, png.Length);
            candidates.Add(png);
            start = end;
        }
        return candidates.Count > 0 ? candidates.MaxBy(p => p.Length) : null;
    }

    private static int PngEnd(byte[] data, int begin)
    {
        int pos = begin + PngMagic.Length;
        while (pos + 8 <= data.Length)
        {
            uint length = ((uint)data[pos] << 24) | ((uint)data[pos + 1] << 16)
                        | ((uint)data[pos + 2] << 8) | data[pos + 3];
            bool isEnd = data[pos + 4] == (byte)'I' && data[pos + 5] == (byte)'E'
                      && data[pos + 6] == (byte)'N' && data[pos + 7] == (byte)'D';
            long next = (long)pos + 8 + length + 4;
            if (next > data.Length)
                return -1;
            pos = (int)next;
            if (isEnd)
                return pos;
        }
        return -1;
    }

    // ---- zlib 流解压 ----

    private static List<byte[]> InflateStreams(byte[] data)
    {
        var result = new List<byte[]>();
        for (int i = 0; i < data.Length - 2; i++)
        {
            if (data[i] != 0x78 || data[i + 1] is not (0x01 or 0x5E or 0x9C or 0xDA))
                continue;
            try
            {
                using var src = new MemoryStream(data, i, data.Length - i);
                using var z = new ZLibStream(src, CompressionMode.Decompress);
                using var dst = new MemoryStream();
                z.CopyTo(dst);
                if (dst.Length > 16)
                    result.Add(dst.ToArray());
                i += 64;   // 解压成功后跳到本段之后继续找
            }
            catch
            {
                // 非真 zlib 头，继续扫
            }
        }
        return result;
    }

    // ---- 参数流记录：FF FE FF <u8 长> <UTF-16LE> ----

    private readonly record struct Rec(int Offset, string Text);

    private static List<Rec> ParseRecords(byte[] param)
    {
        var list = new List<Rec>();
        for (int i = 0; i + 4 < param.Length; i++)
        {
            if (param[i] != 0xFF || param[i + 1] != 0xFE || param[i + 2] != 0xFF)
                continue;
            int len = param[i + 3];                    // 1 字节长度（SN码标签打印预设.btw 实证）
            int start = i + 4;
            if (len <= 0 || start + len * 2 > param.Length)
                continue;
            string text = Encoding.Unicode.GetString(param, start, len * 2);
            if (text.Length > 0 && text.All(c => !char.IsControl(c)))
                list.Add(new Rec(i, text));
            i = start + len * 2 - 1;
        }
        return list;
    }

    private static readonly Regex NameRec = new(@"^(文本|条形码|方框) ?\d*$", RegexOptions.Compiled);

    /// <summary>
    /// 对象块解析：名称记录（文本 N/条形码 N）为块首，到下一个名称记录为止；
    /// 块内「命名数据源样值组」（s01…s07 成组）→ {SN}；否则第一条非样板记录 = 显示内容。
    /// </summary>
    private static List<BtwObject> ExtractObjects(List<Rec> records, byte[] param)
    {
        var noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "1", ".", ",", ":", "/", "_", "CN", "2052", "1...", "bmp", "Picture.bmp", "999999",
            "DbMultiDbStatusPage", "Db Status Page", "Db Table Status Page", "Db Field Status Page",
            "Enter Data", "Sample Text", "Functions and Subs", "OnProcessData", "OnPostSerialize",
            "Data Source", "DataSource", "TextTransforms", "Dialog Control", "Line Control",
            "Picture", "RFID", "Print Quantity", "FormatID", "Format1", "PrinterCopies",
            "SerializedCount", "BatchCount", "User defined", "BarTender", "idbtlf",
            "使用打印机设置", "输入数据", "样本文本", "数据输入表单", "RFID 天线 1",
            "背景磁条 1", "背景色 1", "背景图 1", "背景 1", "层 1", "格式 1", "模板 1",
            "按钮控件 1", "按钮控件 2", "按钮控件 3", "分隔符行 1", "副本 1", "序列号 1",
            "副本:", "序列号:", "Box 1", "Text 1", "Box Options",
        };
        bool IsNoise(string r)
            => noise.Contains(r)
               || r.StartsWith("Root.", StringComparison.Ordinal)
               || r.StartsWith("Db ", StringComparison.Ordinal)
               || r.StartsWith("'")
               || r.StartsWith("s0") && r.Length <= 3
               || r.Contains("___") || r.Contains("000-0000") || r.Contains("???")
               || r.Contains("0123456789")
               || r.StartsWith("Printer Templates") || r.EndsWith(".prn")
               || NameRec.IsMatch(r)
               || ContainsAny(r, "ZDesigner", "Zebra", "ZPL", "TSPL", "USB00", "BackgroundPicture",
                              "PredefinedStocks", "Status Page", "l Dlg", "数据源");

        // 块边界 = 名称记录位置
        var marks = records.Where(r => NameRec.IsMatch(r.Text)).ToList();
        var result = new List<BtwObject>();
        for (int b = 0; b < marks.Count; b++)
        {
            var name = marks[b].Text;
            bool isBarcode = name.StartsWith("条形码", StringComparison.Ordinal);
            bool isText = name.StartsWith("文本", StringComparison.Ordinal);
            if (!isBarcode && !isText)
                continue;

            int from = marks[b].Offset;
            int to = b + 1 < marks.Count ? marks[b + 1].Offset : param.Length;
            var block = records.Where(r => r.Offset > from && r.Offset < to).ToList();

            // 一个 BarTender 文本对象可由多个子文本拼接（如 120 + / + 1）。
            // 每个子文本前都有同一模板记录，不能只保留最后一条值。
            string content = ExtractCompositeText(block);
            for (int i = 0; i < block.Count && content.Length == 0; i++)
            {
                var r = block[i];

                // 命名数据源样值组（s01…s07 连续 ≥2 条）→ {SN} 拼接对象
                if (r.Text.StartsWith("s0") && r.Text.Length <= 3)
                {
                    int run = 1;
                    for (int j = i + 1; j < block.Count
                         && block[j].Text.StartsWith("s0") && block[j].Text.Length <= 3; j++)
                        run++;
                    if (run >= 2)
                    {
                        content = "{SN}";
                        break;
                    }
                    continue;
                }

                if (IsNoise(r.Text))
                    continue;

                // 数据源样值：[值][值][s0x] 成对出现 → 跳过（SN 段样值，如 LS/LS/s01）
                if (i + 2 < block.Count
                    && string.Equals(block[i].Text, block[i + 1].Text, StringComparison.Ordinal)
                    && block[i + 2].Text.StartsWith("s0", StringComparison.Ordinal))
                {
                    i++;
                    continue;
                }

                content = r.Text;   // 值记录在块尾：保留最后一条非样板
            }
            if (content.Length == 0)
                continue;

            result.Add(new BtwObject(isBarcode ? "barcode" : "text", content, GuessCommonFont(param)));
        }
        return result;
    }

    /// <summary>
    /// 复合文本对象：模板以 "(???) ???-????" 分隔每个子文本，其后第一条记录即该段内容。
    /// 返回空串表示不是复合对象，交由原有单文本/数据源逻辑处理。
    /// </summary>
    private static string ExtractCompositeText(IReadOnlyList<Rec> block)
    {
        var markers = new List<int>();
        for (int i = 0; i < block.Count; i++)
            if (block[i].Text == "(???) ???-????")
                markers.Add(i);
        if (markers.Count <= 1)
            return "";

        var parts = new List<string>();
        foreach (int marker in markers)
        {
            int valueAt = marker + 1;
            if (valueAt >= block.Count || block[valueAt].Text == "Box Options")
                continue;
            // 空子文本后直接进入下一模板段。
            if (valueAt + 3 < block.Count
                && block[valueAt].Text == "1"
                && block[valueAt + 1].Text == "999999"
                && block[valueAt + 2].Text == "1"
                && block[valueAt + 3].Text == "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ")
                continue;
            string value = block[valueAt].Text;
            if (parts.Count == 0 || parts[^1] != value)
                parts.Add(value);
        }
        return string.Concat(parts);
    }

    /// <summary>扫参数流找出现最多的已知字体名（Tahoma 为界面字体，跳过）。</summary>
    private static string GuessCommonFont(byte[] param)
    {
        string[] fontNames =
        {
            "Arial", "Times New Roman", "Verdana", "SimSun", "Microsoft YaHei",
            "ZEBRA Swiss Unicode", "Calibri", "Helvetica", "Courier New", "SimHei", "KaiTi",
        };
        var best = "";
        int bestCount = 0;
        foreach (var f in fontNames)
        {
            var needle = Encoding.Unicode.GetBytes(f);
            int count = 0, at = 0;
            while ((at = IndexOf(param, needle, at)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            if (count > bestCount)
            {
                bestCount = count;
                best = f;
            }
        }
        return best;
    }

    private static bool ContainsAny(string s, params string[] keys)
        => keys.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static int IndexOf(byte[] data, byte[] pattern, int start)
    {
        for (int i = start; i <= data.Length - pattern.Length; i++)
        {
            int j = 0;
            while (j < pattern.Length && data[i + j] == pattern[j])
                j++;
            if (j == pattern.Length)
                return i;
        }
        return -1;
    }
}
