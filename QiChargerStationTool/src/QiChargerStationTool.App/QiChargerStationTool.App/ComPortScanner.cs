using System.IO.Ports;
using System.Management;

namespace QiChargerStationTool.App;

/// <summary>枚举 Windows 串口，并按 VID:PID 识别 ZQWL-CANFD。</summary>
public sealed record ComPortInfo(string PortName, string Description, string? PnpDeviceId)
{
    /// <summary>ZQWL-CANFD USB CDC：VID:PID 3562:0101（见 AGENTS.md）。</summary>
    public bool IsZqwlCan =>
        (PnpDeviceId?.Contains("VID_3562&PID_0101", StringComparison.OrdinalIgnoreCase) ?? false)
        || Description.Contains("ZQWL", StringComparison.OrdinalIgnoreCase)
        || Description.Contains("3562", StringComparison.OrdinalIgnoreCase);

    public string DisplayName =>
        IsZqwlCan
            ? $"{PortName}  —  ZQWL-CANFD ★"
            : string.IsNullOrWhiteSpace(Description) || Description == PortName
                ? PortName
                : $"{PortName}  —  {Description}";

    public override string ToString() => DisplayName;
}

public static class ComPortScanner
{
    /// <summary>
    /// 扫描当前系统串口。优先 WMI 拿 PnP 描述/VID:PID；
    /// WMI 失败时退回 SerialPort.GetPortNames()。
    /// </summary>
    public static IReadOnlyList<ComPortInfo> Scan()
    {
        var map = new SortedDictionary<string, ComPortInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, Description FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                string pnp = obj["PNPDeviceID"]?.ToString() ?? "";
                string desc = obj["Description"]?.ToString() ?? name;
                string? port = ExtractComPort(name);
                if (port is null) continue;
                map[port] = new ComPortInfo(port, desc, string.IsNullOrEmpty(pnp) ? null : pnp);
            }
        }
        catch
        {
            // WMI 不可用（精简系统/权限）→ 走 SerialPort 兜底
        }

        if (map.Count == 0)
        {
            foreach (var port in SerialPort.GetPortNames())
                map[port] = new ComPortInfo(port, port, null);
        }
        else
        {
            // 补上 WMI 漏掉但驱动层存在的口
            foreach (var port in SerialPort.GetPortNames())
            {
                if (!map.ContainsKey(port))
                    map[port] = new ComPortInfo(port, port, null);
            }
        }

        return map.Values.ToList();
    }

    /// <summary>从「USB Serial Device (COM7)」提取 COM7。</summary>
    private static string? ExtractComPort(string deviceName)
    {
        int open = deviceName.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;
        int close = deviceName.IndexOf(')', open);
        if (close < 0) return null;
        string com = deviceName.Substring(open + 1, close - open - 1).Trim();
        return com.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? com : null;
    }

    /// <summary>
    /// 选中策略：优先保留 prev；否则自动选 ZQWL-CANFD；否则第一项。
    /// </summary>
    public static ComPortInfo? PickDefault(IReadOnlyList<ComPortInfo> ports, string? prev)
    {
        if (ports.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(prev))
        {
            foreach (var p in ports)
                if (string.Equals(p.PortName, prev, StringComparison.OrdinalIgnoreCase))
                    return p;
        }
        foreach (var p in ports)
            if (p.IsZqwlCan)
                return p;
        return ports[0];
    }
}
