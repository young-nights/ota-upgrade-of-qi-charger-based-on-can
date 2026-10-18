using System.Buffers.Binary;

namespace QiChargerStationTool.Core.Imaging;

/// <summary>
/// XATO packed image (256-byte header + payload).
/// Layout matches pack_image.py / firmware ota_download.c:
///   0x00 magic 0x4F544158 "XATO"
///   0x04 payload length
///   0x08 crc32(payload)
///   0x0C signature 64B (r||s)
///   0x4C reserved 16B (zeros)
///   0x5C timestamp
/// </summary>
public sealed class XatoImage
{
    public const uint Magic = 0x4F544158;
    public const int HeaderSize = 256;

    public byte[] Raw { get; }
    public uint PayloadLength { get; }
    public uint Crc32 { get; }
    public byte[] Signature { get; }
    public uint Timestamp { get; }

    public ReadOnlySpan<byte> Payload => Raw.AsSpan(HeaderSize, (int)PayloadLength);

    private XatoImage(byte[] raw, uint payloadLen, uint crc, byte[] sig, uint ts)
    {
        Raw = raw;
        PayloadLength = payloadLen;
        Crc32 = crc;
        Signature = sig;
        Timestamp = ts;
    }

    public static XatoImage Load(string path)
    {
        var raw = File.ReadAllBytes(path);
        return Parse(raw);
    }

    public static XatoImage Parse(byte[] raw)
    {
        if (raw.Length < HeaderSize + 8)
            throw new InvalidDataException("image too short");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0));
        if (magic != Magic)
            throw new InvalidDataException($"missing XATO magic 0x{magic:X8}");
        uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
        if (HeaderSize + payloadLen != raw.Length)
            throw new InvalidDataException($"header length {payloadLen} != file {raw.Length - HeaderSize}");
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8));
        uint actual = Crc32Of(raw.AsSpan(HeaderSize));
        if (crc != actual)
            throw new InvalidDataException($"CRC mismatch header=0x{crc:X8} calc=0x{actual:X8}");
        var sig = raw.AsSpan(0x0C, 64).ToArray();
        uint ts = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x5C));
        return new XatoImage(raw, payloadLen, crc, sig, ts);
    }

    /// <summary>Extract QC_JYF_MCU1_FW_x.y.z from payload strings (like pack_image.py).</summary>
    public string? ExtractSwVersion()
    {
        var span = Payload;
        var needle = System.Text.Encoding.ASCII.GetBytes("QC_JYF_MCU1_FW_");
        for (int i = 0; i + needle.Length < span.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
                if (span[i + j] != needle[j]) { hit = false; break; }
            if (!hit) continue;
            int end = i;
            while (end < span.Length && span[end] >= 0x20 && span[end] < 0x7F) end++;
            return System.Text.Encoding.ASCII.GetString(span.Slice(i, end - i).ToArray()).TrimEnd();
        }
        return null;
    }

    /// <summary>CRC-32 (IEEE) as in Python zlib.crc32 / firmware.</summary>
    public static uint Crc32Of(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
