using ProductionStationTool.Core.Station;

namespace ProductionStationTool.Tests;

/// <summary>版本预设比对（格式容忍）测试。</summary>
public class VersionCompareTests
{
    [Theory]
    [InlineData("QC_JYF_MCU2_FW_1.2.3", "QC_JYF_MCU2_FW_1.2.3", true)]   // 全串一致
    [InlineData("qc_jyf_mcu2_fw_1.2.3", "QC_JYF_MCU2_FW_1.2.3", true)]   // 大小写
    [InlineData("1.2.3", "QC_JYF_MCU2_FW_1.2.3", true)]                  // 版本尾缀
    [InlineData("1.2.3", "QC_JYF_MCU1_FW_1.2.3", true)]                  // 任意前缀 + 尾缀
    [InlineData("1.2.3", "QC_JYF_MCU2_FW_1.2.30", false)]                // 尾缀边界：1.2.3 ≠ 1.2.30
    [InlineData("1.2", "QC_JYF_MCU2_FW_1.2.3", false)]                   // 1.2 不得命中 1.2.3
    [InlineData("1", "QC_JYF_MCU1_FW_11.2.10", false)]                   // 1 不得命中 11.2.10
    [InlineData("QC_JYF_HW_1.1.5", "QC_JYF_HW_1.1.5", true)]             // 硬件版本全串
    [InlineData("1.0.0", "QC_JYF_BL_1.0.0", true)]                       // Boot 尾缀
    [InlineData("", "QC_JYF_MCU1_FW_1.2.1", true)]                       // 空预设 = 不比对
    [InlineData("1.2.1", null, false)]                                   // 实际缺失 = 不匹配
    [InlineData("1.2.1", "", false)]
    public void Matches_CoversFormatsAndBoundaries(string preset, string? actual, bool expected)
        => Assert.Equal(expected, VersionCompare.Matches(preset, actual));
}
