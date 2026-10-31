using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;

namespace ProductionStationTool.App.Utilities;

/// <summary>串口设备信息（按 USB VID:PID 识别，避免 CAN/PowerZ 搞混）。</summary>
public sealed record PortInfo(string Name, string VidPid)
{
    /// <summary>下拉框显示：COM7  [3562:0101]</summary>
    public string Label => string.IsNullOrEmpty(VidPid) ? Name : $"{Name}  [{VidPid}]";
}

/// <summary>
/// 串口枚举与按 VID:PID 的设备识别。
/// Windows WMI（Win32_PnPEntity）取 PNPDeviceID 解析 VID/PID；
/// WMI 不可用时回退 GetPortNames（无 VID 信息）。
/// </summary>
public static class PortDetector
{
    /// <summary>ZQWL-CANFD（AGENTS.md 硬件表：VID:PID 3562:0101）。</summary>
    public const string CanVid = "3562";
    public const string CanPid = "0101";

    /// <summary>
    /// PowerZ 的 VID/PID——未确认前留空：不按 VID 精确匹配，
    /// 只用「排除 CAN 口」策略；确认后填入即可精确匹配。
    /// </summary>
    public const string PowerZVid = "";
    public const string PowerZPid = "";

    private static readonly Regex ComRx = new(@"COM\d+", RegexOptions.IgnoreCase);
    private static readonly Regex VidPidRx =
        new(@"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);

    /// <summary>枚举全部串口及其 VID:PID。</summary>
    public static List<PortInfo> ListPorts()
    {
        var result = new List<PortInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE 'COM%'");
            foreach (var o in searcher.Get())
            {
                var name = (string)(o["Name"] ?? string.Empty);
                var pnp = (string)(o["PNPDeviceID"] ?? string.Empty);
                var com = ComRx.Match(name);
                if (!com.Success)
                    continue;
                var vp = VidPidRx.Match(pnp);
                var vidPid = vp.Success
                    ? $"{vp.Groups[1].Value.ToUpperInvariant()}:{vp.Groups[2].Value.ToUpperInvariant()}"
                    : string.Empty;
                result.Add(new PortInfo(com.Value, vidPid));
            }
        }
        catch
        {
            // WMI 不可用：回退无 VID 的端口名列表
            foreach (var n in SerialPort.GetPortNames())
                result.Add(new PortInfo(n, string.Empty));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>按 VID:PID 找 ZQWL-CANFD 串口名；找不到返回 null。</summary>
    public static string? FindCanPort(IReadOnlyList<PortInfo> ports)
    {
        var want = $"{CanVid}:{CanPid}";
        foreach (var p in ports)
            if (string.Equals(p.VidPid, want, StringComparison.OrdinalIgnoreCase))
                return p.Name;
        return null;
    }

    /// <summary>找 PowerZ 串口名：VID 已知则精确匹配；否则排除 CAN 口取其余第一个。</summary>
    public static string? FindPowerZPort(IReadOnlyList<PortInfo> ports, string? canPort)
    {
        if (PowerZVid.Length > 0)
        {
            var want = $"{PowerZVid}:{PowerZPid}";
            foreach (var p in ports)
                if (string.Equals(p.VidPid, want, StringComparison.OrdinalIgnoreCase))
                    return p.Name;
        }

        foreach (var p in ports)
            if (p.Name != canPort)
                return p.Name;
        return null;
    }
}
