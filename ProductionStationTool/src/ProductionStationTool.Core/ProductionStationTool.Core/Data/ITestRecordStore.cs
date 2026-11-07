namespace ProductionStationTool.Core.Data;

/// <summary>一条测试记录（SN + 工位维度：同 SN 各工位各一条）。</summary>
public sealed class TestRecord
{
    public long Id { get; set; }
    public string Sn { get; set; } = "";
    public int StationNo { get; set; }
    /// <summary>机位号（同工位多机位时区分来源，如 "1"/"3"）。</summary>
    public string MachineId { get; set; } = "";
    public string Result { get; set; } = "";        // PASS / FAIL
    public double Voltage { get; set; }
    public double Current { get; set; }
    public string AbnormalItems { get; set; } = ""; // 异常项（分号分隔，空 = 正常）
    public DateTime TestTime { get; set; }
}

/// <summary>
/// 组合筛选条件（汇总机「数据库表格」页）：null/空 = 该项不限。
/// </summary>
public sealed record TestRecordFilter(
    DateTime? From = null,
    DateTime? To = null,
    int? StationNo = null,
    string? MachineId = null,
    string? Result = null,
    int Limit = 1000);

/// <summary>一个机位（工位号+机位号）的测试统计（汇总机「工位机接入管理」页）。</summary>
public sealed record MachineTestStats(
    int StationNo,
    string MachineId,
    int Total,
    int Pass,
    int Fail,
    DateTime? LastTestTime);

/// <summary>本地测试记录仓库（SQLite）。</summary>
public interface ITestRecordStore
{
    /// <summary>
    /// 追加一条记录（同 SN 同工位覆盖更新，取最新结果）。
    /// **只有 SN 写入/读取设备成功后才应调用**——构想要求：失败的 SN 不计入数据库。
    /// </summary>
    void Add(TestRecord record);

    /// <summary>按 SN 查全部工位记录（工位3 关联历史 / 工位4 出货门禁），按工位号升序。</summary>
    IReadOnlyList<TestRecord> FindAllBySn(string sn);

    /// <summary>取最近 n 条。</summary>
    IReadOnlyList<TestRecord> Recent(int n);

    /// <summary>组合筛选（时间/工位/机位/结果），按 id 降序。</summary>
    IReadOnlyList<TestRecord> Query(TestRecordFilter filter);

    /// <summary>库里出现过的（工位号, 机位号）组合（筛选下拉用），按工位号升序。</summary>
    IReadOnlyList<(int StationNo, string MachineId)> DistinctSources();

    /// <summary>按机位聚合的测试统计（总数/PASS/FAIL/最近测试时间）。</summary>
    IReadOnlyList<MachineTestStats> StatsByMachine();
}
