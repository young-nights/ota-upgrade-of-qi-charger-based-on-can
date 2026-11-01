using System.Runtime.InteropServices;

namespace QiChargerStationTool.Core.Transport;

/// <summary>
/// Windows 定时器精度钩子。默认系统节拍 15.6ms——导致 Thread.Sleep(1/2)
/// 实际睡 15ms，固件传输每块 18 个 CF 各睡一次 STmin=1ms 就变成 270ms/块，
/// 整体比 Linux（nanosleep 1ms 精度）慢一个数量级。
/// timeBeginPeriod(1) 把节拍提到 1ms，Sleep(1) 才真的是 1ms。
/// </summary>
public static class TimerResolution
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private static int _ref;

    /// <summary>进程内提高定时器精度到 1ms（引用计数，可重复调用）。</summary>
    public static void Enter()
    {
        if (Interlocked.Increment(ref _ref) == 1)
        {
            try { TimeBeginPeriod(1); } catch { /* 非 Windows 平台忽略 */ }
        }
    }

    /// <summary>释放一次精度引用；归零时恢复系统默认节拍。</summary>
    public static void Leave()
    {
        if (Interlocked.Decrement(ref _ref) == 0)
        {
            try { TimeEndPeriod(1); } catch { /* ignore */ }
        }
    }
}
