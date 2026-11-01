using System.Collections.Concurrent;
using HidSharp;

namespace ProductionStationTool.PowerZ;

/// <summary>
/// PowerZ HID 数据源（KINGMETER KM001，0483:5750）。
///
/// 协议已实测验证（2026-10-30）：输出报文 ReportID 0x55（64B），
/// 输入报文 ReportID 0xAA（64B）。采样走服务 0x22 + 命令 0x0B05 +
/// 子命令 1（连续流），每帧含 V/I 原始值（mV/mA，/1000 → V/A）。
/// 详见 <see cref="PowerZProtocol"/>。
///
/// 读取用「专用阻塞读线程 + 队列」——HIDSharp 的 ReadTimeout 超时会抛
/// TimeoutException（即使被捕获 VS 调试器也会弹异常），改为无限阻塞读
/// 只在有数据/断开时返回，正常运行零异常。
/// </summary>
public sealed class HidPowerZSource : IPowerZSource
{
    /// <summary>已知 PowerZ HID VID:PID。</summary>
    public static readonly (int Vid, int Pid)[] KnownIds =
    {
        (0x0483, 0x5750),   // KINGMETER KM001（用户实机）
        (0x5FC9, 0x0063),   // KM003C
        (0x5FC9, 0x0061),   // KM002C
    };

    /// <summary>
    /// 名称关键词兜底（贴牌/改名设备 VID:PID 与已知表不同时按名称识别）。
    /// </summary>
    private static readonly string[] NameKeywords =
    {
        "KINGMETER", "POWER-Z", "POWERZ", "KM001", "KM002", "KM003",
    };

    /// <summary>诊断日志（原始报文/错误），上层可接日志面板。</summary>
    public event Action<string>? Log;

    /// <summary>收到一条有效采样。</summary>
    public event Action<PowerSample>? SampleReceived;

    private HidStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Thread? _reader;
    private readonly BlockingCollection<byte[]> _rx = new(new ConcurrentQueue<byte[]>(), 64);
    private int _writeLen;

    /// <summary>
    /// 枚举本机 PowerZ HID 接口。
    ///
    /// 策略：**全量扫描所有 HID 设备，再按身份过滤**（VID:PID 命中已知表
    /// 或 厂商/产品名含关键词），而非按 VID:PID 定向查询——贴牌机 VID:PID
    /// 可能与已知表不同（如 ST 定制固件），定向查询会漏；全量扫描 + 名称兜底
    /// 能把「改过 VID 的贴牌」「同设备多 HID 接口」都列出来。
    /// 只枚举不打开（打开会占用设备/被占用时误判），可达性校验留给连接时的
    /// <see cref="TryProbe"/>。
    /// </summary>
    public static List<string> ListDevices()
    {
        var result = new List<string>();
        foreach (var d in DeviceList.Local.GetHidDevices())
        {
            if (!IsPowerZ(d))
                continue;
            string name;
            try { name = d.GetProductName(); } catch { name = d.DevicePath; }
            string mfr;
            try { mfr = d.GetManufacturer(); } catch { mfr = ""; }
            result.Add($"VID={d.VendorID:X4} PID={d.ProductID:X4} {mfr} {name}".Trim());
        }
        return result;
    }

    /// <summary>是否 PowerZ 家族设备（VID:PID 命中已知表，或厂商/产品名含关键词）。</summary>
    private static bool IsPowerZ(HidDevice d)
    {
        foreach (var (vid, pid) in KnownIds)
            if (d.VendorID == vid && d.ProductID == pid)
                return true;

        string name;
        try { name = $"{d.GetManufacturer()} {d.GetProductName()}"; }
        catch { name = d.DevicePath; }
        foreach (var kw in NameKeywords)
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// 可达性探针：设备能否真正打开（打开后立即关闭）。
    /// usbipd 把设备挂给 WSL 后 Windows 侧 HID 驱动解绑——
    /// HidSharp 枚举看不见、即使残留 PnP 节点打开也失败。
    /// 枚举/连接前先探一次，把「列表有但实际不可用」的过滤掉。
    /// </summary>
    public static bool TryProbe(out string detail)
    {
        HidDevice? dev;
        try { dev = FindDevice(); }
        catch (Exception ex) { detail = $"HID 枚举失败: {ex.Message}"; return false; }

        if (dev is null)
        {
            detail = "未找到 PowerZ HID 设备（可能已挂载到 WSL 或未插入）";
            return false;
        }

        try
        {
            using var s = dev.Open();
            detail = $"PowerZ HID 可达（{SafeName(dev)}）";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"PowerZ HID 打开失败: {ex.Message}（设备被占用/挂载到 WSL）";
            return false;
        }
    }

    private static string SafeName(HidDevice d)
    {
        try { return d.GetProductName(); } catch { return d.DevicePath; }
    }

    private static HidDevice? FindDevice()
    {
        foreach (var d in DeviceList.Local.GetHidDevices())
            if (IsPowerZ(d))
                return d;
        return null;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_stream is not null)
            return Task.CompletedTask;

        var dev = FindDevice()
            ?? throw new InvalidOperationException("未找到 PowerZ HID 设备（0483:5750 / 5FC9:0063）");

        _stream = dev.Open();
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
                if (_stream is null)
                    return;
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>
    /// 发一帧协议包。KM001 输出报文 = 64B，首字节即 ReportID 0x55，
    /// 直接整包写入（HIDSharp Write 要求含 report id）。
    /// 长度自适应：maxOutput → 64，成功即记住。
    /// </summary>
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
        if (!candidates.Contains(64))
            candidates.Add(64);

        Exception? last = null;
        foreach (var len in candidates)
        {
            if (len < frame.Length)
                continue;
            try
            {
                var buf = new byte[len];
                Buffer.BlockCopy(frame, 0, buf, 0, frame.Length);
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
        int sendFail = 0;
        int rawLogged = 0;

        try
        {
            // 启动测量流：服务 0x22 + 命令 0x0B05 + 子命令 1
            try
            {
                Send(PowerZProtocol.StartStreaming());
                Log?.Invoke("[PowerZ] 已启动 V/I 采样流（svc=0x22 cmd=0x0B05 sub=1）");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[PowerZ] 启动采样流失败: {ex.Message}（转只听模式）");
            }

            while (!ct.IsCancellationRequested)
            {
                // 定期重发启动帧（设备可能停流）
                if (sendFail == 0 && Environment.TickCount64 % 5000 < 250)
                {
                    try { Send(PowerZProtocol.StartStreaming()); } catch { /* 忽略 */ }
                }

                var buf = TakeFrame(250);
                if (buf is { Length: > 0 })
                {
                    if (PowerZProtocol.TryParseSample(buf, out var sample) ||
                        PowerZProtocol.TryParseBlock(buf, out sample))
                    {
                        sendFail = 0;
                        SampleReceived?.Invoke(sample);
                    }
                    else if (PowerZProtocol.IsResponse(buf, out var svc, out var cmd, out var sub))
                    {
                        // 已识别应答但非采样帧：静默（设备信息等）
                    }
                    else if (rawLogged < 30 || (rawLogged % 20) == 0)
                    {
                        Log?.Invoke($"[PowerZ] 未识别报文({buf.Length}B): " +
                                    $"{Convert.ToHexString(buf.AsSpan(0, Math.Min(48, buf.Length)).ToArray())}");
                    }
                    rawLogged++;
                }

                await Task.Delay(50, ct);
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex)
        {
            Log?.Invoke($"[PowerZ] 通信异常: {ex.Message}");
        }
    }

    /// <summary>从读队列取一帧原始报文（含 ReportID），超时返回 null——不抛异常。</summary>
    private byte[]? TakeFrame(int timeoutMs)
    {
        if (!_rx.TryTake(out var rep, timeoutMs))
            return null;
        return rep.Length == 0 ? null : rep;
    }
}
