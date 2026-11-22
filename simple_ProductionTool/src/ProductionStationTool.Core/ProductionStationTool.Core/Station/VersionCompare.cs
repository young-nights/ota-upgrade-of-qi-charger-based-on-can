using System.Text.RegularExpressions;

namespace ProductionStationTool.Core.Station;

/// <summary>
/// 版本预设比对（格式容忍）。设备版本形如 QC_JYF_MCU1_FW_1.2.1 / QC_JYF_BL_1.0.0 /
/// QC_JYF_HW_1.1.5，预设可填全串或只填版本尾缀。匹配规则（任一成立）：
///   ① 全串一致（忽略大小写/首尾空白）；
///   ② 尾缀边界匹配：设备版本以 "_预设" 结尾（1.2.3 ↔ QC_JYF_MCU2_FW_1.2.3）；
///   ③ 双方版本段（结尾 x.y.z…）相等。
/// 故意不做「包含」匹配：1.2 不得命中 1.2.3、1 不得命中 11.2.10。
/// </summary>
public static class VersionCompare
{
    private static readonly Regex VersionTail = new(@"\d+(?:\.\d+)*$", RegexOptions.Compiled);

    /// <summary>预设为空视为不比对（恒 true）；实际为空视为不匹配。</summary>
    public static bool Matches(string preset, string? actual)
    {
        preset = (preset ?? "").Trim();
        actual = (actual ?? "").Trim();
        if (preset.Length == 0)
            return true;
        if (actual.Length == 0)
            return false;
        if (string.Equals(preset, actual, StringComparison.OrdinalIgnoreCase))
            return true;
        if (actual.EndsWith("_" + preset, StringComparison.OrdinalIgnoreCase))
            return true;
        var pt = VersionTail.Match(preset).Value;
        var at = VersionTail.Match(actual).Value;
        return pt.Length > 0 && at.Length > 0
            && string.Equals(pt, at, StringComparison.Ordinal);
    }
}
