using System.Collections.Concurrent;
using QiChargerStationTool.Core.Transport;

namespace QiChargerStationTool.Core.Protocol;

/// <summary>
/// Manual ISO-TP (ISO 15765-2) over <see cref="ICanTransport"/>.
/// Mirrors the proven WSL standalone / zcanpro_ext_ota_auto multi-frame path:
/// FF (retransmit if no FC) → CF… ; last CF retried once; RX auto-FC.
/// </summary>
public sealed class IsoTpClient
{
    private static readonly byte[] FcCts = { 0x30, 0x00, 0x00, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC };

    private readonly ICanTransport _tp;
    private readonly uint _txId;
    private readonly uint _rxId;
    private readonly ConcurrentQueue<CanFrame> _rxq = new();

    public event Action<string>? OnLog;

    public IsoTpClient(ICanTransport transport, uint txId = ProtoIds.UdsReqId,
                       uint rxId = ProtoIds.UdsRespId)
    {
        _tp = transport;
        _txId = txId;
        _rxId = rxId;
    }

    private void Log(string m) => OnLog?.Invoke(m);

    /// <summary>解码 Boot M1–M4 / 生命周期帧，便于一眼看出设备在 Boot 还是 APP。</summary>
    private static string DecodeDiag(uint id, byte[] d)
    {
        if (id == ProtoIds.BootDiagId && d.Length >= 2)
        {
            return d[0] switch
            {
                0xA1 when d.Length >= 5 =>
                    $"Boot M1 src={d[1]} magic_ok={d[2]} ver_ok={d[3]} crc_ok={d[4]}",
                0xA2 when d.Length >= 3 =>
                    $"Boot M2 result={d[1]:X2} detail={d[2]:X2}",
                0xA3 when d.Length >= 4 =>
                    $"Boot M3 pass={d[1]} fail_step={d[2]:X2} target={d[3]:X2}",
                0xA4 when d.Length >= 5 =>
                    $"Boot M4 jump=0x{d[4]:X2}{d[3]:X2}{d[2]:X2}{d[1]:X2}",
                _ => $"Boot Diag {Convert.ToHexString(d)}",
            };
        }
        if (id == ProtoIds.LifecycleId && d.Length >= 2)
        {
            // 帧格式 lifecycle.c：[0]=状态码 [1]=0x41 'A' [2..7]=参数
            // 状态码见 docs/3：0x01=BOOTUP 0x02=OPERATIONAL 0x04=SHUTDOWN
            byte state = d[0];
            string tag = state switch
            {
                0x01 => "BOOTUP",
                0x02 => "OPERATIONAL",
                0x03 => "OPERATIONAL",
                0x04 => "SHUTDOWN",
                0x06 => "SHUTDOWN",
                _ => $"STATE=0x{state:X2}",
            };
            // 参数区 0x41 后的 ASCII 标记：00=上电 / 57 4B='WK' 唤醒 / 42 54='ABT' / 53 42='SB'
            if (d.Length >= 4 && d[1] == 0x41)
            {
                if (d[2] == 0x57 && d[3] == 0x4B)
                    return $"生命周期 [{tag}-WK 唤醒] {Convert.ToHexString(d.AsSpan(0, Math.Min(8, d.Length)).ToArray())}";
                if (d[2] == 0x42 && d[3] == 0x54)
                    return $"生命周期 [ABT Boot安全模式心跳] cause={(d.Length > 4 ? d[4] : (byte)0):X2} fail_step={(d.Length > 5 ? d[5] : (byte)0):X2}（卡在 Boot，APP 未运行）";
                if (d[2] == 0x53 && d[3] == 0x42)
                    return $"生命周期 [{tag}-SB 待机]";
                if (d[2] == 0x00)
                    return $"生命周期 [{tag} 上电/复位标识]";
            }
            return $"生命周期 [{tag}] {Convert.ToHexString(d.AsSpan(0, Math.Min(4, d.Length)).ToArray())}";
        }
        return "";
    }

    /// <summary>Move transport RX into local queue (called by UdsClient pump loop).</summary>
    public void Pump()
    {
        while (_tp.TryReceive(out var f, 0))
            _rxq.Enqueue(f);
    }

    public void Send(byte[] uds, bool quiet = false)
    {
        Pump();
        if (uds.Length == 0) throw new ArgumentException("empty UDS");
        if (uds.Length <= 7)
        {
            var sf = new byte[8];
            sf[0] = (byte)uds.Length;
            uds.CopyTo(sf, 1);
            for (int i = 1 + uds.Length; i < 8; i++) sf[i] = 0xCC;
            _tp.Send(new CanFrame(_txId, sf));
            if (!quiet) Log($"[TX] SF {Hex(uds)}");
            return;
        }

        // FF + CFs
        int total = uds.Length;
        CanFrame? fc = null;
        for (int tryN = 0; tryN < 3 && fc is null; tryN++)
        {
            var ff = new byte[8];
            ff[0] = (byte)(0x10 | ((total >> 8) & 0x0F));
            ff[1] = (byte)(total & 0xFF);
            int n = Math.Min(6, total);
            Array.Copy(uds, 0, ff, 2, n);
            for (int i = 2 + n; i < 8; i++) ff[i] = 0xCC;
            _tp.Send(new CanFrame(_txId, ff));
            if (!quiet) Log($"[TX] FF try{tryN + 1} len={total} {Hex(uds.AsSpan(0, Math.Min(6, total)).ToArray())}");
            fc = WaitFc(1500);
        }
        if (fc is null)
            throw new InvalidOperationException("ISO-TP FC timeout (ECU did not send FlowControl)");

        byte bs = fc.Value.Data.Length > 1 ? fc.Value.Data[1] : (byte)0;
        int stminMs = StminToMs(fc.Value.Data.Length > 2 ? fc.Value.Data[2] : (byte)0);

        int offset = 6, sn = 1, inBlock = 0, cfCount = 0;
        while (offset < total)
        {
            if (bs != 0 && inBlock >= bs)
            {
                var f2 = WaitFc(1500)
                    ?? throw new InvalidOperationException("ISO-TP FC timeout (BS)");
                bs = f2.Data.Length > 1 ? f2.Data[1] : (byte)0;
                stminMs = StminToMs(f2.Data.Length > 2 ? f2.Data[2] : (byte)0);
                inBlock = 0;
            }
            if (stminMs > 0) Thread.Sleep(stminMs);
            int chunk = Math.Min(7, total - offset);
            var cf = new byte[8];
            cf[0] = (byte)(0x20 | (sn & 0x0F));
            Array.Copy(uds, offset, cf, 1, chunk);
            for (int i = 1 + chunk; i < 8; i++) cf[i] = 0xCC;
            _tp.Send(new CanFrame(_txId, cf));
            // last CF: resend once (host→ECU multi-frame reliability)
            if (offset + chunk >= total)
            {
                Thread.Sleep(10);
                _tp.Send(new CanFrame(_txId, cf));
            }
            offset += chunk;
            sn = (sn + 1) & 0x0F;
            inBlock++;
            cfCount++;
        }
        if (!quiet) Log($"[TX] MF CF={cfCount} total={total}");
    }

    /// <summary>
    /// Receive one complete UDS payload. Returns null on timeout (不抛异常，避免弹崩溃框)。
    /// Skips lifecycle/non-matching noise.
    /// </summary>
    public byte[]? TryReceive(Func<byte[], bool> match, int timeoutMs, CancellationToken ct = default)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        byte[]? ffBuf = null;
        int ffTotal = 0, expectSn = 1;
        var cfBuf = new List<byte>();
        int seen = 0, dropped = 0;
        var noiseIds = new Dictionary<uint, int>();
        _sawBootup = false;

        while (Environment.TickCount64 < deadline)
        {
            if (ct.IsCancellationRequested) return null;
            Pump();
            while (_rxq.TryDequeue(out var f))
            {
                seen++;
                if ((f.Id & 0x1FFFFFFF) != (_rxId & 0x1FFFFFFF) || f.Data.Length == 0)
                {
                    dropped++;
                    uint nid = f.Id & 0x1FFFFFFF;
                    noiseIds[nid] = noiseIds.TryGetValue(nid, out var c) ? c + 1 : 1;
                    if (nid == ProtoIds.LifecycleId && f.Data.Length >= 3
                        && f.Data[0] == 0x01 && f.Data[1] == 0x41 && f.Data[2] == 0x00)
                        _sawBootup = true;
                    string diag = DecodeDiag(nid, f.Data);
                    if (noiseIds[nid] <= 3)
                        Log($"[RX?] id={nid:X8} {Hex(f.Data)}{(diag.Length > 0 ? "  → " + diag : "")}");
                    continue;
                }
                int pci = f.Data[0] & 0xF0;
                if (pci == 0x00)
                {
                    int n = f.Data[0] & 0x0F;
                    if (n == 0 || n > 7 || n > f.Data.Length - 1) continue;
                    var data = f.Data.AsSpan(1, n).ToArray();
                    if (match(data)) return data;
                    Log($"[RX] SF 不匹配 {Hex(data)}");
                }
                else if (pci == 0x10)
                {
                    if (ffBuf is not null) continue;
                    ffTotal = ((f.Data[0] & 0x0F) << 8) | f.Data[1];
                    ffBuf = f.Data.AsSpan(2, Math.Min(6, f.Data.Length - 2)).ToArray();
                    _tp.Send(new CanFrame(_txId, FcCts));
                    _tp.Send(new CanFrame(_txId, FcCts)); // double FC, anti-loss
                    cfBuf.Clear();
                    expectSn = 1;
                    Log($"[RX] FF total={ffTotal}");
                }
                else if (pci == 0x20 && ffBuf is not null)
                {
                    int sn = f.Data[0] & 0x0F;
                    if (sn != expectSn) continue; // ignore dup/out-of-order
                    int copy = Math.Min(f.Data.Length - 1, ffTotal - (ffBuf.Length + cfBuf.Count));
                    for (int i = 0; i < copy; i++) cfBuf.Add(f.Data[1 + i]);
                    expectSn = (expectSn + 1) & 0x0F;
                    if (ffBuf.Length + cfBuf.Count >= ffTotal)
                    {
                        var data = new byte[ffTotal];
                        int n = 0;
                        foreach (var b in ffBuf) data[n++] = b;
                        foreach (var b in cfBuf) data[n++] = b;
                        if (match(data)) return data;
                        Log($"[RX] MF 不匹配 {Hex(data)}");
                        ffBuf = null;
                    }
                }
                else if (pci == 0x30)
                {
                    Log($"[RX] FC {Hex(f.Data)}");
                }
            }
            Thread.Sleep(2);
        }

        string noise = string.Join(", ",
            noiseIds.Select(kv => $"0x{kv.Key:X8}×{kv.Value}"));

        // 给出可操作的结论
        string hint = "";
        bool sawBootup = noiseIds.ContainsKey(ProtoIds.LifecycleId) && _sawBootup;
        if (sawBootup)
            hint = "；已见生命周期 BOOTUP（上电标识），APP 刚启动可能尚未就绪，请点「读软件版本」重试";
        else if (noiseIds.ContainsKey(ProtoIds.BootDiagId))
            hint = "；设备在 Boot（收到 0x18FF480D 诊断帧），APP 未运行/UDS 栈不在，请等 APP 起来或先烧录/恢复 APP";
        else if (noiseIds.ContainsKey(ProtoIds.LifecycleId))
            hint = "；仅见生命周期帧，APP 可能刚上电或 UDS 未就绪，请重试";
        else if (dropped == 0 && seen == 0)
            hint = "；总线无任何回包，查 CANH/CANL/终端电阻/盒子 TX";

        Log($"[RX] 超时: 收 {seen} 帧/噪声 {dropped}，目标 0x{_rxId:X8}" +
            (noise.Length > 0 ? $"，噪声ID: {noise}" : "，无任何帧") + hint);
        LastTimeoutDetail =
            $"收到 {seen} 帧/噪声 {dropped}，目标 ID 0x{_rxId:X8}" +
            (noise.Length > 0 ? $"；噪声: {noise}" : "；总线无回包") + hint;
        return null;
    }

    /// <summary>本次等待中是否见过 APP BOOTUP（01 41 00）。</summary>
    private bool _sawBootup;

    /// <summary>最近一次 <see cref="TryReceive"/> 超时的诊断摘要。</summary>
    public string LastTimeoutDetail { get; private set; } = "";

    private CanFrame? WaitFc(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Pump();
            while (_rxq.TryDequeue(out var f))
            {
                if ((f.Id & 0x1FFFFFFF) != (_rxId & 0x1FFFFFFF) || f.Data.Length == 0)
                    continue;
                if ((f.Data[0] & 0xF0) == 0x30) return f;
            }
            Thread.Sleep(2);
        }
        return null;
    }

    private static int StminToMs(byte b)
        => b <= 0x7F ? b : b switch
        {
            0xF1 => 1, 0xF2 => 2, 0xF3 => 3, 0xF4 => 4, 0xF5 => 5,
            0xF6 => 6, 0xF7 => 7, 0xF8 => 8, 0xF9 => 9,
            _ => 125,
        };

    private static string Hex(byte[] d)
        => string.Join(" ", d.Select(b => b.ToString("X2")));
}
