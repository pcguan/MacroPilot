using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MacroPilot.Input;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;
using Xunit.Abstractions;
using static MacroPilot.Tests.Harness;

namespace MacroPilot.Tests;

// Run these separately from other collections: timing evidence should not include the
// intentional sleeps, image-search workers and process launches in unrelated tests.
[CollectionDefinition(PerformanceProbeCollection.Name, DisableParallelization = true)]
public sealed class PerformanceProbeCollection
{
    public const string Name = "Performance probes";
}

[Collection(PerformanceProbeCollection.Name)]
public sealed partial class PerformanceProbeTests
{
    private readonly ITestOutputHelper _output;
    public PerformanceProbeTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(0)]
    [InlineData(50000)]
    public async Task ThreeSecondWaitUsesExecutionTimeAfterAnEventBurst(int burstActions)
    {
        await RunProbe(Plan(Key("A"), Wait(20), Key("B"))); // JIT and worker warm-up
        var plan = Plan();
        if (burstActions > 0)
        {
            var burst = Key("C");
            burst.RepeatCount = burstActions;
            burst.RepeatDelayMs = 0;
            plan.Steps.Add(burst);
        }
        plan.Steps.Add(Key("F9"));
        plan.Steps.Add(Wait(3000));
        plan.Steps.Add(Key("F10"));

        var result = await RunProbe(plan);
        double waitMs = Stopwatch.GetElapsedTime(result.MarkerBefore, result.MarkerAfter).TotalMilliseconds;
        _output.WriteLine($"burst_actions={burstActions}; actual_wait_ms={waitMs:F2}; {result.Describe()}");
        Assert.Equal("Done", result.Reason);
        Assert.Equal(burstActions + 2, result.Calls.Length);
        // This is a seconds-scale regression check (the reported bug was 3s -> 10s),
        // not a guarantee about sub-millisecond Windows scheduling accuracy.
        Assert.InRange(waitMs, 2950, 6000);
        Assert.True(result.ProgressEvents < burstActions * 2L + 500,
            "A wait's final spin must not emit an unbounded number of progress events.");
    }

    [Fact]
    public async Task NestedHooksRepeatsConditionsAndJumpsMatchAnIndependentSequence()
    {
        await RunProbe(Plan(Key("A")));
        var leaf = Key("A");
        leaf.LoopCount = 2; leaf.LoopDelayMs = 0;
        leaf.RepeatCount = 2; leaf.RepeatDelayMs = 0;
        leaf.PreRunAction = Key("B"); leaf.SuccessAction = Key("C"); leaf.CompleteAction = Key("D");
        leaf.CondAlways();
        var skipped = Key("E").CondNever(); skipped.CompleteAction = Key("F");
        var inner = Group(leaf, skipped);
        inner.LoopCount = 2; inner.LoopDelayMs = 0;
        inner.PreRunAction = Key("G"); inner.SuccessAction = Key("H"); inner.CompleteAction = Key("I");
        var middle = Group(inner);
        middle.RepeatCount = 2; middle.RepeatDelayMs = 0;
        middle.PreRunAction = Key("J"); middle.SuccessAction = Key("K"); middle.CompleteAction = Key("L");
        var disabled = Key("P"); disabled.Disabled = true; disabled.CompleteAction = Key("Z");
        var outer = Group(middle, disabled);
        outer.LoopCount = 2; outer.LoopDelayMs = 0;
        outer.PreRunAction = Key("M"); outer.SuccessAction = Key("N"); outer.CompleteAction = Key("O");
        var plan = Plan(outer, JumpTo(outer, 1), Key("Q"));
        plan.LoopCount = 50;

        // Expected order is explicit business semantics, built without walking the
        // plan or calling any execution helper from MacroRunner.
        string[] leafExpected = { "B", "A", "A", "C", "B", "A", "A", "C", "D", "F" };
        var innerExpected = new[] { "G" }.Concat(Enumerable.Range(0, 2).SelectMany(_ => leafExpected))
            .Concat(new[] { "H", "I" }).ToArray();
        var middleExpected = Enumerable.Range(0, 2)
            .SelectMany(_ => new[] { "J" }.Concat(innerExpected).Concat(new[] { "K" }))
            .Concat(new[] { "L" }).ToArray();
        var outerExpected = new[] { "M" }.Concat(Enumerable.Range(0, 2).SelectMany(_ => middleExpected))
            .Concat(new[] { "N", "O" }).ToArray();
        var expected = Enumerable.Range(0, 50)
            .SelectMany(_ => outerExpected.Concat(outerExpected).Concat(new[] { "Q" })).ToArray();

        var nested = await RunProbe(plan);
        var flat = await RunProbe(Plan(expected.Select(Key).ToArray()));
        _output.WriteLine($"nested: {nested.Describe()}");
        _output.WriteLine($"equivalent_flat: {flat.Describe()}");
        Assert.Equal("Done", nested.Reason);
        Assert.Equal("Done", flat.Reason);
        Assert.Equal(expected, nested.Calls);
        Assert.Equal(expected, flat.Calls);
        // Do not assert a fragile speed ratio: nested execution intentionally has
        // more condition/hook/log work. Save the measurements for comparison.
    }

    [Theory]
    [InlineData(100)]
    [InlineData(10000)]
    public async Task FrequentJumpsKeepTheirMeaningInLargePlans(int prefixSteps)
    {
        await RunProbe(Plan(Key("A")));
        var plan = Plan();
        for (int i = 0; i < prefixSteps; i++)
        {
            var disabled = Key("B"); disabled.Disabled = true;
            plan.Steps.Add(disabled);
        }
        var target = Key("A"); target.Alias = "jump-target";
        plan.Steps.Add(target);
        plan.Steps.Add(JumpTo(target, 2000));
        var result = await RunProbe(plan);
        double keySpanMs = Stopwatch.GetElapsedTime(result.FirstKeyAt, result.LastKeyAt).TotalMilliseconds;
        _output.WriteLine($"prefix_nodes={prefixSteps}; jumps=2000; jump_span_ms={keySpanMs:F2}; {result.Describe()}");
        Assert.Equal("Done", result.Reason);
        Assert.Equal(Enumerable.Repeat("A", 2001), result.Calls);
    }

    private static async Task<ProbeResult> RunProbe(MacroPlan plan, Action<MacroRunner>? configure = null)
    {
        var backend = new TimestampBackend();
        var runner = new MacroRunner(backend);
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new ProbeResult();
        long started = Stopwatch.GetTimestamp();
        runner.Log += (_, _) => result.LogEvents++;
        runner.ActBegin += _ => result.ActionBegins++;
        runner.ActEnd += (_, _) => result.ActionEnds++;
        runner.Progress += (_, _) => result.ProgressEvents++;
        runner.StepStateChanged += (_, _) => result.StepEvents++;
        configure?.Invoke(runner);
        runner.Finished += reason => done.TrySetResult(reason);
        try
        {
            runner.Start(plan, 0);
            result.Reason = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
            result.ElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            result.Calls = backend.Recorded.Calls.ToArray();
            result.MarkerBefore = backend.MarkerBefore;
            result.MarkerAfter = backend.MarkerAfter;
            result.FirstKeyAt = backend.FirstKeyAt;
            result.LastKeyAt = backend.LastKeyAt;
            return result;
        }
        finally
        {
            runner.Stop();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ProbeResult
    {
        public string Reason = "";
        public string[] Calls = Array.Empty<string>();
        public double ElapsedMs;
        public long LogEvents, ActionBegins, ActionEnds, ProgressEvents, StepEvents;
        public long MarkerBefore, MarkerAfter;
        public long FirstKeyAt, LastKeyAt;
        public string Describe() => $"run_ms={ElapsedMs:F2}; keys={Calls.Length}; logs={LogEvents}; " +
            $"action_begin={ActionBegins}; action_end={ActionEnds}; progress={ProgressEvents}; step_events={StepEvents}";
    }

    // Every output goes to the existing fake; no native keyboard, pointer, clipboard,
    // capture or serial API is invoked. Timestamps bracket the actual backend calls.
    private sealed class TimestampBackend : IInputBackend
    {
        public readonly FakeBackend Recorded = new();
        public long MarkerBefore, MarkerAfter;
        public long FirstKeyAt, LastKeyAt;
        public bool IsOpen => true;
        public string Describe => "Timestamp fake";
        public bool Open() => true;
        public void Close() { }
        public void Dispose() { }
        public void KeyTap(string key, byte modifier, double holdMs, CancellationToken ct = default)
        {
            long now = Stopwatch.GetTimestamp();
            if (FirstKeyAt == 0) FirstKeyAt = now;
            LastKeyAt = now;
            if (key == "F9") MarkerBefore = now;
            if (key == "F10") MarkerAfter = now;
            Recorded.KeyTap(key, modifier, holdMs, ct);
        }
        public void MouseClick(string button, double holdMs, CancellationToken ct = default) => Recorded.MouseClick(button, holdMs, ct);
        public void MouseMove(int x, int y, CancellationToken ct = default) => Recorded.MouseMove(x, y, ct);
        public void MouseDown(string button) => Recorded.MouseDown(button);
        public void MouseUp(string button) => Recorded.MouseUp(button);
        public void MouseWheel(int amount) => Recorded.MouseWheel(amount);
        public void ReleaseAll() => Recorded.ReleaseAll();
    }
}
