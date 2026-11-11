using ProductionStationTool.Core.Station;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.Tests;

/// <summary>工位 1 流程行为测试（构想文档关键约定 + 步骤 5 双线圈检测）。</summary>
public class Station1WorkflowTests
{
    private sealed class FakeStore : Core.Data.ITestRecordStore
    {
        public readonly List<Core.Data.TestRecord> Rows = new();

        // 与 SQLite 实现同语义：同 SN 同工位覆盖更新取最新
        public void Add(Core.Data.TestRecord r)
        {
            var old = Rows.FirstOrDefault(x => x.Sn == r.Sn && x.StationNo == r.StationNo);
            if (old is not null)
                Rows.Remove(old);
            Rows.Add(r);
        }

        public IReadOnlyList<Core.Data.TestRecord> FindAllBySn(string sn)
            => Rows.Where(x => x.Sn == sn).OrderBy(x => x.StationNo).ToList();

        public IReadOnlyList<Core.Data.TestRecord> Recent(int n) => Rows.TakeLast(n).ToList();

        public IReadOnlyList<Core.Data.TestRecord> Query(Core.Data.TestRecordFilter f)
        {
            IEnumerable<Core.Data.TestRecord> q = Rows;
            if (f.From is { } from) q = q.Where(x => x.TestTime >= from);
            if (f.To is { } to) q = q.Where(x => x.TestTime <= to);
            if (f.StationNo is { } st) q = q.Where(x => x.StationNo == st);
            if (!string.IsNullOrEmpty(f.MachineId)) q = q.Where(x => x.MachineId == f.MachineId);
            if (!string.IsNullOrEmpty(f.Result)) q = q.Where(x => x.Result == f.Result);
            return q.OrderByDescending(x => x.Id).Take(f.Limit > 0 ? f.Limit : 1000).ToList();
        }

        public IReadOnlyList<(int StationNo, string MachineId)> DistinctSources()
            => Rows.Select(x => (x.StationNo, x.MachineId)).Distinct()
                .OrderBy(x => x.StationNo).ThenBy(x => x.MachineId).ToList();

        public IReadOnlyList<Core.Data.MachineTestStats> StatsByMachine()
            => Rows.GroupBy(x => (x.StationNo, x.MachineId))
                .Select(g => new Core.Data.MachineTestStats(
                    g.Key.StationNo, g.Key.MachineId,
                    g.Count(),
                    g.Count(x => x.Result == "PASS"),
                    g.Count(x => x.Result == "FAIL"),
                    g.Max(x => x.TestTime)))
                .ToList();
    }

    private sealed class FakePowerZ : IPowerZSource
    {
        public event Action<PowerSample>? SampleReceived;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void DiscardBuffered() { }
        public void Emit(double voltage, double current)
            => SampleReceived?.Invoke(new PowerSample(voltage, current, DateTime.Now));
        public void Dispose() { }
    }

    /// <summary>缩短时长的检测参数：A 窗口 300ms、跌落确认 100ms、B 持续 200ms、整体 3s。</summary>
    private static CoilTestOptions FastOpts => new()
    {
        CoilACollectMs = 300,
        CoilAThresholdA = 1.0,
        TransitionMs = 100,
        CoilBSustainMs = 200,
        CoilBThresholdA = 1.0,
        OverallTimeoutMs = 3000,
    };

    private static Station1Workflow NewWorkflow(FakeStore store)
        => new(store, snGenerate: (_, s) => $"SN-{s:D4}");

    private static async Task EmitAsync(FakePowerZ pz, double v, double i, int durationMs, int intervalMs = 50)
    {
        var until = Environment.TickCount64 + durationMs;
        while (Environment.TickCount64 < until)
        {
            pz.Emit(v, i);
            await Task.Delay(intervalMs);
        }
    }

    /// <summary>正常手法脚本：A 高电流 → 摘夹子跌落 → B 恢复高电流。</summary>
    private static async Task RunHappyScriptAsync(FakePowerZ pz)
    {
        await EmitAsync(pz, 5.0, 1.2, 400);   // 覆盖 A 窗口（300ms）
        await EmitAsync(pz, 5.0, 0.1, 250);   // 换线圈跌落（≥100ms）
        await EmitAsync(pz, 5.0, 1.2, 400);   // B 恢复并持续（≥200ms）
    }

    [Fact]
    public async Task SnWriteFails_NotStored()
    {
        var store = new FakeStore();
        var wf = NewWorkflow(store);
        var pz = new FakePowerZ();

        var r = await wf.RunAsync(_ => Task.FromResult(false), () => Task.FromResult<string?>(null),
            () => Task.CompletedTask, pz, FastOpts);

        Assert.False(r.Passed);
        Assert.Null(r.Coil);
        Assert.Empty(store.Rows);   // 构想：写失败 SN 不进库
    }

    [Fact]
    public async Task SnReadMismatch_NotStored()
    {
        var store = new FakeStore();
        var wf = NewWorkflow(store);
        var pz = new FakePowerZ();

        var r = await wf.RunAsync(_ => Task.FromResult(true), () => Task.FromResult<string?>("SN-WRONG"),
            () => Task.CompletedTask, pz, FastOpts);

        Assert.False(r.Passed);
        Assert.Null(r.Coil);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task NoSamples_StopsAndNotStored()
    {
        var store = new FakeStore();
        var wf = NewWorkflow(store);
        var pz = new FakePowerZ();            // 不 Emit → A 窗口无采样

        var r = await wf.RunAsync(_ => Task.FromResult(true), () => Task.FromResult<string?>("SN-0001"),
            () => Task.CompletedTask, pz, FastOpts);

        Assert.False(r.Passed);
        Assert.NotNull(r.Coil);
        Assert.True(r.Coil!.NoValidSample);
        Assert.Contains("无有效采样", r.AbnormalItems[0]);
        Assert.Empty(store.Rows);             // 采样失败=流程错误：不入库
    }

    [Fact]
    public async Task AllNormal_PassAndStore()
    {
        var store = new FakeStore();
        var wf = NewWorkflow(store);
        var pz = new FakePowerZ();

        var run = wf.RunAsync(_ => Task.FromResult(true), () => Task.FromResult<string?>("SN-0001"),
            () => Task.CompletedTask, pz, FastOpts);
        await RunHappyScriptAsync(pz);
        var r = await run;

        Assert.True(r.Passed);
        Assert.True(r.Coil!.CoilAPassed);
        Assert.True(r.Coil.CoilBPassed);
        Assert.Single(store.Rows);
        Assert.Equal("PASS", store.Rows[0].Result);
    }

    [Fact]
    public async Task CoilA_LowMean_FailAndStored()
    {
        var store = new FakeStore();
        var wf = NewWorkflow(store);
        var pz = new FakePowerZ();

        var run = wf.RunAsync(_ => Task.FromResult(true), () => Task.FromResult<string?>("SN-0001"),
            () => Task.CompletedTask, pz, FastOpts);
        await EmitAsync(pz, 5.0, 0.5, 400);   // A 窗口均值 0.5A < 1.0A
        var r = await run;

        Assert.False(r.Passed);
        Assert.False(r.Coil!.CoilAPassed);
        Assert.Contains("A 线圈", r.AbnormalItems[0]);
        Assert.Single(store.Rows);            // 有采样 → 入库 FAIL 记录
        Assert.Equal("FAIL", store.Rows[0].Result);
    }

    [Fact]
    public async Task CoilB_NoRecovery_OverallTimeout_FailAndStored()
    {
        var store = new FakeStore();
        var wf = NewWorkflow(store);
        var pz = new FakePowerZ();
        var opts = new CoilTestOptions
        {
            CoilACollectMs = 300,
            CoilAThresholdA = 1.0,
            TransitionMs = 100,
            CoilBSustainMs = 200,
            CoilBThresholdA = 1.0,
            OverallTimeoutMs = 1200,          // 整体限时
        };

        var run = wf.RunAsync(_ => Task.FromResult(true), () => Task.FromResult<string?>("SN-0001"),
            () => Task.CompletedTask, pz, opts);
        await EmitAsync(pz, 5.0, 1.2, 400);   // A 正常
        await EmitAsync(pz, 5.0, 0.1, 1200);  // 跌落后一直低（B 未接入/未恢复）→ 超时
        var r = await run;

        Assert.False(r.Passed);
        Assert.True(r.Coil!.CoilAPassed);
        Assert.False(r.Coil.CoilBPassed);
        Assert.Contains("超时", r.AbnormalItems[0]);
        Assert.Single(store.Rows);
        Assert.Equal("FAIL", store.Rows[0].Result);
    }

    [Fact]
    public async Task NoTransition_OverallTimeout_Fail()
    {
        // A 夹子一直没取下（电流持续高）：看不到跌落 → 超时判败
        var store = new FakeStore();
        var wf = NewWorkflow(store);
        var pz = new FakePowerZ();
        var opts = new CoilTestOptions
        {
            CoilACollectMs = 300,
            CoilAThresholdA = 1.0,
            TransitionMs = 100,
            CoilBSustainMs = 200,
            CoilBThresholdA = 1.0,
            OverallTimeoutMs = 1200,
        };

        var run = wf.RunAsync(_ => Task.FromResult(true), () => Task.FromResult<string?>("SN-0001"),
            () => Task.CompletedTask, pz, opts);
        await EmitAsync(pz, 5.0, 1.2, 1500);  // 始终高电流，无跌落
        var r = await run;

        Assert.False(r.Passed);
        Assert.Contains("未检测到换线圈跌落", r.AbnormalItems[0]);
        Assert.Single(store.Rows);
        Assert.Equal("FAIL", store.Rows[0].Result);
    }

    [Fact]
    public async Task RecordCarriesMachineIdentity()
    {
        // 机位数动态（每工位 1~N 台）：记录必须带本机工位号+机位号，多机位可追溯
        var store = new FakeStore();
        var wf = new Station1Workflow(store,
            snGenerate: (_, s) => $"SN-{s:D4}",
            identity: new StationIdentity(2, "3"));
        var pz = new FakePowerZ();

        var run = wf.RunAsync(_ => Task.FromResult(true), () => Task.FromResult<string?>("SN-0001"),
            () => Task.CompletedTask, pz, FastOpts);
        await RunHappyScriptAsync(pz);
        var r = await run;

        Assert.True(r.Passed);
        Assert.Single(store.Rows);
        Assert.Equal(2, store.Rows[0].StationNo);
        Assert.Equal("3", store.Rows[0].MachineId);
        Assert.Equal("ST-02-3", wf.Identity.Code);
    }
}
