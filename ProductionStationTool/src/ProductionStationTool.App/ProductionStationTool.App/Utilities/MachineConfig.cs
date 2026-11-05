using System.IO;
using System.Text;
using System.Text.Json;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App.Utilities;

/// <summary>
/// 本机身份配置（程序目录 machine.json）：
///   { "stationNo": 1, "machineId": "1", "serverHost": "192.168.10.10" }
/// 每工位机位数随产量动态增减——新增/撤机位只改配置后重启 App，
/// 代码零改动、服务器零改动。文件缺失时生成默认配置并回落 工位1/机位1。
/// 通过设置页保存时写 machine_change.log 留痕。
/// </summary>
public static class MachineConfig
{
    /// <summary>默认服务器地址（doc 18/21 的 QI-SERVER）。</summary>
    public const string DefaultServerHost = "192.168.10.10";

    public static StationIdentity Load() => LoadSettings().Identity;

    public static string LoadServerHost() => LoadSettings().ServerHost;

    public static MachineSettings LoadSettings()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<Cfg>(File.ReadAllText(ConfigPath), JsonOptions);
                if (cfg is not null && cfg.StationNo >= 1 && !string.IsNullOrWhiteSpace(cfg.MachineId))
                {
                    return new MachineSettings(cfg.StationNo, cfg.MachineId.Trim(),
                        string.IsNullOrWhiteSpace(cfg.ServerHost) ? DefaultServerHost : cfg.ServerHost.Trim());
                }
            }
            else
            {
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new Cfg(), JsonOptions));
            }
        }
        catch
        {
            // 配置损坏/不可写时回落默认，不阻塞启动
        }
        return new MachineSettings(1, "1", DefaultServerHost);
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

    /// <summary>保存设置；与上次有差异时写 machine_change.log 留痕。失败抛 IOException。</summary>
    public static void Save(int stationNo, string machineId, string serverHost)
    {
        machineId = machineId.Trim();
        serverHost = string.IsNullOrWhiteSpace(serverHost) ? DefaultServerHost : serverHost.Trim();

        var previous = LoadSettings();
        var cfg = new Cfg { StationNo = stationNo, MachineId = machineId, ServerHost = serverHost };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOptions));

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
    }
}

/// <summary>本机设置全量（机位身份 + 服务器地址）。</summary>
public sealed record MachineSettings(int StationNo, string MachineId, string ServerHost)
{
    public StationIdentity Identity => new(StationNo, MachineId);
}
