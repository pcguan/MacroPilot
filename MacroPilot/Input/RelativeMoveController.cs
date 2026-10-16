using System;
using System.Threading;

namespace MacroPilot.Input;

/// <summary>
/// 一次移动事务只有一个预算，每帧等待预期位移，而不是把任何光标变化当成硬件确认。
/// 不尝试与外部鼠标/应用抢控制权；观察到意外位移或无法到达即停止。
/// </summary>
internal sealed class RelativeMoveController
{
    private readonly Func<(int X, int Y)> _cursor;
    private readonly Action<int, int> _send;
    private readonly Action<CancellationToken> _checkpoint;
    private readonly Action<CancellationToken> _wait;
    private readonly Func<long> _clock;
    private readonly bool _clockExcludesPauses;
    private readonly long _started;
    private long _checkpointTime;
    private (int X, int Y) _expected;
    internal int Frames { get; private set; }

    internal RelativeMoveController(Func<(int X, int Y)> cursor, Action<int, int> send,
        Action<CancellationToken> checkpoint, Action<CancellationToken>? wait = null, Func<long>? clock = null,
        bool clockExcludesPauses = false)
    {
        _cursor = cursor; _send = send; _checkpoint = checkpoint;
        _wait = wait ?? (ct => { if (ct.WaitHandle.WaitOne(1)) ct.ThrowIfCancellationRequested(); });
        _clock = clock ?? (() => Environment.TickCount64);
        _clockExcludesPauses = clockExcludesPauses;
        _started = _clock(); _expected = _cursor();
    }

    private long ActiveNow => _clock() - _checkpointTime;
    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long before = _clock();
        _checkpoint(ct);
        if (!_clockExcludesPauses) _checkpointTime += _clock() - before;
        ct.ThrowIfCancellationRequested();
        if (ActiveNow - _started > 10_000)
            throw new InputSafetyException("鼠标移动超过 10 秒，已安全停止方案，请检查设备和目标窗口。");
    }

    internal void MoveTo(int x, int y, CancellationToken ct)
    {
        Frames = 0;
        for (int i = 0; i < 200; i++)
        {
            Check(ct);
            var p = _cursor();
            if (Math.Abs((long)p.X - _expected.X) > 3 || Math.Abs((long)p.Y - _expected.Y) > 3)
                throw Interference();
            int dx = (int)Math.Clamp((long)x - p.X, -127, 127);
            int dy = (int)Math.Clamp((long)y - p.Y, -127, 127);
            if (Math.Abs((long)x - p.X) <= 1 && Math.Abs((long)y - p.Y) <= 1) { _expected = p; return; }
            Check(ct);
            // Check 可能等待暂停恢复；恢复后不能按暂停前的位置发送旧残差。
            if (_cursor() != p) throw Interference();
            _send(dx, dy); Frames++;
            _expected = WaitForFrame(p, dx, dy, ct);
        }
        throw new InputSafetyException("鼠标移动未能收敛，已安全停止方案。");
    }

    private (int X, int Y) WaitForFrame((int X, int Y) from, int dx, int dy, CancellationToken ct)
    {
        long started = ActiveNow, settledAt = -1;
        var last = from;
        while (true)
        {
            Check(ct);
            var p = _cursor();
            long mx = (long)p.X - from.X, my = (long)p.Y - from.Y;
            // 允许 USB 分批位移和像素舍入；反向、侧向或超出本帧命令的位移不再纠偏。
            if (mx < Math.Min(0, dx) - 3L || mx > Math.Max(0, dx) + 3L ||
                my < Math.Min(0, dy) - 3L || my > Math.Max(0, dy) + 3L)
                throw Interference();
            bool arrived = Math.Abs(mx - dx) <= 1 && Math.Abs(my - dy) <= 1 && p != from;
            if (arrived)
            {
                if (p != last || settledAt < 0) settledAt = ActiveNow;
                if (ActiveNow - settledAt >= 3) return p;
            }
            else settledAt = -1;
            if (ActiveNow - started >= 150)
                throw new InputSafetyException("鼠标未按预期移动（可能有人工操作、窗口捕获或设备延迟），已安全停止方案。");
            last = p;
            _wait(ct);
        }
    }

    private static InputSafetyException Interference() =>
        new("检测到非预期鼠标位移（人工操作或应用捕获），已停止发送并安全结束方案；重新运行前请确认目标位置。");
}
