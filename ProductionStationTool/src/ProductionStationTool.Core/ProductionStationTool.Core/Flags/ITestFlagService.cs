namespace ProductionStationTool.Core.Flags;

/// <summary>工位编号（对应 Flash 中 4 个测试标志位）。</summary>
public enum StationId
{
    Station1 = 0,
    Station2 = 1,
    Station3 = 2,
    Station4 = 3,
}

/// <summary>单个工位的测试标志。</summary>
public enum TestFlag
{
    /// <summary>0 = 没测过（或测过但失败，Flash 标志未更新）。</summary>
    NotTested = 0,

    /// <summary>1 = 测试成功过。</summary>
    Passed = 1,
}

/// <summary>
/// 4 工位测试标志位服务（Flash 标记）。
///
/// 语义（按 18. 产线上位机新构想）：
///  - 设备上电主动上报 / 点「开始」时主动询问对应标志；
///  - 标志为 Passed → 跳过流程直接显示 PASS；
///  - 老设备复测必须先「恢复出厂设置」清标志。
///
/// DID 号待固件侧定义后在实现类中填充；此处先定接口语义。
/// </summary>
public interface ITestFlagService
{
    /// <summary>读某工位标志（上电上报值缓存或主动查询）。</summary>
    Task<TestFlag> GetFlagAsync(StationId station, CancellationToken ct = default);

    /// <summary>读全部 4 个标志（bit0~bit3 对应工位 1~4）。</summary>
    Task<IReadOnlyDictionary<StationId, TestFlag>> GetAllFlagsAsync(CancellationToken ct = default);

    /// <summary>测试成功后把标志写 1。</summary>
    Task SetFlagAsync(StationId station, TestFlag flag, CancellationToken ct = default);

    /// <summary>恢复出厂设置：清全部标志位（老设备复测前必须调用）。</summary>
    Task ClearAllFlagsAsync(CancellationToken ct = default);
}
