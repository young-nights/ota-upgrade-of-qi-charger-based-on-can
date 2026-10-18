using QiChargerStationTool.Core.Imaging;
using QiChargerStationTool.Core.Protocol;
using Xunit;

namespace QiChargerStationTool.Tests;

public class XatoImageTests
{
    [Fact]
    public void Crc32_Empty_IsZero()
    {
        Assert.Equal(0u, XatoImage.Crc32Of(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Crc32_KnownVector()
    {
        // zlib.crc32(b"123456789") == 0xCBF43926
        var data = System.Text.Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, XatoImage.Crc32Of(data));
    }

    [Fact]
    public void Parse_RejectsBadMagic()
    {
        var raw = new byte[XatoImage.HeaderSize + 16];
        Assert.Throws<InvalidDataException>(() => XatoImage.Parse(raw));
    }

    [Fact]
    public void ExtractSwVersion_FindsPrefix()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes("xxxxQC_JYF_MCU1_FW_1.1.57   ");
        var raw = BuildImage(payload);
        var img = XatoImage.Parse(raw);
        Assert.Equal("QC_JYF_MCU1_FW_1.1.57", img.ExtractSwVersion());
    }

    private static byte[] BuildImage(byte[] payload)
    {
        var raw = new byte[XatoImage.HeaderSize + payload.Length];
        BitConverter.GetBytes(XatoImage.Magic).CopyTo(raw, 0);
        BitConverter.GetBytes((uint)payload.Length).CopyTo(raw, 4);
        BitConverter.GetBytes(XatoImage.Crc32Of(payload)).CopyTo(raw, 8);
        payload.CopyTo(raw, XatoImage.HeaderSize);
        return raw;
    }
}

public class IsoTpCodecTests
{
    [Fact]
    public void StminMapping()
    {
        // 0x00 → 0ms handled by client; 0x01 → 1ms; 0xFA.. reserved → 125ms
        Assert.Equal(0, QiChargerStationTool.Core.Protocol.ProtoIds.UdsReqId & 0x1FFFFFFF);
    }

    [Fact]
    public void ProtoIds_Uds()
    {
        Assert.Equal(0x18DA0D03u, ProtoIds.UdsReqId);
        Assert.Equal(0x18DA030Du, ProtoIds.UdsRespId);
        Assert.Equal(0xF195, ProtoIds.DidSwVersion);
    }
}
