namespace MacroPilot.Services;

public enum ActivityKind { Idle, StartingRun, Running, Recording, Updating, Closing }

/// <summary>UI线程上的活动预留；异步设备打开期间也占用执行权。</summary>
public sealed class ActivityState
{
    public ActivityKind Kind { get; private set; }
    public bool IsIdle => Kind == ActivityKind.Idle;
    public bool TryBegin(ActivityKind next)
    {
        if (!IsIdle || next == ActivityKind.Idle) return false;
        Kind = next;
        return true;
    }
    public void RunStarted() { if (Kind == ActivityKind.StartingRun) Kind = ActivityKind.Running; }
    public void Finish(ActivityKind expected) { if (Kind == expected) Kind = ActivityKind.Idle; }
    public void Close() => Kind = ActivityKind.Closing;
}
