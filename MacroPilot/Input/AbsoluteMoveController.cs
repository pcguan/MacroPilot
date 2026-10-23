using System;
using System.Threading;

namespace MacroPilot.Input;

/// <summary>软件绝对移动也确认落点，并在路点之间检测外部位移。</summary>
internal sealed class AbsoluteMoveController
{
    private readonly Func<(int X, int Y)> _cursor;
    private readonly Action<int, int> _send;
    private readonly Action<CancellationToken> _checkpoint;
    private readonly Action<CancellationToken> _wait;
    private readonly Func<long> _clock;
    private readonly bool _clockExcludesPauses;
    private readonly long _started;
    private readonly Func<long>? _externalEpoch;
    private readonly long _initialEpoch;
    private long _paused;
    private (int X, int Y) _expected;

    internal AbsoluteMoveController(Func<(int X, int Y)> cursor, Action<int, int> send,
        Action<CancellationToken> checkpoint, Action<CancellationToken>? wait = null,
        Func<long>? clock = null, bool clockExcludesPauses = false, Func<long>? externalEpoch = null)
    {
        _cursor = cursor; _send = send; _checkpoint = checkpoint;
        _wait = wait ?? (ct => { if (ct.WaitHandle.WaitOne(1)) ct.ThrowIfCancellationRequested(); });
        _clock = clock ?? (() => Environment.TickCount64); _clockExcludesPauses = clockExcludesPauses;
        _externalEpoch = externalEpoch; _initialEpoch = externalEpoch?.Invoke() ?? 0;
        _started = _clock(); _expected = _cursor();
    }
    private long Now => _clock() - _paused;
    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_externalEpoch != null && _externalEpoch() != _initialEpoch)
            throw new InputSafetyException("检测到本程序以外的鼠标移动输入，已安全停止方案。");
        long before = _clock(); _checkpoint(ct);
        if (!_clockExcludesPauses) _paused += _clock() - before;
        ct.ThrowIfCancellationRequested();
        if (_externalEpoch != null && _externalEpoch() != _initialEpoch)
            throw new InputSafetyException("检测到本程序以外的鼠标移动输入，已安全停止方案。");
        if (Now - _started > 10_000) throw new InputSafetyException("鼠标移动超过 10 秒，已安全停止方案。");
    }
    private static bool Near((int X, int Y) a, (int X, int Y) b, int tolerance)
        => Math.Abs((long)a.X - b.X) <= tolerance && Math.Abs((long)a.Y - b.Y) <= tolerance;

    internal void MoveTo(int x, int y, CancellationToken ct)
    {
        Check(ct);
        var start = _cursor();
        if (!Near(start, _expected, 3)) throw Interference();
        Check(ct);
        if (_cursor() != start) throw Interference();
        _send(x, y);
        long began = Now, stable = -1;
        var last = start;
        bool observedTarget = false;
        while (true)
        {
            Check(ct);
            var p = _cursor();
            if (Near(p, (x, y), 1))
            {
                observedTarget = true;
                if (stable < 0 || p != last) stable = Now;
                if (Now - stable >= 3) { _expected = p; return; }
            }
            else
            {
                if (observedTarget || !Near(p, start, 3)) throw Interference();
                stable = -1;
            }
            if (Now - began >= 150) throw new InputSafetyException("软件鼠标定位未按预期到达，已安全停止方案。");
            last = p; _wait(ct);
        }
    }
    private static InputSafetyException Interference()
        => new("检测到非预期鼠标位移或定位受限，已停止发送并安全结束方案。");
}
