using System.IO;
using System.Text;
using System.Text.Json;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App.Utilities;

/// <summary>
/// 本机配置（程序目录 machine.json）：
///   { "stationNo": 1, "machineId": "1",
///     "coilAThresholdA": 1.0, "coilBThresholdA": 1.0,
///     "coilACollectMs": 10000, "coilBSustainMs": 3000, "overallTimeoutMs": 60000 }
/// 分两类：机位身份（「机位设置」页）、步骤 5 双线圈检测参数
/// （「检测参数」设置页）。保存任一类都保留另一类；变更写 machine_change.log 留痕。
/// </summary>
public static class MachineConfig
{
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
        // 下限 = 旧阈值，上限 = 旧阈值×2（保持旧「≥阈值」行为不突变）
        double legacyA = cfg.CoilAThresholdA > 0 ? cfg.CoilAThresholdA : 1.0;
        double legacyB = cfg.CoilBThresholdA > 0 ? cfg.CoilBThresholdA : 1.0;
        return new CoilTestOptions
        {
            CoilALowerA = cfg.CoilALowerA > 0 ? cfg.CoilALowerA.Value : legacyA,
            CoilAUpperA = cfg.CoilAUpperA > 0 ? cfg.CoilAUpperA.Value : legacyA * 2,
            CoilBLowerA = cfg.CoilBLowerA > 0 ? cfg.CoilBLowerA.Value : legacyB,
            CoilBUpperA = cfg.CoilBUpperA > 0 ? cfg.CoilBUpperA.Value : legacyB * 2,
            CoilACollectMs = cfg.CoilACollectMs > 0 ? cfg.CoilACollectMs : 10_000,
            CoilBSustainMs = cfg.CoilBSustainMs > 0 ? cfg.CoilBSustainMs : 3_000,
            TransitionMs = cfg.TransitionMs > 0 ? cfg.TransitionMs : 2_000,
            SampleIntervalMs = cfg.SampleIntervalMs > 0 ? cfg.SampleIntervalMs : 200,
            OverallTimeoutMs = cfg.OverallTimeoutMs > 0 ? cfg.OverallTimeoutMs : 60_000,
        };
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

    /// <summary>版本预设（「版本预设」页）：APP/Boot/Qi/硬件，空 = 不比对该项。</summary>
    public sealed record VersionPresets(string App, string Boot, string Qi, string Hw);

    public static VersionPresets LoadVersionPresets()
    {
        var cfg = ReadCfgOrDefault();
        return new VersionPresets(
            (cfg.PresetAppVersion ?? "").Trim(),
            (cfg.PresetBootVersion ?? "").Trim(),
            (cfg.PresetQiVersion ?? "").Trim(),
            (cfg.PresetHwVersion ?? "").Trim());
    }

    /// <summary>保存版本预设；变更写 machine_change.log 留痕。</summary>
    public static void SaveVersionPresets(VersionPresets p)
    {
        var trimmed = new VersionPresets(p.App.Trim(), p.Boot.Trim(), p.Qi.Trim(), p.Hw.Trim());
        var cfg = ReadCfgOrDefault();
        var before = LoadVersionPresets();
        cfg.PresetAppVersion = trimmed.App;
        cfg.PresetBootVersion = trimmed.Boot;
        cfg.PresetQiVersion = trimmed.Qi;
        cfg.PresetHwVersion = trimmed.Hw;
        WriteCfg(cfg);

        if (before == trimmed)
            return;
        static string Dash(string v) => v.Length == 0 ? "(空)" : v;
        File.AppendAllText(ChangeLogPath,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  版本预设 APP {Dash(before.App)}→{Dash(trimmed.App)}" +
            $" / Boot {Dash(before.Boot)}→{Dash(trimmed.Boot)}" +
            $" / Qi {Dash(before.Qi)}→{Dash(trimmed.Qi)}" +
            $" / HW {Dash(before.Hw)}→{Dash(trimmed.Hw)}  ({Environment.MachineName})\n");
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
        double aWindowSec, double bSustainSec, double timeoutSec, double transitionSec,
        double sampleIntervalMs)
    {
        if (aLower <= 0 || aUpper <= 0 || bLower <= 0 || bUpper <= 0)
            return "电流上下限需大于 0";
        if (aLower >= aUpper)
            return "A 下限需小于 A 上限";
        if (bLower >= bUpper)
            return "B 下限需小于 B 上限";
        if (aWindowSec <= 0 || bSustainSec <= 0)
            return "A 窗口 / B 持续需大于 0";
        if (transitionSec <= 0)
            return "跌落确认需大于 0";
        if (sampleIntervalMs < 50 || sampleIntervalMs > 5000)
            return "采样间隔需在 50~5000 ms 之间";
        if (timeoutSec <= 0)
            return "整体超时需大于 0";
        if (timeoutSec <= aWindowSec)
            return "整体超时需大于 A 窗口（否则 A 未采完即超时）";
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

        var identityChanged = previous.StationNo != stationNo || previous.MachineId != machineId;
        if (!identityChanged)
            return;

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .Append("  ").Append(previous.Identity.Code)
            .Append(" -> ").Append(new StationIdentity(stationNo, machineId).Code);
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
        cfg.CoilACollectMs = o.CoilACollectMs;
        cfg.CoilBSustainMs = o.CoilBSustainMs;
        cfg.TransitionMs = o.TransitionMs;
        cfg.SampleIntervalMs = o.SampleIntervalMs;
        cfg.OverallTimeoutMs = o.OverallTimeoutMs;
        WriteCfg(cfg);

        if (before.CoilALowerA == o.CoilALowerA && before.CoilAUpperA == o.CoilAUpperA &&
            before.CoilBLowerA == o.CoilBLowerA && before.CoilBUpperA == o.CoilBUpperA &&
            before.CoilACollectMs == o.CoilACollectMs &&
            before.CoilBSustainMs == o.CoilBSustainMs &&
            before.TransitionMs == o.TransitionMs &&
            before.SampleIntervalMs == o.SampleIntervalMs &&
            before.OverallTimeoutMs == o.OverallTimeoutMs)
            return;

        File.AppendAllText(ChangeLogPath,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  检测参数 A区间 {before.CoilALowerA:F2}~{before.CoilAUpperA:F2}→{o.CoilALowerA:F2}~{o.CoilAUpperA:F2}A" +
            $" / B区间 {before.CoilBLowerA:F2}~{before.CoilBUpperA:F2}→{o.CoilBLowerA:F2}~{o.CoilBUpperA:F2}A" +
            $" / A窗口 {before.CoilACollectMs / 1000.0:F1}→{o.CoilACollectMs / 1000.0:F1}s" +
            $" / B持续 {before.CoilBSustainMs / 1000.0:F1}→{o.CoilBSustainMs / 1000.0:F1}s" +
            $" / 跌落确认 {before.TransitionMs / 1000.0:F1}→{o.TransitionMs / 1000.0:F1}s" +
            $" / 采样间隔 {before.SampleIntervalMs}→{o.SampleIntervalMs}ms" +
            $" / 超时 {before.OverallTimeoutMs / 1000.0:F0}→{o.OverallTimeoutMs / 1000.0:F0}s" +
            $"  ({Environment.MachineName})\n");
    }

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
        public int CoilACollectMs { get; set; } = 10_000;
        public int CoilBSustainMs { get; set; } = 3_000;
        public int TransitionMs { get; set; } = 2_000;
        public int SampleIntervalMs { get; set; } = 200;
        public int OverallTimeoutMs { get; set; } = 60_000;

        // 扫码等待、SN 输入判定、扫码阶段整体超时（秒）
        public int ScanTimeoutSeconds { get; set; } = 10;
        public int SnJudgeTimeoutSeconds { get; set; } = 2;
        public int ScanOverallTimeoutSeconds { get; set; } = 15;

        // 版本预设（「版本预设」页：APP/Boot/Qi/硬件；空 = 不比对该项）
        public string PresetAppVersion { get; set; } = "";
        public string PresetBootVersion { get; set; } = "";
        public string PresetQiVersion { get; set; } = "";
        public string PresetHwVersion { get; set; } = "";

    }
}

/// <summary>本机设置全量（机位身份）。</summary>
public sealed record MachineSettings(int StationNo, string MachineId)
{
    public StationIdentity Identity => new(StationNo, MachineId);
}
