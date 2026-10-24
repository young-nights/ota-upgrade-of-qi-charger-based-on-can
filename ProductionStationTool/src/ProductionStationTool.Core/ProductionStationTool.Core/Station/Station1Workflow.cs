using ProductionStationTool.Core.Data;
using ProductionStationTool.Core.Flags;
using ProductionStationTool.Core.Sn;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.Core.Station;

/// <summary>工位 1 流程执行结果。</summary>
public sealed record Station1Result(
    string Sn,
    bool Passed,
    bool Skipped,               // 标志位已置 1，跳过流程直接 PASS
    PowerJudgement? Power,
    IReadOnlyList<string> AbnormalItems);

/// <summary>
/// 工位 1 流程编排（构想文档）：
///   查标志 → 生成 SN → 写入 SN → 回读验证 → 开充电 → PowerZ 采样判限 → 入库 → 置标志
///
/// 关键约定：
///  - 标志位 = Passed → 跳过，直接 PASS；
///  - SN 写入失败 → **不入库**，流水号不消耗（下次生成仍用同一序号）；
///  - 任何异常项 → 页面显示异常原因；全正常才 PASS。
/// </summary>
public sealed class Station1Workflow
{
    private readonly ITestFlagService _flags;
    private readonly ITestRecordStore _store;
    private readonly Func<DateTime, int, string> _snGenerate; // 可注入以便测试
    private int _nextSequence;

    public Station1Workflow(
        ITestFlagService flags,
        ITestRecordStore store,
        int startSequence = 1,
        Func<DateTime, int, string>? snGenerate = null)
    {
        _flags = flags;
        _store = store;
        _nextSequence = startSequence;
        _snGenerate = snGenerate ?? ((d, s) => SnGenerator.Generate(d, s));
    }

    /// <summary>执行一次工位 1 测试。snWrite/snRead/device 由上位机 CAN 层提供。</summary>
    public async Task<Station1Result> RunAsync(
        Func<string, Task<bool>> snWriteAsync,      // 写 SN，返回是否成功
        Func<Task<string?>> snReadAsync,            // 回读 SN
        Func<Task> chargeStartAsync,                // 开启无线充电
        IPowerZSource powerZ,
        PowerLimits limits,
        CancellationToken ct = default)
    {
        // 1. 标志位查询：已测成功 → 跳过
        var flag = await _flags.GetFlagAsync(StationId.Station1, ct);
        if (flag == TestFlag.Passed)
            return new Station1Result("", true, true, null, Array.Empty<string>());

        // 2. 生成 SN
        var sn = _snGenerate(DateTime.Now, _nextSequence);

        // 3. 写 SN + 回读验证；失败不入库、不消耗流水号
        if (!await snWriteAsync(sn))
            return new Station1Result(sn, false, false, null,
                new[] { "SN 写入失败" });

        var readBack = await snReadAsync();
        if (!string.Equals(readBack, sn, StringComparison.Ordinal))
            return new Station1Result(sn, false, false, null,
                new[] { $"SN 回读不一致：写入 {sn}，读回 {readBack ?? "(空)"}" });

        // 4. 开充电 + PowerZ 采样判限（等一个采样）
        await chargeStartAsync();
        var sample = await WaitSampleAsync(powerZ, ct);
        var fails = sample is null
            ? new List<string> { "PowerZ 无有效采样" }
            : limits.Judge(sample.Value.Voltage, sample.Value.Current);

        // 5. 结论
        var passed = fails.Count == 0;
        PowerJudgement? judgement = sample is null
            ? null
            : new PowerJudgement(sample.Value.Voltage, sample.Value.Current, passed, fails);

        // 6. 入库（SN 写入成功才会走到这里）→ 置标志
        _store.Add(new TestRecord
        {
            Sn = sn,
            StationNo = 1,
            Result = passed ? "PASS" : "FAIL",
            Voltage = sample?.Voltage ?? 0,
            Current = sample?.Current ?? 0,
            AbnormalItems = string.Join("；", fails),
            TestTime = DateTime.Now,
        });
        _nextSequence++;

        if (passed)
            await _flags.SetFlagAsync(StationId.Station1, TestFlag.Passed, ct);

        return new Station1Result(sn, passed, false, judgement, fails);
    }

    private static async Task<PowerSample?> WaitSampleAsync(IPowerZSource src, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PowerSample>();
        Action<PowerSample> handler = s => tcs.TrySetResult(s);
        src.SampleReceived += handler;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var reg = timeout.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        catch
        {
            return null;
        }
        finally
        {
            src.SampleReceived -= handler;
        }
    }
}
