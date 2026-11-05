using System.IO;
using System.Text.Json;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.App.Utilities;

/// <summary>
/// 本机身份配置（程序目录 machine.json）：
///   { "stationNo": 1, "machineId": "1" }
/// 每工位机位数随产量动态增减——新增/撤机位只改这一个文件后重启 App，
/// 代码零改动、服务器零改动。文件缺失时生成默认配置并回落 工位1/机位1。
/// </summary>
public static class MachineConfig
{
    public static StationIdentity Load()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "machine.json");
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<Cfg>(File.ReadAllText(path), JsonOptions);
                if (cfg is not null && cfg.StationNo >= 1 && !string.IsNullOrWhiteSpace(cfg.MachineId))
                    return new StationIdentity(cfg.StationNo, cfg.MachineId.Trim());
            }
            else
            {
                File.WriteAllText(path,
                    JsonSerializer.Serialize(new Cfg(), JsonOptions));
            }
        }
        catch
        {
            // 配置损坏/不可写时回落默认，不阻塞启动
        }
        return new StationIdentity(1, "1");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed class Cfg
    {
        public int StationNo { get; set; } = 1;
        public string MachineId { get; set; } = "1";
    }
}
