namespace ProductionStationTool.Core.Printing;

/// <summary>
/// .btw 模板预览提取：BarTender 保存 .btw 时在文件内嵌一张 PNG 标签缩略图，
/// 直接按 PNG 块结构提取（同 Material_Label_Print/app/template_preview.py 实证），
/// **无需安装 BarTender / 无需授权**。取体积最大的一张作为「模板样式预览」。
/// </summary>
public static class BtwPreview
{
    private static readonly byte[] PngMagic = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>提取 .btw 内嵌预览 PNG（最大一张）；无内嵌图返回 null。</summary>
    public static byte[]? ExtractPng(string btwPath)
    {
        try
        {
            return ExtractPng(File.ReadAllBytes(btwPath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从字节流提取内嵌 PNG（最大一张）；无则 null。</summary>
    public static byte[]? ExtractPng(byte[] data)
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

    /// <summary>按 PNG 块结构（长4+类型4+数据+CRC4）从签名处走到 IEND 之后；非法返回 -1。</summary>
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
