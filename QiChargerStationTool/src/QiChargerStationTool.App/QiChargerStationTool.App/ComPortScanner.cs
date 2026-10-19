using System.IO.Ports;
using System.Management;

namespace QiChargerStationTool.App;

/// <summary>枚举 Windows 串口，并识别智嵌(ZQWL) CAN 盒子。</summary>
public sealed record ComPortInfo(string PortName, string Description, string? PnpDeviceId)
{
    /// <summary>
    /// 智嵌(ZQWL) USB-CAN：默认认厂商 VID 3562（同厂商不同 PID 的 CAN/CANFD 型号都能中），
    /// 或描述里带 ZQWL/智嵌/CAN。可用 <see cref="ComPortScanner.ExtraMatchIds"/> 追加 VID:PID。
    /// </summary>
    public bool IsZqwlCan => ComPortScanner.Matches(this);

    public string DisplayName =>
        IsZqwlCan
            ? $"{PortName}  —  {ShortLabel()} ★"
            : string.IsNullOrWhiteSpace(Description) || Description == PortName
                ? PortName
                : $"{PortName}  —  {Description}";

    private string ShortLabel()
    {
        if (Description.Contains("CANFD", StringComparison.OrdinalIgnoreCase))
            return "ZQWL CANFD";
        if (Description.Contains("CAN", StringComparison.OrdinalIgnoreCase))
            return "ZQWL CAN";
        return "ZQWL";
    }

    public override string ToString() => DisplayName;
}

public static class ComPortScanner
{
    /// <summary>智嵌(ZQWL) 厂商 VID。同厂商 CAN/CANFD 系列 PID 可能不同，故只按 VID 认。</summary>
    public const string ZqwlVid = "VID_3562";

    /// <summary>本仓库当前盒子：ZQWL-CANFD（AGENTS.md 实测）。</summary>
    public const string ZqwlCanfdPid = "PID_0101";

    /// <summary>
    /// 额外匹配项（可写 appsettings 的 ExtraComMatch，格式 "VID_xxxx&PID_yyyy" 或子串）。
    /// 例如换用其它厂商的 USB-CAN，把其 VID:PID 填进来即可自动选中。
    /// </summary>
    public static readonly List<string> ExtraMatchIds = new();

    /// <summary>描述关键字兜底（驱动把设备名刷成「USB Serial Device」时靠它认）。</summary>
    private static readonly string[] DescKeywords =
    {
        "ZQWL", "智嵌", "CANFD", "USB-CAN", "USBCAN",
    };

    internal static bool Matches(ComPortInfo p)
    {
        string pnp = p.PnpDeviceId ?? "";
        if (pnp.Contains(ZqwlVid, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var id in ExtraMatchIds)
        {
            if (!string.IsNullOrWhiteSpace(id) &&
                pnp.Contains(id.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        foreach (var kw in DescKeywords)
        {
            if (p.Description.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

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
    /// 选中策略：优先保留 prev；否则自动选 ZQWL；否则第一项。
    /// 多个 ZQWL 时优先 PID 0101（本仓库 CANFD），再按名称排序取第一个。
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

        ComPortInfo? anyZqwl = null;
        foreach (var p in ports)
        {
            if (!p.IsZqwlCan) continue;
            if ((p.PnpDeviceId?.Contains(ZqwlCanfdPid, StringComparison.OrdinalIgnoreCase) ?? false)
                || p.Description.Contains("CANFD", StringComparison.OrdinalIgnoreCase))
                return p;
            anyZqwl ??= p;
        }
        return anyZqwl ?? ports[0];
    }
}
