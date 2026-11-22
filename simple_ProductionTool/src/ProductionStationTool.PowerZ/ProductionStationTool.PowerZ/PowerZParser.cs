using System.Globalization;

namespace ProductionStationTool.PowerZ;

/// <summary>
/// PowerZ 串口数据行解析。
///
/// 协议占位：PowerZ 具体输出格式待拿到仪表后按实测样例调整。
/// 当前支持两种常见行格式（\r\n 结尾）：
///   1) "V:5.012,I:0.345"      —— 键值对
///   2) "5.012,0.345"          —— 两个浮点数（电压,电流）
/// 解析失败返回 false，不抛异常。
/// </summary>
public static class PowerZParser
{
    public static bool TryParseLine(string? line, out PowerSample sample)
    {
        sample = default;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        line = line.Trim();

        // 格式 1：V:x,I:y（大小写不敏感，允许空格）
        if (line.Contains('V', StringComparison.OrdinalIgnoreCase) &&
            line.Contains('I', StringComparison.OrdinalIgnoreCase))
        {
            double v = 0, i = 0;
            bool hasV = false, hasI = false;
            foreach (var part in line.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':', 2);
                if (kv.Length != 2)
                    continue;
                var key = kv[0].Trim();
                var val = kv[1].Trim();
                if (key.Equals("V", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    hasV = true;
                else if (key.Equals("I", StringComparison.OrdinalIgnoreCase) &&
                         double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out i))
                    hasI = true;
            }
            if (hasV && hasI)
            {
                sample = new PowerSample(v, i, DateTime.Now);
                return true;
            }
            return false;
        }

        // 格式 2：v,i 两个浮点
        var cols = line.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (cols.Length >= 2 &&
            double.TryParse(cols[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var vv) &&
            double.TryParse(cols[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ii))
        {
            sample = new PowerSample(vv, ii, DateTime.Now);
            return true;
        }

        return false;
    }
}
