using System.Text;
using System.Text.Json;

namespace ProductionStationTool.Core.Sn;

/// <summary>联机取号结果。</summary>
public sealed record SnAllocResult(bool Ok, string? Sn, int Seq, string? Error, string Message)
{
    public static SnAllocResult Fail(string error, string message) => new(false, null, 0, error, message);
}

/// <summary>
/// SN 联机取号客户端（工位1 → 汇总机 :5000）。
/// 接口（与 doc 21 对齐）：
///   POST /api/v1/sn/next   {"station":1,"machine":"1"} → {"ok":true,"date":"…","items":[{"sn":"…","seq":N}]}
///                          超限 → {"ok":false,"error":"CAP_REACHED","message":"…"}
///   POST /api/v1/sn/release {"sn":"…"} → {"ok":true}
/// </summary>
public sealed class SnAllocationClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>向汇总机申请当日新 SN（防撞号：服务端统一发号）。</summary>
    public async Task<SnAllocResult> NextAsync(string serverHost, int station, string machine,
        CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { station, machine });
            var resp = await _http.PostAsync($"http://{serverHost}:5000/api/v1/sn/next",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (ok && root.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                var it = items[0];
                return new SnAllocResult(true, it.GetProperty("sn").GetString(),
                    it.GetProperty("seq").GetInt32(), null, "取号成功");
            }
            string error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "UNKNOWN" : "UNKNOWN";
            string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            return SnAllocResult.Fail(error, string.IsNullOrEmpty(msg) ? "SN 取号失败" : msg);
        }
        catch (Exception ex)
        {
            return SnAllocResult.Fail("NETWORK", $"SN 取号失败：汇总机不可达（{serverHost}:5000）——{ex.Message}");
        }
    }

    /// <summary>标记 SN 弃用（可回收）；失败静默（不阻塞测试流程，日志由调用方打）。</summary>
    public async Task<bool> ReleaseAsync(string serverHost, string sn, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { sn });
            var resp = await _http.PostAsync($"http://{serverHost}:5000/api/v1/sn/release",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
