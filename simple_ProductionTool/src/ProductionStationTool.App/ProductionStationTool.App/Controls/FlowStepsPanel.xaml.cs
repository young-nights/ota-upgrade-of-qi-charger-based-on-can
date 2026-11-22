using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProductionStationTool.App.Controls;

public enum StepStatus
{
    Waiting,
    Running,
    Passed,
    Failed,

    /// <summary>空白：不显示状态文本（PowerZ 汇总行专用——只滚动倒计时提示，
    /// 通过/失败由其子步 5.1/5.2 承载）。</summary>
    None,
}

/// <summary>单个流程步骤的显示状态。</summary>
public sealed class StepVm : INotifyPropertyChanged
{
    private string _statusText = "等待";
    private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

    public string Index { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>步骤名缩进（子步如 5.1/5.2 缩进显示）。</summary>
    public Thickness NameMargin { get; init; } = new(8, 0, 8, 0);

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText))); }
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        set { _statusBrush = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>流程区：步骤列表 + 状态（等待/进行中/通过/失败）。</summary>
public partial class FlowStepsPanel : UserControl
{
    private static readonly Brush WaitingBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly Brush RunningBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly Brush PassedBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush FailedBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    public ObservableCollection<StepVm> Steps { get; } = new();

    public FlowStepsPanel()
    {
        InitializeComponent();
        StepsList.ItemsSource = Steps;
    }

    /// <summary>按名称重建步骤列表（自动序号 1..N，全部回到「等待」）。</summary>
    public void SetSteps(params string[] names)
    {
        Steps.Clear();
        for (var i = 0; i < names.Length; i++)
            Steps.Add(new StepVm { Index = (i + 1).ToString(), Name = names[i] });
    }

    /// <summary>
    /// 带自定义序号的步骤列表（支持 5.1/5.2 子步：序号含 '.' 自动缩进）。
    /// </summary>
    public void SetSteps(params (string Index, string Name)[] steps)
    {
        Steps.Clear();
        foreach (var (idx, name) in steps)
        {
            bool sub = idx.Contains('.');
            Steps.Add(new StepVm
            {
                Index = idx,
                Name = name,
                NameMargin = sub ? new Thickness(20, 0, 8, 0) : new Thickness(8, 0, 8, 0),
            });
        }
    }

    /// <summary>
    /// 标记某一步状态。通过/失败只显示「通过」「失败」——失败原因一律进异常项列表，
    /// 不在流程区展开；Running 的 text 用于步骤提示（如 PowerZ 倒计时）；
    /// Waiting 带 text 显示「未执行」，否则「等待」。
    /// </summary>
    public void Mark(int index, StepStatus status, string? text = null)
    {
        if (index < 0 || index >= Steps.Count)
            return;
        var step = Steps[index];
        switch (status)
        {
            case StepStatus.Waiting:
                step.StatusText = text is null ? "等待" : "未执行";
                step.StatusBrush = WaitingBrush;
                break;
            case StepStatus.Running:
                step.StatusText = text ?? "进行中";
                step.StatusBrush = RunningBrush;
                break;
            case StepStatus.Passed:
                step.StatusText = "通过";
                step.StatusBrush = PassedBrush;
                break;
            case StepStatus.Failed:
                step.StatusText = "失败";
                step.StatusBrush = FailedBrush;
                break;
            case StepStatus.None:
                step.StatusText = "";
                step.StatusBrush = WaitingBrush;
                break;
        }
    }

    /// <summary>全部回到「等待」。</summary>
    public void Reset()
    {
        for (var i = 0; i < Steps.Count; i++)
            Mark(i, StepStatus.Waiting);
    }
}
