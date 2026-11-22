namespace ProductionStationTool.Core.Sn;

/// <summary>
/// 产线 SN 码生成器。
///
/// 格式：LSCH42JY012606020001（20 位）
///   LS      lime 滑板车（产品线）
///   CH      Qi Charger（品类）
///   42      车型版本号
///   JY      金域丰（产地/工厂）
///   01      硬件版本号
///   260602  年月日（YYMMDD）
///   0001    当日流水号（0001 起）
/// </summary>
public static class SnGenerator
{
    public const string ProductPrefix = "LS";     // lime 滑板车
    public const string CategoryPrefix = "CH";    // Qi Charger
    public const string ModelCode = "42";         // 车型版本号
    public const string FactoryCode = "JY";       // 金域丰
    public const int SnLength = 20;

    /// <summary>生成 SN。sequence 为当日流水号（1 起）。</summary>
    public static string Generate(DateTime date, int sequence, string hwVersion = "01")
    {
        if (sequence is < 1 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(sequence), "流水号范围 0001~9999");
        if (hwVersion.Length != 2 || !hwVersion.All(char.IsDigit))
            throw new ArgumentException("硬件版本号必须是 2 位数字", nameof(hwVersion));

        return string.Concat(
            ProductPrefix,
            CategoryPrefix,
            ModelCode,
            FactoryCode,
            hwVersion,
            date.ToString("yyMMdd"),
            sequence.ToString("D4"));
    }

    /// <summary>解析 SN 各字段；格式不符返回 false。</summary>
    public static bool TryParse(string sn, out SnFields fields)
    {
        fields = default;
        if (sn is null || sn.Length != SnLength)
            return false;
        if (!sn.StartsWith(ProductPrefix + CategoryPrefix, StringComparison.Ordinal))
            return false;
        // 布局：LS CH 42 JY 01 | yymmdd | ssss
        //       0  2  4  6  8    10..15   16..19
        if (!int.TryParse(sn.AsSpan(10, 6), out var ymd))
            return false;
        if (!int.TryParse(sn.AsSpan(16, 4), out var seq))
            return false;

        fields = new SnFields(
            sn[..2], sn[2..4], sn[4..6], sn[6..8], sn[8..10],
            ymd, seq);
        return true;
    }
}

/// <summary>SN 解析结果。</summary>
public readonly record struct SnFields(
    string Product,
    string Category,
    string ModelCode,
    string FactoryCode,
    string HwVersion,
    int DateYymmdd,
    int Sequence);
