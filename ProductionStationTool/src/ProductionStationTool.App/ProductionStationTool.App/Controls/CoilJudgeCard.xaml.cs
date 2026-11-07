using System.Windows.Controls;
using System.Windows.Media;

namespace ProductionStationTool.App.Controls;

/// <summary>线圈判定状态：未测（灰）/ 通过（绿）/ 未通过（红）。</summary>
public enum CoilState
{
    Untested,
    Passed,
    Failed,
}

/// <summary>
/// 单线圈判定卡片：标题（线圈 A/B）+ 判定大字 + 均值电流。
/// 三态配色：未测灰、通过绿、未通过红；A/B 各一个实例并排显示。
/// </summary>
public partial class CoilJudgeCard : UserControl
{
    private static readonly Brush UntestedBg = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
    private static readonly Brush PassedBg = new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7));
    private static readonly Brush FailedBg = new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
    private static readonly Brush UntestedFg = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly Brush PassedFg = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush FailedFg = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    /// <summary>卡片标题（"线圈 A"/"线圈 B"），XAML 属性赋值。</summary>
    public string Title
    {
        get => TxtTitle.Text;
        set => TxtTitle.Text = value;
    }

    public CoilJudgeCard()
    {
        InitializeComponent();
    }

    /// <summary>显示判定结果 + 均值电流；未测时电流列显示「—」。</summary>
    public void SetState(CoilState state, double currentA)
        => SetState(state, currentA.ToString("F2") + " A");

    /// <summary>显示判定结果 + 自定义明细行（工位4 门禁/版本卡复用：记录摘要等）。</summary>
    public void SetState(CoilState state, string detail)
    {
        switch (state)
        {
            case CoilState.Passed:
                Card.Background = PassedBg;
                TxtState.Text = "通过";
                TxtState.Foreground = PassedFg;
                TxtCurrent.Text = detail;
                break;
            case CoilState.Failed:
                Card.Background = FailedBg;
                TxtState.Text = "未通过";
                TxtState.Foreground = FailedFg;
                TxtCurrent.Text = detail;
                break;
            default:
                Card.Background = UntestedBg;
                TxtState.Text = "未测";
                TxtState.Foreground = UntestedFg;
                TxtCurrent.Text = "—";
                break;
        }
    }

    /// <summary>复位到未测（灰）。</summary>
    public void Reset() => SetState(CoilState.Untested, "—");
}
