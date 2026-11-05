using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ProductionStationTool.App.Utilities;

/// <summary>本机网络信息与服务器连通检测（设置页用，只读/探测，不改网络配置）。</summary>
public static class NetworkInfo
{
    /// <summary>列出已启用网卡的 IPv4 地址（跳过 loopback）。</summary>
    public static IReadOnlyList<(string Adapter, string Ipv4)> GetLocalIpv4()
    {
        var list = new List<(string, string)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    list.Add((nic.Name, ua.Address.ToString()));
            }
        }
        return list;
    }

    /// <summary>ping 服务器；返回（是否通、往返毫秒、详情）。</summary>
    public static async Task<(bool Ok, long? RttMs, string Detail)> PingAsync(string host, int timeoutMs = 2000)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs);
            return reply.Status == IPStatus.Success
                ? (true, reply.RoundtripTime, $"可达，延迟 {reply.RoundtripTime} ms")
                : (false, null, $"不可达：{reply.Status}");
        }
        catch (PingException ex)
        {
            return (false, null, $"解析/网络错误：{ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, null, $"检测失败：{ex.Message}");
        }
    }
}
