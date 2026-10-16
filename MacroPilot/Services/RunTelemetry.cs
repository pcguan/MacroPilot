using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MacroPilot.Models;

namespace MacroPilot.Services;

/// <summary>A bounded immutable event. Id survives Begin/End and is unique across runs.</summary>
public sealed record RunLogRecord
{
    public const int MaxBodyLength = 4096;
    private static long _nextId;
    public long Id { get; init; } = Interlocked.Increment(ref _nextId);
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public long QueuedAt { get; init; } = Stopwatch.GetTimestamp();
    public string Time => Timestamp.ToString("HH:mm:ss.fff");
    public string Level { get; init; } = "Info";
    public string Body { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusKind { get; init; } = "None";
    public int CharacterCount => Body.Length + Level.Length + Status.Length + StatusKind.Length + 64;
    public bool IsError => Level == "Error" || StatusKind == "Fail";

    public static RunLogRecord Row(string level, string body) => new()
    {
        Level = Limit(level, 32), Body = Limit(body, MaxBodyLength)
    };
    public string ToDiskLine() => $"{Time}  [{Level}] {Body}" + (Status.Length == 0 ? "" : $"  [{Status}]");
    internal static string Limit(string? text, int max) => text is null ? "" : text.Length <= max
        ? text : text[..Math.Max(0, max - 12)] + "…（内容已截断）";
    internal RunLogRecord Bounded() => Body.Length <= MaxBodyLength && Level.Length <= 32 &&
        Status.Length <= 128 && StatusKind.Length <= 32 ? this : this with
        {
            Body = Limit(Body, MaxBodyLength), Level = Limit(Level, 32),
            Status = Limit(Status, 128), StatusKind = Limit(StatusKind, 32)
        };
    internal static RunLogRecord Omitted(string destination, long count, long errors) => Row("Warning",
        $"{destination}处理速度不足，已汇总省略 {count} 条旧日志（其中错误 {errors} 条）；当前动作状态独立更新。")
        with { StatusKind = "Summary" };
}

/// <summary>One consistent snapshot; producers never enqueue progress or highlight updates.</summary>
public readonly record struct RunUiState(long Revision, double? ProgressPercent, string ProgressText,
    MacroStep? Step, string LoopText, bool IsPaused, string? FinishReason);

/// <summary>
/// The production runner's event subscribers do bounded memory work only. UI consumption and disk
/// writing are independent. One instance belongs to one run; clearing the screen never affects
/// Begin/End pairing or the disk journal. No dispatcher or filesystem call is made on a producer.
/// </summary>
public sealed class RunTelemetry
{
    private readonly object _gate = new();
    private readonly LinkedList<RunLogRecord> _pending = new();
    private readonly Dictionary<long, LinkedListNode<RunLogRecord>> _byId = new();
    private readonly Stack<RunLogRecord> _actions = new();
    private readonly BackgroundRunLogWriter? _writer;
    private readonly string? _directory;
    private readonly int _capacity, _characterCapacity;
    private int _characters;
    private long _overflowActions;
    private long _dropped, _unreportedDropped, _unreportedErrors;
    private RunUiState _state = new(0, null, "", null, "", false, null);
    public const int MaximumOpenActions = 64;

    public RunTelemetry(BackgroundRunLogWriter? writer = null, string? directory = null,
        int uiCapacity = 512, int uiCharacterCapacity = 512 * 1024)
    {
        if (uiCapacity < 2) throw new ArgumentOutOfRangeException(nameof(uiCapacity));
        if (uiCharacterCapacity < 512) throw new ArgumentOutOfRangeException(nameof(uiCharacterCapacity));
        _writer = writer; _directory = directory;
        _capacity = uiCapacity; _characterCapacity = uiCharacterCapacity;
    }

    public int PendingCount { get { lock (_gate) return _pending.Count; } }
    public int PendingLogCount => PendingCount;
    public int PendingCharacters { get { lock (_gate) return _characters; } }
    public long UiDroppedTotal { get { lock (_gate) return _dropped; } }
    public RunUiState ReadState() { lock (_gate) return _state; }
    public bool IsActionActive(long id)
    {
        lock (_gate)
        {
            foreach (var action in _actions) if (action.Id == id) return true;
            return false;
        }
    }

    public void Log(string level, string body)
    {
        var record = RunLogRecord.Row(level, body);
        WriteDisk(record);
        lock (_gate) Enqueue(record);
    }

    public void BeginAction(string body)
    {
        var record = RunLogRecord.Row("Info", body) with { Status = "执行中", StatusKind = "Running" };
        lock (_gate)
        {
            // Keep an independent hard limit for malformed event sources, so a missing End cannot
            // turn this service into another unbounded queue.
            if (_actions.Count >= MaximumOpenActions || _overflowActions > 0)
            {
                _overflowActions++;
                if (_overflowActions == 1)
                {
                    var warning = RunLogRecord.Row("Warning", "动作日志嵌套过深，后续内部动作将汇总记录。");
                    Enqueue(warning); WriteDisk(warning);
                }
                return;
            }
            _actions.Push(record);
            Enqueue(record);
        }
    }

    public void EndAction(string status, string kind)
    {
        lock (_gate)
        {
            if (_overflowActions > 0) { _overflowActions--; return; }
            if (!_actions.TryPop(out var begin)) return;
            var record = begin with
            {
                Status = RunLogRecord.Limit(status, 128), StatusKind = RunLogRecord.Limit(kind, 32),
                QueuedAt = Stopwatch.GetTimestamp()
            };
            // Pair on the producer, including after ClearUi or eviction of the original Begin.
            WriteDisk(record);
            Enqueue(record);
        }
    }

    public void StepChanged(MacroStep step, bool executing)
    {
        if (!executing) return; // Keep the latest start visible even for instantaneous actions.
        lock (_gate) _state = _state with { Revision = _state.Revision + 1, Step = step };
    }
    public void Progress(double percent, string text)
    {
        lock (_gate) _state = _state with
        {
            Revision = _state.Revision + 1,
            ProgressPercent = double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0,
            ProgressText = RunLogRecord.Limit(text, 1024)
        };
    }
    public void LoopChanged(string text)
    {
        lock (_gate) _state = _state with { Revision = _state.Revision + 1, LoopText = RunLogRecord.Limit(text, 256) };
    }
    public void PausedChanged(bool paused)
    {
        lock (_gate) _state = _state with { Revision = _state.Revision + 1, IsPaused = paused };
    }
    public void Finish(string reason)
    {
        lock (_gate)
        {
            while (_actions.TryPop(out var begin))
            {
                var ended = begin with { Status = "已结束", StatusKind = reason == "Error" ? "Fail" : "Stopped",
                    QueuedAt = Stopwatch.GetTimestamp() };
                WriteDisk(ended); Enqueue(ended);
            }
            _overflowActions = 0;
            _state = _state with { Revision = _state.Revision + 1, FinishReason = RunLogRecord.Limit(reason, 32) };
        }
    }

    private void WriteDisk(RunLogRecord record)
    {
        if (_writer != null && _directory != null) _writer.Enqueue(_directory, record);
    }

    // Caller owns _gate. End updates coalesce an unconsumed Begin in O(1).
    private void Enqueue(RunLogRecord record)
    {
        if (_byId.Remove(record.Id, out var previous))
        {
            _characters -= previous.Value.CharacterCount;
            _pending.Remove(previous);
        }
        if (record.CharacterCount > _characterCapacity) { CountDrop(record); return; }
        while (_pending.Count >= _capacity || _characters + record.CharacterCount > _characterCapacity)
            RemoveFirst(countDrop: true);
        _byId[record.Id] = _pending.AddLast(record);
        _characters += record.CharacterCount;
    }

    private void CountDrop(RunLogRecord record)
    {
        _dropped++; _unreportedDropped++;
        if (record.IsError) _unreportedErrors++;
    }
    private RunLogRecord RemoveFirst(bool countDrop)
    {
        var record = _pending.First!.Value;
        _pending.RemoveFirst(); _byId.Remove(record.Id); _characters -= record.CharacterCount;
        if (countDrop) CountDrop(record);
        return record;
    }

    /// <summary>
    /// A frame receives at most maxCount recent records, including an explicit omission summary.
    /// Excess history is never replayed for seconds after the engine has moved on. Disk is unaffected.
    /// </summary>
    public IReadOnlyList<RunLogRecord> DrainLogs(int maxCount = 96)
    {
        if (maxCount < 2) throw new ArgumentOutOfRangeException(nameof(maxCount));
        lock (_gate)
        {
            while (_pending.Count > maxCount - (_unreportedDropped > 0 ? 1 : 0))
                RemoveFirst(countDrop: true);
            var result = new List<RunLogRecord>(Math.Min(maxCount, _pending.Count + 1));
            if (_unreportedDropped > 0)
            {
                result.Add(RunLogRecord.Omitted("界面日志", _unreportedDropped, _unreportedErrors));
                _unreportedDropped = _unreportedErrors = 0;
            }
            while (_pending.Count > 0) result.Add(RemoveFirst(countDrop: false));
            return result;
        }
    }

    public void ClearUi()
    {
        lock (_gate)
        {
            _pending.Clear(); _byId.Clear(); _characters = 0;
            _unreportedDropped = _unreportedErrors = 0;
        }
    }
}
