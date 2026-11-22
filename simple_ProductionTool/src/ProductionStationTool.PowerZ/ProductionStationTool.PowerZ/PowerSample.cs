namespace ProductionStationTool.PowerZ;

/// <summary>PowerZ 仪表一次采样（电压 V / 电流 A）。</summary>
public readonly record struct PowerSample(double Voltage, double Current, DateTime Timestamp)
{
    public override string ToString() => $"{Voltage:F3}V {Current:F3}A @{Timestamp:HH:mm:ss.fff}";
}
