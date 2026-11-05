namespace ProductionStationTool.Core.Station;

/// <summary>
/// 本机身份：工位号 + 机位号。
/// 每工位机位数随产量**动态增减**（1~N 台），身份由上位机侧 machine.json 配置，
/// 不写死在代码里——增减机位不改代码、服务器零改动。
/// </summary>
public sealed record StationIdentity(int StationNo, string MachineId)
{
    /// <summary>台账/服务器编码，如 ST-01-1、ST-01-3。</summary>
    public string Code => $"ST-{StationNo:00}-{MachineId}";

    /// <summary>界面显示，如 工位1 · 机位1。</summary>
    public string Label => $"工位{StationNo} · 机位{MachineId}";
}
