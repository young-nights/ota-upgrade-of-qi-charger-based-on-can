using System.Text;
using QiChargerStationTool.Core.Protocol;
using QiChargerStationTool.Core.Security;
using QiChargerStationTool.Core.Transport;

namespace ProductionStationTool.Core.Station;

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

        var dsc = await Task.Run(() => RequestRetry(ProtoIds.SidDsc, new byte[] { 0x02 }, 3000, ct), ct);
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
        var w = await Task.Run(() => RequestRetry(ProtoIds.SidWdbi, payload, 5000, ct), ct);
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
        var dsc = await Task.Run(() => RequestRetry(ProtoIds.SidDsc, new byte[] { 0x03 }, 3000, ct), ct);
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
            new byte[] { 0x21, 0x01, onOff }, 5000, ct), ct);
        if (!w.Ok)
            return (false, $"2E 21 01 {onOff:X2} 失败：{w.Error}");
        Log?.Invoke($"2E 21 01 {onOff:X2} {(onOff != 0 ? "充电已开启" : "充电已停止")}");
        return (true, onOff != 0 ? "充电已开启" : "充电已停止");
    }

    /// <summary>
    /// 不抛异常的 UDS 请求 + 一次重试（无应答的偶发竞态重试即过；
    /// 有 NRC = 设备应答了，不重试）。
    /// </summary>
    private UdsResult RequestRetry(byte sid, byte[] payload, int timeoutMs, CancellationToken ct)
    {
        var r = _uds.TryRequest(sid, payload, timeoutMs: timeoutMs, ct: ct);
        if (r.Ok || r.Nrc != 0)
            return r;
        return _uds.TryRequest(sid, payload, timeoutMs: timeoutMs, ct: ct);
    }

    /// <summary>22 F18C 解析（32B ASCII 空格填充，去尾部 0x00/0x20）。
    /// 超时 6s：35B 多帧应答含设备 FF 重发窗口（~2s）+CF 传输。
    /// 不抛异常；失败返回 null（供上层重试）。</summary>
    private string? ReadSnCore()
    {
        var r = RequestRetry(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x8C }, 6000, CancellationToken.None);
        if (!r.Ok || r.Data.Length < 4)
            return null;
        int len = r.Data.Length - 3;
        while (len > 0 && (r.Data[3 + len - 1] is 0x00 or 0x20))
            len--;
        return Encoding.ASCII.GetString(r.Data, 3, len);
    }
}
