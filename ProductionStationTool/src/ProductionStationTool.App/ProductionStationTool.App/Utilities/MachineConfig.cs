using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProductionStationTool.Core.Printing;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App.Utilities;

/// <summary>
/// 本机配置（程序目录 machine.json）：
///   { "stationNo": 1, "machineId": "1", "serverHost": "192.168.10.10",
///     "coilALowerA": 1.0, "coilAUpperA": 2.0, "coilBLowerA": 1.0,
///     "coilBUpperA": 2.0, "detectionWindowMs": 10000, "overallTimeoutMs": 60000 }
/// 另含工位 4 四项版本预设、汇总机良率显示修正、标签设计、汇总机密码哈希等。
/// 保存任一类都保留其余配置；变更写 machine_change.log 留痕。
/// </summary>
public static class MachineConfig
{
    /// <summary>默认服务器地址（doc 18/21 的 QI-SERVER）。</summary>
    public const string DefaultServerHost = "192.168.10.10";

    // 工位 4 四项版本预设的出厂默认值（当前量产固件版本）；
    // 操作员可在「设置 → 版本预设」中修改，machine.json 显式留空则不比对通过。
    private const string DefaultPresetMcu1App = "QC_JYF_MCU1_FW_1.2.3";
    private const string DefaultPresetMcu2Qi = "QC_JYF_MCU2_FW_1.2.3";
    private const string DefaultPresetBootloader = "QC_JYF_BL_1.0.0";
    private const string DefaultPresetHardware = "QC_JYF_HW_1.1.5";

    public static StationIdentity Load() => LoadSettings().Identity;

    public static string LoadServerHost() => LoadSettings().ServerHost;

    public static MachineSettings LoadSettings()
    {
        var cfg = ReadCfgOrDefault();
        return new MachineSettings(
            cfg.StationNo >= 1 ? cfg.StationNo : 1,
            string.IsNullOrWhiteSpace(cfg.MachineId) ? "1" : cfg.MachineId.Trim(),
            string.IsNullOrWhiteSpace(cfg.ServerHost) ? DefaultServerHost : cfg.ServerHost.Trim());
    }

    /// <summary>步骤 5 双线圈检测参数（「检测参数」设置页保存；每次测试开始时读取）。</summary>
    public static CoilTestOptions LoadCoilOptions()
    {
        var cfg = ReadCfgOrDefault();
        // 上下限缺失时迁移旧单阈值（CoilAThresholdA/CoilBThresholdA）：
        // 下限 = 旧阈值，上限 = 旧阈值×2（保持旧「≥阈值」行为不突变）
        double legacyA = cfg.CoilAThresholdA > 0 ? cfg.CoilAThresholdA : 1.0;
        double legacyB = cfg.CoilBThresholdA > 0 ? cfg.CoilBThresholdA : 1.0;
        // detectionWindowMs 为新键；旧 machine.json 的 coilACollectMs 继续兼容
        int detectionWindow = cfg.DetectionWindowMs > 0
            ? cfg.DetectionWindowMs
            : (cfg.CoilACollectMs > 0 ? cfg.CoilACollectMs : 10_000);
        return new CoilTestOptions
        {
            CoilALowerA = cfg.CoilALowerA > 0 ? cfg.CoilALowerA.Value : legacyA,
            CoilAUpperA = cfg.CoilAUpperA > 0 ? cfg.CoilAUpperA.Value : legacyA * 2,
            CoilBLowerA = cfg.CoilBLowerA > 0 ? cfg.CoilBLowerA.Value : legacyB,
            CoilBUpperA = cfg.CoilBUpperA > 0 ? cfg.CoilBUpperA.Value : legacyB * 2,
            DetectionWindowMs = detectionWindow,
            SampleIntervalMs = cfg.SampleIntervalMs > 0 ? cfg.SampleIntervalMs : 200,
            OverallTimeoutMs = cfg.OverallTimeoutMs > 0 ? cfg.OverallTimeoutMs : 60_000,
        };
    }

    /// <summary>校验机位输入；返回 null 表示通过，否则返回错误提示。</summary>
    public static string? Validate(int stationNo, string? machineId)
    {
        if (stationNo is < 1 or > 99)
            return "工位号需在 1~99 之间";
        var mid = machineId?.Trim() ?? "";
        if (mid.Length is < 1 or > 8)
            return "机位号需 1~8 个字符";
        foreach (var c in mid)
        {
            if (!char.IsAsciiLetterOrDigit(c))
                return "机位号只能是字母或数字";
        }
        return null;
    }

    /// <summary>校验检测参数（秒）；返回 null 表示通过。</summary>
    public static string? ValidateCoil(double aLower, double aUpper,
        double bLower, double bUpper,
        double detectionWindowSec, double timeoutSec, double sampleIntervalMs)
    {
        if (aLower <= 0 || aUpper <= 0 || bLower <= 0 || bUpper <= 0)
            return "电流上下限需大于 0";
        if (aLower >= aUpper)
            return "A 下限需小于 A 上限";
        if (bLower >= bUpper)
            return "B 下限需小于 B 上限";
        double minEffectiveSec = new CoilTestOptions().MinEffectiveMs / 1000.0;
        if (detectionWindowSec < minEffectiveSec)
            return $"检测窗口需至少 {minEffectiveSec:0.#}s（有效检测时间下限）";
        if (sampleIntervalMs < 50 || sampleIntervalMs > 5000)
            return "采样间隔需在 50~5000 ms 之间";
        if (timeoutSec <= 0)
            return "整体超时需大于 0";
        if (timeoutSec <= detectionWindowSec)
            return "整体超时需大于检测窗口（否则 A 未采完即超时）";
        return null;
    }

    /// <summary>保存机位身份/服务器地址；变更时写 machine_change.log 留痕。失败抛 IOException。</summary>
    public static void Save(int stationNo, string machineId, string serverHost)
    {
        machineId = machineId.Trim();
        serverHost = string.IsNullOrWhiteSpace(serverHost) ? DefaultServerHost : serverHost.Trim();

        var cfg = ReadCfgOrDefault();
        var previous = new MachineSettings(cfg.StationNo, cfg.MachineId, cfg.ServerHost);
        cfg.StationNo = stationNo;
        cfg.MachineId = machineId;
        cfg.ServerHost = serverHost;
        WriteCfg(cfg);

        var identityChanged = previous.StationNo != stationNo || previous.MachineId != machineId;
        var serverChanged = !string.Equals(previous.ServerHost, serverHost, StringComparison.OrdinalIgnoreCase);
        if (!identityChanged && !serverChanged)
            return;

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .Append("  ").Append(previous.Identity.Code)
            .Append(" -> ").Append(new StationIdentity(stationNo, machineId).Code);
        if (serverChanged)
            line.Append("  server ").Append(previous.ServerHost).Append(" -> ").Append(serverHost);
        line.Append("  (").Append(Environment.MachineName).AppendLine(")");
        File.AppendAllText(ChangeLogPath, line.ToString());
    }

    /// <summary>保存步骤 5 检测参数；变更时写 machine_change.log 留痕。失败抛 IOException。</summary>
    public static void SaveCoilOptions(CoilTestOptions o)
    {
        var cfg = ReadCfgOrDefault();
        var before = LoadCoilOptions();
        cfg.CoilALowerA = o.CoilALowerA;
        cfg.CoilAUpperA = o.CoilAUpperA;
        cfg.CoilBLowerA = o.CoilBLowerA;
        cfg.CoilBUpperA = o.CoilBUpperA;
        cfg.DetectionWindowMs = o.DetectionWindowMs;
        cfg.CoilACollectMs = o.DetectionWindowMs;   // 旧键同步，兼容旧版本读取
        cfg.SampleIntervalMs = o.SampleIntervalMs;
        cfg.OverallTimeoutMs = o.OverallTimeoutMs;
        WriteCfg(cfg);

        if (before.CoilALowerA == o.CoilALowerA && before.CoilAUpperA == o.CoilAUpperA &&
            before.CoilBLowerA == o.CoilBLowerA && before.CoilBUpperA == o.CoilBUpperA &&
            before.DetectionWindowMs == o.DetectionWindowMs &&
            before.SampleIntervalMs == o.SampleIntervalMs &&
            before.OverallTimeoutMs == o.OverallTimeoutMs)
            return;

        File.AppendAllText(ChangeLogPath,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  检测参数 A区间 {before.CoilALowerA:F2}~{before.CoilAUpperA:F2}→{o.CoilALowerA:F2}~{o.CoilAUpperA:F2}A" +
            $" / B区间 {before.CoilBLowerA:F2}~{before.CoilBUpperA:F2}→{o.CoilBLowerA:F2}~{o.CoilBUpperA:F2}A" +
            $" / 检测窗口 {before.DetectionWindowMs / 1000.0:F1}→{o.DetectionWindowMs / 1000.0:F1}s" +
            $" / 采样间隔 {before.SampleIntervalMs}→{o.SampleIntervalMs}ms" +
            $" / 超时 {before.OverallTimeoutMs / 1000.0:F0}→{o.OverallTimeoutMs / 1000.0:F0}s" +
            $"  ({Environment.MachineName})\n");
    }

    /// <summary>
    /// 工位 4 四项版本预设：MCU1 APP / MCU2 Qi / Bootloader / 硬件。
    /// 读取值与之逐项比对，空 = 该项未预设。
    /// </summary>
    public static VersionPresets LoadVersionPresets()
    {
        var cfg = ReadCfgOrDefault();
        // 新键存在（含显式空串）优先；否则用旧版 machine.json 的软件/硬件项；
        // 旧键也不存在才用出厂默认值。
        return new VersionPresets(
            ResolvePreset(cfg.PresetMcu1App, cfg.PresetSwVersion, DefaultPresetMcu1App),
            ResolvePreset(cfg.PresetMcu2Qi, null, DefaultPresetMcu2Qi),
            ResolvePreset(cfg.PresetBootloader, null, DefaultPresetBootloader),
            ResolvePreset(cfg.PresetHardware, cfg.PresetHwVersion, DefaultPresetHardware));
    }

    /// <summary>保存工位 4 四项版本预设；变更时写 machine_change.log 留痕。</summary>
    public static void SaveVersionPresets(string mcu1App, string mcu2Qi, string bootloader, string hardware)
    {
        mcu1App = mcu1App.Trim();
        mcu2Qi = mcu2Qi.Trim();
        bootloader = bootloader.Trim();
        hardware = hardware.Trim();
        var cfg = ReadCfgOrDefault();
        var before = LoadVersionPresets();
        var after = new VersionPresets(mcu1App, mcu2Qi, bootloader, hardware);
        cfg.PresetMcu1App = mcu1App;
        cfg.PresetMcu2Qi = mcu2Qi;
        cfg.PresetBootloader = bootloader;
        cfg.PresetHardware = hardware;
        // 旧键同步：旧版本程序仍按软件/硬件两项读取
        cfg.PresetSwVersion = mcu1App;
        cfg.PresetHwVersion = hardware;
        WriteCfg(cfg);

        if (before == after)
            return;
        File.AppendAllText(ChangeLogPath,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  版本预设" +
            $" MCU1 {ShowOrDash(before.Mcu1App)}→{ShowOrDash(mcu1App)}" +
            $" / QI {ShowOrDash(before.Mcu2Qi)}→{ShowOrDash(mcu2Qi)}" +
            $" / BL {ShowOrDash(before.Bootloader)}→{ShowOrDash(bootloader)}" +
            $" / HW {ShowOrDash(before.Hardware)}→{ShowOrDash(hardware)}" +
            $"  ({Environment.MachineName})\n");
    }

    private static string ResolvePreset(string? current, string? legacy, string fallback)
    {
        // 键存在即以其值为准（"" 也是显式留空）；旧机器配置没有新键，回退旧键；
        // 新旧键都没有才用出厂默认。
        if (current is not null)
            return current.Trim();
        if (legacy is not null)
            return legacy.Trim();
        return fallback;
    }

    /// <summary>标签设计器规格（「打印机设置」页：尺寸/打印机参数/对象排版/条码）。</summary>
    public static LabelDesign LoadLabelDesign()
        => ReadCfgOrDefault().SnLabelDesign ?? new LabelDesign();

    /// <summary>保存标签设计器规格；变更写 machine_change.log 留痕。</summary>
    public static void SaveLabelDesign(LabelDesign design)
    {
        var cfg = ReadCfgOrDefault();
        cfg.SnLabelDesign = design;
        WriteCfg(cfg);
        File.AppendAllText(ChangeLogPath,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  标签设计 尺寸 {design.LabelWidthMm}×{design.LabelHeightMm}mm" +
            $" 打印机[{ShowOrDash(design.PrinterName)}] {design.Script.ToUpper()}" +
            $" 对象 {design.Objects.Count} 个  ({Environment.MachineName})\n");
    }

    /// <summary>
    /// 汇总机工位卡片良率手动修正（按机位编码）。只覆盖卡片显示，
    /// 不修改 test_records；Enabled=false 时卡片显示实际统计。
    /// </summary>
    public static IReadOnlyList<YieldOverride> LoadYieldOverrides()
    {
        var cfg = ReadCfgOrDefault();
        return (cfg.YieldOverrides ?? new List<YieldOverride>())
            .Where(o => !string.IsNullOrWhiteSpace(o.Code))
            .Select(o => new YieldOverride(o.Code.Trim(), o.Enabled, o.Percent))
            .ToList();
    }

    /// <summary>保存良率手动修正；变更时写 machine_change.log 留痕。</summary>
    public static void SaveYieldOverrides(IEnumerable<YieldOverride> overrides)
    {
        var cfg = ReadCfgOrDefault();
        var before = LoadYieldOverrides();
        var after = overrides
            .Where(o => !string.IsNullOrWhiteSpace(o.Code))
            .GroupBy(o => o.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var last = g.Last();
                return new YieldOverride(g.Key, last.Enabled, last.Percent);
            })
            .ToList();
        cfg.YieldOverrides = after;
        WriteCfg(cfg);

        if (before.SequenceEqual(after))
            return;
        File.AppendAllText(ChangeLogPath,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  良率显示修正" +
            $" {before.Count(o => o.Enabled)}→{after.Count(o => o.Enabled)} 项启用" +
            $"  ({Environment.MachineName})\n");
    }

    private static string ShowOrDash(string s) => string.IsNullOrEmpty(s) ? "(空)" : s;

    /// <summary>SHA-256 十六进制（小写）——汇总机管理密码存哈希不存明文。</summary>
    public static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// 汇总机管理密码哈希（machine.json `summaryPasswordSha256`）。
    /// 未配置时默认密码 "admin123"（首次使用请在汇总机页「修改密码」中改掉）。
    /// </summary>
    public static string LoadSummaryPasswordHash()
    {
        var cfg = ReadCfgOrDefault();
        return string.IsNullOrWhiteSpace(cfg.SummaryPasswordSha256)
            ? Sha256Hex(DefaultSummaryPassword)
            : cfg.SummaryPasswordSha256.Trim().ToLowerInvariant();
    }

    /// <summary>保存汇总机管理密码哈希；只记「已修改」留痕，不写哈希值。</summary>
    public static void SaveSummaryPasswordHash(string hashHex)
    {
        var cfg = ReadCfgOrDefault();
        cfg.SummaryPasswordSha256 = hashHex.Trim().ToLowerInvariant();
        WriteCfg(cfg);
        File.AppendAllText(ChangeLogPath,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  汇总机管理密码已修改  ({Environment.MachineName})\n");
    }

    /// <summary>出厂默认管理密码（仅此处定义，存的是它的哈希）。</summary>
    private const string DefaultSummaryPassword = "admin123";

    private static Cfg ReadCfgOrDefault()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<Cfg>(File.ReadAllText(ConfigPath), JsonOptions);
                if (cfg is not null)
                    return cfg;
            }
        }
        catch
        {
            // 配置损坏时回落默认，不阻塞启动
        }
        return new Cfg();
    }

    private static void WriteCfg(Cfg cfg)
        => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOptions));

    private static string ConfigPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "machine.json");

    private static string ChangeLogPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "machine_change.log");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed class Cfg
    {
        public int StationNo { get; set; } = 1;
        public string MachineId { get; set; } = "1";
        public string ServerHost { get; set; } = DefaultServerHost;

        // 步骤 5 双线圈检测参数（「检测参数」设置页）
        // 上下限（null=旧配置未迁移）；旧单阈值字段仅迁移兼容，不再写入
        public double? CoilALowerA { get; set; }
        public double? CoilAUpperA { get; set; }
        public double? CoilBLowerA { get; set; }
        public double? CoilBUpperA { get; set; }
        public double CoilAThresholdA { get; set; }   // legacy：迁移后被上/下限取代
        public double CoilBThresholdA { get; set; }   // legacy
        public int DetectionWindowMs { get; set; }       // 新键；0=旧配置，回退 CoilACollectMs
        public int CoilACollectMs { get; set; }       // legacy：旧 A 窗口键，读时兼容
        public int SampleIntervalMs { get; set; } = 200;
        public int OverallTimeoutMs { get; set; } = 60_000;

        // 汇总机管理密码（SHA-256 十六进制；空 = 默认密码 admin123）
        public string SummaryPasswordSha256 { get; set; } = "";

        // 工位 4 四项版本预设（null=键不存在，读取时用旧键/出厂默认；""=显式留空）
        public string? PresetMcu1App { get; set; }
        public string? PresetMcu2Qi { get; set; }
        public string? PresetBootloader { get; set; }
        public string? PresetHardware { get; set; }
        // legacy：旧版只有软件/硬件两项，读取时迁移到 MCU1 APP / 硬件
        public string? PresetSwVersion { get; set; }
        public string? PresetHwVersion { get; set; }

        // 汇总机工位卡片良率手动修正（按机位编码；只覆盖显示，不改记录）
        public List<YieldOverride>? YieldOverrides { get; set; } = new();

        // 标签设计器规格（「打印机设置」页）
        public LabelDesign SnLabelDesign { get; set; } = new();
    }
}

/// <summary>本机设置全量（机位身份 + 服务器地址）。</summary>
public sealed record MachineSettings(int StationNo, string MachineId, string ServerHost)
{
    public StationIdentity Identity => new(StationNo, MachineId);
}

/// <summary>工位 4 四项版本预设；空字符串 = 该项未预设。</summary>
public sealed record VersionPresets(string Mcu1App, string Mcu2Qi, string Bootloader, string Hardware);

/// <summary>汇总机工位卡片良率手动修正；Code=ST-01-1，Enabled=false 显示实际统计。</summary>
public sealed record YieldOverride(string Code, bool Enabled, double Percent);
