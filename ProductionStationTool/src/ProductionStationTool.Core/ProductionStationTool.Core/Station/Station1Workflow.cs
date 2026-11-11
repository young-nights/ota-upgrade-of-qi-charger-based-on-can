using ProductionStationTool.Core.Data;
using ProductionStationTool.Core.Sn;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.Core.Station;

/// <summary>工位 1 流程执行结果。</summary>
public sealed record Station1Result(
    string Sn,
    bool Passed,
    CoilJudgement? Coil,
    IReadOnlyList<string> AbnormalItems);

/// <summary>
/// 工位 1 流程编排（构想文档）：
///   生成 SN → 写入 SN → 回读验证 → 开充电 → PowerZ A/B 线圈检测 → 入库
///
/// 关键约定：
///  - SN 写入失败 → **不入库**，流水号不消耗（下次生成仍用同一序号）；
///  - 任何异常项 → 页面显示异常原因；全正常才 PASS；
///  - 步骤 5 双线圈检测见 <see cref="CoilCheck"/>：A 线圈 10s 均值 ≥ 1A、
///    换线圈跌落（正常不判失败）、B 线圈恢复 ≥ 1A 并持续；整体超时判 FAIL。
/// </summary>
public sealed class Station1Workflow
{
    private readonly ITestRecordStore _store;
    private readonly Func<DateTime, int, string> _snGenerate; // 可注入以便测试
    private int _nextSequence;

    /// <summary>本机身份（工位号 + 机位号），入库记录按此标记来源。</summary>
    public StationIdentity Identity { get; }

    public Station1Workflow(
        ITestRecordStore store,
        int startSequence = 1,
        Func<DateTime, int, string>? snGenerate = null,
        StationIdentity? identity = null)
    {
        _store = store;
        _nextSequence = startSequence;
        _snGenerate = snGenerate ?? ((d, s) => SnGenerator.Generate(d, s));
        Identity = identity ?? new StationIdentity(1, "1");
    }

    /// <summary>执行一次工位 1 测试。snWrite/snRead/device 由上位机 CAN 层提供。</summary>
    public async Task<Station1Result> RunAsync(
        Func<string, Task<bool>> snWriteAsync,      // 写 SN，返回是否成功
        Func<Task<string?>> snReadAsync,            // 回读 SN
        Func<Task> chargeStartAsync,                // 开启无线充电
        IPowerZSource powerZ,
        CoilTestOptions coils,
        Action<string>? log = null,
        CancellationToken ct = default,
        Action<CoilProgress>? onProgress = null)
    {
        // 1. 生成 SN
        var sn = _snGenerate(DateTime.Now, _nextSequence);

        // 2. 写 SN + 回读验证；失败不入库、不消耗流水号
        if (!await snWriteAsync(sn))
            return new Station1Result(sn, false, null,
                new[] { "SN 写入失败" });

        var readBack = await snReadAsync();
        if (!string.Equals(readBack, sn, StringComparison.Ordinal))
            return new Station1Result(sn, false, null,
                new[] { $"SN 回读不一致：写入 {sn}，读回 {readBack ?? "(空)"}" });

        // 3. 开充电 + PowerZ 双线圈检测
        //    A 线圈 10s 均值 ≥ 阈值 → 跌落过渡（正常不判失败）→ B 线圈恢复并持续；整体限时
        await chargeStartAsync();
        var coil = await CoilCheck.RunAsync(powerZ, coils, log, ct, onProgress);

        // 4. 无有效采样 = 流程错误：停止，不判限、不入库
        if (coil.NoValidSample)
            return new Station1Result(sn, false, coil,
                new[] { "PowerZ 无有效采样" });

        // 5. 入库（SN 写入成功 + 有采样才走到这里；线圈判 FAIL 也入库为 FAIL 记录）
        var passed = coil.IsNormal;
        _store.Add(new TestRecord
        {
            Sn = sn,
            StationNo = Identity.StationNo,
            MachineId = Identity.MachineId,
            Result = passed ? "PASS" : "FAIL",
            Voltage = coil.CoilAVoltage,
            Current = coil.CoilACurrent,
            AbnormalItems = string.Join("；", coil.AbnormalItems),
            TestTime = DateTime.Now,
        });
        _nextSequence++;

        return new Station1Result(sn, passed, coil, coil.AbnormalItems);
    }
}
