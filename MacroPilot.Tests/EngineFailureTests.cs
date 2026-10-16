using System.Threading;
using MacroPilot.Input;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;
using static MacroPilot.Tests.Harness;

namespace MacroPilot.Tests;

public partial class EngineRegressionTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void NestedInfiniteGroupsConsumeOutwardJump(bool infiniteOuter, bool infiniteInner, bool fromHook)
    {
        var target = Key("target");
        var source = fromHook ? Key("source") : JumpTo(target, 1);
        if (fromHook) source.SuccessAction = JumpTo(target, 1);
        var inner = Group(source);
        inner.LoopCount = infiniteInner ? 0 : 1; inner.LoopDelayMs = 10;
        var outer = Group(inner);
        outer.LoopCount = infiniteOuter ? 0 : 1; outer.LoopDelayMs = 10;
        var result = Run(Plan(outer, target), timeoutMs: 3000);
        Assert.Equal("Done", result.Reason);
        Assert.Equal("target", result.Calls[^1]);
    }

    [Fact]
    public void InfiniteWholePassRepeatStopsWhenItsHookJumps()
    {
        var target = Key("target");
        var source = Key("source");
        source.RepeatCount = 0; source.RepeatDelayMs = 0;
        source.SuccessAction = JumpTo(target, 1);
        source.CompleteAction = Key("must-not-complete");
        var result = Run(Plan(source, Key("skip"), target), timeoutMs: 3000);
        Assert.Equal(new[] { "source", "target" }, result.Calls);
    }

    [Fact]
    public void DisabledHookIsSkippedAndGroupHookHonorsUntil()
    {
        var source = Key("main");
        source.PreRunAction = Key("disabled"); source.PreRunAction.Disabled = true;
        var hook = Group(Key("hook"));
        hook.RepeatUntil = true; hook.UntilConditions.Add(Never()); hook.UntilMaxCount = 2;
        hook.RepeatDelayMs = 0; hook.CompleteAction = Key("done"); source.CompleteAction = hook;
        Assert.Equal(new[] { "main", "hook", "hook", "done" }, Run(Plan(source)).Calls);
    }

    [Theory]
    [InlineData("And", true)]
    [InlineData("Or", true)]
    [InlineData("Expr:!@1", false)]
    public void MissingImageCannotBecomeTrueThroughInversionOrBooleanLogic(string logic, bool invert)
    {
        var source = Key("must-not-run");
        source.RunConditionLogic = logic;
        source.RunConditions.Add(new ConditionItem { Type = "ImageMatch", Image = "not-base64", Invert = invert, RectW = 10, RectH = 10 });
        source.RunConditions.Add(Always());
        var result = Run(Plan(source, Key("must-not-continue")));
        Assert.Equal("Error", result.Reason);
        Assert.Empty(result.Calls);
        Assert.True(result.LogHas("无法观测"));
    }

    [Fact]
    public void ObservationErrorInHookDoesNotContinueHostAction()
    {
        var source = Key("main");
        source.PreRunAction = Key("hook");
        source.PreRunAction.RunConditions.Add(new ConditionItem { Type = "ImageMatch", Image = "broken", Invert = true });
        var result = Run(Plan(source));
        Assert.Empty(result.Calls);
        Assert.Equal("Error", result.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputSafetyFailureStopsWithoutRecoveryHooksAndReleasesInputs(bool inHook)
    {
        var backend = new UnsafeBackend();
        var runner = new MacroRunner(backend);
        var source = inHook ? Wait(1) : Key("boom");
        if (inHook) source.PreRunAction = Key("boom");
        source.FailAction = Key("fail"); source.CompleteAction = Key("complete");
        using var done = new ManualResetEventSlim();
        string reason = "";
        runner.Finished += r => { reason = r; done.Set(); };
        try
        {
            runner.Start(Plan(source, Key("next")), 0);
            Assert.True(done.Wait(3000));
        }
        finally { runner.Stop(); Assert.True(done.Wait(3000)); }
        Assert.Equal("Stopped", reason);
        Assert.Equal(1, backend.Calls);
        Assert.Equal(1, backend.Releases);
        Assert.Null(backend.Context);
    }

    private sealed class UnsafeBackend : IInputBackend
    {
        public bool IsOpen => true;
        public string Describe => "Safety fake";
        public int Calls, Releases;
        public InputExecutionContext? Context;
        public void SetExecutionContext(InputExecutionContext? context) => Context = context;
        public bool Open() => true;
        public void Close() { }
        public void Dispose() { }
        public void KeyTap(string key, byte modifier, double holdMs, CancellationToken ct = default)
        { Calls++; throw new InputSafetyException("manual takeover"); }
        public void MouseClick(string button, double holdMs, CancellationToken ct = default) { }
        public void MouseMove(int x, int y, CancellationToken ct = default) { }
        public void MouseDown(string button) { }
        public void MouseUp(string button) { }
        public void MouseWheel(int amount) { }
        public void ReleaseAll() => Releases++;
    }
}
