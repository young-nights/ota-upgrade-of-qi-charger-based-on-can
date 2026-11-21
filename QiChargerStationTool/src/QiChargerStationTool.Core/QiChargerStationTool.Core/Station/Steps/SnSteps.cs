using System.Text;
using QiChargerStationTool.Core.Protocol;
using QiChargerStationTool.Core.Security;

namespace QiChargerStationTool.Core.Station.Steps;

/// <summary>Read SN DID 0xF18C (default session).</summary>
public sealed class ReadSnStep : ITestStep
{
    public string Name => "读 SN";
    public bool Critical => false;

    public async Task<TestStepResult> RunAsync(StationContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            var rx = ctx.Uds.Request(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x8C }, timeoutMs: 4000, expectDid: ProtoIds.DidSn);
            if (rx.Length < 4)
                return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = "F18C 响应过短" };
            // 32B ASCII, space-padded
            int len = rx.Length - 3;
            while (len > 0 && (rx[3 + len - 1] is 0x00 or 0x20)) len--;
            var sn = Encoding.ASCII.GetString(rx, 3, len);
            ctx.Sn = sn;
            return new TestStepResult { Name = Name, Status = TestStepStatus.Passed, Message = sn };
        }
        catch (Exception ex)
        {
            return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = ex.Message };
        }
    }
}

/// <summary>
/// Write SN: 10 01 → SA unlock → 10 02 → 2E F18C (≤32B ASCII, space-padded) → 读回校验.
/// 确定性门禁顺序（同 OTA 门禁定序定案）：SA 长交换放在 10 02 之前，10 02 后毫秒级
/// 窗口立即写——长 SA 交换夹在进会话与写之间时（S3 误杀/复位致会话被清）写入必吃
/// 0x22；0x22/0x33 = 会话/解锁被清 → 整套重来（最多 3 次）。
/// </summary>
public sealed class WriteSnStep : ITestStep
{
    public string Name => "写 SN";
    public bool Critical => true;

    public async Task<TestStepResult> RunAsync(StationContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var sn = (ctx.Config.SnCode ?? "").Trim();
        if (sn.Length == 0)
            return new TestStepResult { Name = Name, Status = TestStepStatus.Skipped, Message = "未扫码/未配置 SN" };
        if (sn.Length > 32)
            return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = $"SN 过长 {sn.Length}>32" };

        try
        {
            var signer = SeedSigner.FromDefaultKey();
            if (signer is null)
                return new TestStepResult
                {
                    Name = Name,
                    Status = TestStepStatus.Failed,
                    Message = "缺少 keys/private.pem，无法 SA 解锁",
                };

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    // 10 01 复位默认会话（清残留会话/安全状态，起点确定）
                    ctx.Uds.Request(ProtoIds.SidDsc, new byte[] { 0x01 }, expectSub: 0x01);
                    ctx.Log("10 01 复位默认会话");

                    // SA 解锁（27 无会话门禁，默认会话即可解锁；10 02 保留解锁）
                    if (!await SecurityAccess.UnlockAsync(ctx.Uds, signer, ctx.Log, ct))
                        return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = "SA 解锁失败" };

                    // 10 02 切编程会话 → 立即写（毫秒级窗口）
                    ctx.Uds.Request(ProtoIds.SidDsc, new byte[] { 0x02 }, expectSub: 0x02);
                    ctx.Log("进入编程会话");

                    // 2E F18C — 32B ASCII space-padded
                    var body = new byte[32];
                    for (int i = 0; i < 32; i++) body[i] = 0x20;
                    var ascii = Encoding.ASCII.GetBytes(sn);
                    Array.Copy(ascii, body, Math.Min(ascii.Length, 32));
                    var payload = new byte[2 + 32];
                    payload[0] = 0xF1;
                    payload[1] = 0x8C;
                    body.CopyTo(payload, 2);
                    ctx.Uds.Request(ProtoIds.SidWdbi, payload, timeoutMs: 5000, expectSub: 0xF1, expectDid: ProtoIds.DidSn);
                    ctx.Log("2E F18C 写入完成");
                    break;
                }
                catch (UdsException ex) when (ex.Nrc is 0x22 or 0x33)
                {
                    if (attempt >= 3)
                        throw;
                    ctx.Log($"写 SN 第 {attempt} 次门禁 NRC 0x{ex.Nrc:X2}（会话/解锁被清），整套重来…");
                }
            }

            // read back
            var rx = ctx.Uds.Request(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x8C }, timeoutMs: 4000, expectDid: ProtoIds.DidSn);
            int len = rx.Length - 3;
            while (len > 0 && (rx[3 + len - 1] is 0x00 or 0x20)) len--;
            var readBack = Encoding.ASCII.GetString(rx, 3, len);
            if (!string.Equals(readBack, sn, StringComparison.Ordinal))
                return new TestStepResult
                {
                    Name = Name,
                    Status = TestStepStatus.Failed,
                    Message = $"写后读回不符 期望={sn} 实际={readBack}",
                };
            ctx.Sn = readBack;
            return new TestStepResult { Name = Name, Status = TestStepStatus.Passed, Message = $"SN={readBack}" };
        }
        catch (UdsException ex)
        {
            return new TestStepResult
            {
                Name = Name,
                Status = TestStepStatus.Failed,
                Message = $"UDS NRC 0x{ex.Nrc:X2}（0x22=条件不满足 0x33=安全未解锁）",
            };
        }
        catch (Exception ex)
        {
            return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = ex.Message };
        }
    }
}
