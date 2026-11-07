using Microsoft.Data.Sqlite;

namespace ProductionStationTool.Core.Data;

/// <summary>
/// SQLite 实现。库文件默认放程序目录 station_records.db，可指定路径。
/// 表结构（v2）：
///   test_records(id, sn, station_no, machine_id, result, voltage, current,
///                abnormal_items, test_time, UNIQUE(sn, station_no))
/// 同一 SN 在每个工位各一条（多工位流转各自入库）；同工位重测覆盖更新取最新。
/// 旧库（sn 单列 UNIQUE）打开时自动迁移到 v2。
/// </summary>
public sealed class SqliteTestRecordStore : ITestRecordStore, IDisposable
{
    private readonly SqliteConnection _conn;

    public SqliteTestRecordStore(string dbPath = "station_records.db")
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS test_records (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    sn             TEXT    NOT NULL,
                    station_no     INTEGER NOT NULL,
                    machine_id     TEXT    NOT NULL DEFAULT '',
                    result         TEXT    NOT NULL,
                    voltage        REAL    NOT NULL,
                    current        REAL    NOT NULL,
                    abnormal_items TEXT    NOT NULL DEFAULT '',
                    test_time      TEXT    NOT NULL,
                    UNIQUE(sn, station_no)
                )
                """;
            cmd.ExecuteNonQuery();
        }

        // 旧库升级①：补 machine_id 列（列已存在则忽略）
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

        // 旧库升级②：sn 单列 UNIQUE → UNIQUE(sn, station_no)（同 SN 各工位各一条）
        if (HasUniqueIndexOnSnOnly())
            RebuildForMultiStation();
    }

    /// <summary>检测旧表结构：存在仅覆盖 sn 单列的 UNIQUE 约束（sqlite 自动索引）。</summary>
    private bool HasUniqueIndexOnSnOnly()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA index_list(test_records)";
        var uniqueIndexes = new List<string>();
        using (var rd = cmd.ExecuteReader())
        {
            while (rd.Read())
            {
                // index_list 列：seq, name, unique, origin, partial
                if (rd.GetInt64(2) == 1)
                    uniqueIndexes.Add(rd.GetString(1));
            }
        }
        foreach (var idx in uniqueIndexes)
        {
            // PRAGMA 不接受绑定参数；索引名来自本库 index_list，双引号转义后拼接
            using var colCmd = _conn.CreateCommand();
            colCmd.CommandText = "PRAGMA index_info(\"" + idx.Replace("\"", "\"\"") + "\")";
            var cols = new List<string>();
            using var rd = colCmd.ExecuteReader();
            while (rd.Read())
                cols.Add(rd.GetString(2));
            if (cols.Count == 1 && cols[0].Equals("sn", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>重建表为 v2 结构并搬数据（旧库 sn 单列 UNIQUE → UNIQUE(sn, station_no)）。</summary>
    private void RebuildForMultiStation()
    {
        using var tx = _conn.BeginTransaction();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                ALTER TABLE test_records RENAME TO test_records_old;
                CREATE TABLE test_records (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    sn             TEXT    NOT NULL,
                    station_no     INTEGER NOT NULL,
                    machine_id     TEXT    NOT NULL DEFAULT '',
                    result         TEXT    NOT NULL,
                    voltage        REAL    NOT NULL,
                    current        REAL    NOT NULL,
                    abnormal_items TEXT    NOT NULL DEFAULT '',
                    test_time      TEXT    NOT NULL,
                    UNIQUE(sn, station_no)
                );
                INSERT INTO test_records (id, sn, station_no, machine_id, result, voltage,
                                         current, abnormal_items, test_time)
                    SELECT id, sn, station_no, machine_id, result, voltage,
                           current, abnormal_items, test_time FROM test_records_old;
                DROP TABLE test_records_old;
                """;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Add(TestRecord r)
    {
        using var cmd = _conn.CreateCommand();
        // 同 SN 同工位重测：覆盖更新取最新结果（UNIQUE(sn, station_no)）
        cmd.CommandText =
            """
            INSERT INTO test_records (sn, station_no, machine_id, result, voltage, current, abnormal_items, test_time)
            VALUES ($sn, $st, $mid, $res, $v, $i, $ab, $t)
            ON CONFLICT(sn, station_no) DO UPDATE SET
                machine_id     = excluded.machine_id,
                result         = excluded.result,
                voltage        = excluded.voltage,
                current        = excluded.current,
                abnormal_items = excluded.abnormal_items,
                test_time      = excluded.test_time
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

    public IReadOnlyList<TestRecord> FindAllBySn(string sn)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, sn, station_no, machine_id, result, voltage, current, abnormal_items, test_time " +
            "FROM test_records WHERE sn = $sn ORDER BY station_no";
        cmd.Parameters.AddWithValue("$sn", sn);
        return ReadAll(cmd);
    }

    public IReadOnlyList<TestRecord> Recent(int n)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, sn, station_no, machine_id, result, voltage, current, abnormal_items, test_time " +
            "FROM test_records ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", n);
        return ReadAll(cmd);
    }

    public IReadOnlyList<TestRecord> Query(TestRecordFilter f)
    {
        using var cmd = _conn.CreateCommand();
        var sql =
            "SELECT id, sn, station_no, machine_id, result, voltage, current, abnormal_items, test_time " +
            "FROM test_records WHERE 1=1";
        if (f.From is { } from)
        {
            sql += " AND test_time >= $from";
            cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        if (f.To is { } to)
        {
            sql += " AND test_time <= $to";
            cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        if (f.StationNo is { } st)
        {
            sql += " AND station_no = $st";
            cmd.Parameters.AddWithValue("$st", st);
        }
        if (!string.IsNullOrEmpty(f.MachineId))
        {
            sql += " AND machine_id = $mid";
            cmd.Parameters.AddWithValue("$mid", f.MachineId);
        }
        if (!string.IsNullOrEmpty(f.Result))
        {
            sql += " AND result = $res";
            cmd.Parameters.AddWithValue("$res", f.Result);
        }
        sql += " ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", f.Limit > 0 ? f.Limit : 1000);
        cmd.CommandText = sql;
        return ReadAll(cmd);
    }

    public IReadOnlyList<(int StationNo, string MachineId)> DistinctSources()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT DISTINCT station_no, machine_id FROM test_records ORDER BY station_no, machine_id";
        using var rd = cmd.ExecuteReader();
        var list = new List<(int, string)>();
        while (rd.Read())
            list.Add((rd.GetInt32(0), rd.GetString(1)));
        return list;
    }

    public IReadOnlyList<MachineTestStats> StatsByMachine()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT station_no, machine_id, COUNT(*),
                   SUM(CASE WHEN result = 'PASS' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN result = 'FAIL' THEN 1 ELSE 0 END),
                   MAX(test_time)
            FROM test_records GROUP BY station_no, machine_id
            ORDER BY station_no, machine_id
            """;
        using var rd = cmd.ExecuteReader();
        var list = new List<MachineTestStats>();
        while (rd.Read())
        {
            DateTime? last = rd.IsDBNull(5) ? null : DateTime.Parse(rd.GetString(5));
            list.Add(new MachineTestStats(
                rd.GetInt32(0), rd.GetString(1),
                rd.GetInt32(2), rd.GetInt32(3), rd.GetInt32(4), last));
        }
        return list;
    }

    private static List<TestRecord> ReadAll(SqliteCommand cmd)
    {
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
