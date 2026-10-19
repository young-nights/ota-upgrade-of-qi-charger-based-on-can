namespace QiChargerStationTool.Core.Transport;

/// <summary>
/// ZQWL-CANFD USB CDC serial protocol (49 3B config / 5A…A5 data).
/// VID:PID 3562:0101 → COM port. See AGENTS.md「WSL2 串口 CAN 监听」
/// and python_tools/wsl/zqwl_can_listen.py (open_can0_250k 同构).
/// </summary>
public sealed class ZqwlSerialTransport : ICanTransport
{
    private const byte CanHdr = 0x5A;
    private const byte CanFtr = 0xA5;

    private readonly string _portName;
    private readonly System.IO.Ports.SerialPort? _port;
    private readonly object _rxLock = new();
    private readonly Queue<CanFrame> _rxQueue = new();
    private readonly byte[] _buf = new byte[4096];
    private int _bufLen;
    private bool _disposed;

    /// <summary>
    /// 调试日志：只报 CAN 数据帧。盒子心跳(5A FF/FE)、配置应答(49 3B) 一律不上报。
    /// </summary>
    public event Action<string>? OnRawLog;

    public ZqwlSerialTransport(string portName, int baud = 2_000_000)
    {
        _portName = portName;
        _port = new System.IO.Ports.SerialPort(portName, baud)
        {
            ReadTimeout = 50,
            WriteTimeout = 500,
            ReadBufferSize = 8192,
            WriteBufferSize = 8192,
        };
        _port.DataReceived += OnData;
    }

    public bool IsOpen => _port?.IsOpen == true;

    private void WriteRaw(byte[] pkt, string tag)
    {
        if (_port is not { IsOpen: true })
            throw new InvalidOperationException($"ZQWL port {_portName} not open");
        _port.Write(pkt, 0, pkt.Length);
        // 配置帧只在打开阶段打一行摘要，不进 CAN 日志
        if (tag.Length > 0) OnRawLog?.Invoke($"[CFG] {tag}");
    }

    /// <summary>49 3B 配置帧：func + rw + 16B payload + 45 2E。</summary>
    private static byte[] Cfg(byte func, byte rw, byte[]? payload = null)
    {
        var d = new byte[22];
        d[0] = 0x49;
        d[1] = 0x3B;
        d[2] = func;
        d[3] = rw;
        if (payload is { Length: > 0 })
            Array.Copy(payload, 0, d, 4, Math.Min(16, payload.Length));
        d[20] = 0x45;
        d[21] = 0x2E;
        return d;
    }

    public void Open()
    {
        if (_port is null) throw new ObjectDisposedException(nameof(ZqwlSerialTransport));
        try
        {
            if (!_port.IsOpen) _port.Open();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"打开 {_portName} 被拒绝（串口被占用）：" +
                "①关掉其它占用该口的程序（旧的上位机/串口助手/SecureCRT）；" +
                "②若曾 attach 到 WSL，管理员 PowerShell 执行 usbipd detach；" +
                "③拔插一次 USB 盒子；④任务管理器结束残留的 QiChargerStationTool 进程。" +
                " 详情：" + ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"打开 {_portName} 失败：设备可能已拔出或驱动异常，请重新插拔 USB。详情：{ex.Message}", ex);
        }

        OnRawLog?.Invoke($"[OPEN] {_portName} @ {_port.BaudRate}");

        // 与 zqwl_can_listen.py open_can0_250k() 同构，缺一步 CAN 就不会开
        // 1) 读设备信息
        WriteRaw(Cfg(0x40, 0x52), "read device info");
        Thread.Sleep(200);
        Drain(80);

        // 2) 仲裁 250k / 数据 500k → 0x45，ch=0
        WriteRaw(Cfg(0x42, 0x57, new byte[] { 0x00, 0x00, 0x45 }), "baud 250k/500k");
        Thread.Sleep(50);

        // 3) 滤波组0 扩展帧全收；组1 标准帧全收
        WriteRaw(Cfg(0x43, 0x57, new byte[]
        {
            0x00, 0x00, 0x01, 0x01,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        }), "filter ext all");
        Thread.Sleep(50);
        WriteRaw(Cfg(0x43, 0x57, new byte[]
        {
            0x00, 0x01, 0x01, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        }), "filter std all");
        Thread.Sleep(50);

        // 4) 打开 CAN0：不写 flash、不复位（0x44 的复位位会把 USB 打掉）
        WriteRaw(Cfg(0x44, 0x57, new byte[] { 0x00, 0x00, 0x01, 0x00 }), "open CAN0");
        Thread.Sleep(150);
        Drain(80);
        OnRawLog?.Invoke("[OPEN] CAN0 250k ext ready");
    }

    public void Close()
    {
        try { _port?.Close(); } catch { /* ignore */ }
    }

    public void Send(in CanFrame frame)
    {
        if (_port is not { IsOpen: true })
            throw new InvalidOperationException($"ZQWL port {_portName} not open");

        byte[] data = frame.Data.Length >= 8 ? frame.Data.AsSpan(0, 8).ToArray() : frame.Data;
        byte dlc = (byte)data.Length;
        uint id = frame.Id & 0x1FFFFFFF;
        var pkt = new byte[3 + 4 + dlc + 1];
        pkt[0] = CanHdr;
        pkt[1] = (byte)(dlc & 0x7F);
        pkt[2] = 0x04; // extended data frame
        pkt[3] = (byte)((id >> 24) & 0x7F);
        pkt[4] = (byte)((id >> 16) & 0xFF);
        pkt[5] = (byte)((id >> 8) & 0xFF);
        pkt[6] = (byte)(id & 0xFF);
        data.CopyTo(pkt, 7);
        pkt[^1] = CanFtr;
        _port.Write(pkt, 0, pkt.Length);
        OnRawLog?.Invoke($"[CAN-TX] {id:X8} {Convert.ToHexString(data)}");
    }

    public bool TryReceive(out CanFrame frame, int timeoutMs = 0)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            lock (_rxLock)
            {
                if (_rxQueue.Count > 0)
                {
                    frame = _rxQueue.Dequeue();
                    return true;
                }
            }
            if (Environment.TickCount64 >= deadline)
            {
                frame = default;
                return false;
            }
            PumpSerial();
            Thread.Sleep(1);
        }
    }

    public void Drain(int quietMs = 50)
    {
        var last = Environment.TickCount64;
        while (Environment.TickCount64 - last < quietMs)
        {
            PumpSerial();
            lock (_rxLock)
            {
                if (_rxQueue.Count > 0)
                {
                    _rxQueue.Clear();
                    last = Environment.TickCount64;
                }
            }
            Thread.Sleep(2);
        }
    }

    private void OnData(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
        => PumpSerial();

    private void PumpSerial()
    {
        if (_port is not { IsOpen: true }) return;
        try
        {
            int n = _port.BytesToRead;
            if (n <= 0) return;
            var tmp = new byte[n];
            int got = _port.Read(tmp, 0, n);
            if (got <= 0) return;
            lock (_rxLock)
            {
                // 满了先丢弃旧数据，避免 _bufLen 越界
                if (_bufLen + got > _buf.Length)
                    _bufLen = 0;
                if (got > _buf.Length)
                {
                    // 单次超大块：只保留尾部可容纳部分
                    tmp.AsSpan(got - _buf.Length).CopyTo(_buf);
                    _bufLen = _buf.Length;
                }
                else
                {
                    Buffer.BlockCopy(tmp, 0, _buf, _bufLen, got);
                    _bufLen += got;
                }
                ParseBuffer();
            }
        }
        catch (TimeoutException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void ParseBuffer()
    {
        // 防御：_bufLen 不得超过缓冲区
        if (_bufLen > _buf.Length) _bufLen = _buf.Length;
        if (_bufLen < 0) _bufLen = 0;

        int i = 0;
        while (i < _bufLen)
        {
            // heartbeat / cfg replies start with 49 3B —— 静默丢弃
            if (i + 1 < _bufLen && _buf[i] == 0x49 && _buf[i + 1] == 0x3B)
            {
                if (_bufLen - i < 22) break;
                i += 22;
                continue;
            }
            if (_buf[i] != CanHdr)
            {
                i++;
                continue;
            }
            if (_bufLen - i < 8) break;
            byte b1 = _buf[i + 1];
            if (b1 is 0xFF or 0xFE)
            {
                // 盒子状态心跳（FF=1/2路17B / FE=4路32B）：静默丢弃，不进日志
                int hb = b1 == 0xFF ? 17 : 32;
                if (_bufLen - i < hb) break;
                i += hb;
                continue;
            }
            int dlc = b1 & 0x7F;
            if (dlc > 64) { i++; continue; }
            int total = 3 + 4 + dlc + 1;
            if (_bufLen - i < total) break;
            if (_buf[i + total - 1] != CanFtr) { i++; continue; }

            uint id = ((uint)(_buf[i + 3] & 0x7F) << 24)
                    | ((uint)_buf[i + 4] << 16)
                    | ((uint)_buf[i + 5] << 8)
                    | _buf[i + 6];
            var data = new byte[dlc];
            Buffer.BlockCopy(_buf, i + 7, data, 0, dlc);
            _rxQueue.Enqueue(new CanFrame(id, data));
            OnRawLog?.Invoke($"[CAN-RX] {id:X8} {Convert.ToHexString(data)}");
            i += total;
        }
        if (i > 0)
        {
            int left = _bufLen - i;
            if (left > 0) Buffer.BlockCopy(_buf, i, _buf, 0, left);
            _bufLen = left;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        _port?.Dispose();
    }
}
