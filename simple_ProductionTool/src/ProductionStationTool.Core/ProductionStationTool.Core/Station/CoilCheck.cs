using System.Diagnostics;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.Core.Station;

/// <summary>
/// 双线圈（A/B）电流检测参数。
/// A/B 各自：窗口内电流达到下限后开始采样，到窗口结束取均值；
/// 有效采样时间（窗口结束 − 达到下限时刻）不足 <see cref="MinEffectiveMs"/> 判无效。
/// </summary>
public sealed class CoilTestOptions
{
    /// <summary>A/B 检测窗口（ms）：从各自阶段起算（A 从开测、B 从换接等待结束）。</summary>
    public int DetectionWindowMs { get; init; } = 6_000;

    /// <summary>A 线圈电流下限（A）：达到此值后开始采样。</summary>
    public double CoilALowerA { get; init; } = 0.3;

    /// <summary>A 线圈电流上限（A）：均值高于此值判过流异常。</summary>
    public double CoilAUpperA { get; init; } = 2.0;

    /// <summary>B 线圈电流下限（A）：达到此值后开始采样。</summary>
    public double CoilBLowerA { get; init; } = 0.3;

    /// <summary>B 线圈电流上限（A）：均值高于此值判过流异常。</summary>
    public double CoilBUpperA { get; init; } = 2.0;

    /// <summary>采样间隔（ms）：PowerZ 启动帧重发节奏，默认 200ms（≈5Hz）。</summary>
    public int SampleIntervalMs { get; init; } = 200;

    /// <summary>整体兜底超时（ms）：A 窗 + 换接 B + B 窗。</summary>
    public int OverallTimeoutMs { get; init; } = 60_000;

    /// <summary>有效检测时间下限（ms）：达到下限后到窗口结束不足此值判无效。</summary>
    public int MinEffectiveMs { get; init; } = 2_000;

    /// <summary>A 通过后换接 B 的等待（ms）：作业员须在此时间内放入 B。</summary>
    public int BPlaceWaitMs { get; init; } = 2_000;
}

/// <summary>双线圈检测结论。</summary>
public sealed record CoilJudgement(
    double CoilAVoltage,
    double CoilACurrent,
    double CoilBCurrent,
    bool CoilAPassed,
    bool CoilBPassed,
    bool NoValidSample,
    IReadOnlyList<string> AbnormalItems)
{
    public bool IsNormal => CoilAPassed && CoilBPassed && AbnormalItems.Count == 0;
}

/// <summary>
/// 检测过程实时进度：阶段提示（含倒计时）+ A 判定一出即上报。
/// </summary>
public sealed record CoilProgress(
    string Hint,
    bool CoilADecided,
    bool CoilAPassed,
    double CoilACurrent);

/// <summary>
/// 双线圈采样与判定：
///   ① A 窗（DetectionWindowMs）：等待 I ≥ A 下限，达到后到窗口结束取均值；
///      有效时间 = 窗口结束 − 达到下限时刻，不足 MinEffectiveMs 判失败
///      （例：窗 6s，3~6s 或 4~6s 记录；4s 仍未达下限则测不过）。
///   ② A 通过后显示「放入 B 线圈」，等待 BPlaceWaitMs 换接。
///   ③ B 窗（DetectionWindowMs）：同 A 规则。
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
        src.DiscardBuffered();
        src.SampleReceived += OnSample;

        try
        {
            // ── A 窗：从 0 起算 DetectionWindowMs ──
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
                if (onProgress is not null && elapsed - lastReport >= 200)
                {
                    lastReport = elapsed;
                    string hint = aHitT < 0
                        ? $"A 等待达到下限 {Math.Max(0, aWindowEnd - elapsed) / 1000.0:F1}s · {lastI:F2}A"
                        : $"A 采集中 {Math.Max(0, aWindowEnd - elapsed) / 1000.0:F1}s · {lastI:F2}A";
                    onProgress(new CoilProgress(hint, false, false, 0));
                }
                await Task.Delay(50, ct);
            }

            List<(double V, double I, long T)> winA;
            lock (gate)
            {
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
                onProgress?.Invoke(new CoilProgress("A 无有效采样", false, false, 0));
                return new CoilJudgement(0, 0, 0, false, false, true,
                    new[] { "没有读到 PowerZ 电流数据（无有效采样），请检查 PowerZ 连接后重测" });
            }

            if (aHitT < 0)
            {
                string why = $"A 线圈未在窗口 {opt.DetectionWindowMs / 1000.0:F1}s 内达到下限 {opt.CoilALowerA:F2}A" +
                             $"（需在 {(aWindowEnd - opt.MinEffectiveMs) / 1000.0:F1}s 前达到，才能保证 ≥{opt.MinEffectiveMs / 1000.0:F0}s 有效数据），请检查 A 线圈";
                log?.Invoke(why);
                onProgress?.Invoke(new CoilProgress("A 未达到下限", true, false, 0));
                return new CoilJudgement(0, 0, 0, false, false, false, new[] { why });
            }

            var winAEffective = winA.Where(s => s.T >= aHitT).ToList();
            if (winAEffective.Count == 0)
            {
                string why = "A 线圈达到下限后没有采到电流数据，请检查 PowerZ 连接后重测";
                log?.Invoke(why);
                onProgress?.Invoke(new CoilProgress("A 有效采样为空", true, false, 0));
                return new CoilJudgement(0, 0, 0, false, false, false, new[] { why });
            }

            var avgI = winAEffective.Average(s => s.I);
            var avgV = winAEffective.Average(s => s.V);
            long aEffectiveMs = aWindowEnd - aHitT;
            log?.Invoke($"A 线圈：T={aHitT / 1000.0:F2}s 达下限，记录 {aEffectiveMs}ms，{winAEffective.Count} 点，均值 {avgI:F3}A / {avgV:F3}V");

            if (aEffectiveMs < opt.MinEffectiveMs)
            {
                string why = $"A 线圈有效数据只有 {aEffectiveMs}ms（至少 {opt.MinEffectiveMs}ms），请把 A 线圈放正后重测";
                log?.Invoke(why);
                onProgress?.Invoke(new CoilProgress(why, true, false, avgI));
                return new CoilJudgement(avgV, avgI, 0, false, false, false, new[] { why });
            }

            bool aPass = avgI >= opt.CoilALowerA && avgI <= opt.CoilAUpperA;
            if (!aPass)
            {
                string aWhy = avgI < opt.CoilALowerA
                    ? $"A 线圈均值低于下限（{avgI:F2}A < {opt.CoilALowerA:F2}A）"
                    : $"A 线圈均值高于上限（{avgI:F2}A > {opt.CoilAUpperA:F2}A）";
                log?.Invoke(aWhy);
                onProgress?.Invoke(new CoilProgress("A 未通过", true, false, avgI));
                return new CoilJudgement(avgV, avgI, 0, false, false, false, new[] { aWhy });
            }

            // ── 换接 B 等待（数据栏显示「放入 B 线圈」）──
            long bPlaceEnd = sw.ElapsedMilliseconds + opt.BPlaceWaitMs;
            long lastPlace = -1000;
            log?.Invoke($"A 通过，请放入 B 线圈（{opt.BPlaceWaitMs}ms）");
            while (sw.ElapsedMilliseconds < bPlaceEnd)
            {
                ct.ThrowIfCancellationRequested();
                long now = sw.ElapsedMilliseconds;
                if (onProgress is not null && now - lastPlace >= 200)
                {
                    lastPlace = now;
                    double remain = Math.Max(0, bPlaceEnd - now) / 1000.0;
                    double lastI;
                    lock (gate) lastI = samples.Count > 0 ? samples[^1].I : 0;
                    onProgress(new CoilProgress($"请放入 B 线圈 {remain:F1}s · {lastI:F2}A", true, true, avgI));
                }
                await Task.Delay(50, ct);
            }

            // ── B 窗：从 bPlaceEnd 起算 DetectionWindowMs ──
            long bWindowStart = bPlaceEnd;
            long bWindowEnd = bPlaceEnd + Math.Min((long)opt.DetectionWindowMs,
                (long)opt.OverallTimeoutMs - (bPlaceEnd - 0));
            if (bWindowEnd <= bWindowStart)
                bWindowEnd = bWindowStart + opt.DetectionWindowMs;
            long bHitT = -1;
            var bCursor = 0;
            long lastBReport = -1000;
            while (sw.ElapsedMilliseconds < bWindowEnd)
            {
                ct.ThrowIfCancellationRequested();
                long now = sw.ElapsedMilliseconds;
                double lastI;
                lock (gate)
                {
                    while (bCursor < samples.Count)
                    {
                        var s = samples[bCursor++];
                        if (s.T < bWindowStart)
                            continue;
                        if (bHitT < 0 && s.I >= opt.CoilBLowerA)
                        {
                            bHitT = s.T;
                            log?.Invoke($"B 线圈：T={s.T / 1000.0:F2}s 达到下限 {opt.CoilBLowerA:F2}A，开始采样");
                        }
                    }
                    lastI = samples.Count > 0 ? samples[^1].I : 0;
                }
                if (onProgress is not null && now - lastBReport >= 200)
                {
                    lastBReport = now;
                    string hint = bHitT < 0
                        ? $"B 等待达到下限 {Math.Max(0, bWindowEnd - now) / 1000.0:F1}s · {lastI:F2}A"
                        : $"B 采集中 {Math.Max(0, bWindowEnd - now) / 1000.0:F1}s · {lastI:F2}A";
                    onProgress(new CoilProgress(hint, true, true, avgI));
                }
                await Task.Delay(50, ct);
            }

            List<(double V, double I, long T)> winB;
            lock (gate)
            {
                while (bCursor < samples.Count)
                {
                    var s = samples[bCursor++];
                    if (bHitT < 0 && s.T >= bWindowStart && s.T <= bWindowEnd && s.I >= opt.CoilBLowerA)
                        bHitT = s.T;
                }
                winB = samples.Where(s => s.T >= bWindowStart && s.T <= bWindowEnd).ToList();
            }

            if (winB.Count == 0)
            {
                string why = "B 线圈：窗口内无采样，请检查 PowerZ 连接后重测";
                log?.Invoke(why);
                return new CoilJudgement(avgV, avgI, 0, true, false, true, new[] { why });
            }

            if (bHitT < 0)
            {
                string why = $"B 线圈未在窗口 {opt.DetectionWindowMs / 1000.0:F1}s 内达到下限 {opt.CoilBLowerA:F2}A" +
                             $"（需在 {(bWindowEnd - bWindowStart - opt.MinEffectiveMs) / 1000.0:F1}s 内达到），请放入 B 线圈";
                log?.Invoke(why);
                onProgress?.Invoke(new CoilProgress("B 未达到下限", true, true, avgI));
                return new CoilJudgement(avgV, avgI, 0, true, false, false, new[] { why });
            }

            var winBEffective = winB.Where(s => s.T >= bHitT).ToList();
            if (winBEffective.Count == 0)
            {
                string why = "B 线圈达到下限后没有采到电流数据，请检查 PowerZ 连接后重测";
                log?.Invoke(why);
                return new CoilJudgement(avgV, avgI, 0, true, false, false, new[] { why });
            }

            var bAvg = winBEffective.Average(s => s.I);
            long bEffectiveMs = bWindowEnd - bHitT;
            log?.Invoke($"B 线圈：T={bHitT / 1000.0:F2}s 达下限，记录 {bEffectiveMs}ms，{winBEffective.Count} 点，均值 {bAvg:F3}A");

            if (bEffectiveMs < opt.MinEffectiveMs)
            {
                string why = $"B 线圈有效数据只有 {bEffectiveMs}ms（至少 {opt.MinEffectiveMs}ms），请换好 B 线圈后重测";
                log?.Invoke(why);
                onProgress?.Invoke(new CoilProgress(why, true, true, avgI));
                return new CoilJudgement(avgV, avgI, bAvg, true, false, false, new[] { why });
            }

            bool bPass = bAvg >= opt.CoilBLowerA && bAvg <= opt.CoilBUpperA;
            if (!bPass)
            {
                string bWhy = bAvg < opt.CoilBLowerA
                    ? $"B 线圈均值低于下限（{bAvg:F2}A < {opt.CoilBLowerA:F2}A）"
                    : $"B 线圈均值高于上限（{bAvg:F2}A > {opt.CoilBUpperA:F2}A）";
                log?.Invoke(bWhy);
                onProgress?.Invoke(new CoilProgress("B 未通过", true, true, avgI));
                return new CoilJudgement(avgV, avgI, bAvg, true, false, false, new[] { bWhy });
            }

            onProgress?.Invoke(new CoilProgress("A/B 全部通过", true, true, avgI));
            return new CoilJudgement(avgV, avgI, bAvg, true, true, false, Array.Empty<string>());
        }
        finally
        {
            src.SampleReceived -= OnSample;
        }
    }
}
