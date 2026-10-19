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

    /// <summary>ReadDataByIdentifier → 数据区（去掉 62 xx xx）。</summary>
    public byte[] ReadDid(ushort did, int timeoutMs = 3000)
    {
        var rx = Request(ProtoIds.SidRdbi,
            new byte[] { (byte)(did >> 8), (byte)did }, timeoutMs);
        if (rx.Length < 3) throw new InvalidOperationException($"DID 0x{did:X4} 响应过短");
        return rx[3..];
    }

    /// <summary>WriteDataByIdentifier：payload = DID(2B) + data。</summary>
    public byte[] WriteDid(ushort did, byte[] data, int timeoutMs = 4000)
        => Request(ProtoIds.SidWdbi,
            new byte[] { (byte)(did >> 8), (byte)did }.Concat(data).ToArray(), timeoutMs);

    /// <summary>Qi 芯片状态全量查询（任意会话可读，无需 SA）。</summary>
    public QiChipStatus ReadQiChipStatus(CancellationToken ct = default)
    {
        var iapRaw = ReadDid(ProtoIds.DidQiIapStatus, timeoutMs: 4000);
        QiChipStatusCodec.ParseIapStatus(iapRaw,
            out var st, out var prog, out var maj, out var min, out var pat, out var sent, out var total);

        byte? TryU8(ushort did)
        {
            try { return QiChipStatusCodec.ReadU8(ReadDid(did)); }
            catch { return null; }
        }
        ushort? TryU16(ushort did)
        {
            try { return QiChipStatusCodec.ReadU16Le(ReadDid(did)); }
            catch { return null; }
        }

        byte? vRaw = null, iRaw = null;
        try
        {
            var vi = ReadDid(ProtoIds.DidInputVi);
            if (vi.Length >= 2) { vRaw = vi[0]; iRaw = vi[1]; }
        }
        catch { /* optional */ }

        return new QiChipStatus
        {
            IapState = st,
            IapProgress = prog,
            QiVerMajor = maj,
            QiVerMinor = min,
            QiVerPatch = pat,
            IapSent = sent,
            IapTotal = total,
            ChargeState = TryU8(ProtoIds.DidChargeState),
            OutputPowerMw = TryU16(ProtoIds.DidOutputPower),
            InputVoltageRaw = vRaw,
            InputCurrentRaw = iRaw,
            PcbTempC = TryU8(ProtoIds.DidPcbTemp),
            FodStatus = TryU8(ProtoIds.DidFodStatus),
            FaultCode = TryU8(ProtoIds.DidFaultCode),
            ThermalDerate = TryU8(ProtoIds.DidThermalDerate),
            PowerLimit = TryU16(ProtoIds.DidPowerLimit),
        };
    }

    /// <summary>
    /// 设置 Qi 功率档位。固件门禁：扩展会话(10 03)+SA 解锁后写 DID 0x210D
    /// （uint16 LE，仅 500/1000/1500）。
    /// </summary>
    public void WriteQiPowerLimit(ushort powerLimit)
    {
        if (powerLimit is not (ProtoIds.PowerLimit5W or ProtoIds.PowerLimit10W or ProtoIds.PowerLimit15W))
            throw new ArgumentOutOfRangeException(nameof(powerLimit), powerLimit,
                "功率档位仅支持 500(5W) / 1000(10W) / 1500(15W)");
        WriteDid(ProtoIds.DidPowerLimit,
            new byte[] { (byte)(powerLimit & 0xFF), (byte)(powerLimit >> 8) });
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
