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
    public void MagnetName_And_ChargingVerdict_MatchHallGate()
    {
        // DID 0x2118 实测极性：0x00=磁铁存在（未充电），0x01=磁铁不存在
        Assert.Equal("磁铁存在", QiChipStatusCodec.MagnetName(0x00));
        Assert.Equal("磁铁不存在", QiChipStatusCodec.MagnetName(0x01));
        Assert.Equal("读取失败", QiChipStatusCodec.MagnetName(null));

        // 充电显示六态（2026-11-12 用户定义）：失能→充满→磁铁存在→充电中→ping→未充电
        Assert.Equal("磁铁不存在·充电中",
            QiChipStatusCodec.ChargingVerdict(true, 0x01, true));
        Assert.Equal("磁铁不存在·未充电",
            QiChipStatusCodec.ChargingVerdict(true, 0x01, false));
        Assert.Equal("磁铁存在·未充电",
            QiChipStatusCodec.ChargingVerdict(true, 0x00, true));
        Assert.Equal("充电失能",
            QiChipStatusCodec.ChargingVerdict(false, 0x01, true));
        Assert.Equal("需开qi检测",
            QiChipStatusCodec.ChargingVerdict(true, null, true));

        // 状态字节位：b2=充满参与轮转；b0=ping（设备放置）独立、不抢占状态
        Assert.Equal("磁铁不存在·未充电",
            QiChipStatusCodec.ChargingVerdict(true, 0x01, false, QiChipStatusCodec.QiStatusPing));
        Assert.Equal("电已充满",
            QiChipStatusCodec.ChargingVerdict(true, 0x01, false, QiChipStatusCodec.QiStatusFull));
        // 优先级：充满>充电中；充满>磁铁存在
        Assert.Equal("电已充满",
            QiChipStatusCodec.ChargingVerdict(true, 0x01, true,
                (byte)(QiChipStatusCodec.QiStatusFull | QiChipStatusCodec.QiStatusCharging)));
        Assert.Equal("电已充满",
            QiChipStatusCodec.ChargingVerdict(true, 0x00, false, QiChipStatusCodec.QiStatusFull));
        Assert.Equal("磁铁不存在·充电中",
            QiChipStatusCodec.ChargingVerdict(true, 0x01, false,
                (byte)(QiChipStatusCodec.QiStatusPing | QiChipStatusCodec.QiStatusCharging)));

        // 设备放置独立指示（b0）：1=已放置 / 0=未放置 / null=未读到
        Assert.True(QiChipStatusCodec.DevicePlaced(QiChipStatusCodec.QiStatusPing));
        Assert.False(QiChipStatusCodec.DevicePlaced((byte)0x00));
        Assert.Null(QiChipStatusCodec.DevicePlaced(null));

        // 派生常量闭环（与固件 DID 0x2117 编码一致）
        Assert.Equal("电已充满", QiChipStatusCodec.DisplayStateName(QiChipStatusCodec.DisplayFull));
        Assert.Equal("磁铁不存在·未充电", QiChipStatusCodec.DisplayStateName(QiChipStatusCodec.DisplayAbsentIdle));
        Assert.Equal(QiChipStatusCodec.DisplayDisabled,
            QiChipStatusCodec.DeriveDisplayState(false, 0x01, QiChipStatusCodec.QiStatusFull, true));
    }

    [Fact]
    public void ChargingEvidence_UsesRawValues_NoConversion()
    {
        // 电流/功率用 Qi 传回原值判断（不换算单位）：任一非 0 或触发位=1 即有证据
        Assert.True(QiChipStatusCodec.ChargingEvidenceFrom(500, 20, 0x00));
        Assert.True(QiChipStatusCodec.ChargingEvidenceFrom(0, 0, 0x01));   // 触发位
        Assert.True(QiChipStatusCodec.ChargingEvidenceFrom(50, 0, 0x00));  // 功率抖动也算单点证据（去抖滤）
        Assert.False(QiChipStatusCodec.ChargingEvidenceFrom(0, 0, 0x00));
        Assert.False(QiChipStatusCodec.ChargingEvidenceFrom(null, null, null));
    }

    [Fact]
    public void DebounceFilter_FiltersJitter_BothDirections()
    {
        var f = new DebounceFilter(2000);
        // 空载小幅跳动：单点 true 不翻转
        Assert.False(f.Update(false, 0));
        Assert.False(f.Update(true, 1000));
        Assert.False(f.Update(false, 2000));
        // 持续证据 ~2s 才判充电中
        Assert.False(f.Update(true, 3000));
        Assert.False(f.Update(true, 4000));
        Assert.True(f.Update(true, 5000));
        // 充电中瞬时回落不翻转
        Assert.True(f.Update(false, 6000));
        Assert.True(f.Update(true, 7000));
        // 持续回落 ~2s 才关
        Assert.True(f.Update(false, 8000));
        Assert.True(f.Update(false, 9000));
        Assert.False(f.Update(false, 10000));
    }

    [Fact]
    public void DebounceFilter_FirstSampleTrue_NotAdoptedInstantly()
    {
        // 开检测首采样为真（Qi 上电瞬态/单点证据）不瞬时判充电中：
        // 初态未充电，持续证据 ≥2s 才翻转（2026-11-10 误显「充电中」修复）
        var f = new DebounceFilter(2000);
        Assert.False(f.Update(true, 0));     // 首采样真 → 仍未充电
        Assert.False(f.Update(true, 1000));  // 持续 1s → 仍未充电
        Assert.True(f.Update(true, 2000));   // 持续 2s → 充电中
        // 回落后 Reset 再开：首采样真同样不瞬时采纳
        f.Reset();
        Assert.False(f.Update(true, 3000));
        Assert.False(f.Update(true, 4000));
        Assert.True(f.Update(true, 5000));
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
    public void ActiveProtectionNames_None()
    {
        var names = QiChipStatusCodec.ActiveProtectionNames(0x00);
        Assert.Empty(names);
    }

    [Fact]
    public void ActiveProtectionNames_MultipleBits()
    {
        // OVP(b3) + FOD(b7) 同时置位
        var raw = (byte)(QiChipStatusCodec.QiStatusOvp | QiChipStatusCodec.QiStatusFod);
        var names = QiChipStatusCodec.ActiveProtectionNames(raw);
        Assert.Equal(2, names.Count);
        Assert.Contains("OVP 过压", names);
        Assert.Contains("FOD 异物", names);
    }

    [Fact]
    public void ActiveProtectionNames_AllBits()
    {
        var raw = (byte)(QiChipStatusCodec.QiStatusOvp | QiChipStatusCodec.QiStatusUvp |
                         QiChipStatusCodec.QiStatusOcp | QiChipStatusCodec.QiStatusOtp |
                         QiChipStatusCodec.QiStatusFod);
        Assert.Equal(5, QiChipStatusCodec.ActiveProtectionNames(raw).Count);
    }

    [Fact]
    public void ActiveProtectionNames_StateBitsIgnored()
    {
        // PING/CHARGING/FULL 状态位不算保护位
        var raw = (byte)(QiChipStatusCodec.QiStatusPing | QiChipStatusCodec.QiStatusCharging |
                         QiChipStatusCodec.QiStatusFull);
        Assert.Empty(QiChipStatusCodec.ActiveProtectionNames(raw));
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
