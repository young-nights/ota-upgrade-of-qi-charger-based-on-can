namespace ProductionStationTool.PowerZ;

/// <summary>PowerZ 数据源抽象：串口实现见 <see cref="SerialPowerZSource"/>，测试用假实现。</summary>
public interface IPowerZSource : IDisposable
{
    /// <summary>收到一条有效采样。</summary>
    event Action<PowerSample>? SampleReceived;

    /// <summary>打开数据源（串口实现里为打开 COM 口并开始收数）。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>关闭数据源。</summary>
    Task StopAsync(CancellationToken ct = default);
}
