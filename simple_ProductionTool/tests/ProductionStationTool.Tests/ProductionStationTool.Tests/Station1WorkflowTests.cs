using ProductionStationTool.Core.Station;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.Tests;

/// <summary>写 SN 流程行为测试（简化版：外部 SN、无数据库）+ 双线圈检测。</summary>
public class Station1WorkflowTests
{
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

    /// <summary>缩短时长的检测参数：检测窗口 400ms、有效时间下限 100ms、整体 3s。</summary>
    private static CoilTestOptions FastOpts => new()
    {
        DetectionWindowMs = 400,
        MinEffectiveMs = 100,
        CoilALowerA = 1.0,
        CoilAUpperA = 2.0,
        CoilBLowerA = 1.0,
        CoilBUpperA = 2.0,
        OverallTimeoutMs = 3000,
    };

    private static Station1Workflow NewWorkflow() => new();

    private static async Task EmitAsync(FakePowerZ pz, double v, double i, int durationMs, int intervalMs = 50)
    {
        var until = Environment.TickCount64 + durationMs;
        while (Environment.TickCount64 < until)
        {
            pz.Emit(v, i);
            await Task.Delay(intervalMs);
        }
    }

    /// <summary>正常手法脚本：A 达到下限覆盖窗口 → 换线圈短暂低于 B 下限 → B 达到下限覆盖窗口。</summary>
    private static async Task RunHappyScriptAsync(FakePowerZ pz)
    {
        await EmitAsync(pz, 5.0, 1.2, 500);   // 覆盖 A 检测窗口（400ms）
        await EmitAsync(pz, 5.0, 0.1, 150);   // 换线圈：只要求出现低于 B 下限的接入沿，不做持续时间确认
        await EmitAsync(pz, 5.0, 1.2, 500);   // B 达到下限后覆盖检测窗口（400ms）
    }

    [Fact]
    public async Task SnWriteFails_TerminatesBeforeCoil()
    {
        var wf = NewWorkflow();
        var pz = new FakePowerZ();

        var r = await wf.RunAsync("SN-0001", _ => Task.FromResult(false),
            () => Task.FromResult<string?>(null),
            () => Task.CompletedTask, pz, FastOpts);

        Assert.False(r.Passed);
        Assert.Null(r.Coil);
        Assert.Equal("SN-0001", r.Sn);
        Assert.Contains("写入", r.AbnormalItems[0]);
    }

    [Fact]
    public async Task SnReadMismatch_Terminates()
    {
        var wf = NewWorkflow();
        var pz = new FakePowerZ();

        var r = await wf.RunAsync("SN-0001", _ => Task.FromResult(true),
            () => Task.FromResult<string?>("SN-WRONG"),
            () => Task.CompletedTask, pz, FastOpts);

        Assert.False(r.Passed);
        Assert.Null(r.Coil);
        Assert.Contains("对不上", r.AbnormalItems[0]);
    }

    [Fact]
    public async Task NoSamples_StopsAndNotJudged()
    {
        var wf = NewWorkflow();
        var pz = new FakePowerZ();            // 不 Emit → 检测窗口无采样

        var r = await wf.RunAsync("SN-0001", _ => Task.FromResult(true),
            () => Task.FromResult<string?>("SN-0001"),
            () => Task.CompletedTask, pz, FastOpts);

        Assert.False(r.Passed);
        Assert.NotNull(r.Coil);
        Assert.True(r.Coil!.NoValidSample);
        Assert.Contains("无有效采样", r.AbnormalItems[0]);
    }

    [Fact]
    public async Task AllNormal_Pass()
    {
        var wf = NewWorkflow();
        var pz = new FakePowerZ();

        var run = wf.RunAsync("SN-0001", _ => Task.FromResult(true),
            () => Task.FromResult<string?>("SN-0001"),
            () => Task.CompletedTask, pz, FastOpts);
        await RunHappyScriptAsync(pz);
        var r = await run;

        Assert.True(r.Passed);
        Assert.True(r.Coil!.CoilAPassed);
        Assert.True(r.Coil.CoilBPassed);
    }

    [Fact]
    public async Task CoilA_OverUpper_FailWithOverCurrentItem()
    {
        var wf = NewWorkflow();
        var pz = new FakePowerZ();

        var run = wf.RunAsync("SN-0001", _ => Task.FromResult(true),
            () => Task.FromResult<string?>("SN-0001"),
            () => Task.CompletedTask, pz, FastOpts);
        await EmitAsync(pz, 5.0, 2.5, 400);   // A 窗口均值 2.5A 高于上限 2.0A
        var r = await run;

        Assert.False(r.Passed);
        Assert.False(r.Coil!.CoilAPassed);
        Assert.Contains("高于上限", r.AbnormalItems[0]);   // 过流异常项
    }

    [Fact]
    public async Task CoilA_MeanBelowLower_Fail()
    {
        var wf = NewWorkflow();
        var pz = new FakePowerZ();

        var run = wf.RunAsync("SN-0001", _ => Task.FromResult(true),
            () => Task.FromResult<string?>("SN-0001"),
            () => Task.CompletedTask, pz, FastOpts);
        await EmitAsync(pz, 5.0, 1.2, 100);   // 先达到下限
        await EmitAsync(pz, 5.0, 0.5, 500);   // 之后均值被拉低到 1.0A 以下
        var r = await run;

        Assert.False(r.Passed);
        Assert.False(r.Coil!.CoilAPassed);
        Assert.Contains("低于下限", r.AbnormalItems[0]);
    }

    [Fact]
    public async Task CoilA_NeverReachesLower_Fail()
    {
        var wf = NewWorkflow();
        var pz = new FakePowerZ();

        var run = wf.RunAsync("SN-0001", _ => Task.FromResult(true),
            () => Task.FromResult<string?>("SN-0001"),
            () => Task.CompletedTask, pz, FastOpts);
        await EmitAsync(pz, 5.0, 0.5, 500);   // 始终低于 A 下限
        var r = await run;

        Assert.False(r.Passed);
        Assert.False(r.Coil!.CoilAPassed);
        Assert.Contains("未达到下限", r.AbnormalItems[0]);
    }

    [Fact]
    public async Task CoilB_NoRecovery_OverallTimeout_Fail()
    {
        var wf = NewWorkflow();
        var pz = new FakePowerZ();
        var opts = new CoilTestOptions
        {
            DetectionWindowMs = 300,
            MinEffectiveMs = 100,
            CoilALowerA = 1.0,
            CoilAUpperA = 2.0,
            CoilBLowerA = 1.0,
            CoilBUpperA = 2.0,
            OverallTimeoutMs = 1200,          // 整体限时
        };

        var run = wf.RunAsync("SN-0001", _ => Task.FromResult(true),
            () => Task.FromResult<string?>("SN-0001"),
            () => Task.CompletedTask, pz, opts);
        await EmitAsync(pz, 5.0, 1.2, 400);   // A 正常
        await EmitAsync(pz, 5.0, 0.1, 1200);  // 换线圈后一直低（B 未接入/未达到下限）→ 超时
        var r = await run;

        Assert.False(r.Passed);
        Assert.True(r.Coil!.CoilAPassed);
        Assert.False(r.Coil.CoilBPassed);
        Assert.Contains("B 线圈未接入", r.AbnormalItems[0]);
    }

    [Fact]
    public async Task CoilA_EffectiveTimeTooShort_Fail()
    {
        // 电流接近检测窗口结束才达到下限：有效检测时间不足 → 无效
        var wf = NewWorkflow();
        var pz = new FakePowerZ();
        var opts = new CoilTestOptions
        {
            DetectionWindowMs = 400,
            MinEffectiveMs = 200,
            CoilALowerA = 1.0,
            CoilAUpperA = 2.0,
            CoilBLowerA = 1.0,
            CoilBUpperA = 2.0,
            OverallTimeoutMs = 1500,
        };

        var run = wf.RunAsync("SN-0001", _ => Task.FromResult(true),
            () => Task.FromResult<string?>("SN-0001"),
            () => Task.CompletedTask, pz, opts);
        await EmitAsync(pz, 5.0, 0.1, 350);   // 长时间低于下限
        await EmitAsync(pz, 5.0, 1.2, 150);   // 接近窗口末才达到下限
        var r = await run;

        Assert.False(r.Passed);
        Assert.False(r.Coil!.CoilAPassed);
        Assert.Contains("有效检测时间", r.AbnormalItems[0]);
    }
}
