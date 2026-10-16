using System;
using System.Threading;

namespace MacroPilot.Input;

/// <summary>运行器向输入后端提供同一套暂停、取消和可暂停等待语义。</summary>
public sealed class InputExecutionContext
{
    private readonly Action<CancellationToken> _checkpoint;
    private readonly Action<double, CancellationToken> _delay;
    private readonly Func<bool> _isPaused;
    private readonly Func<long> _activeMilliseconds;

    public InputExecutionContext(Action<CancellationToken> checkpoint,
        Action<double, CancellationToken> delay, Func<bool>? isPaused = null, Func<long>? activeMilliseconds = null)
    {
        _checkpoint = checkpoint;
        _delay = delay;
        _isPaused = isPaused ?? (() => false);
        HasActiveClock = activeMilliseconds != null;
        _activeMilliseconds = activeMilliseconds ?? (() => Environment.TickCount64);
    }

    public bool IsPaused => _isPaused();
    public long ActiveMilliseconds => _activeMilliseconds();
    internal bool HasActiveClock { get; }
    public void Checkpoint(CancellationToken ct) { ct.ThrowIfCancellationRequested(); _checkpoint(ct); ct.ThrowIfCancellationRequested(); }
    public void Delay(double ms, CancellationToken ct) { Checkpoint(ct); _delay(ms, ct); ct.ThrowIfCancellationRequested(); }

    // 长按暂停时先释放本任务按下的键，继续后重新按下，只有实际持有的时间计入 hold。
    internal static void Hold(double milliseconds, Action press, Action release,
        InputExecutionContext? context, CancellationToken ct)
    {
        bool held = false;
        void Check() { ct.ThrowIfCancellationRequested(); context?.Checkpoint(ct); }
        try
        {
            Check(); press(); held = true;
            double remaining = Math.Max(0, milliseconds);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (context?.IsPaused == true)
                {
                    release(); held = false;
                    context.Checkpoint(ct);
                    press(); held = true;
                }
                long before = clock.ElapsedTicks;
                Services.PreciseTimer.Sleep(Math.Min(remaining, 5), ct);
                remaining -= (clock.ElapsedTicks - before) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
        }
        finally { if (held) release(); }
    }
}

/// <summary>输入状态已不可确信。必须终止方案，不能由普通失败监听继续向桌面输入。</summary>
public sealed class InputSafetyException : InvalidOperationException
{
    public InputSafetyException(string message, Exception? inner = null) : base(message, inner) { }
}
