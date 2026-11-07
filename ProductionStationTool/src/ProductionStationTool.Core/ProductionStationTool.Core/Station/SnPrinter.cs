namespace ProductionStationTool.Core.Station;

/// <summary>
/// 标签打印机接口（工位4：SN 输出打印标签纸）。
/// 打印机方案未定，先占位；后续实现类对接实际打印机协议。
/// </summary>
public interface ISnPrinter
{
    /// <summary>把 SN 输出到打印机打标签纸。返回（是否成功, 说明）。</summary>
    Task<(bool Ok, string Detail)> PrintSnAsync(string sn, CancellationToken ct = default);
}

/// <summary>占位实现：不真正打印，返回成功并注明「接口待接入」。</summary>
public sealed class NullSnPrinter : ISnPrinter
{
    public Task<(bool Ok, string Detail)> PrintSnAsync(string sn, CancellationToken ct = default)
        => Task.FromResult((true, $"打印机接口占位：SN {sn} 已输出（待接入实际打印机）"));
}
