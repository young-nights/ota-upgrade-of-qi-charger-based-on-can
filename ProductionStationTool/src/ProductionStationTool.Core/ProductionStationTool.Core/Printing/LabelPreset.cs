namespace ProductionStationTool.Core.Printing;

/// <summary>标签打印预设（SN 码预设 / 外包箱预设各自一份）。</summary>
public sealed class LabelPreset
{
    /// <summary>BarTender 模板路径（.btw）。</summary>
    public string TemplatePath { get; set; } = "";

    /// <summary>目标打印机名（与系统打印机名一致）。</summary>
    public string PrinterName { get; set; } = "";

    /// <summary>
    /// 模板命名数据源 → 值映射（键 = .btw 内 NamedSubStrings 名，值支持 {SN} 占位符）。
    /// 打印时 {SN} 替换为实际 SN。
    /// </summary>
    public Dictionary<string, string> NamedValues { get; set; } = new();
}

/// <summary>模板信息（预设 UI 展示用，经 BarTender COM 读取）。</summary>
public sealed record TemplateInfo(
    IReadOnlyList<string> NamedSubstrings,
    IReadOnlyList<TemplateTextObject> TextObjects);

/// <summary>模板内一个文本对象的排版属性（字体/字号/位置）。</summary>
public sealed record TemplateTextObject(string Name, string Font, double Size, double X, double Y);
