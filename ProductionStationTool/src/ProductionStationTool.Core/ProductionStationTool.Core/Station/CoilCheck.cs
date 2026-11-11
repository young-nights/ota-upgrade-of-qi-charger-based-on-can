using System.Diagnostics;
using ProductionStationTool.PowerZ;

namespace ProductionStationTool.Core.Station;

/// <summary>
/// 双线圈（A/B）电流检测参数。整体限时覆盖「A 采集 + 换线圈 + B 判定」全过程，
/// 超时即步骤 5 判 FAIL。
/// </summary>
public sealed class CoilTestOptions
{
    /// <summary>A 线圈采集窗口（ms），均值滤波。</summary>
    public int CoilACollectMs { get; init; } = 10_000;

    /// <summary>A 线圈均值电流阈值（A）。</summary>
    public double CoilAThresholdA { get; init; } = 1.0;

    /// <summary>
    /// 跌落确认时长（ms）：A 通过后实时电流处于跌落状态持续该时长才算有效跌落
    /// （换线圈完成）。设置页「跌落确认 (s)」可调，默认 2000ms。
    /// </summary>
    public int TransitionMs { get; init; } = 2_000;

    /// <summary>
    /// 采样间隔（ms）：PowerZ 启动帧重发节奏（设备本底速率固定，此为主机侧
    /// 采样频率杠杆，有效采样率 ≈ 1000/间隔 Hz）。设置页「采样间隔 (ms)」
    /// 可调，默认 200ms（≈5Hz）。
    /// </summary>
    public int SampleIntervalMs { get; init; } = 200;

    /// <summary>B 线圈恢复后持续达标时长（ms）。</summary>
    public int CoilBSustainMs { get; init; } = 3_000;

    /// <summary>B 线圈均值电流阈值（A）。</summary>
    public double CoilBThresholdA { get; init; } = 1.0;

    /// <summary>步骤 5 整体超时（ms）：超时默认 FAIL。</summary>
    public int OverallTimeoutMs { get; init; } = 60_000;
}

/// <summary>双线圈检测结论。</summary>
public sealed record CoilJudgement(
    double CoilAVoltage,           // A 窗口均值电压（记录/显示用）
    double CoilACurrent,           // A 窗口均值电流
    double CoilBCurrent,           // B 恢复期均值电流（未测到为 0）
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
///   ① A 线圈：开充前已接入，固定窗口采集；判定用「稳定段均值」——剔除开充
///      爬坡（自首个达窗口最大电流 90% 的采样起统计），均值 ≥ 阈值即 A 正常；
///      统计起点 = 本流程启动（启动前积压数据丢弃不算）；
///   ② 过渡：A 通过后实时监测跌落（低于 B 阈值或 A 均值一半），跌落状态
///      持续「跌落确认」时长（默认 2s）且窗口均值仍在跌落线以下 = 有效跌落，
///      单点毛刺不打断；只识别切换、不判失败；
///   ③ B 线圈：电流恢复到 B 阈值以上并持续 CoilBSustainMs（均值）即 B 正常；
///   ④ 整体限时：从进入本流程起算，超时（如 B 一直低电流）判 FAIL。
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
            // ── 阶段 A：固定窗口采集 + 均值滤波（周期上报倒计时）──
            long lastReport = -1000;
            while (sw.ElapsedMilliseconds < opt.CoilACollectMs &&
                   sw.ElapsedMilliseconds <= opt.OverallTimeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                long elapsed = sw.ElapsedMilliseconds;
                if (onProgress is not null && elapsed - lastReport >= 500)
                {
                    lastReport = elapsed;
                    double lastI;
                    lock (gate) lastI = samples.Count > 0 ? samples[^1].I : 0;
                    onProgress(new CoilProgress(
                        $"A 采集中 {Math.Max(0, opt.CoilACollectMs - elapsed) / 1000.0:F0}s · {lastI:F2}A",
                        false, false, 0));
                }
                await Task.Delay(50, ct);
            }

            List<(double V, double I, long T)> winA;
            lock (gate)
                // T = 流程启动后的相对毫秒：A 均值只统计 [0, 窗口] 内采样
                winA = samples.Where(s => s.T <= opt.CoilACollectMs).ToList();

            if (winA.Count == 0)
            {
                log?.Invoke("A 线圈：窗口内无采样");
                onProgress?.Invoke(new CoilProgress("A 窗口无有效采样", false, false, 0));
                return new CoilJudgement(0, 0, 0, false, false, true, new[] { "PowerZ 无有效采样" });
            }

            // A 统计口径：窗口起点 = PowerZ 流程启动（启动前数据已 DiscardBuffered
            // 不算数）；但判定值剔除开充爬坡段——充电开启后电流从 0 升到稳态
            // （约 3s），全程均值会被爬坡拉低（实测 0.45 vs 稳态 0.57）。
            // 稳定段 = 自首个达到「稳态线」（窗口最大电流 90%）的采样起，
            // 反映工作电流；全程本就稳态则不剔除任何点。
            double iMax = winA.Max(s => s.I);
            var settled = winA.SkipWhile(s => s.I < iMax * 0.9).ToList();
            if (settled.Count == 0)
                settled = winA;
            var avgI = settled.Average(s => s.I);
            var avgV = settled.Average(s => s.V);
            log?.Invoke($"A 线圈：{winA.Count} 点（流程启动后 T={winA.Min(s => s.T) / 1000.0:F1}~{winA.Max(s => s.T) / 1000.0:F1}s，" +
                        $"剔除爬坡 {winA.Count - settled.Count} 点），稳定段均值 {avgI:F3}A / {avgV:F3}V" +
                        $"（阈值 {opt.CoilAThresholdA:F2}A，窗口 {opt.CoilACollectMs} ms）");

            // A 判定一出即上报（UI 实时点亮 A 卡片），失败原因进异常项、不进流程区
            bool aPass = avgI >= opt.CoilAThresholdA;
            onProgress?.Invoke(new CoilProgress(
                aPass ? "A 通过，请取下 A 夹子换接 B 线圈" : "A 未通过",
                true, aPass, avgI));

            if (avgI < opt.CoilAThresholdA)
            {
                return new CoilJudgement(avgV, avgI, 0, false, false, false,
                    new[] { $"A 线圈均值电流 {avgI:F3}A 低于阈值 {opt.CoilAThresholdA:F2}A" });
            }

            // ── 阶段 B：识别换线圈跌落（不判失败）→ 恢复到阈值并持续 ──
            // 跌落线 = max(B 阈值, A 均值一半)：低于任一条件即视为「电流跌落」
            var dipLine = Math.Max(opt.CoilBThresholdA, avgI * 0.5);
            var cursor = 0;
            var transition = false;
            var lowStart = -1L;
            double lowSum = 0;
            long lowCnt = 0;
            var recStart = -1L;      // B 恢复到阈值以上的起点
            var dipMin = double.MaxValue;
            long lastBReport = -1000;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var now = sw.ElapsedMilliseconds;
                if (onProgress is not null && now - lastBReport >= 500)
                {
                    lastBReport = now;
                    double lastI;
                    lock (gate) lastI = samples.Count > 0 ? samples[^1].I : 0;
                    var overallRemain = Math.Max(0, opt.OverallTimeoutMs - now) / 1000.0;
                    string hint = !transition
                        ? $"待跌落 {overallRemain:F0}s · {lastI:F2}A"
                        : recStart < 0
                            ? $"待接 B {overallRemain:F0}s · {lastI:F2}A"
                            : $"B 检测 {Math.Max(0, opt.CoilBSustainMs - (now - recStart)) / 1000.0:F0}s · {lastI:F2}A";
                    onProgress(new CoilProgress(hint, true, true, avgI));
                }
                if (now > opt.OverallTimeoutMs)
                {
                    List<(double V, double I, long T)> tail;
                    lock (gate)
                        tail = samples.Where(s => s.T > opt.CoilACollectMs).ToList();
                    var tailAvg = tail.Count > 0 ? tail.Average(s => s.I) : 0;
                    var reason = transition
                        ? $"B 线圈电流未恢复到 {opt.CoilBThresholdA:F2}A（尾段均值 {tailAvg:F3}A），整体超时 {now / 1000.0:F1}s"
                        : $"未检测到换线圈跌落（B 线圈未接入？尾段均值 {tailAvg:F3}A），整体超时 {now / 1000.0:F1}s";
                    return new CoilJudgement(avgV, avgI, tailAvg, true, false, false, new[] { reason });
                }

                lock (gate)
                {
                    while (cursor < samples.Count)
                    {
                        var s = samples[cursor++];
                        if (s.T <= opt.CoilACollectMs)
                            continue;

                        if (!transition)
                        {
                            // 跌落检测（A 通过后实时）：低于跌落线进入候选；
                            // 持续 TransitionMs 且窗口均值仍在跌落线以下 = 有效跌落。
                            // 抗毛刺：单点回升不打断，窗口均值回到跌落线以上才重计。
                            if (s.I < dipLine)
                            {
                                if (lowStart < 0) { lowStart = s.T; lowSum = 0; lowCnt = 0; }
                                lowSum += s.I;
                                lowCnt++;
                                if (s.I < dipMin) dipMin = s.I;
                                if (s.T - lowStart >= opt.TransitionMs && lowSum / lowCnt < dipLine)
                                {
                                    transition = true;
                                    log?.Invoke($"检测到换线圈跌落（最低 {dipMin:F3}A，确认 {opt.TransitionMs}ms），等待 B 线圈接入…");
                                }
                            }
                            else if (lowStart >= 0)
                            {
                                lowSum += s.I;
                                lowCnt++;
                                if (lowSum / lowCnt >= dipLine)
                                {
                                    lowStart = -1;
                                    lowSum = 0;
                                    lowCnt = 0;
                                }
                            }
                        }
                        else
                        {
                            // 恢复跟踪：≥ 阈值起算，中途再跌落则重新计
                            if (s.I >= opt.CoilBThresholdA)
                            {
                                if (recStart < 0) recStart = s.T;
                            }
                            else
                            {
                                recStart = -1;
                            }
                        }
                    }

                    if (transition && recStart >= 0 && now - recStart >= opt.CoilBSustainMs)
                    {
                        var winB = samples.Where(s => s.T >= recStart).ToList();
                        var bAvg = winB.Average(s => s.I);
                        if (bAvg >= opt.CoilBThresholdA)
                        {
                            log?.Invoke($"B 线圈：{winB.Count} 点，均值 {bAvg:F3}A ≥ {opt.CoilBThresholdA:F2}A" +
                                        $"，持续 {now - recStart} ms → 正常");
                            return new CoilJudgement(avgV, avgI, bAvg, true, true, false, Array.Empty<string>());
                        }
                        recStart = -1;   // 均值不足（噪声），重新累积
                    }
                }

                await Task.Delay(50, ct);
            }
        }
        finally
        {
            src.SampleReceived -= OnSample;
        }
    }
}
