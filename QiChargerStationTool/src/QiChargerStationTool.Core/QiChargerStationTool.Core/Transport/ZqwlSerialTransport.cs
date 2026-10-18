namespace QiChargerStationTool.Core.Transport;

/// <summary>
/// ZQWL-CANFD USB CDC serial protocol (49 3B config / 5A…A5 data).
/// VID:PID 3562:0101 → COM port. See AGENTS.md「WSL2 串口 CAN 监听」.
/// </summary>
public sealed class ZqwlSerialTransport : ICanTransport
{
    private const byte CanHdr = 0x5A;
    private const byte CanFtr = 0xA5;

    private static readonly byte[] OpenCan0250k =
    {
        // read device info
        0x49, 0x3B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x45, 0x2E,
    };

    private readonly string _portName;
    private readonly System.IO.Ports.SerialPort? _port;
    private readonly object _rxLock = new();
    private readonly Queue<CanFrame> _rxQueue = new();
    private readonly byte[] _buf = new byte[4096];
    private int _bufLen;
    private bool _disposed;

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

    public void Open()
    {
        if (_port is null) throw new ObjectDisposedException(nameof(ZqwlSerialTransport));
        if (!_port.IsOpen) _port.Open();

        // configure CAN0 250k extended; never issue 0x44 with system-reset bit
        _port.Write(OpenCan0250k, 0, OpenCan0250k.Length);
        Thread.Sleep(150);
        Drain(80);
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
            lock (_rxLock)
            {
                if (_bufLen + got > _buf.Length) _bufLen = 0;
                Buffer.BlockCopy(tmp, 0, _buf, _bufLen, got);
                _bufLen += got;
                ParseBuffer();
            }
        }
        catch (TimeoutException) { }
        catch (IOException) { }
    }

    private void ParseBuffer()
    {
        int i = 0;
        while (true)
        {
            // heartbeat / cfg replies start with 49 3B
            if (i + 1 < _bufLen && _buf[i] == 0x49 && _buf[i + 1] == 0x3B)
            {
                if (_bufLen - i < 22) break;
                i += 22;
                continue;
            }
            if (i >= _bufLen || _buf[i] != CanHdr)
            {
                i++;
                continue;
            }
            if (_bufLen - i < 8) break;
            byte b1 = _buf[i + 1];
            if (b1 is 0xFF or 0xFE)
            {
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
