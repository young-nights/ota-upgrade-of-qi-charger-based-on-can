using QiChargerStationTool.Core.Protocol;

namespace QiChargerStationTool.Core.Station.Steps;

/// <summary>Probe device online: 3E 00 + 22 2113.</summary>
public sealed class ConnectStep : ITestStep
{
    public string Name => "连接探测";
    public bool Critical => true;

    public async Task<TestStepResult> RunAsync(StationContext ctx, CancellationToken ct)
    {
        try
        {
            for (int i = 0; i < 8; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    ctx.Uds.Request(ProtoIds.SidTp, new byte[] { 0x00 }, timeoutMs: 1500);
                    var slot = ctx.Uds.ReadSlot();
                    ctx.Slot = slot;
                    return new TestStepResult
                    {
                        Name = Name,
                        Status = TestStepStatus.Passed,
                        Message = $"在线 slot=0x{slot:X2}",
                    };
                }
                catch (Exception ex)
                {
                    ctx.Log($"探测 {i + 1}/8: {ex.Message}");
                    await Task.Delay(300, ct);
                }
            }
            return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = "设备无应答" };
        }
        catch (OperationCanceledException)
        {
            return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = "取消" };
        }
    }
}

/// <summary>Read APP version DID 0xF195.</summary>
public sealed class ReadVersionStep : ITestStep
{
    public string Name => "读软件版本";
    public bool Critical => true;

    public async Task<TestStepResult> RunAsync(StationContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            var ver = ctx.Uds.ReadSwVersion();
            ctx.SwVersion = ver;
            return new TestStepResult
            {
                Name = Name,
                Status = TestStepStatus.Passed,
                Message = ver,
            };
        }
        catch (Exception ex)
        {
            return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = ex.Message };
        }
    }
}
