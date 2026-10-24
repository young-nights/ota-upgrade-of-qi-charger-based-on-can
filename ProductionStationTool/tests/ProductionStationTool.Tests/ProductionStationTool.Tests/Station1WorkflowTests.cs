using ProductionStationTool.Core.Flags;
using ProductionStationTool.Core.Station;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.Tests;

/// <summary>工位 1 流程行为测试（构想文档关键约定）。</summary>
public class Station1WorkflowTests
{
    private sealed class FakeFlags : ITestFlagService
    {
        public TestFlag Flag = TestFlag.NotTested;
        public bool Cleared;
        public Task<TestFlag> GetFlagAsync(StationId s, CancellationToken ct = default)
            => Task.FromResult(Flag);
        public Task<IReadOnlyDictionary<StationId, TestFlag>> GetAllFlagsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<StationId, TestFlag>>(
                new Dictionary<StationId, TestFlag> { [StationId.Station1] = Flag });
        public Task SetFlagAsync(StationId s, TestFlag f, CancellationToken ct = default)
        { Flag = f; return Task.CompletedTask; }
        public Task ClearAllFlagsAsync(CancellationToken ct = default)
        { Cleared = true; Flag = TestFlag.NotTested; return Task.CompletedTask; }
    }

    private sealed class FakeStore : Core.Data.ITestRecordStore
    {
        public readonly List<Core.Data.TestRecord> Rows = new();
        public void Add(Core.Data.TestRecord r) => Rows.Add(r);
        public Core.Data.TestRecord? FindBySn(string sn) => Rows.FirstOrDefault(x => x.Sn == sn);
        public IReadOnlyList<Core.Data.TestRecord> Recent(int n) => Rows.TakeLast(n).ToList();
    }

    private sealed class FakePowerZ : IPowerZSource
    {
        private readonly PowerSample _sample;
        public FakePowerZ(double v, double i) => _sample = new(v, i, DateTime.Now);
        public event Action<PowerSample>? SampleReceived;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Emit() => SampleReceived?.Invoke(_sample);
        public void Dispose() { }
    }

    private static Station1Workflow NewWorkflow(FakeFlags flags, FakeStore store)
        => new(flags, store, snGenerate: (_, s) => $"SN-{s:D4}");

    [Fact]
    public async Task FlagAlreadyPassed_SkipsFlow()
    {
        var flags = new FakeFlags { Flag = TestFlag.Passed };
        var store = new FakeStore();
        var wf = NewWorkflow(flags, store);
        var pz = new FakePowerZ(5.0, 1.0);

        var r = await wf.RunAsync(_ => Task.FromResult(true), () => Task.FromResult("SN-0001"),
            () => Task.CompletedTask, pz, new PowerLimits { VoltageMin = 0, VoltageMax = 9, CurrentMin = 0, CurrentMax = 9 });

        Assert.True(r.Skipped);
        Assert.True(r.Passed);
        Assert.Empty(store.Rows);   // 跳过时不动库
    }

    [Fact]
    public async Task SnWriteFails_NotStored()
    {
        var flags = new FakeFlags();
        var store = new FakeStore();
        var wf = NewWorkflow(flags, store);

        var r = await wf.RunAsync(_ => Task.FromResult(false), () => Task.FromResult<string?>(null),
            () => Task.CompletedTask, new FakePowerZ(5, 1),
            new PowerLimits { VoltageMin = 0, VoltageMax = 9, CurrentMin = 0, CurrentMax = 9 });

        Assert.False(r.Passed);
        Assert.Empty(store.Rows);   // 构想：写失败 SN 不进库
    }

    [Fact]
    public async Task SnReadMismatch_NotStored()
    {
        var store = new FakeStore();
        var wf = NewWorkflow(new FakeFlags(), store);

        var r = await wf.RunAsync(_ => Task.FromResult(true), () => Task.FromResult<string?>("SN-WRONG"),
            () => Task.CompletedTask, new FakePowerZ(5, 1),
            new PowerLimits { VoltageMin = 0, VoltageMax = 9, CurrentMin = 0, CurrentMax = 9 });

        Assert.False(r.Passed);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task AllNormal_PassAndStoreAndSetFlag()
    {
        var flags = new FakeFlags();
        var store = new FakeStore();
        var wf = NewWorkflow(flags, store);
        var pz = new FakePowerZ(5.0, 1.0);

        var run = wf.RunAsync(_ => Task.FromResult(true), () => Task.FromResult("SN-0001"),
            () => Task.CompletedTask, pz,
            new PowerLimits { VoltageMin = 4.7, VoltageMax = 5.3, CurrentMin = 0.5, CurrentMax = 2 });
        _ = Task.Run(pz.Emit);      // 流程等待采样时发一个
        var r = await run;

        Assert.True(r.Passed);
        Assert.Single(store.Rows);
        Assert.Equal("PASS", store.Rows[0].Result);
        Assert.Equal(TestFlag.Passed, flags.Flag);   // 成功置标志
    }

    [Fact]
    public async Task VoltageOutOfRange_FailWithAbnormalItem()
    {
        var flags = new FakeFlags();
        var store = new FakeStore();
        var wf = NewWorkflow(flags, store);
        var pz = new FakePowerZ(3.0, 1.0);           // 电压过低

        var run = wf.RunAsync(_ => Task.FromResult(true), () => Task.FromResult("SN-0001"),
            () => Task.CompletedTask, pz,
            new PowerLimits { VoltageMin = 4.7, VoltageMax = 5.3, CurrentMin = 0.5, CurrentMax = 2 });
        _ = Task.Run(pz.Emit);
        var r = await run;

        Assert.False(r.Passed);
        Assert.NotEmpty(r.AbnormalItems);            // UI 红字显示
        Assert.Single(store.Rows);                   // SN 写成功了，仍入库（FAIL 记录）
        Assert.Equal("FAIL", store.Rows[0].Result);
        Assert.Equal(TestFlag.NotTested, flags.Flag); // 失败不置标志
    }
}
