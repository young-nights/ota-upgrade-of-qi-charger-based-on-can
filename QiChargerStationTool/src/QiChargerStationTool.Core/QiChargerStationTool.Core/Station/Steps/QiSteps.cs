using QiChargerStationTool.Core.Protocol;
using QiChargerStationTool.Core.Security;

namespace QiChargerStationTool.Core.Station.Steps;

/// <summary>Qi 芯片状态全量查询（IAP + 充电 + 传感器 + 功率档位），任意会话可读。</summary>
public sealed class QiStatusQueryStep : ITestStep
{
    public string Name => "Qi 状态查询";
    public bool Critical => false;

    public async Task<TestStepResult> RunAsync(StationContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            var st = ctx.Uds.ReadQiChipStatus(ct);
            ctx.QiStatus = st;
            ctx.Log(st.ToDisplayString());
            if (st.IapState == 0x03)
                return new TestStepResult
                {
                    Name = Name,
                    Status = TestStepStatus.Failed,
                    Message = $"IAP 失败态；{st.ChargeStateName} / {st.FaultName}",
                };
            return new TestStepResult
            {
                Name = Name,
                Status = TestStepStatus.Passed,
                Message = $"Qi {st.QiVerMajor}.{st.QiVerMinor}.{st.QiVerPatch} · {st.ChargeStateName} · {st.OutputPowerMw}mW · {st.FaultName}",
            };
        }
        catch (Exception ex)
        {
            return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = ex.Message };
        }
    }
}

/// <summary>
/// 设置 Qi 功率：10 03 扩展会话 → SA 解锁 → 2E 210D（uint16 LE, 500/1000/1500）→ 读回校验。
/// 与 python_tools/zcanpro/qi/zcanpro_qi_set_power.py 同构。
/// </summary>
public sealed class SetQiPowerStep : ITestStep
{
    public string Name => "设置 Qi 功率";
    public bool Critical => false;

    /// <summary>目标档位（500=5W / 1000=10W / 1500=15W）。</summary>
    public ushort PowerLimit { get; init; } = ProtoIds.PowerLimit15W;

    public async Task<TestStepResult> RunAsync(StationContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        string label = QiChipStatusCodec.PowerLimitName(PowerLimit);
        try
        {
            ctx.Uds.Request(ProtoIds.SidDsc, new byte[] { 0x03 }); // extended
            ctx.Log("进入扩展会话 10 03");

            var signer = SeedSigner.FromDefaultKey();
            if (signer is null)
                return new TestStepResult
                {
                    Name = Name,
                    Status = TestStepStatus.Failed,
                    Message = "缺少 keys/private.pem，无法 SA 解锁",
                };
            if (!await SecurityAccess.UnlockAsync(ctx.Uds, signer, m => ctx.Log("[SA] " + m), ct))
                return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = "SA 解锁失败" };

            ctx.Uds.WriteQiPowerLimit(PowerLimit);
            ctx.Log($"2E 210D 写入 {PowerLimit} ({label})");

            // 读回校验
            var readBack = QiChipStatusCodec.ReadU16Le(ctx.Uds.ReadDid(ProtoIds.DidPowerLimit));
            if (readBack != PowerLimit)
                return new TestStepResult
                {
                    Name = Name,
                    Status = TestStepStatus.Failed,
                    Message = $"写后读回不符 期望={PowerLimit} 实际={readBack}",
                };

            return new TestStepResult { Name = Name, Status = TestStepStatus.Passed, Message = label };
        }
        catch (Exception ex)
        {
            return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = ex.Message };
        }
    }
}
