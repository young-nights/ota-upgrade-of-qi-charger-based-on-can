namespace QiChargerStationTool.Core.Protocol;

/// <summary>UDS client on top of ISO-TP. Lifecycle noise on resp ID is skipped.</summary>
public sealed class UdsClient
{
    private readonly IsoTpClient _tp;

    public event Action<string>? OnLog;

    public UdsClient(IsoTpClient tp)
    {
        _tp = tp;
        _tp.OnLog += m => OnLog?.Invoke(m);
    }

    private void Log(string m) => OnLog?.Invoke(m);

    public byte[] Request(byte sid, byte[] payload, int timeoutMs = 3000,
                          int waitPendingMs = 45_000, byte? expectSub = null)
    {
        var uds = new byte[1 + payload.Length];
        uds[0] = sid;
        payload.CopyTo(uds, 1);
        _tp.Send(uds);

        long pendingDeadline = Environment.TickCount64 + waitPendingMs;
        while (true)
        {
            var data = _tp.Receive(
                d =>
                {
                    if (d.Length == 0) return false;
                    if (d[0] == ProtoIds.SidNrc)
                        return d.Length >= 2 && d[1] == sid;
                    if (d[0] != sid + ProtoIds.SidPr) return false;
                    if (expectSub is { } sub && (d.Length < 2 || d[1] != sub)) return false;
                    return true;
                },
                timeoutMs);

            if (data[0] == ProtoIds.SidNrc)
            {
                byte nrc = data.Length >= 3 ? data[2] : (byte)0;
                if (nrc == ProtoIds.NrcRcrrp)
                {
                    if (Environment.TickCount64 >= pendingDeadline)
                        throw new UdsException(sid, nrc, "only NRC 0x78 (responsePending)");
                    Log("[RX] NRC 0x78 responsePending");
                    Thread.Sleep(200);
                    continue;
                }
                throw new UdsException(sid, nrc, $"NRC 0x{nrc:X2}");
            }
            return data;
        }
    }

    public byte[] RequestSuppress(byte sid, byte[] payload)
    {
        // 3E 80 etc. — fire and forget
        var uds = new byte[1 + payload.Length];
        uds[0] = sid;
        payload.CopyTo(uds, 1);
        _tp.Send(uds, quiet: true);
        return Array.Empty<byte>();
    }

    public void KeepAlive() => RequestSuppress(ProtoIds.SidTp, new byte[] { 0x80 });

    public string ReadSwVersion()
    {
        var rx = Request(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x95 }, timeoutMs: 5000);
        if (rx.Length < 4) throw new InvalidOperationException("F195 response too short");
        int start = 3;
        int len = rx.Length - start;
        // strip trailing 0x00 / spaces
        while (len > 0 && (rx[start + len - 1] is 0x00 or 0x20)) len--;
        return System.Text.Encoding.ASCII.GetString(rx, start, len);
    }

    public byte ReadSlot()
    {
        var rx = Request(ProtoIds.SidRdbi, new byte[] { 0x21, 0x13 });
        return rx.Length >= 4 ? rx[3] : (byte)0xFF;
    }
}

public sealed class UdsException : Exception
{
    public byte Sid { get; }
    public byte Nrc { get; }

    public UdsException(byte sid, byte nrc, string message) : base(message)
    {
        Sid = sid;
        Nrc = nrc;
    }
}
