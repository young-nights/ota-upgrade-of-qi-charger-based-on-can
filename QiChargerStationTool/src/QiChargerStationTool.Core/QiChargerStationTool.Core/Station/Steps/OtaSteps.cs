using QiChargerStationTool.Core.Protocol;

namespace QiChargerStationTool.Core.Station.Steps;

/// <summary>
/// OTA upgrade: 10 02 → SA → 2E 20 10 01 → 31 erase → 34/36/37.
/// SA unlock uses 27 03 chunks + 27 02; seed signing via <see cref="Security.SeedSigner"/>.
/// </summary>
public sealed class OtaUpgradeStep : ITestStep
{
    public string Name => "OTA 升级";
    public bool Critical => true;

    public async Task<TestStepResult> RunAsync(StationContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var fw = ctx.Config.FirmwarePath;
        if (string.IsNullOrWhiteSpace(fw) || !File.Exists(fw))
            return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = $"固件不存在: {fw}" };

        try
        {
            var image = Imaging.XatoImage.Load(fw);
            ctx.Log($"镜像 {image.PayloadLength + Imaging.XatoImage.HeaderSize}B ver={image.ExtractSwVersion()}");

            // 10 02 programming
            ctx.Uds.Request(ProtoIds.SidDsc, new byte[] { 0x02 });
            ctx.Log("进入编程会话");

            // SA — requires signer
            var signer = Security.SeedSigner.FromDefaultKey();
            if (signer is null)
                return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = "缺少 docs/keys/private.pem，无法 SA 解锁" };

            if (!await Security.SecurityAccess.UnlockAsync(ctx.Uds, signer, ctx.Log, ct))
                return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = "SA 解锁失败" };

            // 2E 20 10 01 MCU1
            ctx.Uds.Request(ProtoIds.SidWdbi, new byte[] { 0x20, 0x10, 0x01 });
            ctx.Log("目标 MCU1");

            // 31 erase backup
            ctx.Uds.Request(ProtoIds.SidRc, new byte[] { 0x01, 0xFF, 0x00 }, timeoutMs: 15_000, waitPendingMs: 90_000);
            ctx.Log("擦除 Backup 完成");

            // 34 RequestDownload
            int size = image.Raw.Length;
            var rd = new byte[]
            {
                0x00, 0x44,
                0x08, 0x01, 0x00, 0x00,
                (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size,
            };
            ctx.Uds.Request(ProtoIds.SidRd, rd, timeoutMs: 8000);
            ctx.Log($"RequestDownload {size}B @ 0x08010000");

            // 36 TransferData
            int block = Math.Clamp(ctx.Config.TransferBlock, 1, 254);
            var img = image.Raw;
            int off = 0, seq = 1, blk = 0;
            int nblocks = (size + block - 1) / block;
            while (off < size)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(block, size - off);
                var payload = new byte[1 + n];
                payload[0] = (byte)(seq & 0xFF);
                Buffer.BlockCopy(img, off, payload, 1, n);
                ctx.Uds.Request(ProtoIds.SidTd, payload, timeoutMs: 8000, waitPendingMs: 30_000);
                off += n;
                blk++;
                seq = (seq + 1) & 0xFF;
                if (blk % 16 == 0 || off >= size)
                    ctx.Log($"[进度] {blk}/{nblocks} 块  {off}/{size} 字节 ({off * 100 / size}%)");
            }

            // 37 TransferExit (device self-resets after success)
            try
            {
                ctx.Uds.Request(ProtoIds.SidRte, Array.Empty<byte>(), timeoutMs: 8000, waitPendingMs: 60_000);
                ctx.Log("0x37 确认，设备自复位搬运");
            }
            catch (Exception ex)
            {
                ctx.Log($"0x37 异常（可能已自复位）: {ex.Message}");
            }

            return new TestStepResult { Name = Name, Status = TestStepStatus.Passed, Message = $"传输 {size}B 完成" };
        }
        catch (Exception ex)
        {
            return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = ex.Message };
        }
    }
}

/// <summary>Charge enable / disable smoke test (2E 21 01).</summary>
public sealed class ChargeTestStep : ITestStep
{
    public string Name => "充电启停";
    public bool Critical => false;

    public async Task<TestStepResult> RunAsync(StationContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            ctx.Uds.Request(ProtoIds.SidDsc, new byte[] { 0x03 }); // extended
            ctx.Uds.RequestSuppress(ProtoIds.SidTp, new byte[] { 0x80 });
            // enable
            ctx.Uds.Request(ProtoIds.SidWdbi, new byte[] { 0x21, 0x01, 0x01 });
            await Task.Delay(1000, ct);
            // disable
            ctx.Uds.Request(ProtoIds.SidWdbi, new byte[] { 0x21, 0x01, 0x00 });
            return new TestStepResult { Name = Name, Status = TestStepStatus.Passed, Message = "启停正常" };
        }
        catch (Exception ex)
        {
            return new TestStepResult { Name = Name, Status = TestStepStatus.Failed, Message = ex.Message };
        }
    }
}
