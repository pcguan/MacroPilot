using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MacroPilot.Input;
using Xunit;

namespace MacroPilot.Tests;

public sealed class InputSafetyTests
{
    [Fact]
    public void RelativeMovementArrivesWithBoundedFrames()
    {
        (int X, int Y) position = (0, 0);
        long now = 0;
        var frames = new List<(int, int)>();
        var controller = new RelativeMoveController(() => position,
            (x, y) => { frames.Add((x, y)); position = (position.X + x, position.Y + y); },
            ct => ct.ThrowIfCancellationRequested(), _ => now++, () => now);
        controller.MoveTo(-600, 80, default);
        Assert.Equal((-600, 80), position);
        Assert.Equal(5, frames.Count);
        Assert.All(frames, f => { Assert.InRange(f.Item1, -127, 127); Assert.InRange(f.Item2, -127, 127); });
    }

    [Fact]
    public void ExternalMovementStopsWithoutCorrectiveFeedbackLoop()
    {
        (int X, int Y) position = (0, 0);
        long now = 0; int sends = 0;
        var controller = new RelativeMoveController(() => position,
            (_, _) => { sends++; position = (-30, 100); }, _ => { }, _ => now++, () => now);
        Assert.Throws<InputSafetyException>(() => controller.MoveTo(600, 80, default));
        Assert.Equal(1, sends);
    }

    [Fact]
    public void MovementBetweenWaypointsDoesNotRestartConvergence()
    {
        (int X, int Y) position = (0, 0); long now = 0; int sends = 0;
        var controller = new RelativeMoveController(() => position,
            (x, y) => { sends++; position = (position.X + x, position.Y + y); }, _ => { }, _ => now++, () => now);
        controller.MoveTo(10, 10, default);
        position = (50, 90);
        Assert.Throws<InputSafetyException>(() => controller.MoveTo(20, 20, default));
        Assert.Equal(1, sends);
    }

    [Fact]
    public void CursorMovedWhilePausedBeforeSendDoesNotReceiveStaleCommand()
    {
        (int X, int Y) position = (0, 0); int checks = 0, sends = 0;
        var controller = new RelativeMoveController(() => position, (_, _) => sends++,
            _ => { if (++checks == 2) position = (50, 50); });
        Assert.Throws<InputSafetyException>(() => controller.MoveTo(100, 0, default));
        Assert.Equal(0, sends);
    }

    [Fact]
    public void MissingMovementTimesOutWithoutSendingAnotherFrame()
    {
        long now = 0; int sends = 0;
        var controller = new RelativeMoveController(() => (0, 0), (_, _) => sends++, _ => { }, _ => now++, () => now);
        Assert.Throws<InputSafetyException>(() => controller.MoveTo(100, 0, default));
        Assert.Equal(1, sends);
        Assert.InRange(now, 150, 151);
    }

    [Fact]
    public void StopInterruptsWaitingForPhysicalMovement()
    {
        using var cancel = new CancellationTokenSource();
        int sends = 0;
        var controller = new RelativeMoveController(() => (0, 0), (_, _) => sends++,
            ct => ct.ThrowIfCancellationRequested(), _ => cancel.Cancel());
        Assert.Throws<OperationCanceledException>(() => controller.MoveTo(100, 0, cancel.Token));
        Assert.Equal(1, sends);
    }

    [Fact]
    public void ATransactionBudgetIsSharedAcrossWaypoints()
    {
        (int X, int Y) position = (0, 0); long now = 0; int sends = 0;
        var controller = new RelativeMoveController(() => position,
            (x, y) => { sends++; position = (position.X + x, position.Y + y); }, _ => { }, _ => now++, () => now);
        controller.MoveTo(10, 0, default);
        now += 10_001;
        Assert.Throws<InputSafetyException>(() => controller.MoveTo(20, 0, default));
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData(0, 0, -1920, -1080)]
    [InlineData(1, 1, -1, -1)]
    [InlineData(0.5, 0.5, -960, -540)]
    public void NormalizedCoordinatesStayInsideSelectedMonitor(double x, double y, int expectedX, int expectedY)
    {
        var monitor = new ScreenInfo.Monitor("test", -1920, -1080, 0, 0, false);
        Assert.Equal((expectedX, expectedY), ScreenInfo.ResolveOnMonitor(monitor, x, y));
    }

    [Fact]
    public void PauseBetweenWaypointsDoesNotConsumeMovementBudget()
    {
        long wall = 0, pausedTime = 0;
        (int X, int Y) position = (0, 0);
        int sends = 0;
        var context = new InputExecutionContext(_ => { }, (_, _) => { },
            activeMilliseconds: () => wall - pausedTime);
        var controller = new RelativeMoveController(() => position,
            (x, y) => { sends++; position = (position.X + x, position.Y + y); }, context.Checkpoint,
            _ => wall++, () => context.ActiveMilliseconds, clockExcludesPauses: true);
        controller.MoveTo(10, 0, default);
        wall += 60_000; pausedTime += 60_000; // 外层 Gate 暂停，控制器内部没有 Checkpoint。
        controller.MoveTo(20, 0, default);
        Assert.Equal((20, 0), position);
        wall += 10_001;
        Assert.Throws<InputSafetyException>(() => controller.MoveTo(30, 0, default));
        Assert.Equal(2, sends);
    }

    [Fact]
    public void FailedInjectionIsReported()
    {
        Assert.Throws<InputSafetyException>(() => NativeInputDevice.ValidateSent(0, 1));
        NativeInputDevice.ValidateSent(1, 1);
    }

    [Fact]
    public void FailedMouseRestoreRetainsBackupForRetry()
    {
        string dir = Path.Combine(Path.GetTempPath(), "MacroPilotMouseTest_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "backup.json");
        try
        {
            Ch9329Device.SaveMouseBackup(path, new[] { 6, 10, 1 }, 15);
            Assert.False(Ch9329Device.RestoreBackup(path, _ => true, _ => false));
            Assert.True(File.Exists(path));
            Assert.True(Ch9329Device.RestoreBackup(path, m => m[2] == 1, speed => speed == 15));
            Assert.False(File.Exists(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task PausedHoldReleasesAndOnlyResumesAfterGateOpens()
    {
        using var entered = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        int paused = 0, presses = 0;
        var context = new InputExecutionContext(ct => { if (Volatile.Read(ref paused) != 0) resume.Wait(ct); },
            (_, _) => { }, () => Volatile.Read(ref paused) != 0);
        var task = Task.Run(() => InputExecutionContext.Hold(60_000,
            () => { Interlocked.Increment(ref presses); entered.Set(); }, () => released.Set(), context, cancel.Token));
        try
        {
            Assert.True(entered.Wait(2000));
            Volatile.Write(ref paused, 1);
            Assert.True(released.Wait(2000));
            Assert.Equal(1, Volatile.Read(ref presses));
            Volatile.Write(ref paused, 0); resume.Set();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref presses) == 2, 2000));
        }
        finally
        {
            cancel.Cancel(); resume.Set();
            try { await task.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }
        }
    }
}
