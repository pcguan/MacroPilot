using System;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using MacroPilot.Services;
using Xunit;
using static MacroPilot.Tests.Harness;

namespace MacroPilot.Tests;

// Boundary regressions discovered in the v0.6.22 review.
// FakeBackend records strings only. Every manually started runner is stopped in finally.
public partial class EngineRegressionTests
{
    [Fact]
    public async Task InfiniteNestedGroupCanExitByJumpToTopLevelTarget()
    {
        var target = Key("target");
        var jump = JumpTo(target, 1);
        var inner = Group(jump, Key("after-jump"));
        var outer = Group(inner);
        outer.LoopCount = 0;
        outer.LoopDelayMs = 20;
        var backend = new FakeBackend();
        var runner = new MacroRunner(backend);
        var jumpExecuted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new ConcurrentQueue<string>();
        runner.Log += (_, text) => logs.Enqueue(text);
        runner.StepStateChanged += (step, active) =>
        {
            if (ReferenceEquals(step, jump) && !active) jumpExecuted.TrySetResult(true);
        };
        runner.Finished += reason => done.TrySetResult(reason);
        string Diagnostics()
        {
            string calls;
            lock (backend.Calls) calls = string.Join(",", backend.Calls.TakeLast(20));
            return $"Running={runner.IsRunning}, Paused={runner.IsPaused}, calls=[{calls}], logs=[{string.Join(" | ", logs.TakeLast(20))}]";
        }
        try
        {
            runner.Start(Plan(outer, target), 0);
            // Separate worker scheduling from the behavior under test; async waits leave workers free.
            try { await jumpExecuted.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { Assert.Fail("Runner did not execute the jump. " + Diagnostics()); }
            string reason = "";
            try { reason = await done.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { Assert.Fail("Pending jump failed to leave its infinite enclosing group. " + Diagnostics()); }
            Assert.Equal("Done", reason);
            // A finite inner group completes its current children, then the infinite outer group exits.
            Assert.Equal(new[] { "after-jump", "target" }, backend.Calls);
        }
        finally
        {
            runner.Stop();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void UntilTimeLimitExcludesPausedTime()
    {
        var step = Wait(30);
        step.RepeatUntil = true;
        step.UntilConditions.Add(Never());
        step.UntilTimeoutMs = 600;
        step.RepeatDelayMs = 50;
        var backend = new FakeBackend();
        var runner = new MacroRunner(backend);
        using var paused = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        int requestedPause = 0;
        runner.Log += (_, message) =>
        {
            if (message.Contains("停止条件检查 第 1 趟") && Interlocked.Exchange(ref requestedPause, 1) == 0)
            {
                runner.Pause();
                paused.Set();
            }
        };
        runner.Finished += _ => done.Set();
        long afterResumeMs = 0;
        try
        {
            runner.Start(Plan(step), 0);
            Assert.True(paused.Wait(3000), "First iteration should enter the controlled pause.");
            Assert.True(runner.IsPaused);
            Thread.Sleep(900); // longer than the 600ms Until limit; must not consume its active-time budget
            var clock = Stopwatch.StartNew();
            runner.Resume();
            Assert.True(done.Wait(4000));
            afterResumeMs = clock.ElapsedMilliseconds;
        }
        finally
        {
            runner.Stop();
            Assert.True(done.Wait(3000), "Runner must stop during cleanup.");
        }
        Assert.True(afterResumeMs >= 400,
            $"Pausing after the first 30ms iteration must leave most of the 600ms budget; elapsed after resume={afterResumeMs}ms.");
    }

    [Fact]
    public void GroupHookHonorsRepeatCountLikeOrdinaryGroup()
    {
        var hook = Group(Key("hook"));
        hook.RepeatCount = 3;
        hook.RepeatDelayMs = 0;
        var step = Key("main");
        step.CompleteAction = hook;
        // Harness.Run always calls Stop and waits if its timeout is reached.
        var result = Run(Plan(step), timeoutMs: 3000);
        Assert.Equal(new[] { "main", "hook", "hook", "hook" }, result.Calls);
    }

    [Fact]
    public void FailureOnLastActionRemainsPausedUntilUserContinuesOrStops()
    {
        var backend = new FakeBackend { FailOnKey = "boom" };
        var runner = new MacroRunner(backend);
        using var paused = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        runner.Log += (_, message) => { if (message.Contains("自动暂停")) paused.Set(); };
        runner.Finished += _ => done.Set();
        bool finishedWhilePaused;
        try
        {
            // Wait ensures Start has published its Task before the intentional failure.
            runner.Start(Plan(Wait(30), Key("boom")), 0);
            Assert.True(paused.Wait(3000), "The failing final action should request automatic pause.");
            finishedWhilePaused = done.Wait(250);
        }
        finally
        {
            runner.Stop();
            Assert.True(done.Wait(3000), "Runner must stop during cleanup.");
        }
        Assert.False(finishedWhilePaused, "PauseOnFail must not immediately finish the plan when its last action fails.");
    }
}
