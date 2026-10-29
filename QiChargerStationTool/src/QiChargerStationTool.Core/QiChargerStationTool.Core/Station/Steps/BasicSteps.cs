using QiChargerStationTool.Core.Protocol;

namespace QiChargerStationTool.Core.Station.Steps;

/// <summary>Probe device online: wake burst + 3E 00 + 22 2113.</summary>
public sealed class ConnectStep : ITestStep
{
    public string Name => "连接探测";
    public bool Critical => true;

    /// <summary>SIT1145 WUF 唤醒需要 ≥6 帧 raw CAN burst（DLC=3 的 02 3E 00，PNDM=1 严格匹配）。</summary>
    private const int WakeBurstCount = 8;
    private const int WakeBurstGapMs = 250;

    public async Task<TestStepResult> RunAsync(StationContext ctx, CancellationToken ct)
    {
        try
        {
            // 唤醒 burst：发 ≥6 帧 raw CAN 02 3E 00（DLC=3）到 0x18DA0D03，触发 SIT1145 WUF
            ctx.Log($"发送唤醒 burst ({WakeBurstCount} 帧, DLC=3)…");
            for (int i = 0; i < WakeBurstCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                ctx.Uds.SendWake();
                await Task.Delay(WakeBurstGapMs, ct);
            }

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
