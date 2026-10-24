namespace ProductionStationTool.Core.Data;

/// <summary>一条测试记录（工位 1：SN + 电流电压检测结论）。</summary>
public sealed class TestRecord
{
    public long Id { get; set; }
    public string Sn { get; set; } = "";
    public int StationNo { get; set; }
    public string Result { get; set; } = "";        // PASS / FAIL
    public double Voltage { get; set; }
    public double Current { get; set; }
    public string AbnormalItems { get; set; } = ""; // 异常项（分号分隔，空 = 正常）
    public DateTime TestTime { get; set; }
}

/// <summary>本地测试记录仓库（SQLite）。</summary>
public interface ITestRecordStore
{
    /// <summary>
    /// 追加一条记录。**只有 SN 写入设备成功后才应调用**——
    /// 构想要求：写入失败的 SN 不计入数据库。
    /// </summary>
    void Add(TestRecord record);

    /// <summary>按 SN 查（工位复检/追溯用）。</summary>
    TestRecord? FindBySn(string sn);

    /// <summary>取最近 n 条。</summary>
    IReadOnlyList<TestRecord> Recent(int n);
}
