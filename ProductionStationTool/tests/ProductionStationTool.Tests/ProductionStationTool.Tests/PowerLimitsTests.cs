using ProductionStationTool.Core.Station;

namespace ProductionStationTool.Tests;

public class PowerLimitsTests
{
    private static readonly PowerLimits Limits = new()
    {
        VoltageMin = 4.75, VoltageMax = 5.25,
        CurrentMin = 0.05, CurrentMax = 3.0,
    };

    [Fact]
    public void InRange_NoFail()
    {
        Assert.Empty(Limits.Judge(5.0, 1.0));
    }

    [Theory]
    [InlineData(4.0, 1.0)]   // 电压过低
    [InlineData(6.0, 1.0)]   // 电压过高
    [InlineData(5.0, 0.01)]  // 电流过低
    [InlineData(5.0, 5.0)]   // 电流过高
    public void OutOfRange_ReportsFail(double v, double i)
    {
        var fails = Limits.Judge(v, i);
        Assert.NotEmpty(fails);
    }

    [Fact]
    public void BoundaryValues_AreAccepted()
    {
        Assert.Empty(Limits.Judge(4.75, 0.05));
        Assert.Empty(Limits.Judge(5.25, 3.0));
    }
}
