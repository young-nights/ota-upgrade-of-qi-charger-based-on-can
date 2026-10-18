using System.Diagnostics;
using QiChargerStationTool.Core.Protocol;
using QiChargerStationTool.Core.Station.Steps;

namespace QiChargerStationTool.Core.Station;

/// <summary>Production test flow orchestrator.</summary>
public sealed class StationRunner
{
    public event Action<string>? OnLog;
    public event Action<TestStepResult>? OnStep;

    public async Task<IReadOnlyList<TestStepResult>> RunAsync(
        StationConfig config, ITransportFactory factory, CancellationToken ct)
    {
        var results = new List<TestStepResult>();
        void Log(string m) => OnLog?.Invoke(m);

        Log("==== 产线测试开始 ====");
        using var transport = factory.Create(config);
        transport.Open();
        Log($"CAN 打开 {config.ComPort} 250k ext");

        var iso = new IsoTpClient(transport);
        iso.OnLog += m => Log(m);
        var uds = new UdsClient(iso);
        uds.OnLog += m => Log(m);
        var ctx = new StationContext { Config = config, Uds = uds };
        ctx.OnLog += Log;

        foreach (var step in BuildSteps(config))
        {
            ct.ThrowIfCancellationRequested();
            Log($"---- {step.Name} ----");
            var sw = Stopwatch.StartNew();
            TestStepResult result;
            try
            {
                result = await step.RunAsync(ctx, ct);
            }
            catch (OperationCanceledException)
            {
                result = new TestStepResult { Name = step.Name, Status = TestStepStatus.Failed, Message = "取消" };
                sw.Stop();
                result.Elapsed = sw.Elapsed;
                results.Add(result);
                OnStep?.Invoke(result);
                break;
            }
            catch (Exception ex)
            {
                result = new TestStepResult { Name = step.Name, Status = TestStepStatus.Failed, Message = ex.Message };
            }
            sw.Stop();
            result.Elapsed = sw.Elapsed;
            results.Add(result);
            OnStep?.Invoke(result);
            Log(result.ToString());
            if (result.Status == TestStepStatus.Failed && step.Critical)
            {
                Log("关键步骤失败，中止后续测试");
                break;
            }
        }

        bool pass = results.All(r => r.Status is TestStepStatus.Passed or TestStepStatus.Skipped);
        Log(pass ? "==== 整机 PASS ====" : "==== 整机 FAIL ====");
        return results;
    }

    private static IEnumerable<ITestStep> BuildSteps(StationConfig c)
    {
        yield return new ConnectStep();
        if (c.DoReadVersion) yield return new ReadVersionStep();
        if (c.DoWriteSn) yield return new WriteSnStep();
        if (c.DoOtaUpgrade) yield return new OtaUpgradeStep();
        if (c.DoChargeTest) yield return new ChargeTestStep();
    }
}

public interface ITransportFactory
{
    Transport.ICanTransport Create(StationConfig config);
}

public sealed class ZqwlTransportFactory : ITransportFactory
{
    public Transport.ICanTransport Create(StationConfig config)
        => new Transport.ZqwlSerialTransport(config.ComPort, config.Baud);
}
