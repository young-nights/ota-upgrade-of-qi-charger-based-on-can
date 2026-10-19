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
/// 设置 Qi 功率：10 03 扩展会话 → SA 解锁 → 2E 210D（1 字节档位码 0x01/0x02/0x03）→ 读回校验。
/// UI 5W/10W/15W 映射 Qi 芯片 QI_POWER_5W/10W/15W；同 zcanpro_qi_set_power.py。
/// </summary>
public sealed class SetQiPowerStep : ITestStep
{
    public string Name => "设置 Qi 功率";
    public bool Critical => false;

    /// <summary>目标档位码（0x01=5W / 0x02=10W / 0x03=15W）。</summary>
    public byte PowerCode { get; init; } = ProtoIds.Power15W;

    public async Task<TestStepResult> RunAsync(StationContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        string label = QiChipStatusCodec.PowerLimitName(PowerCode);
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

            ctx.Uds.WriteQiPowerLimit(PowerCode);
            ctx.Log($"2E 210D 写入 0x{PowerCode:X2} ({label})");

            // 读回校验
            var readBack = QiChipStatusCodec.ReadU8(ctx.Uds.ReadDid(ProtoIds.DidPowerLimit));
            if (readBack != PowerCode)
                return new TestStepResult
                {
                    Name = Name,
                    Status = TestStepStatus.Failed,
                    Message = $"写后读回不符 期望=0x{PowerCode:X2} 实际=0x{readBack:X2}",
                };

            return new TestStepResult { Name = Name, Status = TestStepStatus.Passed, Message = label };
        }
        catch (Exception ex)
        {
            return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = ex.Message };
        }
    }
}
