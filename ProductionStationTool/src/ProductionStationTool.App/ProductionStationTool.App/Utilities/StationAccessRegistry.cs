using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProductionStationTool.App.Utilities;

/// <summary>一台工位机的接入登记：编码（ST-xx-x）、备注、是否允许接入。</summary>
public sealed class StationAccessEntry
{
    public string Code { get; set; } = "";
    public string Note { get; set; } = "";
    public bool Allowed { get; set; } = true;
}

/// <summary>
/// 工位机接入登记表（程序目录 station_access.json）：
/// 汇总机页维护「允许接入服务器的工位机清单」，增删/允许/禁止即时落盘。
/// </summary>
public static class StationAccessRegistry
{
    public static List<StationAccessEntry> Load()
    {
        try
        {
            if (File.Exists(RegistryPath))
            {
                var list = JsonSerializer.Deserialize<List<StationAccessEntry>>(
                    File.ReadAllText(RegistryPath), JsonOptions);
                if (list is not null)
                    return list;
            }
        }
        catch
        {
            // 文件损坏时回落空表，不阻塞汇总机页
        }
        return new List<StationAccessEntry>();
    }

    public static void Save(IEnumerable<StationAccessEntry> entries)
        => File.WriteAllText(RegistryPath,
            JsonSerializer.Serialize(entries.ToList(), JsonOptions));

    /// <summary>校验工位机编码；返回 null 表示通过，否则返回错误提示。</summary>
    public static string? ValidateCode(string? code)
    {
        code = code?.Trim() ?? "";
        if (code.Length is < 1 or > 16)
            return "编码需 1~16 个字符";
        foreach (var c in code)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                return "编码只能是字母、数字或短横线";
        }
        return null;
    }

    private static string RegistryPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "station_access.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
