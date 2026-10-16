using System;
using System.Diagnostics;
using System.Threading;
using MacroPilot.Input;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;
using static MacroPilot.Tests.Harness;

namespace MacroPilot.Tests;

public class PauseExecutionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlanAndStepRetryBudgetsDoNotAdvanceWhilePaused(bool planCondition)
    {
        var step = Key("blocked");
        var plan = Plan(step);
        IRunCondition condition = planCondition ? plan : step;
        condition.CondNever(); condition.RunConditionRetry = true;
        condition.RunConditionRetryIntervalMs = 20; condition.RunConditionRetryTimeoutMs = 350;
        var runner = new MacroRunner(new FakeBackend());
        using var paused = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        int requested = 0;
        runner.Log += (_, message) =>
        {
            if (message.Contains("条件未满足") && Interlocked.Exchange(ref requested, 1) == 0)
            { runner.Pause(); paused.Set(); }
        };
        runner.Finished += _ => done.Set();
        try
        {
            runner.Start(plan, 0);
            Assert.True(paused.Wait(3000));
            Assert.False(done.Wait(500), "A paused retry must not exhaust its budget and finish.");
            var afterResume = Stopwatch.StartNew();
            runner.Resume();
            Assert.True(done.Wait(3000));
            Assert.True(afterResume.ElapsedMilliseconds >= 250, "Most of the active-time budget must remain after resume.");
        }
        finally { runner.Stop(); Assert.True(done.Wait(3000)); }
    }

    [Fact]
    public void RunnerProvidesPauseAwareContextForWholeTextOperation()
    {
        using var firstCharacter = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        var backend = new TextBackend(firstCharacter);
        var runner = new MacroRunner(backend);
        runner.Finished += _ => done.Set();
        try
        {
            runner.Start(Plan(new MacroStep { Type = "TextInput", Text = "abcd", TextCharDelayMs = 100 }), 0);
            Assert.True(firstCharacter.Wait(3000));
            runner.Pause();
            Thread.Sleep(100); // let a character already at the checkpoint settle
            int count = Volatile.Read(ref backend.Characters);
            Assert.False(done.Wait(200));
            Assert.Equal(count, Volatile.Read(ref backend.Characters));
            runner.Resume();
            Assert.True(done.Wait(3000));
            Assert.Equal(4, backend.Characters);
            Assert.Null(backend.Context);
        }
        finally { runner.Stop(); Assert.True(done.Wait(3000)); }
    }

    private sealed class TextBackend : IInputBackend
    {
        private readonly ManualResetEventSlim _first;
        public TextBackend(ManualResetEventSlim first) => _first = first;
        public InputExecutionContext? Context;
        public int Characters;
        public bool IsOpen => true;
        public bool SupportsUnicodeText => true;
        public string Describe => "Context fake";
        public bool Open() => true;
        public void Close() { }
        public void Dispose() { }
        public void SetExecutionContext(InputExecutionContext? context) => Context = context;
        public void TypeText(string text, double charDelayMs, CancellationToken ct = default)
        {
            foreach (char unused in text)
            {
                Context!.Checkpoint(ct);
                Interlocked.Increment(ref Characters); _first.Set();
                Context.Delay(charDelayMs, ct);
            }
        }
        public void KeyTap(string key, byte modifier, double holdMs, CancellationToken ct = default) { }
        public void MouseClick(string button, double holdMs, CancellationToken ct = default) { }
        public void MouseMove(int x, int y, CancellationToken ct = default) { }
        public void MouseDown(string button) { }
        public void MouseUp(string button) { }
        public void MouseWheel(int amount) { }
        public void ReleaseAll() { }
    }
}
