using System.Diagnostics;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.Core.Station;

/// <summary>
/// 双线圈（A/B）电流检测参数。
/// 检测窗口从开充起算：电流达到各自下限后，到窗口结束的区间取均值；
/// 有效检测时间不足 <see cref="MinEffectiveMs"/> 判无效。整体限时覆盖
/// 「A 检测 + 换线圈 + B 检测」全过程，超时即步骤 5 判 FAIL。
/// </summary>
public sealed class CoilTestOptions
{
    /// <summary>A/B 检测窗口（ms）：从开充起算，电流达到下限后到窗口结束取均值。</summary>
    public int DetectionWindowMs { get; init; } = 10_000;

    /// <summary>A 线圈电流下限（A）：爬坡达到此值后开始采样；均值低于此值判低流异常。</summary>
    public double CoilALowerA { get; init; } = 1.0;

    /// <summary>A 线圈电流上限（A）：均值高于此值判过流异常。</summary>
    public double CoilAUpperA { get; init; } = 2.0;

    /// <summary>B 线圈电流下限（A）：达到此值后开始采样；均值低于此值判低流异常。</summary>
    public double CoilBLowerA { get; init; } = 1.0;

    /// <summary>B 线圈电流上限（A）：均值高于此值判过流异常。</summary>
    public double CoilBUpperA { get; init; } = 2.0;

    /// <summary>
    /// 采样间隔（ms）：PowerZ 启动帧重发节奏（设备本底速率固定，此为主机侧
    /// 采样频率杠杆，有效采样率 ≈ 1000/间隔 Hz）。设置页「采样间隔 (ms)」
    /// 可调，默认 200ms（≈5Hz）。
    /// </summary>
    public int SampleIntervalMs { get; init; } = 200;

    /// <summary>步骤 5 整体超时（ms）：A 检测 + 换线圈 + B 检测，超时判 FAIL。</summary>
    public int OverallTimeoutMs { get; init; } = 60_000;

    /// <summary>
    /// 有效检测时间下限（ms）：电流达到下限后到窗口结束不足此值，判无效检测。
    /// 默认 2000ms；设置页不暴露，固定规则。
    /// </summary>
    public int MinEffectiveMs { get; init; } = 2_000;
}

/// <summary>双线圈检测结论。</summary>
public sealed record CoilJudgement(
    double CoilAVoltage,           // A 窗口均值电压（记录/显示用）
    double CoilACurrent,           // A 窗口均值电流
    double CoilBCurrent,           // B 窗口均值电流（未测到为 0）
    bool CoilAPassed,
    bool CoilBPassed,
    bool NoValidSample,            // A 窗口无任何采样（PowerZ 流程错误，不入库）
    IReadOnlyList<string> AbnormalItems)
{
    public bool IsNormal => CoilAPassed && CoilBPassed && AbnormalItems.Count == 0;
}

/// <summary>
/// 检测过程实时进度（UI 反馈用）：阶段提示（含倒计时）+ A 判定一出即上报，
/// 测试员据此知道 A 已通过、可以换接 B 线圈。
/// </summary>
public sealed record CoilProgress(
    string Hint,          // 步骤提示（含倒计时），显示在流程区「进行中」
    bool CoilADecided,    // A 判定是否已出（false 时下面两项无效）
    bool CoilAPassed,     // A 判定结果
    double CoilACurrent); // A 均值电流

/// <summary>
/// 双线圈采样与判定流程（工位 1 步骤 5）：
///   ① A 线圈：从开充起算，等待电流达到 A 下限；达到后到检测窗口结束
///      取均值。有效检测时间（窗口结束 - 达到下限时刻）不足 MinEffectiveMs
///      判无效；均值落在 [A 下限, A 上限] 内即 A 正常。
///   ② A 结束后不再等待跌落/确认：整体倒计时继续，A 判定立即上报，
///      测试员换接 B 线圈。
///   ③ B 线圈：等待电流出现「低于 B 下限 → 达到 B 下限」的接入沿后，
///      按与 A 相同的窗口/均值/有效时间规则判定。接入沿只用于识别 B，
///      不做持续时间确认，也不因跌落判失败。
///   ④ 整体限时：从进入本流程起算；B 检测窗口结束时间取
///      min(B 达到下限 + DetectionWindowMs, OverallTimeoutMs)，超时判 FAIL。
/// </summary>
public static class CoilCheck
{
    public static async Task<CoilJudgement> RunAsync(
        IPowerZSource src,
        CoilTestOptions opt,
        Action<string>? log = null,
        CancellationToken ct = default,
        Action<CoilProgress>? onProgress = null)
    {
        var sw = Stopwatch.StartNew();
        var samples = new List<(double V, double I, long T)>();
        var gate = new object();
        void OnSample(PowerSample s)
        {
            lock (gate) samples.Add((s.Voltage, s.Current, sw.ElapsedMilliseconds));
        }
        // 统计起点 = PowerZ 检测流程启动：先丢弃数据源积压帧（流式首帧突发可能
        // 早于本流程开始），再订阅——启动之前的电流数据一律不算数
        src.DiscardBuffered();
        src.SampleReceived += OnSample;

        try
        {
            // ── 阶段 A：等待达到下限 → 到窗口结束取均值 ──
            long aWindowEnd = Math.Min((long)opt.DetectionWindowMs, (long)opt.OverallTimeoutMs);
            long aHitT = -1;
            var cursor = 0;
            long lastReport = -1000;
            while (sw.ElapsedMilliseconds < aWindowEnd)
            {
                ct.ThrowIfCancellationRequested();
                long elapsed = sw.ElapsedMilliseconds;
                double lastI;
                lock (gate)
                {
                    while (cursor < samples.Count)
                    {
                        var s = samples[cursor++];
                        if (aHitT < 0 && s.I >= opt.CoilALowerA)
                        {
                            aHitT = s.T;
                            log?.Invoke($"A 线圈：T={s.T / 1000.0:F2}s 达到下限 {opt.CoilALowerA:F2}A，开始采样");
                        }
                    }
                    lastI = samples.Count > 0 ? samples[^1].I : 0;
                }
                if (onProgress is not null && elapsed - lastReport >= 500)
                {
                    lastReport = elapsed;
                    string hint = aHitT < 0
                        ? $"A 等待达到下限 {Math.Max(0, aWindowEnd - elapsed) / 1000.0:F0}s · {lastI:F2}A"
                        : $"A 采集中 {Math.Max(0, aWindowEnd - elapsed) / 1000.0:F0}s · {lastI:F2}A";
                    onProgress(new CoilProgress(hint, false, false, 0));
                }
                await Task.Delay(50, ct);
            }

            List<(double V, double I, long T)> winA;
            lock (gate)
            {
                // 窗口结束边界上到达的采样也纳入
                while (cursor < samples.Count)
                {
                    var s = samples[cursor++];
                    if (aHitT < 0 && s.T <= aWindowEnd && s.I >= opt.CoilALowerA)
                        aHitT = s.T;
                }
                winA = samples.Where(s => s.T <= aWindowEnd).ToList();
            }

            if (winA.Count == 0)
            {
                log?.Invoke("A 线圈：窗口内无采样");
                onProgress?.Invoke(new CoilProgress("A 窗口无有效采样", false, false, 0));
                return new CoilJudgement(0, 0, 0, false, false, true, new[] { "PowerZ 无有效采样" });
            }

            if (aHitT < 0)
            {
                string why = $"A 线圈在 {opt.DetectionWindowMs / 1000.0:F1}s 检测窗口内未达到下限 {opt.CoilALowerA:F2}A";
                log?.Invoke(why);
                onProgress?.Invoke(new CoilProgress("A 未达到下限", true, false, 0));
                return new CoilJudgement(0, 0, 0, false, false, false, new[] { why });
            }

            var winAEffective = winA.Where(s => s.T >= aHitT).ToList();
            if (winAEffective.Count == 0)
            {
                string why = "A 线圈达到下限后窗口内无有效采样";
                log?.Invoke(why);
                onProgress?.Invoke(new CoilProgress("A 有效采样为空", true, false, 0));
                return new CoilJudgement(0, 0, 0, false, false, false, new[] { why });
            }

            var avgI = winAEffective.Average(s => s.I);
            var avgV = winAEffective.Average(s => s.V);
            long aEffectiveMs = aWindowEnd - aHitT;
            log?.Invoke($"A 线圈：T={aHitT / 1000.0:F2}s 达到下限，有效 {aEffectiveMs}ms，{winAEffective.Count} 点，" +
                        $"均值 {avgI:F3}A / {avgV:F3}V（区间 {opt.CoilALowerA:F2}~{opt.CoilAUpperA:F2}A）");

            if (aEffectiveMs < opt.MinEffectiveMs)
            {
                string why = $"A 线圈有效检测时间 {aEffectiveMs}ms < {opt.MinEffectiveMs}ms（达到下限过晚），判定无效";
                log?.Invoke(why);
                onProgress?.Invoke(new CoilProgress(why, true, false, avgI));
                return new CoilJudgement(avgV, avgI, 0, false, false, false, new[] { why });
            }

            bool aPass = avgI >= opt.CoilALowerA && avgI <= opt.CoilAUpperA;
            onProgress?.Invoke(new CoilProgress(
                aPass ? "A 通过，请换接 B 线圈" : "A 未通过",
                true, aPass, avgI));

            if (!aPass)
            {
                string aWhy = avgI < opt.CoilALowerA
                    ? $"A 线圈均值电流 {avgI:F3}A 低于下限 {opt.CoilALowerA:F2}A"
                    : $"A 线圈均值电流 {avgI:F3}A 高于上限 {opt.CoilAUpperA:F2}A";
                return new CoilJudgement(avgV, avgI, 0, false, false, false, new[] { aWhy });
            }

            // ── 阶段 B：等待接入沿（低于 B 下限 → 达到 B 下限）→ 同窗口/均值逻辑 ──
            // 接入沿只用于识别 B 线圈已换好，不做跌落持续时间确认，也不判失败。
            bool sawBelowB = false;
            long bHitT = -1;
            var bCursor = 0;
            long lastBReport = -1000;
            while (sw.ElapsedMilliseconds <= opt.OverallTimeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                long now = sw.ElapsedMilliseconds;
                double lastI;
                lock (gate)
                {
                    while (bCursor < samples.Count)
                    {
                        var s = samples[bCursor++];
                        if (s.T <= aWindowEnd)
                            continue;
                        if (!sawBelowB)
                        {
                            if (s.I < opt.CoilBLowerA)
                                sawBelowB = true;
                        }
                        else if (s.I >= opt.CoilBLowerA)
                        {
                            bHitT = s.T;
                            log?.Invoke($"B 线圈：T={s.T / 1000.0:F2}s 接入并达到下限 {opt.CoilBLowerA:F2}A，开始采样");
                            break;
                        }
                    }
                    lastI = samples.Count > 0 ? samples[^1].I : 0;
                }
                if (bHitT >= 0)
                    break;

                if (onProgress is not null && now - lastBReport >= 500)
                {
                    lastBReport = now;
                    double overallRemain = Math.Max(0, opt.OverallTimeoutMs - now) / 1000.0;
                    string hint = sawBelowB
                        ? $"等待 B 达到下限（整体剩余 {overallRemain:F0}s）· {lastI:F2}A"
                        : $"请换接 B 线圈（整体剩余 {overallRemain:F0}s）· {lastI:F2}A";
                    onProgress(new CoilProgress(hint, true, true, avgI));
                }
                await Task.Delay(50, ct);
            }

            if (bHitT < 0)
            {
                double tailAvg;
                lock (gate)
                    tailAvg = samples.Where(s => s.T > aWindowEnd)
                                     .Select(s => s.I)
                                     .DefaultIfEmpty(0)
                                     .Average();
                string why = $"B 线圈未接入/未达到下限 {opt.CoilBLowerA:F2}A（尾段均值 {tailAvg:F3}A），" +
                             $"整体超时 {sw.ElapsedMilliseconds / 1000.0:F1}s";
                log?.Invoke(why);
                onProgress?.Invoke(new CoilProgress("B 未接入，整体超时", true, true, avgI));
                return new CoilJudgement(avgV, avgI, tailAvg, true, false, false, new[] { why });
            }

            long bWindowEnd = Math.Min(bHitT + (long)opt.DetectionWindowMs, (long)opt.OverallTimeoutMs);
            long bEffectiveMs = bWindowEnd - bHitT;
            if (bEffectiveMs < opt.MinEffectiveMs)
            {
                double partialAvg;
                lock (gate)
                    partialAvg = samples.Where(s => s.T >= bHitT)
                                        .Select(s => s.I)
                                        .DefaultIfEmpty(0)
                                        .Average();
                string why = $"B 线圈有效检测时间 {bEffectiveMs}ms < {opt.MinEffectiveMs}ms（接入过晚），" +
                             $"判定无效（已采均值 {partialAvg:F3}A）";
                log?.Invoke(why);
                onProgress?.Invoke(new CoilProgress(why, true, true, avgI));
                return new CoilJudgement(avgV, avgI, partialAvg, true, false, false, new[] { why });
            }

            while (sw.ElapsedMilliseconds < bWindowEnd)
            {
                ct.ThrowIfCancellationRequested();
                long now = sw.ElapsedMilliseconds;
                if (onProgress is not null && now - lastBReport >= 500)
                {
                    lastBReport = now;
                    double lastI;
                    lock (gate) lastI = samples.Count > 0 ? samples[^1].I : 0;
                    double overallRemain = Math.Max(0, opt.OverallTimeoutMs - now) / 1000.0;
                    onProgress(new CoilProgress(
                        $"B 采集中 {Math.Max(0, bWindowEnd - now) / 1000.0:F0}s（整体剩余 {overallRemain:F0}s）· {lastI:F2}A",
                        true, true, avgI));
                }
                await Task.Delay(50, ct);
            }

            List<(double V, double I, long T)> winB;
            lock (gate)
                winB = samples.Where(s => s.T >= bHitT && s.T <= bWindowEnd).ToList();

            if (winB.Count == 0)
            {
                string why = "B 线圈窗口内无有效采样";
                log?.Invoke(why);
                return new CoilJudgement(avgV, avgI, 0, true, false, false, new[] { why });
            }

            var bAvg = winB.Average(s => s.I);
            bool bPass = bAvg >= opt.CoilBLowerA && bAvg <= opt.CoilBUpperA;
            log?.Invoke($"B 线圈：有效 {bEffectiveMs}ms，{winB.Count} 点，均值 {bAvg:F3}A" +
                        $"（区间 {opt.CoilBLowerA:F2}~{opt.CoilBUpperA:F2}A）");
            if (!bPass)
            {
                string bWhy = bAvg < opt.CoilBLowerA
                    ? $"B 线圈均值电流 {bAvg:F3}A 低于下限 {opt.CoilBLowerA:F2}A"
                    : $"B 线圈均值电流 {bAvg:F3}A 高于上限 {opt.CoilBUpperA:F2}A";
                return new CoilJudgement(avgV, avgI, bAvg, true, false, false, new[] { bWhy });
            }

            return new CoilJudgement(avgV, avgI, bAvg, true, true, false, Array.Empty<string>());
        }
        finally
        {
            src.SampleReceived -= OnSample;
        }
    }
}
