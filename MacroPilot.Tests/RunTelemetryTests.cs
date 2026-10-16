using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

[Collection(PerformanceProbeCollection.Name)]
public class RunTelemetryTests
{
    [Fact]
    public void ClearingTheScreenDoesNotLoseCompletionFromDiskOrUi()
    {
        var saved = new List<RunLogRecord>();
        using var writer = new BackgroundRunLogWriter(sink: (_, batch) => saved.AddRange(batch));
        var telemetry = new RunTelemetry(writer, "captured-directory");
        telemetry.BeginAction("wait");
        var begin = Assert.Single(telemetry.DrainLogs());
        Assert.True(telemetry.IsActionActive(begin.Id));
        telemetry.ClearUi();
        telemetry.EndAction("success", "Success");
        var end = Assert.Single(telemetry.DrainLogs());
        Assert.Equal(begin.Id, end.Id);
        Assert.Equal("wait", end.Body);
        Assert.Equal("Success", end.StatusKind);
        Assert.False(telemetry.IsActionActive(end.Id));
        Assert.True(writer.Complete(TimeSpan.FromSeconds(5)));
        Assert.Equal(end, Assert.Single(saved));
    }

    [Fact]
    public void BeginAndEndCoalesceWithoutChangingIdentityOrStartTime()
    {
        var telemetry = new RunTelemetry();
        telemetry.BeginAction("A");
        telemetry.EndAction("ok", "Success");
        var record = Assert.Single(telemetry.DrainLogs());
        Assert.Equal("Success", record.StatusKind);
        Assert.Equal(0, telemetry.UiDroppedTotal);
        telemetry.BeginAction("B");
        var next = Assert.Single(telemetry.DrainLogs());
        Assert.NotEqual(record.Id, next.Id);
    }

    [Fact]
    public void AFrameContainsRecentRecordsAndAnExplicitBoundedOmissionSummary()
    {
        var telemetry = new RunTelemetry(uiCapacity: 4, uiCharacterCapacity: 1024);
        for (int i = 0; i < 100; i++) telemetry.Log(i == 0 ? "Error" : "Info", i.ToString());
        Assert.Equal(4, telemetry.PendingCount);
        Assert.InRange(telemetry.PendingCharacters, 0, 1024);
        telemetry.Progress(33.5, "latest progress"); telemetry.PausedChanged(true); telemetry.Finish("Stopped");
        var state = telemetry.ReadState();
        Assert.Equal(33.5, state.ProgressPercent);
        Assert.True(state.IsPaused); Assert.Equal("Stopped", state.FinishReason);
        var logs = telemetry.DrainLogs(3);
        Assert.Equal(3, logs.Count);
        Assert.Equal("Summary", logs[0].StatusKind);
        Assert.Contains("98", logs[0].Body); Assert.Contains("错误 1", logs[0].Body);
        Assert.Equal(new[] { "98", "99" }, logs.Skip(1).Select(row => row.Body));
        Assert.Equal(98, telemetry.UiDroppedTotal);
        Assert.Equal(0, telemetry.PendingCount); Assert.Equal(0, telemetry.PendingCharacters);
        Assert.Empty(telemetry.DrainLogs());
    }

    [Fact]
    public void TextAndCharacterBudgetsApplyEvenWhenOnlyOneHugeMessageArrives()
    {
        var telemetry = new RunTelemetry(uiCapacity: 10, uiCharacterCapacity: 512);
        telemetry.Log("Info", new string('x', 1_000_000));
        Assert.InRange(telemetry.PendingCharacters, 0, 512);
        Assert.Equal("Summary", Assert.Single(telemetry.DrainLogs()).StatusKind);
        var row = RunLogRecord.Row("Info", new string('x', 1_000_000));
        Assert.InRange(row.Body.Length, 0, RunLogRecord.MaxBodyLength);
        Assert.Contains("截断", row.Body);
    }

    [Fact]
    public async Task ProgressPercentAndTextAreReadAsOneSnapshot()
    {
        var telemetry = new RunTelemetry();
        var producer = Task.Run(() =>
        {
            for (int i = 0; i < 100_000; i++) telemetry.Progress(i % 101, (i % 101).ToString());
        });
        do
        {
            var snapshot = telemetry.ReadState();
            if (snapshot.ProgressPercent.HasValue)
                Assert.Equal(snapshot.ProgressPercent.Value.ToString(), snapshot.ProgressText);
        } while (!producer.IsCompleted);
        await producer;
        Assert.Equal(0, telemetry.PendingCount);
    }

    [Fact]
    public void SessionsKeepIndependentFinishPauseAndLogs()
    {
        var old = new RunTelemetry(); var current = new RunTelemetry();
        old.Finish("Stopped"); old.PausedChanged(true); old.Log("Error", "old");
        current.Progress(10, "new");
        Assert.Null(current.ReadState().FinishReason); Assert.False(current.ReadState().IsPaused);
        Assert.Empty(current.DrainLogs());
    }

    [Fact]
    public void UnbalancedActionBeginsHaveAHardCapacityAndFinishClearsActivity()
    {
        var telemetry = new RunTelemetry();
        telemetry.BeginAction("first");
        long id = Assert.Single(telemetry.DrainLogs()).Id;
        for (int i = 0; i < 10000; i++) telemetry.BeginAction("nested");
        Assert.True(telemetry.IsActionActive(id));
        Assert.InRange(telemetry.PendingCount, 0, RunTelemetry.MaximumOpenActions + 1);
        telemetry.Finish("Stopped");
        Assert.False(telemetry.IsActionActive(id));
        Assert.DoesNotContain(telemetry.DrainLogs(), row => row.StatusKind == "Running");
    }

    [Fact]
    public void DiskWritesPreserveOrderAndTheCapturedDirectory()
    {
        var received = new List<(string directory, string body)>();
        using var writer = new BackgroundRunLogWriter(sink: (directory, batch) =>
            received.AddRange(batch.Select(row => (directory, row.Body))));
        var old = new RunTelemetry(writer, "old-directory");
        var next = new RunTelemetry(writer, "next-directory");
        old.Log("Info", "one"); next.Log("Info", "two"); old.Log("Info", "three");
        Assert.True(writer.Complete(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { ("old-directory", "one"), ("next-directory", "two"), ("old-directory", "three") }, received);
    }

    [Fact]
    public void SlowDiskHasBoundedMemoryAndShutdownDoesNotWaitForever()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var written = new List<RunLogRecord>();
        using var writer = new BackgroundRunLogWriter(capacity: 4, characterCapacity: 1024, sink: (_, batch) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test sink not released");
            written.AddRange(batch);
        });
        try
        {
            writer.Enqueue("dir", RunLogRecord.Row("Info", "first"));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            for (int i = 0; i < 100; i++) writer.Enqueue("dir", RunLogRecord.Row("Info", i.ToString()));
            Assert.Equal(4, writer.PendingCount); Assert.Equal(96, writer.DroppedTotal);
            Assert.InRange(writer.PendingCharacters, 0, 1024);
            Assert.False(writer.Complete(TimeSpan.FromMilliseconds(10)));
        }
        finally { release.Set(); Assert.True(writer.Complete(TimeSpan.FromSeconds(5))); }
        Assert.Contains(written, row => row.StatusKind == "Summary" && row.Body.Contains("96"));
        Assert.Equal("99", written[^1].Body);
        Assert.Null(writer.LastError); Assert.Equal(0, writer.PendingCharacters);
    }

    [Fact]
    public void DiskFailuresAreVisibleAndDoNotFaultTheProducer()
    {
        using var writer = new BackgroundRunLogWriter(sink: (_, _) => throw new IOException("unavailable"));
        writer.Enqueue("dir", RunLogRecord.Row("Error", "important"));
        Assert.True(writer.Complete(TimeSpan.FromSeconds(5)));
        Assert.Contains("unavailable", writer.LastError);
        Assert.Equal(1, writer.FailedRecordsTotal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RotationBoundsDiskUsageAndPreservesUnrelatedFiles(bool relativeDirectory)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MacroPilot-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string unrelated = Path.Combine(directory, "run-personal.log");
            File.WriteAllText(unrelated, "keep");
            string legacy = Path.Combine(directory, "run-20200101.log");
            File.WriteAllText(legacy, "legacy history");
            using var writer = new BackgroundRunLogWriter(maxFileBytes: 1024, retainedFiles: 3);
            string destination = relativeDirectory ? Path.GetRelativePath(Environment.CurrentDirectory, directory) : directory;
            for (int i = 0; i < 100; i++) writer.Enqueue(destination, RunLogRecord.Row("Info", $"{i:D3}-" + new string('字', 100)));
            Assert.True(writer.Complete(TimeSpan.FromSeconds(5))); Assert.Null(writer.LastError);
            var files = new DirectoryInfo(directory).GetFiles("run-*.log")
                .Where(file => file.FullName != unrelated && file.FullName != legacy).ToArray();
            Assert.Equal(3, files.Length);
            Assert.All(files, file => Assert.InRange(file.Length, 1, 1024));
            Assert.Contains(files, file => File.ReadAllText(file.FullName).Contains("099-"));
            Assert.Equal("keep", File.ReadAllText(unrelated));
            Assert.Equal("legacy history", File.ReadAllText(legacy));
        }
        finally { Directory.Delete(directory, true); }
    }
}
