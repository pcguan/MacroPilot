using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MacroPilot.Services;

/// <summary>
/// One bounded queue and one background thread, shared by ordinary and run logs. Enqueue never does
/// filesystem work or waits for a consumer. The directory is captured with each event, not at flush.
/// Overflow and write failures are observable; a slow/unavailable disk must not stall input timing.
/// </summary>
public sealed class BackgroundRunLogWriter : IDisposable
{
    private readonly record struct Pending(string Directory, RunLogRecord Record);
    private readonly object _gate = new();
    private readonly Queue<Pending> _pending = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly int _capacity, _characterCapacity;
    private readonly Action<string, IReadOnlyList<RunLogRecord>> _sink;
    private readonly RotatingLogSink? _fileSink;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _characters;
    private bool _accepting = true, _completed;
    private long _dropped, _unreportedDropped, _unreportedErrors, _failed;
    private string? _overflowDirectory;
    private string? _lastError;

    public BackgroundRunLogWriter(int capacity = 8192, int characterCapacity = 2 * 1024 * 1024,
        Action<string, IReadOnlyList<RunLogRecord>>? sink = null,
        long maxFileBytes = 8 * 1024 * 1024, int retainedFiles = 16)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (characterCapacity < 512) throw new ArgumentOutOfRangeException(nameof(characterCapacity));
        if (maxFileBytes < 512) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        if (retainedFiles < 1) throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        _capacity = capacity; _characterCapacity = characterCapacity;
        if (sink == null)
        {
            _fileSink = new RotatingLogSink(maxFileBytes, retainedFiles);
            _sink = _fileSink.Write;
        }
        else _sink = sink;
        _thread = new Thread(Consume) { IsBackground = true, Name = "MacroPilot log writer" };
        _thread.Start();
    }

    public int PendingCount { get { lock (_gate) return _pending.Count; } }
    public int PendingCharacters { get { lock (_gate) return _characters; } }
    public long DroppedTotal { get { lock (_gate) return _dropped; } }
    public long FailedRecordsTotal => Interlocked.Read(ref _failed);
    public string? LastError => Volatile.Read(ref _lastError);
    public Task Completion => _completion.Task;

    public void Enqueue(string directory, RunLogRecord record)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        record = record.Bounded();
        lock (_gate)
        {
            if (!_accepting) return;
            bool needsWake = _pending.Count == 0 && _unreportedDropped == 0;
            if (record.CharacterCount > _characterCapacity)
            {
                Drop(new Pending(directory, record));
                if (needsWake) _wake.Set();
                return;
            }
            while (_pending.Count >= _capacity || _characters + record.CharacterCount > _characterCapacity)
            {
                var old = _pending.Dequeue();
                _characters -= old.Record.CharacterCount;
                Drop(old);
            }
            _pending.Enqueue(new Pending(directory, record));
            _characters += record.CharacterCount;
            if (needsWake) _wake.Set();
        }
    }

    private void Drop(Pending item)
    {
        _dropped++; _unreportedDropped++;
        if (item.Record.IsError) _unreportedErrors++;
        _overflowDirectory ??= item.Directory;
    }

    private void Consume()
    {
        try
        {
            while (true)
            {
                string? directory;
                var batch = new List<RunLogRecord>(128);
                lock (_gate)
                {
                    directory = _overflowDirectory ?? (_pending.TryPeek(out var first) ? first.Directory : null);
                    if (_unreportedDropped > 0)
                    {
                        batch.Add(RunLogRecord.Omitted("磁盘日志（可含多个运行会话）", _unreportedDropped, _unreportedErrors));
                        _unreportedDropped = _unreportedErrors = 0;
                        _overflowDirectory = null;
                    }
                    while (batch.Count < 128 && _pending.TryPeek(out var item) &&
                        string.Equals(directory, item.Directory, StringComparison.OrdinalIgnoreCase))
                    {
                        _pending.Dequeue(); _characters -= item.Record.CharacterCount;
                        batch.Add(item.Record);
                    }
                    if (batch.Count == 0 && !_accepting && _pending.Count == 0) break;
                }
                if (batch.Count == 0) { _wake.WaitOne(); continue; }
                try { _sink(directory!, batch); }
                catch (Exception ex)
                {
                    Interlocked.Add(ref _failed, batch.Count);
                    Volatile.Write(ref _lastError, "日志写入失败：" + RunLogRecord.Limit(ex.Message, 512));
                    // Back off on a failing volume. This thread owns no engine/UI resources; queue
                    // capacity still bounds memory while producers continue at their normal speed.
                    Thread.Sleep(100);
                }
            }
        }
        finally
        {
            try { _fileSink?.Dispose(); }
            catch (Exception ex) { Volatile.Write(ref _lastError, "日志关闭失败：" + RunLogRecord.Limit(ex.Message, 512)); }
            lock (_gate) { _completed = true; _accepting = false; _wake.Dispose(); }
            _completion.TrySetResult();
        }
    }

    /// <summary>Stop accepting, drain and flush within the caller's shutdown budget.</summary>
    public bool Complete(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_completed) return true;
            _accepting = false;
            _wake.Set();
        }
        return _thread.Join(timeout);
    }
    public void Dispose() => Complete(TimeSpan.FromSeconds(1));

    private sealed class RotatingLogSink : IDisposable
    {
        private readonly long _maxBytes;
        private readonly int _retained;
        private FileStream? _stream;
        private string? _directory, _day, _path;
        private int _index;
        // Legacy daily logs predate this retention policy and may contain valuable history.
        // Only files created by the rotating writer participate in automatic retention.
        private static readonly Regex OwnedName = new(@"^run-\d{8}-\d{3,}\.log$", RegexOptions.CultureInvariant);
        private static readonly UTF8Encoding Utf8 = new(false);
        public RotatingLogSink(long maxBytes, int retained) { _maxBytes = maxBytes; _retained = retained; }

        public void Write(string directory, IReadOnlyList<RunLogRecord> records)
        {
            try
            {
                directory = Path.GetFullPath(directory);
                foreach (var record in records)
                {
                    string day = record.Timestamp.ToString("yyyyMMdd");
                    if (_stream == null || !string.Equals(directory, _directory, StringComparison.OrdinalIgnoreCase) || day != _day)
                        Open(directory, day);
                    byte[] line = Utf8.GetBytes(record.ToDiskLine() + Environment.NewLine);
                    // A custom very small file budget still bounds one exceptionally long record.
                    if (line.Length > _maxBytes)
                    {
                        string shortened = record.ToDiskLine();
                        while (Utf8.GetByteCount(shortened) + 32 > _maxBytes) shortened = shortened[..(shortened.Length / 2)];
                        line = Utf8.GetBytes(shortened + "…（内容已截断）" + Environment.NewLine);
                    }
                    if (_stream!.Length + line.Length > _maxBytes) Rotate();
                    _stream!.Write(line);
                }
                _stream?.Flush();
            }
            catch
            {
                Dispose(); // Retry opening after a removable/unavailable directory recovers.
                throw;
            }
        }

        private void Open(string directory, string day)
        {
            Dispose();
            Directory.CreateDirectory(directory);
            _directory = directory; _day = day;
            _index = 0;
            foreach (string path in Directory.EnumerateFiles(directory, $"run-{day}-*.log"))
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                if (int.TryParse(stem[("run-" + day + "-").Length..], out int index)) _index = Math.Max(_index, index);
            }
            OpenCurrent();
            Prune();
        }
        private void OpenCurrent()
        {
            _path = Path.Combine(_directory!, $"run-{_day}-{_index:D3}.log");
            _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        }
        private void Rotate()
        {
            _stream?.Dispose(); _stream = null;
            _index++;
            OpenCurrent();
            Prune();
        }
        private void Prune()
        {
            var files = new DirectoryInfo(_directory!).EnumerateFiles("run-*.log")
                .Where(file => OwnedName.IsMatch(file.Name))
                .OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).ToList();
            int excess = files.Count - _retained;
            foreach (var file in files)
            {
                if (excess <= 0) break;
                if (string.Equals(file.FullName, _path, StringComparison.OrdinalIgnoreCase)) continue;
                file.Delete(); excess--;
            }
        }
        public void Dispose() { _stream?.Dispose(); _stream = null; }
    }
}
