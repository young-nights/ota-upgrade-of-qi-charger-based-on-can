using System.Text;
using QiChargerStationTool.Core.Imaging;
using QiChargerStationTool.Core.Protocol;
using QiChargerStationTool.Core.Security;
using QiChargerStationTool.Core.Transport;

namespace ProductionStationTool.Core.Station;

/// <summary>OTA 升级进度（Percent 0~100，Phase 阶段描述）。</summary>
public sealed record OtaProgress(int Percent, string Phase);

/// <summary>
/// CAN/UDS 设备访问（ZQWL-CANFD 串口 → ISO-TP/UDS），复用
/// QiChargerStationTool.Core 协议栈（不改维修工具，只引用）。
///
/// 按固件门禁（can_protocol.c WDBI 注释）封装工位流程所需操作：
///   写 SN   0xF18C：Programming(10 02) + SA → 2E F18C → 22 F18C 回读；
///   开充电  0x2101：Extended(10 03) + SA → 2E 21 01 01；
///   唤醒    0x18DA0D03 的 02 3E 00 CC…（DLC=8）burst + 3E 探活。
/// </summary>
public sealed class CanUdsDevice : IDisposable
{
    private readonly ZqwlSerialTransport _transport;
    private readonly IsoTpClient _iso;
    private readonly UdsClient _uds;

    /// <summary>诊断日志（UDS 层）。</summary>
    public event Action<string>? Log;

    public bool IsOpen => _transport.IsOpen;

    public CanUdsDevice(string portName, int baud = 2_000_000)
    {
        _transport = new ZqwlSerialTransport(portName, baud);
        _iso = new IsoTpClient(_transport);
        _uds = new UdsClient(_iso);
        _uds.OnLog += m => Log?.Invoke(m);
    }

    public Task OpenAsync(CancellationToken ct = default)
        => Task.Run(() => _transport.Open(), ct);

    public Task CloseAsync(CancellationToken ct = default)
        => Task.Run(() => _transport.Close(), ct);

    public void Dispose() => _transport.Dispose();

    /// <summary>唤醒 burst（8× 02 3E 00 CC…）+ 3E 探活；返回设备是否应答。</summary>
    public async Task<bool> TryWakeAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            for (int i = 0; i < 8; i++)
            {
                _uds.SendWake();
                Thread.Sleep(250);
            }
            var r = _uds.TryRequest(ProtoIds.SidTp, new byte[] { 0x00 }, timeoutMs: 1500, ct: ct);
            return r.Ok || r.Nrc != 0;   // 有正响应或 NRC 都算栈活着
        }, ct);
    }

    /// <summary>
    /// 唤醒 + 等 UDS 就绪（每次跑流程前必调）：
    /// 设备空闲 10s 会真休眠（整板断电），首帧 UDS 会触发 WUF 唤醒但本身
    /// 丢在重新供电的冷启动窗口（~2-3s）里——必须 burst 唤醒后轮询探活
    /// 到应答为止（最长 8s），再开始正式请求。
    /// </summary>
    public async Task<bool> WaitReadyAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            for (int i = 0; i < 8; i++)
            {
                _uds.SendWake();
                Thread.Sleep(250);
            }
            long deadline = Environment.TickCount64 + 8000;
            while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
            {
                var r = _uds.TryRequest(ProtoIds.SidTp, new byte[] { 0x00 }, timeoutMs: 1500, ct: ct);
                if (r.Ok || r.Nrc != 0)
                    return true;
                // 无应答：再补 2 帧唤醒（可能仍在 Boot），0.3s 后重探
                _uds.SendWake();
                _uds.SendWake();
                Thread.Sleep(300);
            }
            return false;
        }, ct);
    }

    /// <summary>读全套版本：APP 0xF195 / Qi 0x2133 / Boot 0xF180 / HW 0xF193。</summary>
    public Task<VersionSet> TryReadAllVersionsAsync(CancellationToken ct = default)
        => Task.Run(() => _uds.TryReadAllVersions(ct), ct);

    /// <summary>
    /// 写 SN 并回读校验（DID 0xF18C，Programming+SA，32B ASCII 空格填充）：
    /// 10 02 → SA 解锁 → 2E F18C → 22 F18C 比对。
    /// </summary>
    public async Task<(bool Ok, string Detail)> WriteSnAsync(string sn, CancellationToken ct = default)
    {
        sn = sn.Trim();
        if (sn.Length == 0)
            return (false, "SN 为空");
        if (sn.Length > 32)
            return (false, $"SN 过长 {sn.Length}>32");

        var dsc = await Task.Run(() => RequestRetry(ProtoIds.SidDsc, new byte[] { 0x02 }, 3000, ct,
            expectSub: 0x02), ct);
        if (!dsc.Ok)
            return (false, $"10 02 进编程会话失败：{dsc.Error}");
        Log?.Invoke("进入编程会话 10 02");

        var signer = SeedSigner.FromDefaultKey();
        if (signer is null)
            return (false, "缺少 keys/private.pem，无法 SA 解锁");
        if (!await SecurityAccess.UnlockAsync(_uds, signer, m => Log?.Invoke(m), ct))
            return (false, "SA 解锁失败");

        var body = new byte[32];
        for (int i = 0; i < 32; i++)
            body[i] = 0x20;
        var ascii = Encoding.ASCII.GetBytes(sn);
        Array.Copy(ascii, body, Math.Min(ascii.Length, 32));
        var payload = new byte[34];
        payload[0] = 0xF1;
        payload[1] = 0x8C;
        body.CopyTo(payload, 2);
        var w = await Task.Run(() => RequestRetry(ProtoIds.SidWdbi, payload, 5000, ct,
            expectSub: 0xF1, expectDid: ProtoIds.DidSn), ct);
        if (!w.Ok)
            return (false, $"2E F18C 写入失败：{w.Error}");
        Log?.Invoke("2E F18C 写入完成");

        var readBack = await Task.Run(() => ReadSnCore(), ct);
        if (readBack is null)
        {
            await Task.Delay(200, ct);
            readBack = await Task.Run(() => ReadSnCore(), ct);
        }
        return string.Equals(readBack, sn, StringComparison.Ordinal)
            ? (true, $"SN={readBack}")
            : (false, $"写后读回不符 期望={sn} 实际={readBack ?? "(空/超时)"}");
    }

    /// <summary>读 SN（DID 0xF18C，默认会话）；失败返回 null。</summary>
    public async Task<string?> TryReadSnAsync(CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => ReadSnCore(), ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>开启无线充电（DID 0x2101=0x01，Extended+SA）。</summary>
    public Task<(bool Ok, string Detail)> EnableChargingAsync(CancellationToken ct = default)
        => SetChargingAsync(0x01, ct);

    /// <summary>停止无线充电（DID 0x2101=0x00，Extended+SA）。</summary>
    public Task<(bool Ok, string Detail)> DisableChargingAsync(CancellationToken ct = default)
        => SetChargingAsync(0x00, ct);

    /// <summary>写充电开关 2E 21 01 [onOff]：10 03(Extended) → SA → 2E。</summary>
    private async Task<(bool Ok, string Detail)> SetChargingAsync(byte onOff, CancellationToken ct = default)
    {
        var dsc = await Task.Run(() => RequestRetry(ProtoIds.SidDsc, new byte[] { 0x03 }, 3000, ct,
            expectSub: 0x03), ct);
        if (!dsc.Ok)
            return (false, $"10 03 进扩机会话失败：{dsc.Error}");
        Log?.Invoke("进入扩机会话 10 03");
        await Task.Run(() => _uds.KeepAlive(), ct);

        var signer = SeedSigner.FromDefaultKey();
        if (signer is null)
            return (false, "缺少 keys/private.pem，无法 SA 解锁");
        if (!await SecurityAccess.UnlockAsync(_uds, signer, m => Log?.Invoke(m), ct))
            return (false, "SA 解锁失败");

        var w = await Task.Run(() => RequestRetry(ProtoIds.SidWdbi,
            new byte[] { 0x21, 0x01, onOff }, 5000, ct, expectSub: 0x21,
            expectDid: ProtoIds.DidChargerEnable), ct);
        if (!w.Ok)
            return (false, $"2E 21 01 {onOff:X2} 失败：{w.Error}");
        Log?.Invoke($"2E 21 01 {onOff:X2} {(onOff != 0 ? "充电已开启" : "充电已停止")}");
        return (true, onOff != 0 ? "充电已开启" : "充电已停止");
    }

    /// <summary>
    /// OTA 升级（通信设计对齐 QiChargerStationTool 的 RunUpgradeAsync）：
    /// XATO 镜像解析（magic/长度/CRC32 烧前校验）→ 唤醒就绪 → 门禁（10 02 +
    /// SA + 2E 20 10，NRC 0x22/0x33 会话/安全丢失时整套重做一次）→ 31 擦除
    /// 备份区 → 34/36 传输（进度回调 + 逐块耗时遥测）→ 37 收尾（设备自复位
    /// 搬运，异常容忍）→ **版本回读验证**（等复位搬运后读 0xF195 比对镜像
    /// 版本，验签失败回滚会在此暴露）。
    /// otaTarget：0x01=MCU1/APP（XATO 签名镜像），0x04=MCU2/Qi（原始 bin）。
    /// transferBlock：请求块长上限；0=按 0x34 应答宣告的 maxBlockLen 自适应
    /// （固件 MCU1=256 / MCU2=22，往返次数减半提速），工具侧封顶 256。
    /// </summary>
    public async Task<(bool Ok, string Detail)> OtaUpgradeAsync(
        string imagePath,
        byte otaTarget = 0x01,
        int transferBlock = 0,
        IProgress<OtaProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return (false, $"固件不存在: {imagePath}");

        byte[] raw = await Task.Run(() => File.ReadAllBytes(imagePath), ct);
        string verHint;
        if (otaTarget == 0x01)
        {
            // AT32 OTA 走 XATO 签名镜像
            var image = XatoImage.Parse(raw);
            verHint = image.ExtractSwVersion() ?? "";
            Log?.Invoke($"[OTA] XATO 镜像 {raw.Length}B ver={verHint} 目标=MCU1/APP");
        }
        else
        {
            // Qi IAP 原始 bin，无需 XATO 头
            verHint = "";
            Log?.Invoke($"[IAP] 原始固件 {raw.Length}B 目标=MCU2/Qi");
        }

        // 刷写前先等 APP/UDS 就绪，避免踩到 Boot 窗口
        if (!await WaitReadyAsync(ct))
            return (false, "设备未就绪（唤醒/探活超时）");

        // 门禁（对齐维修工具 TryGateAsync）：10 02 必须校验应答子功能 0x02
        // （只认 SID(0x50) 时队列里迟到的 50 03 会被误判成功）；ZQWL 偶发
        // 丢帧 → 未通过补发一次；2E 20 10 拿 0x22/0x33 = 会话/安全状态已丢
        // （设备复位/S3 等）→ 整套 10 02 + SA 重做一次再试。
        async Task<(bool Ok, byte Nrc)> TryGateAsync()
        {
            UdsResult d = UdsResult.Fail("未发送");
            for (int t = 1; t <= 2; t++)
            {
                d = await Task.Run(() => _uds.TryRequest(ProtoIds.SidDsc,
                    new byte[] { 0x02 }, expectSub: 0x02, ct: ct), ct);
                if (d.Ok) break;
                Log?.Invoke($"[10 02] 第 {t} 次失败: {d.Error}");
            }
            if (!d.Ok)
                return (false, d.Nrc);
            Log?.Invoke("进入编程会话 10 02");

            var signer = SeedSigner.FromDefaultKey();
            if (signer is null)
                return (false, (byte)0);
            if (!await SecurityAccess.UnlockAsync(_uds, signer, m => Log?.Invoke("[SA] " + m), ct))
                return (false, (byte)0);

            var w = await Task.Run(() => _uds.TryRequest(ProtoIds.SidWdbi,
                new byte[] { 0x20, 0x10, otaTarget }, timeoutMs: 4000,
                expectSub: 0x20, ct: ct, expectDid: ProtoIds.DidFwType), ct);
            if (!w.Ok)
            {
                Log?.Invoke($"[2E 20 10] 失败: {w.Error}");
                return (false, w.Nrc);
            }
            return (true, (byte)0);
        }

        var (gateOk, gateNrc) = await TryGateAsync();
        if (!gateOk && (gateNrc is 0x22 or 0x33))
        {
            Log?.Invoke($"[恢复] NRC 0x{gateNrc:X2} 会话/安全丢失，重做 10 02 + SA 后重试 2E 20 10…");
            (gateOk, gateNrc) = await TryGateAsync();
        }
        if (!gateOk)
        {
            return (false, gateNrc != 0
                ? $"2E 20 10: NRC 0x{gateNrc:X2}（重试后仍失败）"
                : "门禁失败（10 02 / SA）");
        }
        Log?.Invoke($"2E 20 10 {otaTarget:X2} 选择目标 OK");

        // 擦除 + 下载 + 传输整体放一个 Task.Run（逐块 Task.Run 会白付调度开销），
        // 进度经 IProgress 回传 UI 线程
        int size = raw.Length;
        return await Task.Run<(bool Ok, string Detail)>(async () =>
        {
            var erase = _uds.TryRequest(ProtoIds.SidRc,
                new byte[] { 0x01, 0xFF, 0x00 }, timeoutMs: 15_000, waitPendingMs: 90_000, ct: ct);
            if (!erase.Ok)
                return (false, $"31 擦除失败: {erase.Error}");
            Log?.Invoke("31 擦除备份区完成");
            progress?.Report(new OtaProgress(0, "擦除完成"));

            var rd = _uds.TryRequest(ProtoIds.SidRd, new byte[]
            {
                0x00, 0x44, 0x08, 0x01, 0x00, 0x00,
                (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size,
            }, timeoutMs: 8000, ct: ct);
            if (!rd.Ok)
                return (false, $"34 RequestDownload 失败: {rd.Error}");

            // 34 正响应 74 20 <maxBlockLen:2B>：按设备宣告取块长（固件
            // MCU1=256 / MCU2=22）——块长翻倍则 0x36 往返次数减半，传输
            // 近似提速一倍；请求块长只作上限（0=自适应）。工具侧封顶 256。
            ushort advertised = 128;
            if (rd.Data.Length >= 4)
            {
                ushort m = (ushort)((rd.Data[2] << 8) | rd.Data[3]);
                if (m > 0) advertised = m;
            }
            int block = transferBlock > 0 ? Math.Min(transferBlock, advertised) : advertised;
            if (block > 256) block = 256;
            if (block < 1) block = 1;
            Log?.Invoke($"34 RequestDownload {size}B @ 0x08010000 block={block}（设备宣告 {advertised}）");

            int off = 0, seq = 1, blk = 0;
            int nblocks = (size + block - 1) / block;
            int lastPct = -1;
            long sumSend = 0, sumRecv = 0, sumFc = 0;
            var swTr = System.Diagnostics.Stopwatch.StartNew();
            while (off < size)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(block, size - off);
                var payload = new byte[1 + n];
                payload[0] = (byte)(seq & 0xFF);
                Buffer.BlockCopy(raw, off, payload, 1, n);
                var t = _uds.TryRequest(ProtoIds.SidTd, payload,
                    timeoutMs: 8000, waitPendingMs: 30_000, ct: ct,
                    expectSub: (byte)(seq & 0xFF), requestRetries: 2);
                if (!t.Ok)
                    return (false, $"36 块 {blk + 1}/{nblocks} 失败: {t.Error}");
                sumSend += _uds.LastSendMs;
                sumRecv += _uds.LastRecvMs;
                sumFc += _uds.LastFcTries;
                off += n;
                blk++;
                seq = (seq + 1) & 0xFF;

                // 每 2% 或首尾报一次进度（带均速，便于判读传输效率）
                int pct = size > 0 ? (int)(off * 100L / size) : 100;
                if (pct != lastPct && (pct % 2 == 0 || off >= size))
                {
                    lastPct = pct;
                    progress?.Report(new OtaProgress(pct,
                        $"传输中 {blk}/{nblocks} 块  {off}/{size}B  " +
                        $"均 {swTr.Elapsed.TotalMilliseconds / blk:F1}ms/块"));
                }
            }
            Log?.Invoke($"[计时] 传输 {swTr.Elapsed.TotalSeconds:F1}s / {nblocks} 块 = " +
                        $"{swTr.Elapsed.TotalMilliseconds / nblocks:F1}ms/块" +
                        $"（发 {sumSend / nblocks} + 等 {sumRecv / nblocks}ms，FC重试 {sumFc}/{nblocks}）");

            try
            {
                _uds.Request(ProtoIds.SidRte,
                    Array.Empty<byte>(), timeoutMs: 8000, waitPendingMs: 60_000, ct: ct);
                Log?.Invoke("37 TransferExit 确认，设备自复位搬运");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"37 异常（设备可能已自复位）: {ex.Message}");
            }
            progress?.Report(new OtaProgress(100, "完成"));

            // 版本回读验证（闭环到 PASS/FAIL）：设备自复位后 BOOT 搬运
            // 备份区→App 并验签，验签失败回滚旧 App——复位后读 0xF195
            // 与镜像版本串比对，不符即暴露。等 5s 完成搬运再唤醒读取。
            if (otaTarget == 0x01 && verHint.Length > 0)
            {
                Log?.Invoke("[验证] 等待设备自复位搬运（约 5s）…");
                await Task.Delay(5000, ct);
                if (!await WaitReadyAsync(ct))
                    return (false, "OTA 传输完成，但复位后设备无应答（无法验证版本）");
                var vs = await Task.Run(() => _uds.TryReadAllVersions(ct), ct);
                var read = vs.Mcu1App;
                if (string.Equals(read, verHint, StringComparison.Ordinal))
                {
                    Log?.Invoke($"[验证] 版本回读 {read} = 镜像版本，升级生效");
                }
                else
                {
                    return (false,
                        $"OTA 后版本回读不符：镜像={verHint} 实际={read ?? "(空)"}" +
                        "（疑验签失败已回滚，请重试或换镜像）");
                }
            }
            return (true, $"传输 {size}B 完成" + (verHint.Length > 0 ? $" ver={verHint}" : ""));
        }, ct);
    }

    /// <summary>
    /// 不抛异常的 UDS 请求 + 一次重试（无应答的偶发竞态重试即过；
    /// 有 NRC = 设备应答了，不重试）。
    /// </summary>
    private UdsResult RequestRetry(byte sid, byte[] payload, int timeoutMs, CancellationToken ct,
                                   byte? expectSub = null, ushort? expectDid = null)
    {
        var r = _uds.TryRequest(sid, payload, timeoutMs: timeoutMs, expectSub: expectSub,
                                ct: ct, expectDid: expectDid);
        if (r.Ok || r.Nrc != 0)
            return r;
        return _uds.TryRequest(sid, payload, timeoutMs: timeoutMs, expectSub: expectSub,
                               ct: ct, expectDid: expectDid);
    }

    /// <summary>22 F18C 解析（32B ASCII 空格填充，去尾部 0x00/0x20）。
    /// 超时 6s：35B 多帧应答含设备 FF 重发窗口（~2s）+CF 传输。
    /// 不抛异常；失败返回 null（供上层重试）。</summary>
    private string? ReadSnCore()
    {
        var r = RequestRetry(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x8C }, 6000, CancellationToken.None,
            expectDid: ProtoIds.DidSn);
        if (!r.Ok || r.Data.Length < 4)
            return null;
        int len = r.Data.Length - 3;
        while (len > 0 && (r.Data[3 + len - 1] is 0x00 or 0x20))
            len--;
        return Encoding.ASCII.GetString(r.Data, 3, len);
    }
}
