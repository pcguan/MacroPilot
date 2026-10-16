using System.Diagnostics;

namespace MacroPilot.Services;

/// <summary>一次运行累计的有效时间；所有嵌套等待/限时共享同一暂停边界。</summary>
internal sealed class PausableClock
{
    private readonly object _sync = new();
    private readonly Stopwatch _clock = new();
    public double ElapsedMilliseconds { get { lock (_sync) return _clock.Elapsed.TotalMilliseconds; } }
    public void Restart() { lock (_sync) _clock.Restart(); }
    public void Pause() { lock (_sync) _clock.Stop(); }
    public void Resume() { lock (_sync) _clock.Start(); }
}
