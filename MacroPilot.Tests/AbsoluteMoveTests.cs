using System;
using System.Threading;
using System.Threading.Tasks;
using MacroPilot.Input;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

public sealed class AbsoluteMoveTests
{
    [Fact]
    public void ExternalEventOverwrittenByOwnPacketStillStopsMovement()
    {
        (int X, int Y) p = (0, 0); long epoch = 0; int sends = 0;
        var controller = new AbsoluteMoveController(() => p,
            (x, y) => { sends++; epoch++; p = (x, y); }, _ => { }, externalEpoch: () => epoch);
        Assert.Throws<InputSafetyException>(() => controller.MoveTo(100, 100, default));
        Assert.Equal((100, 100), p); Assert.Equal(1, sends);
    }

    [Fact]
    public void ExternalEventDuringCheckpointStopsBeforeSendingEvenIfCursorReturns()
    {
        long epoch = 0; int sends = 0;
        var controller = new AbsoluteMoveController(() => (0, 0), (_, _) => sends++,
            _ => epoch++, externalEpoch: () => epoch);
        Assert.Throws<InputSafetyException>(() => controller.MoveTo(100, 100, default));
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task InterferenceDuringDragStartupWaitStopsAndReleasesButton()
    {
        var backend = new FakeBackend { InterfereAfterMouseDown = true };
        var runner = new MacroRunner(backend); string? reason = null;
        runner.Finished += r => reason = r;
        var plan = new MacroPlan();
        plan.Steps.Add(new MacroStep { Type = "MouseDrag", X = 100, Y = 100 });
        runner.Start(plan, 0);
        try { await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { runner.Stop(); }
        Assert.Equal("Stopped", reason);
        Assert.Equal(new[] { "move:100,100", "down:Left", "up:Left" }, backend.Calls);
    }

    [Fact]
    public async Task CoordinateClickDoesNotClickAfterPositionBecomesUntrusted()
    {
        var backend = new FakeBackend { RejectPositionVerification = true };
        var runner = new MacroRunner(backend); string? reason = null;
        runner.Finished += r => reason = r;
        var plan = new MacroPlan();
        plan.Steps.Add(new MacroStep { Type = "MouseClickAt", X = 100, Y = 100 });
        runner.Start(plan, 0);
        try { await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { runner.Stop(); }
        Assert.Equal("Stopped", reason);
        Assert.Equal(new[] { "move:100,100" }, backend.Calls);
    }

    [Fact]
    public void ArrivesAndDetectsInterferenceBetweenWaypoints()
    {
        (int X, int Y) p = (0, 0); long now = 0; int sends = 0;
        var controller = new AbsoluteMoveController(() => p, (x, y) => { sends++; p = (x, y); },
            _ => { }, _ => now++, () => now);
        controller.MoveTo(-900, 500, default);
        Assert.Equal((-900, 500), p);
        p = (200, 200);
        Assert.Throws<InputSafetyException>(() => controller.MoveTo(20, 30, default));
        Assert.Equal(1, sends);
    }

    [Fact]
    public void MissingAbsoluteMovementTimesOutWithoutRetrying()
    {
        long now = 0; int sends = 0;
        var controller = new AbsoluteMoveController(() => (0, 0), (_, _) => sends++,
            _ => { }, _ => now++, () => now);
        Assert.Throws<InputSafetyException>(() => controller.MoveTo(100, 100, default));
        Assert.Equal(1, sends); Assert.InRange(now, 150, 151);
    }

    [Fact]
    public void DelayedAbsolutePacketIsAwaited()
    {
        (int X, int Y) p = (0, 0); long now = 0;
        var controller = new AbsoluteMoveController(() => p, (_, _) => { }, _ => { },
            _ => { if (++now == 10) p = (100, 100); }, () => now);
        controller.MoveTo(100, 100, default);
        Assert.InRange(now, 13, 14);
    }

    [Fact]
    public void MovementDuringPauseIsCheckedBeforeSending()
    {
        (int X, int Y) p = (0, 0); int checks = 0, sends = 0;
        var controller = new AbsoluteMoveController(() => p, (_, _) => sends++,
            _ => { if (++checks == 2) p = (20, 30); });
        Assert.Throws<InputSafetyException>(() => controller.MoveTo(100, 100, default));
        Assert.Equal(0, sends);
    }

    [Fact]
    public void CancellationInterruptsAbsoluteAcknowledgement()
    {
        using var cancel = new CancellationTokenSource();
        var controller = new AbsoluteMoveController(() => (0, 0), (_, _) => { },
            _ => { }, _ => cancel.Cancel());
        Assert.Throws<OperationCanceledException>(() => controller.MoveTo(100, 100, cancel.Token));
    }

    [Fact]
    public void RelativeOnePixelMoveMustNotBeSkipped()
    {
        (int X, int Y) p = (0, 0); long now = 0;
        var controller = new RelativeMoveController(() => p, (x, y) => p = (p.X + x, p.Y + y),
            _ => { }, _ => now++, () => now);
        controller.MoveTo(1, 0, default);
        Assert.Equal((1, 0), p);
    }

    [Fact]
    public void PartiallyDeliveredRelativePacketMustCompleteBeforeAnotherSend()
    {
        (int X, int Y) p = (0, 0); long now = 0; int sends = 0;
        var controller = new RelativeMoveController(() => p, (x, y) => { sends++; p = (x - 1, y); },
            _ => { }, _ => { if (++now == 20) p = (100, 0); }, () => now);
        controller.MoveTo(100, 0, default);
        Assert.Equal(1, sends); Assert.Equal((100, 0), p); Assert.True(now >= 23);
    }
}
