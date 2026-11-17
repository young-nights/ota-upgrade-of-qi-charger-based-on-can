using System.Runtime.InteropServices;
using System.Text;

namespace ProductionStationTool.Core.Printing;

/// <summary>
/// 按 <see cref="LabelDesign"/> 生成 .btw：复制画布模板 → COM 应用参数（标签尺寸/打印机/对象排版）
/// → 保存为输出模板。全部走已验证的 BarTender 10.1 ActiveX 通道（Formats.Open / Objects /
/// NamedSubStrings / Save），属性按候选名逐个探测（不同版本命名有差异，探不到即跳过并报告）。
/// 打印仍走 <see cref="BarTenderLabelPrinter"/> 填值链路，不在此处理。
/// </summary>
public static class BarTenderTemplateBuilder
{
    private const string ProgId = "BarTender.Application";

    /// <summary>本机可用的打印机名（winspool 枚举，默认打印机排前）。</summary>
    public static List<string> ListPrinters()
    {
        var names = new List<string>();
        try
        {
            const uint flags = 0x00000002 | 0x00000004;   // LOCAL | CONNECTIONS
            EnumPrintersW(flags, null, 1, IntPtr.Zero, 0, out var needed, out _);
            if (needed == 0)
                return names;
            var buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!EnumPrintersW(flags, null, 1, buf, needed, out _, out var returned))
                    return names;
                int stride = IntPtr.Size * 4;              // PRINTER_INFO_1W: 4 个指针宽字段
                for (int i = 0; i < returned; i++)
                {
                    var pName = Marshal.ReadIntPtr(buf, i * stride + IntPtr.Size * 2);
                    var name = Marshal.PtrToStringUni(pName);
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }

            // 默认打印机排前（不重复）
            var def = GetDefaultPrinterW(out var defName) ? defName : null;
            if (!string.IsNullOrEmpty(def) && names.Remove(def!))
                names.Insert(0, def!);
        }
        catch
        {
            // 枚举失败交由手填
        }
        return names;
    }

    /// <summary>
    /// 生成输出 .btw：画布复制后应用设计参数。
    /// 返回（是否成功, 说明）——说明里逐项报告应用/跳过，便于现场核对。
    /// </summary>
    public static (bool Ok, string Detail) Generate(LabelDesign design)
    {
        var applied = new List<string>();
        var skipped = new List<string>();

        if (string.IsNullOrWhiteSpace(design.CanvasTemplatePath) || !File.Exists(design.CanvasTemplatePath))
            return (false, $"画布模板不存在：{design.CanvasTemplatePath}");
        if (string.IsNullOrWhiteSpace(design.OutputTemplatePath))
            return (false, "未设置输出模板路径");

        try
        {
            var dir = Path.GetDirectoryName(design.OutputTemplatePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.Copy(design.CanvasTemplatePath, design.OutputTemplatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            return (false, $"输出模板复制失败：{ex.Message}");
        }

        dynamic? app = null;
        try
        {
            var t = Type.GetTypeFromProgID(ProgId)
                ?? throw new InvalidOperationException("未安装 BarTender（Automation 及以上）");
            app = Activator.CreateInstance(t);
            try { app!.Visible = false; } catch { /* 部分版本不支持 */ }

            dynamic format = OpenFormat(app!, design.OutputTemplatePath);
            try
            {
                // 各段独立容错：一段失败不拖垮整个生成
                try { ApplyPageSetup(format, design, applied, skipped); }
                catch (Exception ex) { skipped.Add($"尺寸（{ex.Message}）"); }
                try { ApplyPrinter(format, design, applied, skipped); }
                catch (Exception ex) { skipped.Add($"打印机（{ex.Message}）"); }
                try { ApplyObjects(format, design, applied, skipped); }
                catch (Exception ex) { skipped.Add($"对象（{ex.Message}）"); }
                format.Save();
                applied.Add("已保存");
            }
            finally
            {
                try { format.Close(1); } catch { /* 已 Save，Close 不再存 */ }
            }

            var detail = $"已生成：{design.OutputTemplatePath}；应用 {applied.Count} 项"
                + (applied.Count > 0 ? "（" + string.Join("；", applied) + "）" : "");
            if (skipped.Count > 0)
                detail += "；未支持/跳过：" + string.Join("；", skipped);
            return (true, detail);
        }
        catch (Exception ex)
        {
            return (false, $"生成失败：{ex.Message}");
        }
        finally
        {
            if (app is not null)
            {
                try { app.Quit(1); } catch { /* ignore */ }
                try { Marshal.ReleaseComObject(app); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>兼容不同 BarTender 版本的 Open 签名（同 Material_Label_Print 实证链）。</summary>
    private static dynamic OpenFormat(dynamic app, string path)
    {
        dynamic formats = app.Formats;
        try { return formats.Open(path, false, ""); }
        catch { /* 下一签名 */ }
        try { return formats.Open(path, false); }
        catch { /* 下一签名 */ }
        try { return formats.Open(path); }
        catch { /* 下一签名 */ }
        dynamic documents = app.Documents;
        return documents.Open(path);
    }

    // ---- 标签尺寸（mm）：PageSetup（ActiveX 文档确认有 PaperWidth/Height；Label* 按候选探测）----

    private static void ApplyPageSetup(dynamic format, LabelDesign d, List<string> applied, List<string> skipped)
    {
        dynamic ps = format.PageSetup;
        double factor = UnitsToMmFactor(ps, skipped);
        if (TrySet(ps, new[] { "LabelWidth" }, d.LabelWidthMm / factor))
            applied.Add($"标签宽 {d.LabelWidthMm}mm");
        else
            skipped.Add("标签宽");
        if (TrySet(ps, new[] { "LabelHeight" }, d.LabelHeightMm / factor))
            applied.Add($"标签高 {d.LabelHeightMm}mm");
        else
            skipped.Add("标签高");
        double paperH = d.PaperHeightMm > 0 ? d.PaperHeightMm : d.LabelHeightMm;
        if (TrySet(ps, new[] { "PaperHeight" }, paperH / factor))
            applied.Add($"纸高 {paperH}mm");
        if (TrySet(ps, new[] { "PaperWidth" }, Math.Max(d.LabelWidthMm, paperH) / factor))
            applied.Add("纸宽已按标签适配");
    }

    /// <summary>把 mm 换算到 PageSetup 当前单位：返回 1mm 折合多少单位。探不到单位按 mm。</summary>
    private static double UnitsToMmFactor(dynamic ps, List<string> skipped)
    {
        try
        {
            int units = (int)ps.Units;
            // BtUnits：0=英寸, 1=毫米（探不到/异常按毫米）
            return units == 0 ? 25.4 : 1.0;
        }
        catch
        {
            skipped.Add("单位按 mm");
            return 1.0;
        }
    }

    // ---- 打印机参数 ----

    private static void ApplyPrinter(dynamic format, LabelDesign d, List<string> applied, List<string> skipped)
    {
        if (!string.IsNullOrWhiteSpace(d.PrinterName))
        {
            bool ok = false;
            try { format.Printer = d.PrinterName; ok = true; }
            catch
            {
                ok = TrySet(format.PrintSetup, new[] { "PrinterName" }, d.PrinterName);
            }
            if (ok) applied.Add($"打印机 {d.PrinterName}");
            else skipped.Add("打印机名设置失败");
        }
        if (d.Copies >= 1 && TrySet(format.PrintSetup, new[] { "IdenticalCopiesOfLabel", "Copies", "NumberOfCopies" }, d.Copies))
            applied.Add($"份数 {d.Copies}");
        if (d.PrintSpeed > 0)
        {
            if (TrySet(format.PrintSetup, new[] { "PrintSpeed", "Speed" }, d.PrintSpeed))
                applied.Add($"速度 {d.PrintSpeed}");
            else
                skipped.Add("速度（驱动相关，未支持）");
        }
        if (d.PrintDarkness > 0)
        {
            if (TrySet(format.PrintSetup, new[] { "PrintDarkness", "Darkness" }, d.PrintDarkness))
                applied.Add($"浓度 {d.PrintDarkness}");
            else
                skipped.Add("浓度（驱动相关，未支持）");
        }
    }

    // ---- 对象排版：文本/字体/字号/粗体/位置（按对象名匹配）----

    private static void ApplyObjects(dynamic format, LabelDesign d, List<string> applied, List<string> skipped)
    {
        if (d.Objects.Count == 0)
            return;

        // 对象索引：Name -> COM 对象；命名数据源集合
        var byName = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var o in format.Objects)
            {
                try { byName[(string)o.Name] = o; }
                catch { /* 无名对象跳过 */ }
            }
        }
        catch
        {
            skipped.Add("对象枚举");
            return;
        }

        foreach (var design in d.Objects)
        {
            if (string.IsNullOrWhiteSpace(design.Name))
                continue;
            if (!byName.TryGetValue(design.Name, out var obj))
            {
                skipped.Add($"对象 {design.Name}（画布无此对象）");
                continue;
            }

            int n = 0;
            if (!string.IsNullOrEmpty(design.Content))
            {
                // {SN} 占位必须落在命名数据源上（打印时替换）；静态内容写对象值即可
                bool set = TrySetNamed(format, design.Name, design.Content);
                if (!set && !design.Content.Contains("{SN}", StringComparison.OrdinalIgnoreCase))
                    set = TrySet(obj, new[] { "Value", "Text" }, design.Content);
                if (set) n++;
                else skipped.Add(design.Content.Contains("{SN}", StringComparison.OrdinalIgnoreCase)
                    ? $"{design.Name} 的 {{SN}} 需命名数据源"
                    : $"{design.Name} 内容");
            }
            if (!string.IsNullOrEmpty(design.FontName))
            {
                if (TrySet(obj.Font, new[] { "Name" }, design.FontName)) n++;
                else skipped.Add($"{design.Name} 字体");
            }
            if (design.FontSizePt > 0)
            {
                if (TrySet(obj.Font, new[] { "Size" }, design.FontSizePt)) n++;
                else skipped.Add($"{design.Name} 字号");
            }
            if (design.Bold is { } bold)
            {
                if (TrySet(obj.Font, new[] { "Bold" }, bold)) n++;
                else skipped.Add($"{design.Name} 粗体");
            }
            if (design.XMm is { } x)
            {
                if (TrySet(obj, new[] { "X" }, x)) n++;
                else skipped.Add($"{design.Name} X");
            }
            if (design.YMm is { } y)
            {
                if (TrySet(obj, new[] { "Y" }, y)) n++;
                else skipped.Add($"{design.Name} Y");
            }
            if (n > 0)
                applied.Add($"对象 {design.Name} ×{n}");
        }
    }

    private static bool TrySetNamed(dynamic format, string name, string value)
    {
        try
        {
            format.SetNamedSubStringValue(name, value);
            return true;
        }
        catch { return false; }
    }

    /// <summary>按候选属性名尝试赋值；全部失败返回 false。</summary>
    private static bool TrySet(dynamic target, string[] names, object value)
    {
        foreach (var name in names)
        {
            try
            {
                target.GetType().InvokeMember(name,
                    System.Reflection.BindingFlags.SetProperty, null, target, new[] { value });
                return true;
            }
            catch { /* 试下一个候选名 */ }
        }
        return false;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrintersW(uint flags, string? name, uint level,
        IntPtr buffer, uint cbNeeded, out uint pcbNeeded, out uint pcReturned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDefaultPrinterW(StringBuilder? pszBuffer, ref uint pcchBuffer);

    private static bool GetDefaultPrinterW(out string? name)
    {
        name = null;
        uint size = 0;
        GetDefaultPrinterW(null, ref size);
        if (size == 0)
            return false;
        var sb = new StringBuilder((int)size);
        if (!GetDefaultPrinterW(sb, ref size))
            return false;
        name = sb.ToString();
        return true;
    }
}
