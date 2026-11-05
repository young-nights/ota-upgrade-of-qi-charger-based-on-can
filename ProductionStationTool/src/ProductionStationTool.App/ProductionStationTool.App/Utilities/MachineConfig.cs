using System.IO;
using System.Text;
using System.Text.Json;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App.Utilities;

/// <summary>
/// 本机配置（程序目录 machine.json）：
///   { "stationNo": 1, "machineId": "1", "serverHost": "192.168.10.10",
///     "coilAThresholdA": 1.0, "coilBThresholdA": 1.0,
///     "coilACollectMs": 10000, "coilBSustainMs": 3000, "overallTimeoutMs": 60000 }
/// 分两类：机位身份/服务器地址（「机位与网络」设置页）、步骤 5 双线圈检测参数
/// （「检测参数」设置页）。保存任一类都保留另一类；变更写 machine_change.log 留痕。
/// </summary>
public static class MachineConfig
{
    /// <summary>默认服务器地址（doc 18/21 的 QI-SERVER）。</summary>
    public const string DefaultServerHost = "192.168.10.10";

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
        return new CoilTestOptions
        {
            CoilAThresholdA = cfg.CoilAThresholdA > 0 ? cfg.CoilAThresholdA : 1.0,
            CoilBThresholdA = cfg.CoilBThresholdA > 0 ? cfg.CoilBThresholdA : 1.0,
            CoilACollectMs = cfg.CoilACollectMs > 0 ? cfg.CoilACollectMs : 10_000,
            CoilBSustainMs = cfg.CoilBSustainMs > 0 ? cfg.CoilBSustainMs : 3_000,
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
    public static string? ValidateCoil(double aThreshold, double bThreshold,
        double aWindowSec, double bSustainSec, double timeoutSec)
    {
        if (aThreshold <= 0 || bThreshold <= 0)
            return "电流阈值需大于 0";
        if (aWindowSec <= 0 || bSustainSec <= 0)
            return "A 窗口 / B 持续需大于 0";
        if (timeoutSec <= 0)
            return "整体超时需大于 0";
        if (timeoutSec <= aWindowSec)
            return "整体超时需大于 A 窗口（否则 A 未采完即超时）";
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
        cfg.CoilAThresholdA = o.CoilAThresholdA;
        cfg.CoilBThresholdA = o.CoilBThresholdA;
        cfg.CoilACollectMs = o.CoilACollectMs;
        cfg.CoilBSustainMs = o.CoilBSustainMs;
        cfg.OverallTimeoutMs = o.OverallTimeoutMs;
        WriteCfg(cfg);

        if (before.CoilAThresholdA == o.CoilAThresholdA &&
            before.CoilBThresholdA == o.CoilBThresholdA &&
            before.CoilACollectMs == o.CoilACollectMs &&
            before.CoilBSustainMs == o.CoilBSustainMs &&
            before.OverallTimeoutMs == o.OverallTimeoutMs)
            return;

        File.AppendAllText(ChangeLogPath,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  检测参数 A阈值 {before.CoilAThresholdA:F2}→{o.CoilAThresholdA:F2}A" +
            $" / B阈值 {before.CoilBThresholdA:F2}→{o.CoilBThresholdA:F2}A" +
            $" / A窗口 {before.CoilACollectMs / 1000.0:F1}→{o.CoilACollectMs / 1000.0:F1}s" +
            $" / B持续 {before.CoilBSustainMs / 1000.0:F1}→{o.CoilBSustainMs / 1000.0:F1}s" +
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
        public string ServerHost { get; set; } = DefaultServerHost;

        // 步骤 5 双线圈检测参数（「检测参数」设置页）
        public double CoilAThresholdA { get; set; } = 1.0;
        public double CoilBThresholdA { get; set; } = 1.0;
        public int CoilACollectMs { get; set; } = 10_000;
        public int CoilBSustainMs { get; set; } = 3_000;
        public int OverallTimeoutMs { get; set; } = 60_000;
    }
}

/// <summary>本机设置全量（机位身份 + 服务器地址）。</summary>
public sealed record MachineSettings(int StationNo, string MachineId, string ServerHost)
{
    public StationIdentity Identity => new(StationNo, MachineId);
}
