using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ProductionStationTool.Core.Station;

/// <summary>一台工位机的一次接入上报（IP 取自 TCP 连接的远端地址）。</summary>
public sealed record StationPresence(
    string Code,
    int StationNo,
    string MachineId,
    string Ip,
    DateTime SeenAt);

/// <summary>
/// 汇总机接入监听：TCP 监听局域网端口，工位机心跳上报（POST /hello + JSON 身份）即记录一条
/// 接入（<see cref="MachineSeen"/>，IP 取连接远端地址）。自实现极简 HTTP 收发——
/// HttpListener 非管理员要 URL ACL，TcpListener 无此部署负担。
/// </summary>
public sealed class StationPresenceListener : IDisposable
{
    /// <summary>默认端口与 doc 18 QI-SERVER 一致。</summary>
    public const int DefaultPort = 5000;

    private TcpListener? _tcp;
    private CancellationTokenSource? _cts;

    /// <summary>SN 发号台账（汇总机联机取号）；null = 取号接口未启用。</summary>
    public Sn.SnIssuedStore? SnStore { get; set; }

    /// <summary>实际监听端口（Start(0) 时为系统分配的临时端口）。</summary>
    public int Port { get; private set; }

    /// <summary>检测到工位机接入（含心跳重报；调用方按 Code 覆盖更新）。</summary>
    public event Action<StationPresence>? MachineSeen;

    public void Start(int port = DefaultPort)
    {
        if (_tcp is not null)
            return;
        _cts = new CancellationTokenSource();
        _tcp = new TcpListener(IPAddress.Any, port);
        _tcp.Start();
        Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _tcp!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;   // 监听已停止
            }
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
                var stream = client.GetStream();
                var (path, body) = await ReadHttpAsync(stream, ct);

                // SN 联机取号接口（doc 21）：/api/v1/sn/next|release|lookup
                if (path.StartsWith("/api/v1/sn/", StringComparison.Ordinal))
                {
                    var json = HandleSnApi(path, body);
                    var payload = Encoding.UTF8.GetBytes(json);
                    var head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                        $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, ct);
                    await stream.WriteAsync(payload, ct);
                    return;
                }

                // 其余路径按工位机心跳处理（POST /hello + JSON 身份）
                var r = ParseReport(body);
                MachineSeen?.Invoke(new StationPresence(
                    string.IsNullOrEmpty(r.Code) ? ip : r.Code,
                    r.StationNo, r.MachineId, ip, DateTime.Now));

                var resp = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                    "Content-Length: 2\r\nConnection: close\r\n\r\n{}");
                await stream.WriteAsync(resp, ct);
            }
            catch
            {
                // 单条连接失败不影响监听
            }
        }
    }

    /// <summary>SN 取号接口派发（next=发号 / release=弃用 / lookup=查询）。</summary>
    private string HandleSnApi(string path, string body)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (SnStore is null)
            return JsonSerializer.Serialize(new { ok = false, error = "NO_STORE", message = "联机取号服务未启用" });
        try
        {
            if (path.Contains("/sn/next"))
            {
                var dto = JsonSerializer.Deserialize<SnReqDto>(body, opts) ?? new SnReqDto();
                var r = SnStore.Allocate(DateTime.Now, dto.Station, dto.Machine);
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    date = r.Day,
                    items = new[] { new { sn = r.Sn, seq = r.Seq } },
                });
            }
            if (path.Contains("/sn/release"))
            {
                var dto = JsonSerializer.Deserialize<SnReqDto>(body, opts) ?? new SnReqDto();
                if (string.IsNullOrEmpty(dto.Sn))
                    return JsonSerializer.Serialize(new { ok = false, error = "BAD_REQUEST", message = "缺少 sn" });
                SnStore.Release(dto.Sn);
                return JsonSerializer.Serialize(new { ok = true });
            }
            if (path.Contains("/sn/lookup"))
            {
                var sn = QueryParam(path, "sn") ?? "";
                var item = SnStore.Lookup(sn);
                return item is null
                    ? JsonSerializer.Serialize(new { ok = false, error = "NOT_FOUND" })
                    : JsonSerializer.Serialize(new { ok = true, item = new
                      {
                          sn = item.Value.Sn, day = item.Value.Day, seq = item.Value.Seq,
                          state = item.Value.State, machine = item.Value.Machine,
                      } });
            }
            return JsonSerializer.Serialize(new { ok = false, error = "NOT_FOUND" });
        }
        catch (Sn.SnExceededException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = "CAP_REACHED", message = ex.Message });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = "INTERNAL", message = ex.Message });
        }
    }

    /// <summary>从 URL 查询串取参数（极简）。</summary>
    private static string? QueryParam(string path, string key)
    {
        var q = path.IndexOf('?');
        if (q < 0)
            return null;
        foreach (var kv in path[(q + 1)..].Split('&'))
        {
            var p = kv.Split('=', 2);
            if (p.Length == 2 && p[0] == key)
                return Uri.UnescapeDataString(p[1]);
        }
        return null;
    }

    private sealed class SnReqDto
    {
        public int Station { get; set; }
        public string Machine { get; set; } = "";
        public string Sn { get; set; } = "";
    }

    /// <summary>极简 HTTP 解析：返回请求行 path 与 body（Content-Length 上限 4KB）。</summary>
    private static async Task<(string Path, string Body)> ReadHttpAsync(NetworkStream stream, CancellationToken ct)
    {
        string path = "/";
        var head = new MemoryStream();
        var b = new byte[1];
        while (head.Length < 8192)
        {
            var n = await stream.ReadAsync(b, ct);
            if (n == 0)
                break;
            head.WriteByte(b[0]);
            var h = head.GetBuffer();
            var len = (int)head.Length;
            if (len >= 4 && h[len - 4] == '\r' && h[len - 3] == '\n' && h[len - 2] == '\r' && h[len - 1] == '\n')
                break;
        }

        var contentLen = 0;
        foreach (var line in Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length).Split("\r\n"))
        {
            // 请求行 "POST /api/v1/sn/next HTTP/1.1" → 取 path
            if (line.StartsWith("GET ") || line.StartsWith("POST ") || line.StartsWith("PUT "))
            {
                var parts = line.Split(' ');
                if (parts.Length >= 2)
                    path = parts[1];
            }
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line["Content-Length:".Length..].Trim(), out contentLen);
        }
        if (contentLen <= 0 || contentLen > 4096)
            return (path, "");

        var body = new byte[contentLen];
        var read = 0;
        while (read < contentLen)
        {
            var n = await stream.ReadAsync(body.AsMemory(read), ct);
            if (n == 0)
                break;
            read += n;
        }
        return (path, Encoding.UTF8.GetString(body, 0, read));
    }

    private sealed class ReportDto
    {
        public string Code { get; set; } = "";
        public int StationNo { get; set; }
        public string MachineId { get; set; } = "";
    }

    private static ReportDto ParseReport(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ReportDto>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ReportDto();
        }
        catch
        {
            return new ReportDto();
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _tcp?.Stop(); } catch { /* 忽略 */ }
    }
}

/// <summary>
/// 工位机接入心跳：周期性 POST /hello（JSON 身份）到汇总机，失败静默、下轮重试。
/// 汇总机按连接远端 IP 自动登记该工位机。
/// </summary>
public sealed class StationPresenceReporter : IDisposable
{
    private readonly StationIdentity _identity;
    private readonly string _url;
    private readonly TimeSpan _interval;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private CancellationTokenSource? _cts;

    public StationPresenceReporter(
        StationIdentity identity, string serverHost,
        int port = StationPresenceListener.DefaultPort, TimeSpan? interval = null)
    {
        _identity = identity;
        _url = $"http://{serverHost}:{port}/hello";
        _interval = interval ?? TimeSpan.FromSeconds(15);
    }

    public void Start()
    {
        if (_cts is not null)
            return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var json = JsonSerializer.Serialize(new
                {
                    code = _identity.Code,
                    stationNo = _identity.StationNo,
                    machineId = _identity.MachineId,
                });
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _http.PostAsync(_url, content, ct);
            }
            catch
            {
                // 汇总机不可达：静默，下轮重试（断网不阻塞测试）
            }
            try
            {
                await Task.Delay(_interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _http.Dispose();
    }
}
