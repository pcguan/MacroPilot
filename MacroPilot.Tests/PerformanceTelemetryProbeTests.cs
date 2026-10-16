using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;
using static MacroPilot.Tests.Harness;

namespace MacroPilot.Tests;

public sealed partial class PerformanceProbeTests
{
    [Fact]
    public async Task SlowTelemetryConsumerKeepsThreeSecondWaitAndCurrentStateFresh()
    {
        await RunProbe(Plan(Key("A"), Wait(20)));
        var telemetry = new RunTelemetry(uiCapacity: 128, uiCharacterCapacity: 64 * 1024);
        using var cancellation = new CancellationTokenSource();
        var eventAges = new List<double>();
        long finishObserved = 0;
        int peakPending = 0, samples = 0;
        bool sawWaiting = false;
        async Task Consume()
        {
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                // This measures the real transport with an 80ms consumer. It does
                // not claim to measure WPF layout/rendering or monitor presentation.
                var state = telemetry.ReadState();
                samples++;
                sawWaiting |= state.ProgressText.Contains("等待", StringComparison.Ordinal)
                    && state.ProgressPercent is > 0 and < 100;
                peakPending = Math.Max(peakPending, telemetry.PendingCount);
                foreach (var record in telemetry.DrainLogs(32))
                    if (record.StatusKind != "Summary")
                        eventAges.Add(Stopwatch.GetElapsedTime(record.QueuedAt).TotalMilliseconds);
                if (!string.IsNullOrEmpty(state.FinishReason))
                {
                    finishObserved = Stopwatch.GetTimestamp();
                    return;
                }
                await Task.Delay(80, cancellation.Token);
            }
        }

        var consumer = Consume();
        try
        {
            var result = await RunProbe(BurstThenWait(50000), runner => AttachTelemetry(runner, telemetry));
            await consumer.WaitAsync(TimeSpan.FromSeconds(3));
            double actualWaitMs = Stopwatch.GetElapsedTime(result.MarkerBefore, result.MarkerAfter).TotalMilliseconds;
            double finishDisplayLagMs = Stopwatch.GetElapsedTime(result.MarkerAfter, finishObserved).TotalMilliseconds;
            double maxEventAgeMs = eventAges.DefaultIfEmpty(0).Max();
            _output.WriteLine($"consumer_period_ms=80; burst_actions=50000; actual_wait_ms={actualWaitMs:F2}; " +
                $"finish_observed_lag_ms={finishDisplayLagMs:F2}; max_retained_event_age_ms={maxEventAgeMs:F2}; " +
                $"samples={samples}; ui_peak_pending={peakPending}; ui_dropped={telemetry.UiDroppedTotal}; {result.Describe()}");
            Assert.Equal("Done", result.Reason);
            Assert.True(sawWaiting, "Current wait progress must remain visible while historical logs are discarded.");
            Assert.InRange(actualWaitMs, 2950, 6000);
            Assert.InRange(finishDisplayLagMs, 0, 1500);
            Assert.InRange(maxEventAgeMs, 0, 1500);
            Assert.InRange(peakPending, 0, 128);
            Assert.True(telemetry.UiDroppedTotal > 0, "The deliberately slow consumer must exercise overflow recovery.");
        }
        finally
        {
            cancellation.Cancel();
            try { await consumer.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task BlockedWriterDoesNotBackPressureEngineAndBothBuffersStayBounded()
    {
        const int uiCapacity = 64, uiCharacters = 16 * 1024;
        const int diskCapacity = 128, diskCharacters = 64 * 1024;
        using var releaseSink = new ManualResetEventSlim();
        var enteredSink = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string directory = Path.Combine(Path.GetTempPath(), "MacroPilot-perf-" + Guid.NewGuid().ToString("N"));
        long writtenRecords = 0;
        var writer = new BackgroundRunLogWriter(diskCapacity, diskCharacters, (_, records) =>
        {
            enteredSink.TrySetResult(true);
            // Always bounded even if a test assertion fails before reaching cleanup.
            if (!releaseSink.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Test sink was not released.");
            Interlocked.Add(ref writtenRecords, records.Count);
        });
        var telemetry = new RunTelemetry(writer, directory, uiCapacity, uiCharacters);
        bool completed = false;
        try
        {
            telemetry.Log("Info", "blocked sink seed");
            await enteredSink.Task.WaitAsync(TimeSpan.FromSeconds(3));
            long allocatedBefore = GC.GetTotalAllocatedBytes(false);
            var result = await RunProbe(BurstThenWait(250000), runner => AttachTelemetry(runner, telemetry));
            long allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
            double actualWaitMs = Stopwatch.GetElapsedTime(result.MarkerBefore, result.MarkerAfter).TotalMilliseconds;
            _output.WriteLine($"blocked_writer=true; burst_actions=250000; actual_wait_ms={actualWaitMs:F2}; " +
                $"ui_pending={telemetry.PendingCount}; ui_chars={telemetry.PendingCharacters}; ui_dropped={telemetry.UiDroppedTotal}; " +
                $"disk_pending={writer.PendingCount}; disk_chars={writer.PendingCharacters}; disk_dropped={writer.DroppedTotal}; " +
                $"process_allocated_bytes={allocated}; {result.Describe()}");
            Assert.Equal("Done", result.Reason);
            Assert.Equal("Done", telemetry.ReadState().FinishReason);
            Assert.Equal(250002, result.Calls.Length);
            Assert.InRange(actualWaitMs, 2950, 6000);
            Assert.InRange(telemetry.PendingCount, 0, uiCapacity);
            Assert.InRange(telemetry.PendingCharacters, 0, uiCharacters);
            Assert.InRange(writer.PendingCount, 0, diskCapacity);
            Assert.InRange(writer.PendingCharacters, 0, diskCharacters);
            Assert.True(telemetry.UiDroppedTotal > 0);
            Assert.True(writer.DroppedTotal > 0);
            Assert.Null(writer.LastError);
            Assert.Equal(0, Interlocked.Read(ref writtenRecords));

            releaseSink.Set();
            completed = writer.Complete(TimeSpan.FromSeconds(3));
            Assert.True(completed, "Writer must terminate after its controlled sink is released.");
            Assert.True(Interlocked.Read(ref writtenRecords) > 0);
            Assert.Equal(0, writer.PendingCount);
        }
        finally
        {
            releaseSink.Set();
            if (!completed) Assert.True(writer.Complete(TimeSpan.FromSeconds(3)));
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static MacroPlan BurstThenWait(int count)
    {
        var burst = Key("C"); burst.RepeatCount = count; burst.RepeatDelayMs = 0;
        return Plan(burst, Key("F9"), Wait(3000), Key("F10"));
    }

    private static void AttachTelemetry(MacroRunner runner, RunTelemetry telemetry)
    {
        runner.Log += telemetry.Log;
        runner.ActBegin += telemetry.BeginAction;
        runner.ActEnd += telemetry.EndAction;
        runner.StepStateChanged += telemetry.StepChanged;
        runner.Progress += telemetry.Progress;
        runner.PlanLoopChanged += telemetry.LoopChanged;
        runner.PausedChanged += telemetry.PausedChanged;
        runner.Finished += telemetry.Finish;
    }
}
