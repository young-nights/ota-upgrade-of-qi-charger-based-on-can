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
    public void ProtoIds_Uds()
    {
        Assert.Equal(0x18DA0D03u, ProtoIds.UdsReqId);
        Assert.Equal(0x18DA030Du, ProtoIds.UdsRespId);
        Assert.Equal(0xF195, ProtoIds.DidSwVersion);
    }

    [Fact]
    public void ProtoIds_QiDids()
    {
        Assert.Equal(0x2132, ProtoIds.DidQiIapStatus);
        Assert.Equal(0x2133, ProtoIds.DidQiFwVersion);
        Assert.Equal(0x210D, ProtoIds.DidPowerLimit);
        Assert.Equal(0x01, ProtoIds.Power5W);
        Assert.Equal(0x02, ProtoIds.Power10W);
        Assert.Equal(0x03, ProtoIds.Power15W);
    }
}

public class QiChipStatusTests
{
    [Fact]
    public void ParseIapStatus_FullPayload()
    {
        // state=0x01 progress=42 ver=1.2.3 sent=0x1234 total=0x0ABC
        byte[] d = { 0x01, 42, 1, 2, 3, 0x34, 0x12, 0xBC, 0x0A };
        QiChipStatusCodec.ParseIapStatus(d,
            out var state, out var progress,
            out var maj, out var min, out var pat,
            out var sent, out var total);
        Assert.Equal(0x01, state);
        Assert.Equal(42, progress);
        Assert.Equal(1, maj);
        Assert.Equal(2, min);
        Assert.Equal(3, pat);
        Assert.Equal(0x1234, sent);
        Assert.Equal(0x0ABC, total);
    }

    [Fact]
    public void ParseIapStatus_TooShort_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            QiChipStatusCodec.ParseIapStatus(new byte[] { 1, 2, 3 },
                out _, out _, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void ReadU8_ReadU16Le_FromDidData()
    {
        // ReadDid 已去掉 62 xx xx，这里只给数据区
        Assert.Equal(0x04, QiChipStatusCodec.ReadU8(new byte[] { 0x04 }));
        Assert.Equal(300, QiChipStatusCodec.ReadU16Le(new byte[] { 0x2C, 0x01 }));
    }

    [Fact]
    public void DidData_StripsRdbiHeader()
    {
        var data = QiChipStatusCodec.DidData(new byte[] { 0x62, 0x21, 0x02, 0x04 });
        Assert.Equal(1, data.Length);
        Assert.Equal(0x04, data[0]);
    }

    [Fact]
    public void PowerCodeFromWatts_MapsToQiChipCodes()
    {
        Assert.Equal(0x01, QiChipStatusCodec.PowerCodeFromWatts(5));
        Assert.Equal(0x02, QiChipStatusCodec.PowerCodeFromWatts(10));
        Assert.Equal(0x03, QiChipStatusCodec.PowerCodeFromWatts(15));
        Assert.Throws<ArgumentOutOfRangeException>(() => QiChipStatusCodec.PowerCodeFromWatts(7));
    }

    [Fact]
    public void PowerLimitName_KnownAndUnknown()
    {
        Assert.Equal("5W (0x01)", QiChipStatusCodec.PowerLimitName(0x01));
        Assert.Equal("10W (0x02)", QiChipStatusCodec.PowerLimitName(0x02));
        Assert.Equal("15W (0x03)", QiChipStatusCodec.PowerLimitName(0x03));
        Assert.StartsWith("未知", QiChipStatusCodec.PowerLimitName(0x77));
    }

    [Fact]
    public void ChargeStateName_Known()
    {
        Assert.Equal("充电中 (CHARGING)", QiChipStatusCodec.ChargeStateName(0x04));
        Assert.StartsWith("未知", QiChipStatusCodec.ChargeStateName(0xEE));
    }

    [Fact]
    public void ToDisplayString_IncludesKeyFields()
    {
        var st = new QiChipStatus
        {
            IapState = 0x02,
            IapProgress = 100,
            QiVerMajor = 1, QiVerMinor = 2, QiVerPatch = 3,
            IapSent = 10, IapTotal = 10,
            ChargeState = 0x04,
            OutputPowerMw = 1500,
            FaultCode = 0x00,
            PowerLimit = 0x03,
        };
        var text = st.ToDisplayString();
        Assert.Contains("成功 (SUCCESS)", text);
        Assert.Contains("1.2.3", text);
        Assert.Contains("充电中 (CHARGING)", text);
        Assert.Contains("1500 mW", text);
        Assert.Contains("15W (0x03)", text);
    }
}
