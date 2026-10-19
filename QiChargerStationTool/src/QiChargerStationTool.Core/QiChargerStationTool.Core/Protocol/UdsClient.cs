namespace QiChargerStationTool.Core.Protocol;

/// <summary>UDS 请求结果：失败时填 Error，不靠异常上抛。</summary>
public readonly record struct UdsResult(bool Ok, byte[] Data, byte Nrc, string Error)
{
    public static UdsResult Fail(string error, byte nrc = 0) => new(false, Array.Empty<byte>(), nrc, error);
    public static UdsResult OkData(byte[] data) => new(true, data, 0, "");
}

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

    /// <summary>
    /// 发送并等待正响应。失败返回 <see cref="UdsResult"/>（Ok=false），不抛异常。
    /// </summary>
    public UdsResult TryRequest(byte sid, byte[] payload, int timeoutMs = 3000,
                                int waitPendingMs = 45_000, byte? expectSub = null,
                                CancellationToken ct = default)
    {
        try
        {
            var uds = new byte[1 + payload.Length];
            uds[0] = sid;
            payload.CopyTo(uds, 1);
            _tp.Send(uds);

            long pendingDeadline = Environment.TickCount64 + waitPendingMs;
            while (true)
            {
                if (ct.IsCancellationRequested)
                    return UdsResult.Fail("已取消");
                var data = _tp.TryReceive(
                    d =>
                    {
                        if (d.Length == 0) return false;
                        if (d[0] == ProtoIds.SidNrc)
                            return d.Length >= 2 && d[1] == sid;
                        if (d[0] != sid + ProtoIds.SidPr) return false;
                        if (expectSub is { } sub && (d.Length < 2 || d[1] != sub)) return false;
                        return true;
                    },
                    timeoutMs, ct);

                if (data is null)
                    return UdsResult.Fail($"UDS 无应答: {_tp.LastTimeoutDetail}");

                if (data[0] == ProtoIds.SidNrc)
                {
                    byte nrc = data.Length >= 3 ? data[2] : (byte)0;
                    if (nrc == ProtoIds.NrcRcrrp)
                    {
                        if (Environment.TickCount64 >= pendingDeadline)
                            return UdsResult.Fail("NRC 0x78 响应挂起超时", nrc);
                        Log("[RX] NRC 0x78 responsePending");
                        Thread.Sleep(200);
                        continue;
                    }
                    var tmp = new UdsException(sid, nrc, $"NRC 0x{nrc:X2}");
                    return UdsResult.Fail(tmp.Explain(payload), nrc);
                }
                return UdsResult.OkData(data);
            }
        }
        catch (OperationCanceledException)
        {
            return UdsResult.Fail("已取消");
        }
        catch (Exception ex)
        {
            return UdsResult.Fail(ex.Message);
        }
    }

    /// <summary>需要正响应的封装：失败抛 <see cref="UdsException"/>（供 Station 步骤用）。</summary>
    public byte[] Request(byte sid, byte[] payload, int timeoutMs = 3000,
                          int waitPendingMs = 45_000, byte? expectSub = null,
                          CancellationToken ct = default)
    {
        var r = TryRequest(sid, payload, timeoutMs, waitPendingMs, expectSub, ct);
        if (r.Ok) return r.Data;
        throw new UdsException(sid, r.Nrc, r.Error);
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

    /// <summary>读软件版本；失败返回 (false, 错误信息)。</summary>
    public (bool Ok, string Version, string Error) TryReadSwVersion(CancellationToken ct = default)
    {
        var r = TryRequest(ProtoIds.SidRdbi, new byte[] { 0xF1, 0x95 }, timeoutMs: 3000, ct: ct);
        if (!r.Ok) return (false, "", r.Error);
        if (r.Data.Length < 4) return (false, "", "F195 响应过短");
        int start = 3;
        int len = r.Data.Length - start;
        while (len > 0 && (r.Data[start + len - 1] is 0x00 or 0x20)) len--;
        return (true, System.Text.Encoding.ASCII.GetString(r.Data, start, len), "");
    }

    public string ReadSwVersion(CancellationToken ct = default)
    {
        var (ok, ver, err) = TryReadSwVersion(ct);
        if (!ok) throw new UdsException(ProtoIds.SidRdbi, 0, err);
        return ver;
    }

    public byte ReadSlot()
    {
        var rx = Request(ProtoIds.SidRdbi, new byte[] { 0x21, 0x13 });
        return rx.Length >= 4 ? rx[3] : (byte)0xFF;
    }

    /// <summary>WriteDataByIdentifier：payload = DID(2B) + data。失败返回 UdsResult，不抛。</summary>
    public UdsResult TryWriteDid(ushort did, byte[] data, int timeoutMs = 4000)
        => TryRequest(ProtoIds.SidWdbi,
            new byte[] { (byte)(did >> 8), (byte)did }.Concat(data).ToArray(), timeoutMs);

    /// <summary>ReadDataByIdentifier → 数据区；失败返回 (null, error)。</summary>
    public (byte[]? Data, string Error) TryReadDid(ushort did, int timeoutMs = 3000)
    {
        var r = TryRequest(ProtoIds.SidRdbi,
            new byte[] { (byte)(did >> 8), (byte)did }, timeoutMs);
        if (!r.Ok) return (null, r.Error);
        if (r.Data.Length < 3) return (null, $"DID 0x{did:X4} 响应过短");
        return (r.Data[3..], "");
    }

    /// <summary>ReadDataByIdentifier → 数据区（去掉 62 xx xx）。</summary>
    public byte[] ReadDid(ushort did, int timeoutMs = 3000)
    {
        var (data, err) = TryReadDid(did, timeoutMs);
        return data ?? throw new UdsException(ProtoIds.SidRdbi, 0, err);
    }

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
            PowerLimit = TryU8(ProtoIds.DidPowerLimit),
        };
    }

    /// <summary>
    /// 设置 Qi 功率档位码。失败返回 UdsResult，不抛。
    /// </summary>
    public UdsResult TryWriteQiPowerLimit(byte powerCode)
    {
        if (powerCode is not (ProtoIds.Power5W or ProtoIds.Power10W or ProtoIds.Power15W))
            return UdsResult.Fail("功率档位码仅支持 0x01(5W) / 0x02(10W) / 0x03(15W)");
        return TryWriteDid(ProtoIds.DidPowerLimit, new byte[] { powerCode });
    }

    public void WriteQiPowerLimit(byte powerCode)
    {
        var r = TryWriteQiPowerLimit(powerCode);
        if (!r.Ok) throw new UdsException(ProtoIds.SidWdbi, r.Nrc, r.Error);
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

    /// <summary>NRC 中文名。</summary>
    public string NrcName => Nrc switch
    {
        0x13 => "0x13 响应长度不正确",
        0x22 => "0x22 条件不满足（会话/状态不对）",
        0x24 => "0x24 请求顺序错误",
        0x31 => "0x31 请求超范围（DID 不支持/不在当前模式）",
        0x33 => "0x33 安全访问被拒（未解锁）",
        0x35 => "0x35 密钥无效",
        0x36 => "0x36 失败次数超限",
        0x37 => "0x37 需延时重试（SA 锁定）",
        0x72 => "0x72 通用编程失败",
        0x78 => "0x78 响应挂起",
        _ => $"NRC 0x{Nrc:X2}",
    };

    /// <summary>结合 SID/DID 给可操作建议。</summary>
    public string Explain(byte[]? requestPayload = null)
    {
        var s = NrcName;
        if (Nrc == 0x31 && requestPayload is { Length: >= 2 })
        {
            ushort did = (ushort)((requestPayload[0] << 8) | requestPayload[1]);
            if (did is 0xF18C or 0xF195 or 0xF193)
                s += " —— 该 DID 在 Boot Safe Mode 不提供，设备可能未进 APP（查 0x18FF480D/ABT 心跳）";
            else
                s += $" —— DID 0x{did:X4} 不支持或当前模式不可读";
        }
        else if (Nrc == 0x22)
            s += " —— 先切换会话（10 03 扩展 / 10 02 编程）再试";
        else if (Nrc == 0x33)
            s += " —— 需 SecurityAccess 解锁";
        else if (Nrc is 0x36 or 0x37)
            s += " —— SA 锁定，等约 30s 后重试";
        return s;
    }
}
