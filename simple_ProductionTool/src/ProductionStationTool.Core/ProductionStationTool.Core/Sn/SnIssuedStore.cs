using Microsoft.Data.Sqlite;

namespace ProductionStationTool.Core.Sn;

/// <summary>
/// SN 发放台账（汇总机侧，联机取号；库文件与测试记录同库 station_records.db）。
/// 取号规则（2026-11-12 用户规定）：
///   ① 当日流水号 0001~9999，两台工位机同时取号不撞号（服务端统一发号）；
///   ② 超过 9999 不再发放，报「今日 SN 已达上限」；
///   ③ 失败弃用的 SN（state=released）在 9999 用尽后可回收重发；
///   ④ 回收号重测通过由上层覆盖失败记录（test_records 按 SN+工位 upsert）。
/// </summary>
public sealed class SnIssuedStore : IDisposable
{
    /// <summary>发放成功结果。</summary>
    public sealed record SnIssueResult(string Sn, int Seq, string Day);

    private readonly SqliteConnection _conn;

    public SnIssuedStore(string dbPath = "station_records.db")
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS sn_issued (
                sn        TEXT    PRIMARY KEY,
                day       TEXT    NOT NULL,
                seq       INTEGER NOT NULL,
                station   INTEGER NOT NULL,
                machine   TEXT    NOT NULL,
                state     TEXT    NOT NULL,
                issued_at TEXT    NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>当日 SN 发放：优先未用流水号；9999 用尽后回收弃用号；全满抛 <see cref="SnExceededException"/>。</summary>
    public SnIssueResult Allocate(DateTime date, int station, string machine, string hwVersion = "01")
    {
        string day = date.ToString("yyMMdd");
        lock (_conn)
        {
            using var tx = _conn.BeginTransaction();
            var used = new Dictionary<int, string>();   // seq -> state
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT seq, state FROM sn_issued WHERE day = $d";
                cmd.Parameters.AddWithValue("$d", day);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                    used[rd.GetInt32(0)] = rd.GetString(1);
            }

            // ① 当日未用流水号
            int pick = -1;
            bool recycled = false;
            for (int seq = 1; seq <= 9999; seq++)
            {
                if (!used.ContainsKey(seq))
                {
                    pick = seq;
                    break;
                }
            }
            // ② 全部发过：回收弃用（released）号
            if (pick < 0)
            {
                foreach (var kv in used.OrderBy(kv => kv.Key))
                {
                    if (kv.Value == "released")
                    {
                        pick = kv.Key;
                        recycled = true;
                        break;
                    }
                }
            }
            if (pick < 0)
            {
                tx.Rollback();
                throw new SnExceededException("今日 SN 已达上限 9999（无弃用号可回收）");
            }

            string sn = SnGenerator.Generate(date, pick, hwVersion);
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                if (recycled)
                {
                    cmd.CommandText =
                        "UPDATE sn_issued SET station=$st, machine=$m, state='issued', issued_at=$t WHERE sn=$sn";
                }
                else
                {
                    cmd.CommandText =
                        "INSERT INTO sn_issued (sn, day, seq, station, machine, state, issued_at) " +
                        "VALUES ($sn, $d, $seq, $st, $m, 'issued', $t)";
                    cmd.Parameters.AddWithValue("$d", day);
                    cmd.Parameters.AddWithValue("$seq", pick);
                }
                cmd.Parameters.AddWithValue("$sn", sn);
                cmd.Parameters.AddWithValue("$st", station);
                cmd.Parameters.AddWithValue("$m", machine);
                cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return new SnIssueResult(sn, pick, day);
        }
    }

    /// <summary>标记弃用（可回收）：工位1 失败弃用 / 被新 SN 取代时调用。幂等。</summary>
    public void Release(string sn)
    {
        lock (_conn)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE sn_issued SET state = 'released' WHERE sn = $sn AND state = 'issued'";
            cmd.Parameters.AddWithValue("$sn", sn);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>查询 SN 发放状态；未发放返回 null。</summary>
    public (string Sn, string Day, int Seq, string State, string Machine)? Lookup(string sn)
    {
        lock (_conn)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT sn, day, seq, state, machine FROM sn_issued WHERE sn = $sn";
            cmd.Parameters.AddWithValue("$sn", sn);
            using var rd = cmd.ExecuteReader();
            if (!rd.Read())
                return null;
            return (rd.GetString(0), rd.GetString(1), rd.GetInt32(2), rd.GetString(3), rd.GetString(4));
        }
    }

    /// <summary>当日弃用（可回收）数量。</summary>
    public int CountReleased(DateTime date)
    {
        lock (_conn)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sn_issued WHERE day = $d AND state = 'released'";
            cmd.Parameters.AddWithValue("$d", date.ToString("yyMMdd"));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void Dispose() => _conn.Dispose();
}

/// <summary>当日 SN 已达上限（9999 用尽且无弃用号可回收）。</summary>
public sealed class SnExceededException : Exception
{
    public SnExceededException(string message) : base(message) { }
}
