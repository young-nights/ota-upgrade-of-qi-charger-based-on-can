using Microsoft.Data.Sqlite;
using ProductionStationTool.Core.Data;

namespace ProductionStationTool.Tests;

/// <summary>SQLite 测试记录仓库：多工位入库、组合筛选、机位统计、旧库迁移。</summary>
public class SqliteTestRecordStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"pst_store_test_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + suffix;
            if (File.Exists(p))
                File.Delete(p);
        }
    }

    private static TestRecord Rec(string sn, int stationNo, string machineId = "1",
        string result = "PASS", DateTime? time = null) => new()
    {
        Sn = sn,
        StationNo = stationNo,
        MachineId = machineId,
        Result = result,
        Voltage = 5.0,
        Current = 1.2,
        AbnormalItems = result == "PASS" ? "" : "测试异常",
        TestTime = time ?? new DateTime(2026, 11, 7, 10, 0, 0),
    };

    [Fact]
    public void Add_SameSnSameStation_Overwrites()
    {
        using var store = new SqliteTestRecordStore(_dbPath);
        store.Add(Rec("SN-1", 1, result: "PASS"));
        store.Add(Rec("SN-1", 1, result: "FAIL"));

        var rows = store.FindAllBySn("SN-1");
        Assert.Single(rows);
        Assert.Equal("FAIL", rows[0].Result);
    }

    [Fact]
    public void Add_SameSnDifferentStations_Coexist()
    {
        using var store = new SqliteTestRecordStore(_dbPath);
        store.Add(Rec("SN-1", 3));
        store.Add(Rec("SN-1", 1));
        store.Add(Rec("SN-1", 2, result: "FAIL"));

        var rows = store.FindAllBySn("SN-1");
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.StationNo));   // 按工位号升序
    }

    [Fact]
    public void Query_FiltersByResultStationMachineTime()
    {
        using var store = new SqliteTestRecordStore(_dbPath);
        store.Add(Rec("A", 1, "1", "PASS", new DateTime(2026, 11, 1)));
        store.Add(Rec("B", 1, "2", "FAIL", new DateTime(2026, 11, 2)));
        store.Add(Rec("C", 3, "1", "FAIL", new DateTime(2026, 11, 3)));
        store.Add(Rec("D", 3, "1", "PASS", new DateTime(2026, 11, 4)));

        var fail = store.Query(new TestRecordFilter(Result: "FAIL"));
        Assert.Equal(2, fail.Count);

        var st3 = store.Query(new TestRecordFilter(StationNo: 3));
        Assert.Equal(2, st3.Count);
        Assert.All(st3, r => Assert.Equal(3, r.StationNo));

        var mid2 = store.Query(new TestRecordFilter(MachineId: "2"));
        Assert.Single(mid2);
        Assert.Equal("B", mid2[0].Sn);

        var ranged = store.Query(new TestRecordFilter(
            From: new DateTime(2026, 11, 2), To: new DateTime(2026, 11, 3, 23, 59, 59)));
        Assert.Equal(new[] { "C", "B" }, ranged.Select(r => r.Sn));   // id 降序
    }

    [Fact]
    public void DistinctSources_ReturnsStationMachinePairs()
    {
        using var store = new SqliteTestRecordStore(_dbPath);
        store.Add(Rec("A", 1, "1"));
        store.Add(Rec("B", 1, "2"));
        store.Add(Rec("C", 1, "1"));       // 重复组合
        store.Add(Rec("D", 3, "1"));

        var pairs = store.DistinctSources();
        Assert.Equal(3, pairs.Count);
        Assert.Equal((1, "1"), pairs[0]);
        Assert.Equal((1, "2"), pairs[1]);
        Assert.Equal((3, "1"), pairs[2]);
    }

    [Fact]
    public void StatsByMachine_AggregatesCounts()
    {
        using var store = new SqliteTestRecordStore(_dbPath);
        store.Add(Rec("A", 1, "1", "PASS", new DateTime(2026, 11, 1)));
        store.Add(Rec("B", 1, "1", "FAIL", new DateTime(2026, 11, 2)));
        store.Add(Rec("C", 1, "1", "PASS", new DateTime(2026, 11, 3)));
        store.Add(Rec("D", 3, "2", "PASS", new DateTime(2026, 11, 4)));

        var stats = store.StatsByMachine();
        Assert.Equal(2, stats.Count);
        var m11 = stats.First(s => s.StationNo == 1);
        Assert.Equal(3, m11.Total);
        Assert.Equal(2, m11.Pass);
        Assert.Equal(1, m11.Fail);
        Assert.Equal(new DateTime(2026, 11, 3), m11.LastTestTime);
        var m32 = stats.First(s => s.StationNo == 3);
        Assert.Equal(1, m32.Total);
    }

    [Fact]
    public void OpensOldSnUniqueSchema_MigratesToMultiStation()
    {
        // 旧库：sn 单列 UNIQUE（v1 结构），同 SN 只能一条
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE test_records (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    sn             TEXT    NOT NULL UNIQUE,
                    station_no     INTEGER NOT NULL,
                    result         TEXT    NOT NULL,
                    voltage        REAL    NOT NULL,
                    current        REAL    NOT NULL,
                    abnormal_items TEXT    NOT NULL DEFAULT '',
                    test_time      TEXT    NOT NULL
                );
                INSERT INTO test_records (sn, station_no, result, voltage, current, abnormal_items, test_time)
                VALUES ('SN-OLD', 1, 'PASS', 5.0, 1.2, '', '2026-11-01 09:00:00');
                """;
            cmd.ExecuteNonQuery();
        }

        using (var store = new SqliteTestRecordStore(_dbPath))
        {
            // 迁移后旧数据还在，且同 SN 可在其他工位入库
            store.Add(Rec("SN-OLD", 3, result: "FAIL"));
            var rows = store.FindAllBySn("SN-OLD");
            Assert.Equal(2, rows.Count);
            Assert.Equal("PASS", rows.First(r => r.StationNo == 1).Result);
            Assert.Equal("FAIL", rows.First(r => r.StationNo == 3).Result);
            Assert.Equal("", rows.First(r => r.StationNo == 1).MachineId); // 旧库无机位列
        }
    }
}
