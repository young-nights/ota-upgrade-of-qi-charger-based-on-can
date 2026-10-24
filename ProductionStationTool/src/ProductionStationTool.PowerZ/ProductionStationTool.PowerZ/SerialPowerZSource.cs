using System.IO.Ports;
using System.Text;

namespace ProductionStationTool.PowerZ;

/// <summary>
/// PowerZ 串口数据源：按行读取并用 <see cref="PowerZParser"/> 解析。
/// 波特率默认 115200（按仪表实际输出调整）。
/// </summary>
public sealed class SerialPowerZSource : IPowerZSource
{
    private readonly string _portName;
    private readonly int _baudRate;
    private SerialPort? _port;
    private readonly StringBuilder _lineBuffer = new();
    private volatile bool _running;

    public event Action<PowerSample>? SampleReceived;

    public SerialPowerZSource(string portName, int baudRate = 115200)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_running)
            return Task.CompletedTask;

        _port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            NewLine = "\n",
        };
        _port.DataReceived += OnDataReceived;
        _port.Open();
        _running = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _running = false;
        if (_port != null)
        {
            try { _port.DataReceived -= OnDataReceived; _port.Close(); }
            catch { /* 关闭失败忽略 */ }
            _port.Dispose();
            _port = null;
        }
        return Task.CompletedTask;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var port = (SerialPort)sender;
        try
        {
            var chunk = port.ReadExisting();
            lock (_lineBuffer)
            {
                _lineBuffer.Append(chunk);
                while (true)
                {
                    var s = _lineBuffer.ToString();
                    var idx = s.IndexOfAny(['\r', '\n']);
                    if (idx < 0)
                        break;
                    var line = s[..idx];
                    _lineBuffer.Remove(0, idx + 1);
                    if (PowerZParser.TryParseLine(line, out var sample))
                        SampleReceived?.Invoke(sample);
                }
            }
        }
        catch
        {
            // 读异常（拔线等）不打断收数循环
        }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
