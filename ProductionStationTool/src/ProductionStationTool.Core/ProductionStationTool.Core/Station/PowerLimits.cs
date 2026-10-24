namespace ProductionStationTool.Core.Station;

/// <summary>
/// 电流/电压上下限判定（工位 1 的 PowerZ 检测项）。
/// 阈值由上位机页面配置，超限即判设备异常。
/// </summary>
public sealed class PowerLimits
{
    /// <summary>电压下限（V）。</summary>
    public double VoltageMin { get; init; }

    /// <summary>电压上限（V）。</summary>
    public double VoltageMax { get; init; }

    /// <summary>电流下限（A）。</summary>
    public double CurrentMin { get; init; }

    /// <summary>电流上限（A）。</summary>
    public double CurrentMax { get; init; }

    /// <summary>判定单个采样：返回异常项列表（空 = 正常）。</summary>
    public List<string> Judge(double voltage, double current)
    {
        var fails = new List<string>();
        if (voltage < VoltageMin)
            fails.Add($"电压 {voltage:F3}V 低于下限 {VoltageMin:F3}V");
        else if (voltage > VoltageMax)
            fails.Add($"电压 {voltage:F3}V 超过上限 {VoltageMax:F3}V");

        if (current < CurrentMin)
            fails.Add($"电流 {current:F3}A 低于下限 {CurrentMin:F3}A");
        else if (current > CurrentMax)
            fails.Add($"电流 {current:F3}A 超过上限 {CurrentMax:F3}A");

        return fails;
    }
}

/// <summary>单次测试的电流电压检测结论。</summary>
public sealed record PowerJudgement(
    double Voltage,
    double Current,
    bool IsNormal,
    IReadOnlyList<string> AbnormalItems);
