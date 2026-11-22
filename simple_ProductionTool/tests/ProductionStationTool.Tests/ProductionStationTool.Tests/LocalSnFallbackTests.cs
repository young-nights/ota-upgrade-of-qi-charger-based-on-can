using ProductionStationTool.Core.Sn;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.Tests;

/// <summary>汇总机不可达时的本机应急取号：离线号段、按机位分槽、跨重启/跨日计数。</summary>
public class LocalSnFallbackTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"sn_offline_{Guid.NewGuid():N}.json");

    [Fact]
    public void Next_SeparatesMachinesAndSurvivesRestart()
    {
        var path = TempPath();
        var date = new DateTime(2026, 11, 21);
        try
        {
            var a1 = LocalSnFallback.Next(new StationIdentity(1, "1"), date, path);
            var a2 = LocalSnFallback.Next(new StationIdentity(1, "1"), date, path);
            var b1 = LocalSnFallback.Next(new StationIdentity(1, "2"), date, path);

            Assert.True(a1.Ok);
            Assert.True(a2.Ok);
            Assert.True(b1.Ok);
            Assert.NotEqual(a1.Seq, a2.Seq);      // 同机位持久递增
            Assert.NotEqual(a1.Seq, b1.Seq);      // 不同机位不同槽
            Assert.True(SnGenerator.TryParse(a1.Sn!, out _));
            Assert.Equal(9000, a1.Seq);
            Assert.Equal(9025, b1.Seq);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Next_CapsPerMachinePerDay_AndResetsNextDay()
    {
        var path = TempPath();
        var date = new DateTime(2026, 11, 21);
        try
        {
            for (var i = 0; i < LocalSnFallback.PerSlotPerDay; i++)
                Assert.True(LocalSnFallback.Next(new StationIdentity(1, "1"), date, path).Ok);

            var over = LocalSnFallback.Next(new StationIdentity(1, "1"), date, path);
            Assert.False(over.Ok);
            Assert.Equal("OFFLINE_CAP", over.Error);

            var nextDay = LocalSnFallback.Next(new StationIdentity(1, "1"), date.AddDays(1), path);
            Assert.True(nextDay.Ok);
            Assert.Equal(9000, nextDay.Seq);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Next_CorruptFile_FailsWithoutOverwriting()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "not-json");
            var r = LocalSnFallback.Next(new StationIdentity(1, "1"), new DateTime(2026, 11, 21), path);
            Assert.False(r.Ok);
            Assert.Equal("OFFLINE_IO", r.Error);
            Assert.Equal("not-json", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
