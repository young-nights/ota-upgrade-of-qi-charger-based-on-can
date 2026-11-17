using System.IO.Compression;
using System.Text;

namespace ProductionStationTool.Core.Printing;

/// <summary>.btw 导入结果（尽力提取；坐标/尺寸为二进制暂不可解析）。</summary>
public sealed record BtwImportResult(
    byte[]? PreviewPng,
    string? PrinterName,
    IReadOnlyList<(string Content, string Font)> Texts,
    string Note);

/// <summary>
/// .btw 参考导入：不解模板坐标，但可提取——
///   ① 内嵌 PNG 标签预览（无需 BarTender）；
///   ② 参数 zlib 流里的 UTF-16 文本对象内容与字体；
///   ③ 目标打印机名（顺带推断指令语言：含 Zebra/ZPL → ZPL，否则 TSPL）。
/// 结构依据（SN码标签打印预设.btw 实证）：文件 = 文本头 + zlib 压缩段，
/// 参数段内记录形如 FF FE FF &lt;u16 长度&gt; &lt;UTF-16LE 字符&gt;。
/// </summary>
public static class BtwImport
{
    private static readonly byte[] PngMagic = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>解析 .btw，返回预览图/打印机/文本行。</summary>
    public static BtwImportResult Extract(string btwPath)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(btwPath);
        }
        catch (Exception ex)
        {
            return new BtwImportResult(null, null, Array.Empty<(string, string)>(), $"读取失败：{ex.Message}");
        }

        var png = ExtractPng(data);

        // 取含 BarTender 标识的参数 zlib 流
        byte[]? param = null;
        foreach (var raw in InflateStreams(data))
        {
            // 参数流判据 = 解压后含 UTF-16 "BarTender"（不按长度过滤）
            if (IndexOf(raw, Encoding.Unicode.GetBytes("BarTender"), 0) >= 0)
            {
                param = raw;
                break;
            }
        }
        if (param is null)
            return new BtwImportResult(png, null, Array.Empty<(string, string)>(),
                "已提取预览图；未找到参数流（文件版本可能不同），无法导入文本/打印机");

        var records = ParseRecords(param);
        string? printer = records.FirstOrDefault(r => ContainsAny(r, "ZDesigner", "Zebra", "ZPL", "TSPL", "TSC", "Toshiba", "SATO", "Honeywell", "Datamax", "Godex"));
        var texts = ExtractTexts(records, out int skipped, param);

        var note = $"已导入文本 {texts.Count} 行"
            + (printer is not null ? $"、打印机「{printer}」" : "")
            + $"（另跳过模板样板文本 {skipped} 条）；"
            + "坐标/字号为二进制格式暂不可解析——请在效果预览里手动摆位";
        return new BtwImportResult(png, printer, texts, note);
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

    // ---- 参数流记录：FF FE FF <u16 长> <UTF-16LE> ----

    private static List<string> ParseRecords(byte[] param)
    {
        var list = new List<string>();
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
                list.Add(text);
            i = start + len * 2 - 1;
        }
        return list;
    }

    /// <summary>
    /// 只留真正的标签文本。参数流记录结构（实证）：
    ///   显示文本：[内容] [方框 1] [文本 N / 条形码 N]   → 留（名称 = 对象名）
    ///   数据源值：[值] [值] [s01…s07 等数据源名]       → 弃（SN 段样值）
    ///   其余为 BarTender 中英文界面样板 → 弃。
    /// </summary>
    private static List<(string Content, string Font)> ExtractTexts(List<string> records, out int skipped, byte[]? param)
    {
        var uiNoise = new[]
        {
            "1", ".", ",", ":", "/", "_", "CN", "2052", "1...", "bmp", "Picture.bmp",
            "DbMultiDbStatusPage", "Db Status Page", "Db Table Status Page", "Db Field Status Page",
            "Enter Data", "Sample Text", "Functions and Subs", "OnProcessData", "OnPostSerialize",
            "Data Source", "DataSource", "TextTransforms", "Dialog Control", "Line Control",
            "Picture", "RFID", "Print Quantity", "FormatID", "Format1", "PrinterCopies",
            "SerializedCount", "BatchCount", "User defined", "BarTender", "idbtlf",
            "使用打印机设置", "输入数据", "样本文本", "数据输入表单", "RFID 天线 1",
            "背景磁条 1", "背景色 1", "背景图 1", "背景 1", "层 1", "格式 1", "模板 1",
            "按钮控件 1", "按钮控件 2", "按钮控件 3", "分隔符行 1", "副本 1", "序列号 1",
            "副本:", "序列号:", "Box 1", "Text 1",
        };

        bool IsUiNoise(string r)
            => uiNoise.Any(n => string.Equals(r, n, StringComparison.OrdinalIgnoreCase))
               || r.StartsWith("Root.", StringComparison.Ordinal)
               || r.StartsWith("Db ", StringComparison.Ordinal)
               || r.StartsWith("'")
               || r.StartsWith("s0") && r.Length <= 3
               || r.Contains("___") || r.Contains("000-0000")
               || r.Contains("0123456789")
               || r.StartsWith("Printer Templates") || r.EndsWith(".prn")
               || System.Text.RegularExpressions.Regex.IsMatch(r, @"^(文本|方框|条形码|模板|背景|按钮控件|分隔符行|副本|序列号) ?\d*$")
               || ContainsAny(r, "ZDesigner", "Zebra", "ZPL", "TSPL", "USB00", "BackgroundPicture",
                              "PredefinedStocks", "Status Page", "l Dlg", "数据源");

        var texts = new List<(string, string)>();
        skipped = 0;
        for (int i = 0; i < records.Count; i++)
        {
            string r = records[i];
            if (IsUiNoise(r))
            {
                skipped++;
                continue;
            }

            // 结构判定：[内容][方框 1][文本 N] = 显示文本；[值][值][s0x] = 数据源样值
            string name = "";
            bool looksLikeValue = i + 2 < records.Count
                && string.Equals(records[i + 1], records[i], StringComparison.Ordinal)
                && records[i + 2].StartsWith("s0", StringComparison.Ordinal);
            if (looksLikeValue)
            {
                skipped += 2;
                i += 2;
                continue;
            }
            if (i + 2 < records.Count
                && records[i + 2].StartsWith("文本", StringComparison.Ordinal))
                name = records[i + 2].Replace("文本", "Text");
            else if (i + 2 < records.Count
                && records[i + 2].StartsWith("条形码", StringComparison.Ordinal))
                name = records[i + 2].Replace("条形码", "Barcode");

            texts.Add((r, name));
        }

        // 字体名不在记录里（定长字段），扫原始流取出现最多的非界面字体，统一套用
        string font = GuessCommonFont(param);
        return texts.Select(t => (t.Item1, font)).ToList();
    }

    /// <summary>扫参数流找出现最多的已知字体名（Tahoma 为界面字体，跳过）。</summary>
    private static string GuessCommonFont(byte[]? param)
    {
        if (param is null)
            return "";
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
