namespace ProductionStationTool.App.Controls;

/// <summary>
/// 运行日志缓冲（非 UI）：流程写入 → 异步刷新到任一绑定视图。
/// 主页面不占位，弹出日志窗与流程共用同一缓冲。
/// </summary>
public sealed class LogBuffer
{
    private readonly List<string> _lines = new();
    private readonly object _gate = new();

    /// <summary>内容变更（任意线程触发；视图自行切 UI 线程刷新）。</summary>
    public event Action? Changed;

    public void Append(string message)
    {
        lock (_gate)
            _lines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
            _lines.Clear();
        Changed?.Invoke();
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return _lines.ToArray();
    }
}
