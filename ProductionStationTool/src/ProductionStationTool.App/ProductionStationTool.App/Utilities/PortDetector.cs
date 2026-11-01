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
    /// PowerZ 的 VID/PID（STMicroelectronics 定制固件，0483:5750）。
    /// 注意：PowerZ 在 Windows 上枚举为 **HID 设备**（「USB 输入设备」），
    /// 不是 COM 口——串口枚举看不见它，必须走 HID 设备枚举。
    /// 兼容 ChargerLAB KM003C(5FC9:0063)/KM002C(5FC9:0061)。
    /// </summary>
    public const string PowerZVid = "0483";
    public const string PowerZPid = "5750";

    private static readonly (string Vid, string Pid)[] PowerZIds =
    {
        ("0483", "5750"),   // 用户实机（ST 方案）
        ("5FC9", "0063"),   // KM003C
        ("5FC9", "0061"),   // KM002C
    };

    /// <summary>PowerZ 名称关键词兜底（贴牌机 VID:PID 不在已知表时按名称识别）。</summary>
    private static readonly string[] PowerZNameKeywords =
    {
        "KINGMETER", "POWER-Z", "POWERZ", "KM001", "KM002", "KM003",
    };

    private static readonly Regex ComRx = new(@"COM\d+", RegexOptions.IgnoreCase);
    private static readonly Regex VidPidRx =
        new(@"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);

    /// <summary>
    /// 枚举全部串口及其 VID:PID / 描述。
    /// 多来源合并（Win32_PnPEntity + Win32_SerialPort + GetPortNames），
    /// 按 COM 名去重（优先带 VID 信息的条目）——单一 WMI 类在部分
    /// 驱动/系统上会漏口。
    /// </summary>
    public static List<PortInfo> ListPorts()
    {
        var byName = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);

        void add(string name, string desc, string pnp)
        {
            var com = ComRx.Match(name);
            if (!com.Success)
                return;
            var vp = VidPidRx.Match(pnp);
            var vidPid = vp.Success
                ? $"{vp.Groups[1].Value.ToUpperInvariant()}:{vp.Groups[2].Value.ToUpperInvariant()}"
                : string.Empty;
            var info = new PortInfo(com.Value, vidPid, desc);
            if (byName.TryGetValue(info.Name, out var old))
            {
                // 保留有 VID 信息的条目
                if (old.VidPid.Length == 0 && info.VidPid.Length > 0)
                    byName[info.Name] = info;
            }
            else
            {
                byName[info.Name] = info;
            }
        }

        try
        {
            // 中文 Windows 的 USB 串口名为「USB 串行设备 (COM7)」不以 COM
            // 开头——必须 '%COM%' 包含匹配。
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Description, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%COM%'");
            foreach (var o in searcher.Get())
                add((string)(o["Name"] ?? string.Empty),
                    (string)(o["Description"] ?? string.Empty),
                    (string)(o["PNPDeviceID"] ?? string.Empty));
        }
        catch { /* WMI 不可用则继续下一来源 */ }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, PNPDeviceID FROM Win32_SerialPort");
            foreach (var o in searcher.Get())
                add((string)(o["DeviceID"] ?? string.Empty) + " " + (string)(o["Name"] ?? string.Empty),
                    (string)(o["Name"] ?? string.Empty),
                    (string)(o["PNPDeviceID"] ?? string.Empty));
        }
        catch { /* 忽略 */ }

        if (byName.Count == 0)
        {
            foreach (var n in SerialPort.GetPortNames())
                add(n, string.Empty, string.Empty);
        }

        var result = new List<PortInfo>(byName.Values);
        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>
    /// 枚举 PowerZ HID 接口（0483:5750 等，Windows 上显示「USB 输入设备」）。
    /// 返回的 PortInfo.Name 固定为 PowerZ(HID)——HID 无 COM 口名。
    ///
    /// 策略：**全量扫描所有 HID 设备再按身份过滤**（VID:PID 命中已知表
    /// 或厂商/产品名含 KINGMETER/POWER-Z/KM00 关键词），而非按 VID:PID
    /// 定向查询——贴牌机 VID:PID 可能不同，定向查会漏；全量扫描 + 名称兜底
    /// 能把改过 VID 的贴牌、同设备多 HID 接口都列出来。
    /// 只枚举不打开设备（打开会占用/被占用时误判），可达性校验在连接时做。
    /// </summary>
    public static List<PortInfo> ListPowerZDevices()
    {
        var result = new List<PortInfo>();
        try
        {
            foreach (var d in HidSharp.DeviceList.Local.GetHidDevices())
            {
                if (!IsPowerZHid(d))
                    continue;
                string name;
                try { name = d.GetProductName(); } catch { name = "PowerZ"; }
                result.Add(new PortInfo("PowerZ(HID)",
                    $"{d.VendorID:X4}:{d.ProductID:X4}", name));
            }
        }
        catch { /* HidSharp 不可用则无 HID 识别 */ }
        return result;
    }

    /// <summary>PowerZ HID 身份判定：VID:PID 命中已知表，或厂商/产品名含关键词。</summary>
    private static bool IsPowerZHid(HidSharp.HidDevice d)
    {
        foreach (var (vid, pid) in PowerZIds)
        {
            if (d.VendorID == Convert.ToInt32(vid, 16) &&
                d.ProductID == Convert.ToInt32(pid, 16))
                return true;
        }

        string name;
        try { name = $"{d.GetManufacturer()} {d.GetProductName()}"; }
        catch { name = d.DevicePath; }
        foreach (var kw in PowerZNameKeywords)
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>是否 CAN 盒子设备（VID=3562:0101 或描述含 ZQWL/CANFD 关键词）。</summary>
    public static bool IsCanDevice(PortInfo p)
    {
        if (string.Equals(p.VidPid, $"{CanVid}:{CanPid}", StringComparison.OrdinalIgnoreCase))
            return true;
        var hay = $"{p.Description} {p.Label}";
        foreach (var kw in CanKeywords)
            if (hay.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>找 CAN（ZQWL）串口名：层①VID 精确 → 层②描述关键词 → null。</summary>
    public static string? FindCanPort(IReadOnlyList<PortInfo> ports)
    {
        foreach (var p in ports)
            if (IsCanDevice(p))
                return p.Name;
        return null;
    }

    /// <summary>
    /// 找 PowerZ 串口名：VID 已知则精确匹配；否则取「普通串口」——
    /// 排除一切 CAN 盒子设备（VID/关键词身份，不管 CAN 下拉当前选什么）
    /// 与 CAN 下拉当前选中的口。
    /// </summary>
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
            if (p.Name != canPort && !IsCanDevice(p))
                return p.Name;
        return null;
    }
}
