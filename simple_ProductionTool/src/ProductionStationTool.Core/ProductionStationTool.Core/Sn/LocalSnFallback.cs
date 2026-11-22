using System.Text.Json;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.Core.Sn;

/// <summary>
/// 汇总机不可达时的本机应急取号（只用于断网应急，产线仍应优先用汇总机统一发号）。
/// 号段固定 9000~9999，按机位分 40 个槽、每槽每天 25 个号：
///   seq = 9000 + slot*25 + 当日已用数
/// 常见 4 工位 × 8 机位映射到不同槽，避免同线多机位离线时互相重号；
/// 计数写程序目录 sn_offline.json，跨重启沿用，跨日自动清零。
/// </summary>
public static class LocalSnFallback
{
    public const int OfflineStartSeq = 9000;
    public const int SlotCount = 40;
    public const int PerSlotPerDay = 25;

    private const string FileName = "sn_offline.json";
    private static readonly object Gate = new();

    /// <summary>取一个本机应急号；号段用尽/文件异常返回失败（不会退回纯内存自增）。</summary>
    public static SnAllocResult Next(StationIdentity identity, DateTime date, string? path = null)
    {
        lock (Gate)
        {
            try
            {
                path ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
                var day = date.ToString("yyyy-MM-dd");
                var state = Load(path);
                if (!string.Equals(state.Date, day, StringComparison.Ordinal))
                    state = new State { Date = day };

                var slotKey = SlotOf(identity).ToString();
                state.Counters.TryGetValue(slotKey, out var used);
                if (used < 0)
                    used = 0;
                if (used >= PerSlotPerDay)
                {
                    return SnAllocResult.Fail("OFFLINE_CAP",
                        $"本机应急号今天已经用完（每个机位每天 {PerSlotPerDay} 个），请连接汇总机后再取号");
                }

                var seq = OfflineStartSeq + int.Parse(slotKey) * PerSlotPerDay + used;
                state.Counters[slotKey] = used + 1;
                Save(path, state);

                var sn = SnGenerator.Generate(date, seq);
                return new SnAllocResult(true, sn, seq, null,
                    $"汇总机不可达，已用本机应急号（{sn}，每机位每天最多 {PerSlotPerDay} 个）");
            }
            catch (Exception ex)
            {
                return SnAllocResult.Fail("OFFLINE_IO", $"本机应急取号失败：{ex.Message}");
            }
        }
    }

    /// <summary>常见 1~5 工位 × 1~8 机位映射到 0~39；其它编码用稳定哈希落到 0~39。</summary>
    private static int SlotOf(StationIdentity identity)
    {
        var machineNo = ParseMachineNo(identity.MachineId);
        if (identity.StationNo is >= 1 and <= 5 && machineNo is >= 1 and <= 8)
            return (identity.StationNo - 1) * 8 + (machineNo - 1);

        var hash = HashCode.Combine(identity.StationNo, identity.MachineId.ToUpperInvariant());
        return (hash & 0x7FFFFFFF) % SlotCount;
    }

    private static int ParseMachineNo(string machineId)
    {
        var digits = new string(machineId.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    private static State Load(string path)
    {
        if (!File.Exists(path))
            return new State();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<State>(json)
               ?? throw new InvalidDataException("sn_offline.json 内容为空");
    }

    private static void Save(string path, State state)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state));
        File.Move(tmp, path, overwrite: true);
    }

    private sealed class State
    {
        public string Date { get; set; } = "";
        public Dictionary<string, int> Counters { get; set; } = new();
    }
}
