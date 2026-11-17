namespace ProductionStationTool.Core.Printing;

/// <summary>
/// 标签设计器规格（「打印机设置」UI 直接配置，生成 .btw，不经手工导入）：
///   标签尺寸（mm）+ 打印机参数（打印机名/份数/速度/浓度）+ 画布上的对象级排版
///   （文本内容/字体/字号/粗体/位置 mm）。保存后由 <see cref="BarTenderTemplateBuilder"/>
///   把参数应用到画布模板并生成输出 .btw；打印仍走既有 COM 填值链路。
/// </summary>
public sealed class LabelDesign
{
    /// <summary>画布 .btw（参数的载体/骨架；默认用内置 SN 码模板）。</summary>
    public string CanvasTemplatePath { get; set; } = "";

    /// <summary>生成输出的 .btw 路径（保存时生成；打印用它）。</summary>
    public string OutputTemplatePath { get; set; } = "";

    // ---- 标签尺寸（mm）----
    public double LabelWidthMm { get; set; } = 50;
    public double LabelHeightMm { get; set; } = 30;
    /// <summary>纸高（mm）；0 = 跟随标签高。</summary>
    public double PaperHeightMm { get; set; }

    // ---- 打印机参数 ----
    public string PrinterName { get; set; } = "";
    /// <summary>打印份数（≥1）。</summary>
    public int Copies { get; set; } = 1;
    /// <summary>打印速度（0 = 打印机默认；需驱动支持）。</summary>
    public double PrintSpeed { get; set; }
    /// <summary>打印浓度（0 = 打印机默认；需驱动支持）。</summary>
    public double PrintDarkness { get; set; }

    /// <summary>对象级排版（画布上各对象的文本/字体/位置）。</summary>
    public List<LabelObjectDesign> Objects { get; set; } = new();

    // ---- 直驱打印（TSPL/ZPL 指令直发打印机，无需 BarTender/授权）----
    /// <summary>打印方式："direct" = TSPL/ZPL 直驱；"bartender" = BarTender 模板。默认直驱。</summary>
    public string PrintMode { get; set; } = "direct";

    /// <summary>直驱脚本语言："tspl"（TSC 及兼容机，中文 TTF 友好）/ "zpl"（Zebra）。</summary>
    public string Script { get; set; } = "tspl";

    /// <summary>打印分辨率（dpi）：203 / 300。</summary>
    public int Dpi { get; set; } = 203;

    /// <summary>标签间距 mm（0 = 连续纸）。</summary>
    public double GapMm { get; set; } = 2;

    /// <summary>条码设置（Code128，内容默认 {SN}）。</summary>
    public LabelBarcodeDesign Barcode { get; set; } = new();
}

/// <summary>条码排版参数（直驱脚本用）。</summary>
public sealed class LabelBarcodeDesign
{
    public bool Enabled { get; set; } = true;
    /// <summary>条码内容（支持 {SN} 占位）。</summary>
    public string Content { get; set; } = "{SN}";
    public double XMm { get; set; } = 5;
    public double YMm { get; set; } = 15;
    /// <summary>条码高度 mm。</summary>
    public double HeightMm { get; set; } = 8;
    /// <summary>窄条宽 mm（模块宽）。</summary>
    public double ModuleMm { get; set; } = 0.4;
    /// <summary>条码下方显示人读字符。</summary>
    public bool HumanReadable { get; set; } = true;
}

/// <summary>画布上一个对象的排版参数。</summary>
public sealed class LabelObjectDesign
{
    /// <summary>对象名（画布内标识，生成时按名匹配）。</summary>
    public string Name { get; set; } = "";

    /// <summary>文本内容（支持 {SN} 占位；空 = 不改画布内容）。</summary>
    public string Content { get; set; } = "";

    /// <summary>字体名（空 = 不改）。</summary>
    public string FontName { get; set; } = "";

    /// <summary>字号 pt（0 = 不改）。</summary>
    public double FontSizePt { get; set; }

    /// <summary>加粗（null = 不改）。</summary>
    public bool? Bold { get; set; }

    /// <summary>X 坐标 mm（null = 不改）。</summary>
    public double? XMm { get; set; }

    /// <summary>Y 坐标 mm（null = 不改）。</summary>
    public double? YMm { get; set; }
}
