using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProductionStationTool.App.Controls;

public enum StepStatus
{
    Waiting,
    Running,
    Passed,
    Failed,
}

/// <summary>单个流程步骤的显示状态。</summary>
public sealed class StepVm : INotifyPropertyChanged
{
    private string _statusText = "等待";
    private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

    public int Index { get; init; }
    public string Name { get; init; } = "";

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

    /// <summary>按名称重建步骤列表（全部回到「等待」）。</summary>
    public void SetSteps(params string[] names)
    {
        Steps.Clear();
        for (var i = 0; i < names.Length; i++)
            Steps.Add(new StepVm { Index = i + 1, Name = names[i] });
    }

    /// <summary>标记某一步状态。</summary>
    public void Mark(int index, StepStatus status, string? text = null)
    {
        if (index < 0 || index >= Steps.Count)
            return;
        var step = Steps[index];
        switch (status)
        {
            case StepStatus.Waiting:
                step.StatusText = text ?? "等待";
                step.StatusBrush = WaitingBrush;
                break;
            case StepStatus.Running:
                step.StatusText = text ?? "进行中";
                step.StatusBrush = RunningBrush;
                break;
            case StepStatus.Passed:
                step.StatusText = text ?? "通过";
                step.StatusBrush = PassedBrush;
                break;
            case StepStatus.Failed:
                step.StatusText = text ?? "失败";
                step.StatusBrush = FailedBrush;
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
