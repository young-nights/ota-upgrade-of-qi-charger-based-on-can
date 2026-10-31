using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;

namespace ProductionStationTool.App.Utilities;

/// <summary>串口设备信息（按 USB VID:PID / 描述识别，避免 CAN/PowerZ 搞混）。</summary>
public sealed record PortInfo(string Name, string VidPid, string Description)
{
    /// <summary>下拉空选项（未选择）。</summary>
    public static readonly PortInfo Empty = new(string.Empty, string.Empty, string.Empty);

    public bool IsEmpty => Name.Length == 0;

    /// <summary>下拉框显示：COM7  [3562:0101]；空选项显示 —</summary>
    public string Label => IsEmpty ? "—"
        : string.IsNullOrEmpty(VidPid) ? Name : $"{Name}  [{VidPid}]";
}

/// <summary>
/// 串口枚举与 CAN 设备识别（分层匹配，通用化）：
/// ① VID:PID=3562:0101 精确命中 ZQWL-CANFD；
/// ② 设备描述/名称含 "ZQWL" 关键词（固件版本差异的兜底）；
/// ③ 都未命中返回 null——由操作员在下拉人工选择。
/// WMI（Win32_PnPEntity）取 PNPDeviceID；不可用时回退 GetPortNames。
/// 注意：盒子若 usbipd 挂在 WSL，Windows 侧不可见（先 detach）。
/// </summary>
public static class PortDetector
{
    /// <summary>ZQWL-CANFD（AGENTS.md 硬件表：VID:PID 3562:0101）。</summary>
    public const string CanVid = "3562";
    public const string CanPid = "0101";

    /// <summary>CAN 描述关键词兜底匹配（层②）。</summary>
    private static readonly string[] CanKeywords = { "ZQWL", "ZQWL-CANFD", "CANFD" };

    /// <summary>
    /// PowerZ 的 VID/PID——未确认前留空：不按 VID 精确匹配，
    /// 只用「排除 CAN 口」策略；确认后填入即可精确匹配。
    /// </summary>
    public const string PowerZVid = "";
    public const string PowerZPid = "";

    private static readonly Regex ComRx = new(@"COM\d+", RegexOptions.IgnoreCase);
    private static readonly Regex VidPidRx =
        new(@"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);

    /// <summary>枚举全部串口及其 VID:PID / 描述。</summary>
    public static List<PortInfo> ListPorts()
    {
        var result = new List<PortInfo>();
        try
        {
            // 注意：中文 Windows 上 USB 串口名形如「USB 串行设备 (COM7)」，
            // 不以 COM 开头——必须用 '%COM%' 包含匹配（'COM%' 只匹配以 COM
            // 开头的旧式命名，会漏掉全部 USB CDC 串口）。
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Description, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%COM%'");
            foreach (var o in searcher.Get())
            {
                var name = (string)(o["Name"] ?? string.Empty);
                var desc = (string)(o["Description"] ?? string.Empty);
                var pnp = (string)(o["PNPDeviceID"] ?? string.Empty);
                var com = ComRx.Match(name);
                if (!com.Success)
                    continue;
                var vp = VidPidRx.Match(pnp);
                var vidPid = vp.Success
                    ? $"{vp.Groups[1].Value.ToUpperInvariant()}:{vp.Groups[2].Value.ToUpperInvariant()}"
                    : string.Empty;
                result.Add(new PortInfo(com.Value, vidPid, desc));
            }
        }
        catch
        {
            // WMI 不可用：回退无 VID 的端口名列表
        }

        if (result.Count == 0)
        {
            // WMI 查询空/异常兜底：GetPortNames 至少能列出端口（无 VID 信息）
            foreach (var n in SerialPort.GetPortNames())
                result.Add(new PortInfo(n, string.Empty, string.Empty));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>找 CAN（ZQWL）串口名：层①VID 精确 → 层②描述关键词 → null。</summary>
    public static string? FindCanPort(IReadOnlyList<PortInfo> ports)
    {
        var want = $"{CanVid}:{CanPid}";
        foreach (var p in ports)
            if (string.Equals(p.VidPid, want, StringComparison.OrdinalIgnoreCase))
                return p.Name;

        foreach (var p in ports)
        {
            var hay = $"{p.Description} {p.Label}";
            foreach (var kw in CanKeywords)
                if (hay.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return p.Name;
        }

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
