using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;
using Xunit.Abstractions;

namespace MacroPilot.Tests;

[Collection(PerformanceProbeCollection.Name)]
public sealed class PerformanceSoakTests
{
    private readonly ITestOutputHelper _output;
    public PerformanceSoakTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SustainedTelemetryTrafficKeepsBoundedQueuesAndFreshFinalState()
    {
        const int uiCapacity = 128, uiCharacters = 64 * 1024;
        const int diskCapacity = 512, diskCharacters = 256 * 1024;
        // The normal suite remains short; a dedicated run can set this to 60.
        // Neither duration represents an hours-long run with actual WPF rendering.
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("MACROPILOT_SOAK_SECONDS"), out int requested)
            ? Math.Clamp(requested, 1, 300) : 10;
        using var cancellation = new CancellationTokenSource();
        using var releaseSink = new ManualResetEventSlim();
        using var process = Process.GetCurrentProcess();
        string directory = Path.Combine(Path.GetTempPath(), "MacroPilot-soak-" + Guid.NewGuid().ToString("N"));
        long published = 0, batches = 0, productionTicks = 0, maxBatchTicks = 0;
        long written = 0, uiConsumed = 0, finishPublishedAt = 0, finishObservedAt = 0;
        double maxUiAgeMs = 0;
        using var writer = new BackgroundRunLogWriter(diskCapacity, diskCharacters, (_, records) =>
        {
            releaseSink.Wait(15); // a controlled slow disk, never under the producer's lock
            Interlocked.Add(ref written, records.Count);
        });
        var telemetry = new RunTelemetry(writer, directory, uiCapacity, uiCharacters);
        var steps = new[]
        {
            new MacroStep { Type = "Wait", DurationMs = 0 },
            new MacroStep { Type = "Wait", DurationMs = 0 },
            new MacroStep { Type = "Wait", DurationMs = 0 },
            new MacroStep { Type = "Wait", DurationMs = 0 }
        };
        string[] bodies = { "soak action A", "soak action B", "soak action C", "soak action D" };
        // Warm the transport without retaining an ever-growing FakeBackend.Calls list.
        var warmup = new RunTelemetry();
        for (int i = 0; i < 256; i++)
        {
            warmup.BeginAction(bodies[i % 4]); warmup.Progress(i % 100, bodies[i % 4]);
            warmup.EndAction("执行成功", "Success");
        }
        warmup.ClearUi();
        long allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var clock = Stopwatch.StartNew();
        Task? producer = null, consumer = null, observer = null;
        bool writerCompleted = false;
        int uiPeakCount = 0, uiPeakCharacters = 0, diskPeakCount = 0, diskPeakCharacters = 0;

        async Task ConsumeUi()
        {
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var state = telemetry.ReadState();
                foreach (var record in telemetry.DrainLogs(64))
                {
                    uiConsumed++;
                    if (record.StatusKind != "Summary")
                        maxUiAgeMs = Math.Max(maxUiAgeMs, Stopwatch.GetElapsedTime(record.QueuedAt).TotalMilliseconds);
                }
                if (!string.IsNullOrEmpty(state.FinishReason))
                {
                    finishObservedAt = Stopwatch.GetTimestamp();
                    return;
                }
                await Task.Delay(80, cancellation.Token);
            }
        }

        async Task Observe()
        {
            int intervalUiCount = 0, intervalUiChars = 0, intervalDiskCount = 0, intervalDiskChars = 0;
            double nextReport = 1;
            long previousPublished = 0;
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                int uiCount = telemetry.PendingCount, uiChars = telemetry.PendingCharacters;
                int diskCount = writer.PendingCount, diskChars = writer.PendingCharacters;
                Assert.InRange(uiCount, 0, uiCapacity);
                Assert.InRange(uiChars, 0, uiCharacters);
                Assert.InRange(diskCount, 0, diskCapacity);
                Assert.InRange(diskChars, 0, diskCharacters);
                intervalUiCount = Math.Max(intervalUiCount, uiCount);
                intervalUiChars = Math.Max(intervalUiChars, uiChars);
                intervalDiskCount = Math.Max(intervalDiskCount, diskCount);
                intervalDiskChars = Math.Max(intervalDiskChars, diskChars);
                uiPeakCount = Math.Max(uiPeakCount, uiCount); uiPeakCharacters = Math.Max(uiPeakCharacters, uiChars);
                diskPeakCount = Math.Max(diskPeakCount, diskCount); diskPeakCharacters = Math.Max(diskPeakCharacters, diskChars);
                bool finished = producer!.IsCompleted;
                if (clock.Elapsed.TotalSeconds >= nextReport || finished)
                {
                    process.Refresh();
                    long currentPublished = Interlocked.Read(ref published);
                    // Peaks are sampled every ~20ms, not claimed as exact between-sample maxima.
                    _output.WriteLine($"t_s={clock.Elapsed.TotalSeconds:F2}; produced={currentPublished}; " +
                        $"interval_actions={currentPublished - previousPublished}; " +
                        $"ui_count={uiCount}; ui_chars={uiChars}; ui_sampled_peak_count={intervalUiCount}; ui_sampled_peak_chars={intervalUiChars}; " +
                        $"disk_count={diskCount}; disk_chars={diskChars}; disk_sampled_peak_count={intervalDiskCount}; disk_sampled_peak_chars={intervalDiskChars}; " +
                        $"ui_dropped={telemetry.UiDroppedTotal}; disk_dropped={writer.DroppedTotal}; " +
                        $"producer_ms={Interlocked.Read(ref productionTicks) * 1000.0 / Stopwatch.Frequency:F2}; " +
                        $"max_retained_ui_event_age_ms={Volatile.Read(ref maxUiAgeMs):F2}; " +
                        $"managed_heap_bytes={GC.GetTotalMemory(false)}; working_set_bytes={process.WorkingSet64}; " +
                        $"allocated_bytes={GC.GetTotalAllocatedBytes(false) - allocatedBefore}");
                    previousPublished = currentPublished;
                    intervalUiCount = intervalUiChars = intervalDiskCount = intervalDiskChars = 0;
                    nextReport = Math.Floor(clock.Elapsed.TotalSeconds) + 1;
                }
                if (finished) return;
                await Task.Delay(20, cancellation.Token);
            }
        }

        try
        {
            consumer = ConsumeUi();
            // A dedicated producer models the engine's synchronous publication path without
            // occupying a thread-pool worker needed by the simulated UI and test continuations.
            producer = Task.Factory.StartNew(() =>
            {
                var productionClock = Stopwatch.StartNew();
                while (productionClock.Elapsed.TotalSeconds < seconds)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    long begin = Stopwatch.GetTimestamp();
                    for (int i = 0; i < 256; i++)
                    {
                        int index = i % 4;
                        telemetry.BeginAction(bodies[index]);
                        telemetry.StepChanged(steps[index], true);
                        telemetry.Progress(i % 100, bodies[index]);
                        telemetry.EndAction("执行成功", "Success");
                    }
                    long elapsed = Stopwatch.GetTimestamp() - begin;
                    Interlocked.Add(ref productionTicks, elapsed);
                    if (elapsed > Interlocked.Read(ref maxBatchTicks)) Interlocked.Exchange(ref maxBatchTicks, elapsed);
                    Interlocked.Increment(ref batches);
                    Interlocked.Add(ref published, 256);
                }
                Volatile.Write(ref finishPublishedAt, Stopwatch.GetTimestamp());
                telemetry.Finish("Done");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            observer = Observe();
            await Task.WhenAll(producer, consumer, observer).WaitAsync(TimeSpan.FromSeconds(seconds + 10));
            releaseSink.Set();
            writerCompleted = writer.Complete(TimeSpan.FromSeconds(3));

            double finishLagMs = Stopwatch.GetElapsedTime(finishPublishedAt, finishObservedAt).TotalMilliseconds;
            double producerMs = Interlocked.Read(ref productionTicks) * 1000.0 / Stopwatch.Frequency;
            double maxBatchMs = Interlocked.Read(ref maxBatchTicks) * 1000.0 / Stopwatch.Frequency;
            _output.WriteLine($"duration_s={seconds}; actions={published}; batches={batches}; producer_ms={producerMs:F2}; " +
                $"mean_publish_us={producerMs * 1000 / Math.Max(1, published):F2}; max_256_action_batch_ms={maxBatchMs:F2}; " +
                $"finish_observed_lag_ms={finishLagMs:F2}; max_retained_ui_event_age_ms={maxUiAgeMs:F2}; " +
                $"ui_consumed={uiConsumed}; disk_written={written}; ui_dropped={telemetry.UiDroppedTotal}; disk_dropped={writer.DroppedTotal}; " +
                $"ui_sampled_peak={uiPeakCount}/{uiPeakCharacters}; disk_sampled_peak={diskPeakCount}/{diskPeakCharacters}");
            Assert.True(writerCompleted, "The writer thread must terminate within the shutdown budget.");
            Assert.Equal("Done", telemetry.ReadState().FinishReason);
            Assert.True(published > 0 && written > 0 && uiConsumed > 0);
            Assert.Null(writer.LastError);
            Assert.InRange(finishLagMs, 0, 1500);
            Assert.InRange(maxUiAgeMs, 0, 1500);
            Assert.Equal(0, writer.PendingCount);
            Assert.Equal(0, writer.PendingCharacters);
        }
        finally
        {
            cancellation.Cancel(); releaseSink.Set();
            try
            {
                foreach (var task in new[] { producer, consumer, observer })
                {
                    if (task == null) continue;
                    try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (OperationCanceledException) { }
                    catch (Exception) when (task.IsFaulted) { } // the main WhenAll already reports these failures
                }
            }
            finally
            {
                if (!writerCompleted) Assert.True(writer.Complete(TimeSpan.FromSeconds(3)));
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }
}
