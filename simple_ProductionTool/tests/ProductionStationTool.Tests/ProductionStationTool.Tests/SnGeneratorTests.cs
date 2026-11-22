using ProductionStationTool.Core.Sn;

namespace ProductionStationTool.Tests;

public class SnGeneratorTests
{
    [Fact]
    public void Generate_FormatMatchesSpec()
    {
        // LS + CH + 42 + JY + 01 + 260602 + 0001
        var sn = SnGenerator.Generate(new DateTime(2026, 6, 2), 1);
        Assert.Equal("LSCH42JY012606020001", sn);
    }

    [Fact]
    public void Generate_PadsSequenceTo4Digits()
    {
        var sn = SnGenerator.Generate(new DateTime(2026, 1, 15), 42);
        Assert.Equal("LSCH42JY012601150042", sn);
    }

    [Fact]
    public void Generate_SequenceOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SnGenerator.Generate(DateTime.Now, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SnGenerator.Generate(DateTime.Now, 10000));
    }

    [Theory]
    [InlineData("LSCH42JY012606020001", true)]
    [InlineData("XXCH42JY012606020001", false)]  // 前缀不对
    [InlineData("LSCH42JY01260602001", false)]   // 长度 19
    [InlineData("", false)]
    public void TryParse_ValidatesShape(string sn, bool expected)
    {
        Assert.Equal(expected, SnGenerator.TryParse(sn, out _));
    }

    [Fact]
    public void TryParse_RoundTrip()
    {
        var sn = SnGenerator.Generate(new DateTime(2026, 6, 2), 7);
        Assert.True(SnGenerator.TryParse(sn, out var f));
        Assert.Equal("LS", f.Product);
        Assert.Equal("CH", f.Category);
        Assert.Equal("42", f.ModelCode);
        Assert.Equal("JY", f.FactoryCode);
        Assert.Equal("01", f.HwVersion);
        Assert.Equal(260602, f.DateYymmdd);
        Assert.Equal(7, f.Sequence);
    }
}
