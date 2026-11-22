using System.IO;
using System.Text;
using System.Text.Json;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App.Utilities;

/// <summary>
/// 本机配置（程序目录 machine.json）：
///   { "stationNo": 1, "machineId": "1",
///     "coilALowerA": 0.3, "coilAUpperA": 2.0, "coilBLowerA": 0.3,
///     "coilBUpperA": 2.0, "detectionWindowMs": 10000, "overallTimeoutMs": 60000 }
/// 另含扫码时间参数与四项版本预设。
/// 保存任一类都保留其余配置；变更写 machine_change.log 留痕。
/// 所有参数保存在本机 machine.json，重启上位机自动沿用本地保存值。
/// </summary>
public static class MachineConfig
{
    // 工位 4 四项版本预设的出厂默认值（当前量产固件版本）；
    // 操作员可在「设置 → 版本预设」中修改，machine.json 显式留空则不比对通过。
    private const string DefaultPresetMcu1App = "QC_JYF_MCU1_FW_1.2.3";
    private const string DefaultPresetMcu2Qi = "QC_JYF_MCU2_FW_1.2.3";
    private const string DefaultPresetBootloader = "QC_JYF_BL_1.0.0";
    private const string DefaultPresetHardware = "QC_JYF_HW_1.1.5";

    public static StationIdentity Load() => LoadSettings().Identity;

    public static MachineSettings LoadSettings()
    {
        var cfg = ReadCfgOrDefault();
        return new MachineSettings(
            cfg.StationNo >= 1 ? cfg.StationNo : 1,
            string.IsNullOrWhiteSpace(cfg.MachineId) ? "1" : cfg.MachineId.Trim());
    }

    /// <summary>步骤 5 双线圈检测参数（「检测参数」设置页保存；每次测试开始时读取）。</summary>
    public static CoilTestOptions LoadCoilOptions()
    {
        var cfg = ReadCfgOrDefault();
        // 上下限缺失时迁移旧单阈值（CoilAThresholdA/CoilBThresholdA）：
        // 下限 = 旧阈值，上限 = 旧阈值×2（保持旧「≥阈值」行为不突变）；
        // 无任何旧值时用出厂默认：下限 0.3A、上限 2A（本机保存过的值优先）。
        const double defaultLower = 0.3;
        const double defaultUpper = 2.0;
        double? legacyA = cfg.CoilAThresholdA > 0 ? cfg.CoilAThresholdA : null;
        double? legacyB = cfg.CoilBThresholdA > 0 ? cfg.CoilBThresholdA : null;
        // detectionWindowMs 为新键；旧 machine.json 的 coilACollectMs 继续兼容
        int detectionWindow = cfg.DetectionWindowMs > 0
            ? cfg.DetectionWindowMs
            : (cfg.CoilACollectMs > 0 ? cfg.CoilACollectMs : 10_000);
        return new CoilTestOptions
        {
            CoilALowerA = cfg.CoilALowerA > 0 ? cfg.CoilALowerA.Value : legacyA ?? defaultLower,
            CoilAUpperA = cfg.CoilAUpperA > 0 ? cfg.CoilAUpperA.Value : (legacyA is { } la ? la * 2 : defaultUpper),
            CoilBLowerA = cfg.CoilBLowerA > 0 ? cfg.CoilBLowerA.Value : legacyB ?? defaultLower,
            CoilBUpperA = cfg.CoilBUpperA > 0 ? cfg.CoilBUpperA.Value : (legacyB is { } lb ? lb * 2 : defaultUpper),
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

    /// <summary>保存机位身份；变更时写 machine_change.log 留痕。失败抛 IOException。</summary>
    public static void Save(int stationNo, string machineId)
    {
        machineId = machineId.Trim();

        var cfg = ReadCfgOrDefault();
        var previous = new MachineSettings(cfg.StationNo, cfg.MachineId);
        cfg.StationNo = stationNo;
        cfg.MachineId = machineId;
        WriteCfg(cfg);

        if (previous.StationNo == stationNo && previous.MachineId == machineId)
            return;

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .Append("  ").Append(previous.Identity.Code)
            .Append(" -> ").Append(new StationIdentity(stationNo, machineId).Code)
            .Append("  (").Append(Environment.MachineName).AppendLine(")");
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

    /// <summary>扫码时间参数：无输入等待、输入停止后的 SN 判定、整个扫码阶段硬超时。</summary>
    public static (int ScanSeconds, int JudgeSeconds, int OverallSeconds) LoadScanTiming()
    {
        var cfg = ReadCfgOrDefault();
        int scan = cfg.ScanTimeoutSeconds is >= 1 and <= 120 ? cfg.ScanTimeoutSeconds : 10;
        int judge = cfg.SnJudgeTimeoutSeconds is >= 1 and <= 120 ? cfg.SnJudgeTimeoutSeconds : 2;
        int overall = cfg.ScanOverallTimeoutSeconds is >= 1 and <= 300
            ? cfg.ScanOverallTimeoutSeconds
            : Math.Max(15, scan + judge + 1);
        if (overall <= scan + judge)
            overall = scan + judge + 1;
        return (scan, judge, overall);
    }

    public static string? ValidateScanTiming(int scan, int judge, int overall)
    {
        if (scan is < 1 or > 120)
            return "扫码超时需在 1~120 秒之间";
        if (judge is < 1 or > 120)
            return "SN 码判定超时需在 1~120 秒之间";
        if (overall is < 1 or > 300)
            return "整体超时需在 1~300 秒之间";
        if (overall <= scan + judge)
            return "整体超时必须大于扫码超时与 SN 码判定超时之和";
        return null;
    }

    /// <summary>保存扫码时间参数；变更时写 machine_change.log 留痕。</summary>
    public static void SaveScanTiming(int scan, int judge, int overall)
    {
        var cfg = ReadCfgOrDefault();
        var before = LoadScanTiming();
        cfg.ScanTimeoutSeconds = scan;
        cfg.SnJudgeTimeoutSeconds = judge;
        cfg.ScanOverallTimeoutSeconds = overall;
        WriteCfg(cfg);
        if (before == (scan, judge, overall))
            return;
        File.AppendAllText(ChangeLogPath,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  扫码超时 {before.ScanSeconds}s→{scan}s" +
            $" / SN判定 {before.JudgeSeconds}s→{judge}s" +
            $" / 整体 {before.OverallSeconds}s→{overall}s  ({Environment.MachineName})\n");
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

    private static string ShowOrDash(string s) => string.IsNullOrEmpty(s) ? "(空)" : s;

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

        // 扫码时间参数（工位3 扫码枪：无输入等待 / SN 判定 / 整体硬超时）
        public int ScanTimeoutSeconds { get; set; } = 10;
        public int SnJudgeTimeoutSeconds { get; set; } = 2;
        public int ScanOverallTimeoutSeconds { get; set; } = 15;

        // 工位 4 四项版本预设（null=键不存在，读取时用旧键/出厂默认；""=显式留空）
        public string? PresetMcu1App { get; set; }
        public string? PresetMcu2Qi { get; set; }
        public string? PresetBootloader { get; set; }
        public string? PresetHardware { get; set; }
        // legacy：旧版只有软件/硬件两项，读取时迁移到 MCU1 APP / 硬件
        public string? PresetSwVersion { get; set; }
        public string? PresetHwVersion { get; set; }

    }
}

/// <summary>本机设置全量（机位身份）。</summary>
public sealed record MachineSettings(int StationNo, string MachineId)
{
    public StationIdentity Identity => new(StationNo, MachineId);
}

/// <summary>工位 4 四项版本预设；空字符串 = 该项未预设。</summary>
public sealed record VersionPresets(string Mcu1App, string Mcu2Qi, string Bootloader, string Hardware);
