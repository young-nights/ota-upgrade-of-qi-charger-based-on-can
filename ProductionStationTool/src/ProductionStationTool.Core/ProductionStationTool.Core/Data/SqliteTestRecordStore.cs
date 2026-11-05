using Microsoft.Data.Sqlite;

namespace ProductionStationTool.Core.Data;

/// <summary>
/// SQLite 实现。库文件默认放程序目录 station_records.db，可指定路径。
/// 表结构：
///   test_records(id, sn UNIQUE, station_no, result, voltage, current,
///                abnormal_items, test_time)
/// sn 唯一约束天然挡住重复入库。
/// </summary>
public sealed class SqliteTestRecordStore : ITestRecordStore, IDisposable
{
    private readonly SqliteConnection _conn;

    public SqliteTestRecordStore(string dbPath = "station_records.db")
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS test_records (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                sn             TEXT    NOT NULL UNIQUE,
                station_no     INTEGER NOT NULL,
                machine_id     TEXT    NOT NULL DEFAULT '',
                result         TEXT    NOT NULL,
                voltage        REAL    NOT NULL,
                current        REAL    NOT NULL,
                abnormal_items TEXT    NOT NULL DEFAULT '',
                test_time      TEXT    NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();

        // 旧库升级：补 machine_id 列（列已存在则忽略）
        try
        {
            using var mig = _conn.CreateCommand();
            mig.CommandText =
                "ALTER TABLE test_records ADD COLUMN machine_id TEXT NOT NULL DEFAULT ''";
            mig.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
    }

    public void Add(TestRecord r)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO test_records (sn, station_no, machine_id, result, voltage, current, abnormal_items, test_time)
            VALUES ($sn, $st, $mid, $res, $v, $i, $ab, $t)
            """;
        cmd.Parameters.AddWithValue("$sn", r.Sn);
        cmd.Parameters.AddWithValue("$st", r.StationNo);
        cmd.Parameters.AddWithValue("$mid", r.MachineId);
        cmd.Parameters.AddWithValue("$res", r.Result);
        cmd.Parameters.AddWithValue("$v", r.Voltage);
        cmd.Parameters.AddWithValue("$i", r.Current);
        cmd.Parameters.AddWithValue("$ab", r.AbnormalItems);
        cmd.Parameters.AddWithValue("$t", r.TestTime.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    public TestRecord? FindBySn(string sn)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, sn, station_no, machine_id, result, voltage, current, abnormal_items, test_time " +
            "FROM test_records WHERE sn = $sn";
        cmd.Parameters.AddWithValue("$sn", sn);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? ReadRecord(rd) : null;
    }

    public IReadOnlyList<TestRecord> Recent(int n)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, sn, station_no, machine_id, result, voltage, current, abnormal_items, test_time " +
            "FROM test_records ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", n);
        using var rd = cmd.ExecuteReader();
        var list = new List<TestRecord>();
        while (rd.Read())
            list.Add(ReadRecord(rd));
        return list;
    }

    private static TestRecord ReadRecord(SqliteDataReader rd) => new()
    {
        Id = rd.GetInt64(0),
        Sn = rd.GetString(1),
        StationNo = rd.GetInt32(2),
        MachineId = rd.GetString(3),
        Result = rd.GetString(4),
        Voltage = rd.GetDouble(5),
        Current = rd.GetDouble(6),
        AbnormalItems = rd.GetString(7),
        TestTime = DateTime.Parse(rd.GetString(8)),
    };

    public void Dispose() => _conn.Dispose();
}
