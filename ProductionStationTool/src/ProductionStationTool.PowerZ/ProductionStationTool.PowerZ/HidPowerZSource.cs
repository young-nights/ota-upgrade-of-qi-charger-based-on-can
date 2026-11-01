using System.Collections.Concurrent;
using HidSharp;

namespace ProductionStationTool.PowerZ;

/// <summary>
/// PowerZ HID 数据源（KINGMETER KM001 / ChargerLAB KM003C 族）。
///
/// 读取用「专用阻塞读线程 + 队列」——HIDSharp 的 ReadTimeout 超时会抛
/// TimeoutException（即使被捕获 VS 调试器也会弹异常），改为无限阻塞读
/// 只在有数据/断开时返回，正常运行零异常。
///
/// KM001(0483:5750) 与 KM003C(5FC9:0063) 协议不保证一致：发送走长度
/// 自适应，未识别输入报文输出原始 hex 供实测校准。
/// </summary>
public sealed class HidPowerZSource : IPowerZSource
{
    /// <summary>已知 PowerZ HID VID:PID。</summary>
    public static readonly (int Vid, int Pid)[] KnownIds =
    {
        (0x5FC9, 0x0063),   // KM003C
        (0x5FC9, 0x0061),   // KM002C
        (0x0483, 0x5750),   // KINGMETER KM001（用户实机）
    };

    /// <summary>诊断日志（原始报文/错误），上层可接日志面板。</summary>
    public event Action<string>? Log;

    /// <summary>收到一条有效采样。</summary>
    public event Action<PowerSample>? SampleReceived;

    private HidStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Thread? _reader;
    private readonly BlockingCollection<byte[]> _rx = new(new ConcurrentQueue<byte[]>(), 16);
    private int _writeLen;

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

        _stream = dev.Open();          // ReadTimeout 默认无限：阻塞读不抛超时异常
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reader = new Thread(() => ReadLoop(dev)) { IsBackground = true };
        _reader.Start();
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

        if (_stream is not null)
        {
            try { Send(PowerZProtocol.Disconnect(0)); } catch { /* 忽略 */ }
        }

        // 关闭流解除阻塞读，读线程退出
        var stream = _stream;
        _stream = null;
        try { stream?.Dispose(); } catch { /* 忽略 */ }
        _reader?.Join(500);
        _reader = null;

        if (_loop is not null)
        {
            try { await _loop; } catch { /* 退出异常忽略 */ }
        }
        _loop = null;
        _cts?.Dispose();
        _cts = null;
        Log?.Invoke("PowerZ HID 已关闭");
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    /// <summary>
    /// 专用阻塞读线程：无限等待输入报文（无超时异常）。
    /// 设备断开/流关闭时 Read 抛异常 → 线程退出。
    /// </summary>
    private void ReadLoop(HidDevice dev)
    {
        var stream = _stream;
        while (stream == _stream && _stream is not null)
        {
            try
            {
                var rep = stream.Read();
                if (rep is { Length: > 0 })
                {
                    try { _rx.Add(rep); } catch { /* 队列已释放 */ }
                }
            }
            catch
            {
                // 设备断开/流关闭/瞬时 IO 错误：流已失效则退出，否则退避再读
                if (_stream is null)
                    return;
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>发一帧协议包（HID 输出报文：report id=0 + 帧）。
    /// 长度自适应：maxOutput → 65 → 64 → 帧长+1，成功即记住。</summary>
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
                    if (sendFail++ < 3 || sendFail % 20 == 0)
                        Log?.Invoke($"[PowerZ] 发送失败: {ex.Message}（只听模式）");
                }

                var buf = TakeFrame(250);
                if (buf is { Length: > 0 })
                {
                    if (PowerZProtocol.TryParseAdc(buf, out _, out var sample))
                    {
                        SampleReceived?.Invoke(sample);
                    }
                    else if (rawLogged < 30 || (rawLogged % 20) == 0)
                    {
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
            var buf = TakeFrame(250);
            if (buf is { Length: >= 2 } && PowerZProtocol.RespType(buf) == PowerZProtocol.CmdAccept)
                return true;
        }
        return false;
    }

    /// <summary>从读队列取一帧（去 report id 前导），超时返回 null——不抛异常。</summary>
    private byte[]? TakeFrame(int timeoutMs)
    {
        if (!_rx.TryTake(out var rep, timeoutMs))
            return null;
        if (rep.Length == 0)
            return null;
        int offset = rep[0] == 0x00 ? 1 : 0;
        if (offset >= rep.Length)
            return null;
        var frame = new byte[rep.Length - offset];
        Buffer.BlockCopy(rep, offset, frame, 0, frame.Length);
        return frame;
    }
}
