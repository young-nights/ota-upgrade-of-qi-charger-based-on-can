using ProductionStationTool.PowerZ;

namespace ProductionStationTool.Core.Station;

/// <summary>写 SN 流程执行结果。</summary>
public sealed record Station1Result(
    string Sn,
    bool Passed,
    CoilJudgement? Coil,
    IReadOnlyList<string> AbnormalItems);

/// <summary>
/// 写 SN 流程编排（简化版：无数据库、SN 由外部提供——工位3 扫码枪扫描 + 结构判定）：
///   写入 SN → 回读验证 → 开充电 → PowerZ A/B 线圈检测 → 结束
///
/// 关键约定：
///  - SN 写入/回读失败 → 终止（不进入充电/线圈步骤）；
///  - 无有效采样 → 终止不判限；
///  - 任何异常项 → 页面显示异常原因；全正常才 PASS；
///  - 双线圈检测见 <see cref="CoilCheck"/>。
/// </summary>
public sealed class Station1Workflow
{
    /// <summary>执行一次测试。snWrite/snRead/chargeStart 由上位机 CAN 层提供。</summary>
    public async Task<Station1Result> RunAsync(
        string sn,                         // 外部提供（扫码枪扫描 + 结构判定通过）
        Func<string, Task<bool>> snWriteAsync,      // 写 SN，返回是否成功
        Func<Task<string?>> snReadAsync,            // 回读 SN
        Func<Task> chargeStartAsync,                // 开启无线充电
        IPowerZSource powerZ,
        CoilTestOptions coils,
        Action<string>? log = null,
        CancellationToken ct = default,
        Action<CoilProgress>? onProgress = null)
    {
        // 1. 写 SN + 回读验证；失败终止
        if (!await snWriteAsync(sn))
            return new Station1Result(sn, false, null,
                new[] { "SN 写入设备失败，请检查设备连接后重试" });

        var readBack = await snReadAsync();
        if (!string.Equals(readBack, sn, StringComparison.Ordinal))
            return new Station1Result(sn, false, null,
                new[] { $"写进去的 SN 和设备读回来的对不上（写入 {sn}，读回 {readBack ?? "(空)"}），请重试" });

        // 2. 开充电 + PowerZ 双线圈检测
        //    A 线圈窗口均值 ≥ 下限且 ≤ 上限 → 跌落过渡（正常不判失败）→ B 线圈恢复并持续；整体限时
        await chargeStartAsync();
        var coil = await CoilCheck.RunAsync(powerZ, coils, log, ct, onProgress);

        // 3. 无有效采样 = 流程错误：停止，不判限
        if (coil.NoValidSample)
            return new Station1Result(sn, false, coil,
                new[] { "没有读到 PowerZ 电流数据（无有效采样），测试未完成，请检查 PowerZ 连接后重试" });

        return new Station1Result(sn, coil.IsNormal, coil, coil.AbnormalItems);
    }
}
