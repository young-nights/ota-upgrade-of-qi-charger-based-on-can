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
        if (fc is null) throw new TimeoutException("ISO-TP FC timeout (ECU did not send FlowControl)");

        byte bs = fc.Value.Data.Length > 1 ? fc.Value.Data[1] : (byte)0;
        int stminMs = StminToMs(fc.Value.Data.Length > 2 ? fc.Value.Data[2] : (byte)0);

        int offset = 6, sn = 1, inBlock = 0, cfCount = 0;
        while (offset < total)
        {
            if (bs != 0 && inBlock >= bs)
            {
                var f2 = WaitFc(1500) ?? throw new TimeoutException("ISO-TP FC timeout (BS)");
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

    /// <summary>Receive one complete UDS payload. Skips lifecycle/non-matching noise.</summary>
    public byte[] Receive(Func<byte[], bool> match, int timeoutMs, CancellationToken ct = default)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        byte[]? ffBuf = null;
        int ffTotal = 0, expectSn = 1;
        var cfBuf = new List<byte>();
        int seen = 0, dropped = 0;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            Pump();
            while (_rxq.TryDequeue(out var f))
            {
                seen++;
                if ((f.Id & 0x1FFFFFFF) != (_rxId & 0x1FFFFFFF) || f.Data.Length == 0)
                {
                    dropped++;
                    // 噪声帧 ID 打出来，方便确认设备在发什么
                    Log($"[RX?] id={f.Id:X8x} {Hex(f.Data)}");
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
        Log($"[RX] timeout: 收到 {seen} 帧（噪声 {dropped}），无匹配 UDS 应答");
        throw new TimeoutException(
            $"UDS response timeout (收到 {seen} 帧/噪声 {dropped}，目标 ID 0x{_rxId:X8})");
    }

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
