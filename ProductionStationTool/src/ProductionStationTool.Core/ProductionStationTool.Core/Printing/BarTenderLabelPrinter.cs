using System.Runtime.InteropServices;
using ProductionStationTool.Core.Station;

namespace ProductionStationTool.Core.Printing;

/// <summary>
/// BarTender COM 原生打印（C# 动态绑定 BarTender.Application，Seagull ActiveX 自动化）。
/// 实现 <see cref="ISnPrinter"/>（工位4 SN 打印步），另供外箱标签打印与
/// 预设 UI 的模板信息读取/字体应用。协议要点（同 Material_Label_Print 血泪经验）：
///   ① 打印用「即发即走」PrintOut(false, false)——部分驱动不回报任务状态，
///     等回执会把已打出的标签误报失败；
///   ② 以 COM 是否抛异常判成败，不解读返回值；
///   ③ 需本机安装 BarTender（Automation 及以上）。
/// </summary>
public sealed class BarTenderLabelPrinter : ISnPrinter, IDisposable
{
    public const string ProgId = "BarTender.Application";

    private readonly LabelPreset _preset;

    public BarTenderLabelPrinter(LabelPreset preset) => _preset = preset;

    /// <summary>本机是否装了 BarTender COM 组件（不启动 BarTender）。</summary>
    public static bool Available()
    {
        try { return Type.GetTypeFromProgID(ProgId) is not null; }
        catch { return false; }
    }

    /// <summary>ISnPrinter：按 SN 预设打标签（NamedValues 里的 {SN} 占位替换为实际 SN）。</summary>
    public Task<(bool Ok, string Detail)> PrintSnAsync(string sn, CancellationToken ct = default)
        => PrintAsync(new Dictionary<string, string> { ["{SN}"] = sn }, ct);

    /// <summary>按预设打印一张标签；values 提供 {SN} 等占位符的替换值。</summary>
    public Task<(bool Ok, string Detail)> PrintAsync(
        IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(_preset.TemplatePath) || !File.Exists(_preset.TemplatePath))
                return (false, $"模板不存在：{_preset.TemplatePath}");
            dynamic? app = null;
            try
            {
                var t = Type.GetTypeFromProgID(ProgId)
                    ?? throw new InvalidOperationException("未安装 BarTender（Automation 及以上）");
                app = Activator.CreateInstance(t);
                var formats = app!.Formats;
                dynamic format = formats.Open(_preset.TemplatePath, false);
                try
                {
                    foreach (var kv in _preset.NamedValues)
                    {
                        string name = kv.Key;
                        string val = kv.Value;
                        foreach (var v in values)
                            val = val.Replace(v.Key, v.Value);
                        format.NamedSubStrings(name).Value = val;
                    }
                    if (!string.IsNullOrWhiteSpace(_preset.PrinterName))
                        format.Printer = _preset.PrinterName;
                    // 即发即走：不等回执（部分驱动不报任务状态，等回执会误报失败）
                    format.PrintOut(false, false);
                }
                finally
                {
                    // 不保存模板：只填值打印，模板文件不动
                    try { format.Close(0); } catch { /* ignore */ }
                }
                return (true, $"已输出到 {_preset.PrinterName}");
            }
            catch (Exception ex)
            {
                return (false, $"BarTender 打印失败：{ex.Message}");
            }
            finally
            {
                if (app is not null)
                {
                    try { ((IDisposable)app).Dispose(); } catch { /* ignore */ }
                    try { Marshal.ReleaseComObject(app); } catch { /* ignore */ }
                }
            }
        }, ct);

    /// <summary>读模板信息：命名数据源列表 + 文本对象字体/字号/位置（预设 UI 展示）。</summary>
    public static TemplateInfo? ReadTemplateInfo(string templatePath)
    {
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
            return null;
        dynamic? app = null;
        try
        {
            var t = Type.GetTypeFromProgID(ProgId)
                ?? throw new InvalidOperationException("未安装 BarTender");
            app = Activator.CreateInstance(t);
            dynamic format = app!.Formats.Open(templatePath, true);
            try
            {
                var names = new List<string>();
                foreach (var ns in format.NamedSubStrings)
                    names.Add((string)ns.Name);

                var objs = new List<TemplateTextObject>();
                foreach (var o in format.Objects)
                {
                    try
                    {
                        string oname = o.Name;
                        string font = "";
                        double size = 0, x = 0, y = 0;
                        try { font = (string)o.Font.Name; } catch { /* 非文本对象 */ }
                        try { size = (double)o.Font.Size; } catch { }
                        try { x = (double)o.X; } catch { }
                        try { y = (double)o.Y; } catch { }
                        if (font.Length > 0)
                            objs.Add(new TemplateTextObject(oname, font, size, x, y));
                    }
                    catch { /* 单对象属性失败不阻断 */ }
                }
                return new TemplateInfo(names, objs);
            }
            finally
            {
                try { format.Close(0); } catch { /* ignore */ }
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (app is not null)
            {
                try { ((IDisposable)app).Dispose(); } catch { /* ignore */ }
                try { Marshal.ReleaseComObject(app); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// 把模板内全部文本对象的字体/字号套用并保存（预设 UI「应用到模板」）。
    /// 行距等 BarTender COM 未暴露的属性仍需在 Designer 调。返回（是否成功, 说明）。
    /// </summary>
    public static (bool Ok, string Detail) ApplyTemplateFont(
        string templatePath, string fontName, double sizePt)
    {
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
            return (false, $"模板不存在：{templatePath}");
        dynamic? app = null;
        try
        {
            var t = Type.GetTypeFromProgID(ProgId)
                ?? throw new InvalidOperationException("未安装 BarTender");
            app = Activator.CreateInstance(t);
            dynamic format = app!.Formats.Open(templatePath, true);
            try
            {
                int n = 0;
                foreach (var o in format.Objects)
                {
                    try
                    {
                        if (fontName.Length > 0) o.Font.Name = fontName;
                        if (sizePt > 0) o.Font.Size = sizePt;
                        n++;
                    }
                    catch { /* 非文本对象跳过 */ }
                }
                format.Save();
                return (true, $"已应用到 {n} 个对象并保存模板");
            }
            finally
            {
                try { format.Close(0); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            return (false, $"套用字体失败：{ex.Message}");
        }
        finally
        {
            if (app is not null)
            {
                try { ((IDisposable)app).Dispose(); } catch { /* ignore */ }
                try { Marshal.ReleaseComObject(app); } catch { /* ignore */ }
            }
        }
    }

    public void Dispose()
    {
        // 每次打印/读取独立开关 BarTender 会话，无常驻句柄
    }
}
