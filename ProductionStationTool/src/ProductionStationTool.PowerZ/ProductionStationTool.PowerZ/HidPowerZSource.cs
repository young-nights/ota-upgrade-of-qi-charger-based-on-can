using HidSharp;

namespace ProductionStationTool.PowerZ;

/// <summary>
/// PowerZ HID 数据源：USB HID 中断端点承载 KM003C 协议（基本 ADC 轮询）。
///
/// 设备族：ChargerLAB KM003C(5FC9:0063)/KM002C(5FC9:0061)、
/// 用户实机 0483:5750（ST 方案兼容机，按 KM003C 协议最佳尝试）。
/// HID 报文布局：首字节 report id=0，其后为协议帧（4B 包头 + 载荷）。
/// 协议对不上时原始报文经 <see cref="Log"/> 输出，供实测样例校准。
/// </summary>
public sealed class HidPowerZSource : IPowerZSource
{
    /// <summary>已知 PowerZ HID VID:PID。</summary>
    public static readonly (int Vid, int Pid)[] KnownIds =
    {
        (0x5FC9, 0x0063),   // KM003C
        (0x5FC9, 0x0061),   // KM002C
        (0x0483, 0x5750),   // 用户实机（ST 方案，协议待实测校准）
    };

    /// <summary>诊断日志（原始报文/错误），上层可接日志面板。</summary>
    public event Action<string>? Log;

    /// <summary>收到一条有效采样。</summary>
    public event Action<PowerSample>? SampleReceived;

    private HidStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>枚举本机已插的 PowerZ HID 设备。</summary>
    public static List<string> ListDevices()
    {
        var result = new List<string>();
        foreach (var (vid, pid) in KnownIds)
        {
            foreach (var d in DeviceList.Local.GetHidDevices(vid, pid, null, null))
            {
                string name;
                try { name = d.GetFriendlyName(); } catch { name = d.DevicePath; }
                result.Add($"VID={vid:X4} PID={pid:X4} {name}");
            }
        }
        return result;
    }

    private static HidDevice? FindDevice()
    {
        foreach (var (vid, pid) in KnownIds)
        {
            foreach (var d in DeviceList.Local.GetHidDevices(vid, pid, null, null))
                return d;
        }
        return null;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_stream is not null)
            return Task.CompletedTask;

        var dev = FindDevice()
            ?? throw new InvalidOperationException("未找到 PowerZ HID 设备（0483:5750 / 5FC9:0063）");

        _stream = dev.Open();
        _stream.ReadTimeout = 250;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => PollLoopAsync(_cts.Token));
        try
        {
            Log?.Invoke($"PowerZ HID 已打开（{dev.GetProductName()}） " +
                        $"报文长度 in={SafeMaxIn(dev)} out={SafeMaxOut(dev)}");
        }
        catch
        {
            Log?.Invoke("PowerZ HID 已打开");
        }
        return Task.CompletedTask;
    }

    private static int SafeMaxIn(HidDevice d)
    {
        try { return d.GetMaxInputReportLength(); } catch { return -1; }
    }

    private static int SafeMaxOut(HidDevice d)
    {
        try { return d.GetMaxOutputReportLength(); } catch { return -1; }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_cts is not null)
            _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch { /* 退出异常忽略 */ }
        }
        _loop = null;
        _cts?.Dispose();
        _cts = null;

        if (_stream is not null)
        {
            try { Send(PowerZProtocol.Disconnect(0)); } catch { /* 忽略 */ }
            _stream.Dispose();
            _stream = null;
            Log?.Invoke("PowerZ HID 已关闭");
        }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    /// <summary>报文长度探测结果（0=未探测到可用长度）。</summary>
    private int _writeLen;

    /// <summary>发一帧协议包（HID 输出报文：report id=0 + 帧）。
    /// 长度自适应：maxOutput → 65 → 64 → 帧长+1 依次尝试，成功即记住。</summary>
    private void Send(byte[] frame)
    {
        var stream = _stream;
        if (stream is null)
            return;

        var candidates = new List<int>();
        if (_writeLen > 0)
            candidates.Add(_writeLen);
        try
        {
            int max = stream.Device.GetMaxOutputReportLength();
            if (max > 0 && !candidates.Contains(max))
                candidates.Add(max);
        }
        catch { }
        foreach (var n in new[] { 65, 64, frame.Length + 1 })
            if (!candidates.Contains(n))
                candidates.Add(n);

        Exception? last = null;
        foreach (var len in candidates)
        {
            if (len < frame.Length + 1)
                continue;
            try
            {
                var buf = new byte[len];
                buf[0] = 0x00;                       // report id 0
                Buffer.BlockCopy(frame, 0, buf, 1, frame.Length);
                stream.Write(buf);
                _writeLen = len;
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw last ?? new InvalidOperationException("无可用 HID 报文长度");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        byte tid = 1;
        int sendFail = 0;
        int rawLogged = 0;

        try
        {
            try
            {
                Send(PowerZProtocol.Connect(tid++));
                if (!await WaitForAcceptAsync(ct))
                    Log?.Invoke("PowerZ Connect 无 Accept（设备协议可能不兼容），继续轮询/抓报文");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[PowerZ] 发送失败: {ex.Message}（转只听模式抓原始报文）");
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Send(PowerZProtocol.GetDataAdc(tid++));
                    sendFail = 0;
                }
                catch (Exception ex)
                {
                    // 写失败（报文长度/协议不符）：节流日志，转只听抓原始报文
                    if (sendFail++ < 3 || sendFail % 20 == 0)
                        Log?.Invoke($"[PowerZ] 发送失败: {ex.Message}（只听模式）");
                }

                var buf = await ReadFrameAsync(ct);
                if (buf is { Length: > 0 })
                {
                    if (PowerZProtocol.TryParseAdc(buf, out _, out var sample))
                    {
                        SampleReceived?.Invoke(sample);
                    }
                    else if (rawLogged < 30 || (rawLogged % 20) == 0)
                    {
                        // 未识别报文：原始 hex 供实测样例校准（节流防刷屏）
                        Log?.Invoke($"[PowerZ] 原始报文({buf.Length}B): " +
                                    $"{Convert.ToHexString(buf.AsSpan(0, Math.Min(48, buf.Length)).ToArray())}");
                    }
                    rawLogged++;
                }

                await Task.Delay(200, ct);
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex)
        {
            Log?.Invoke($"[PowerZ] 通信异常: {ex.Message}");
        }
    }

    private async Task<bool> WaitForAcceptAsync(CancellationToken ct)
    {
        for (int i = 0; i < 5 && !ct.IsCancellationRequested; i++)
        {
            var buf = await ReadFrameAsync(ct);
            if (buf is { Length: >= 2 } && PowerZProtocol.RespType(buf) == PowerZProtocol.CmdAccept)
                return true;
        }
        return false;
    }

    /// <summary>读一帧 HID 输入报文（去 report id 前导），超时返回 null。</summary>
    private async Task<byte[]?> ReadFrameAsync(CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null)
            return null;
        try
        {
            var rep = await Task.Run(() => stream.Read(), ct);
            if (rep is not { Length: > 0 })
                return null;
            // 首字节 report id（无编号报文为 0x00）
            int offset = rep[0] == 0x00 ? 1 : 0;
            if (offset >= rep.Length)
                return null;
            var frame = new byte[rep.Length - offset];
            Buffer.BlockCopy(rep, offset, frame, 0, frame.Length);
            return frame;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException ex) when (ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }
}
