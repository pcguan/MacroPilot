using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;
using static MacroPilot.Tests.Harness;

namespace MacroPilot.Tests;

[Collection(PerformanceProbeCollection.Name)]
public class EnginePerformanceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryTimeLimitBoundsLongerPollingInterval(bool planCondition)
    {
        var step = Key("blocked");
        var plan = Plan(step);
        IRunCondition condition = planCondition ? plan : step;
        condition.CondNever();
        condition.RunConditionRetry = true;
        condition.RunConditionRetryTimeoutMs = 100;
        condition.RunConditionRetryIntervalMs = 2000;
        var runner = new MacroRunner(new FakeBackend());
        var elapsed = new Stopwatch();
        runner.Log += (_, message) =>
        {
            if (message.Contains("条件未满足") && !elapsed.IsRunning) elapsed.Start();
        };
        runner.Finished += _ => elapsed.Stop();
        Assert.Equal("Done", await RunAndStop(runner, plan));
        Assert.InRange(elapsed.ElapsedMilliseconds, 50, 1000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public async Task UntilTimeLimitDoesNotStartAnotherPassAfterLongInterval(double jitterMs)
    {
        var step = Key("once");
        step.RepeatUntil = true;
        step.UntilConditions.Add(Never());
        step.UntilTimeoutMs = 100;
        step.RepeatDelayMs = 2000;
        var backend = new FakeBackend();
        Assert.Equal("Done", await RunAndStop(new MacroRunner(backend), Plan(step), jitterMs: jitterMs));
        Assert.Equal(new[] { "once" }, backend.Calls);
    }

    [Theory]
    [InlineData("jump-body")]
    [InlineData("pre-run")]
    [InlineData("pre-condition")]
    public async Task PendingJumpLeavesUnboundedInnerOperation(string placement)
    {
        var target = Key("target");
        var jump = JumpTo(target, 1);
        MacroStep source;
        if (placement == "jump-body")
        {
            source = jump;
            source.LoopCount = 0;
            source.LoopDelayMs = 10;
        }
        else
        {
            source = Key("source");
            if (placement == "pre-run")
            {
                source.PreRunAction = jump;
                source.LoopCount = 0;
                source.LoopDelayMs = 10;
            }
            else
            {
                source.CondNever();
                source.PreCondAction = jump;
                source.RunConditionRetry = true;
                source.RunConditionRetryIntervalMs = 10;
            }
        }
        var backend = new FakeBackend();
        var runner = new MacroRunner(backend);
        Assert.Equal("Done", await RunAndStop(runner, Plan(source, target), 1500));
        Assert.Equal("target", backend.Calls[^1]);
    }

    [Fact]
    public async Task NestedGroupHookRestoresEnclosingProgressContext()
    {
        var step = Key("B");
        step.PreRunAction = Group(Key("hook"));
        var group = Group(Key("A"), step);
        group.LoopCount = 2;
        group.LoopDelayMs = 0;
        var runner = new MacroRunner(new FakeBackend());
        MacroStep? active = null;
        var statuses = new ConcurrentQueue<string>();
        runner.StepStateChanged += (current, running) => { if (running) active = current; };
        runner.Progress += (_, text) => { if (ReferenceEquals(active, step)) statuses.Enqueue(text); };
        Assert.Equal("Done", await RunAndStop(runner, Plan(group)));
        Assert.Contains(statuses, text => text.Contains("子 2/2") && text.Contains("组循环 2/2"));
    }

    [Fact]
    public void ExcessivelyLargeConditionExpressionIsRejectedBeforeEvaluation()
    {
        // A long flat expression builds its tree iteratively; validation must reject its
        // unbounded evaluation depth without evaluating it and risking StackOverflow.
        string expression = string.Join(" && ", Enumerable.Repeat("@1", 10000));
        Assert.NotNull(CondExpr.Validate(expression, 1));
    }

    [Theory]
    [InlineData("unary")]
    [InlineData("parentheses")]
    [InlineData("flat")]
    [InlineData("nodes")]
    public void ConditionExpressionBudgetCoversEveryRecursivePath(string shape)
    {
        string Balanced(int levels) => levels == 0 ? "@1" : $"({Balanced(levels - 1)}&&{Balanced(levels - 1)})";
        string expression = shape switch
        {
            "unary" => new string('!', CondExpr.MaxDepth + 1) + "@1",
            "parentheses" => new string('(', CondExpr.MaxDepth + 1) + "@1" + new string(')', CondExpr.MaxDepth + 1),
            "flat" => string.Join("&&", Enumerable.Repeat("@1", CondExpr.MaxDepth + 1)),
            _ => Balanced(10),
        };
        Assert.NotNull(CondExpr.Validate(expression, 1));
        Assert.Throws<FormatException>(() => CondExpr.Eval(expression, 1, _ => true));
    }

    [Fact]
    public void CachedExpressionReevaluatesConditionsOnEachPass()
    {
        var step = Key("A").Cond("Expr:@1", Never());
        step.RepeatCount = 2;
        step.RepeatDelayMs = 0;
        var result = Run(Plan(step), onLog: (_, _, message) =>
        {
            if (message.Contains("条件不满足，跳过动作")) MakeSatisfied(step);
        });
        Assert.Equal(new[] { "A" }, result.Calls);
    }

    [Fact]
    public void ExpressionCacheKeepsDifferentConditionListSizesIndependent()
    {
        var invalid = Key("A").Cond("Expr:@2", Never());
        var valid = Key("B").Cond("Expr:@2", Never(), Always());
        Assert.Equal(new[] { "B" }, Run(Plan(invalid, valid)).Calls);
    }

    [Fact]
    public void FiniteGroupStillFinishesItsConfiguredBodyLoopsBeforeJump()
    {
        var target = Key("target");
        var group = Group(JumpTo(target, 1), Key("tail"));
        group.LoopCount = 2;
        group.LoopDelayMs = 0;
        Assert.Equal(new[] { "tail", "tail", "target" }, Run(Plan(group, Key("skip"), target)).Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionSkippedTopLevelActionClearsItsExecutingState(bool group)
    {
        var step = group ? Group(Key("blocked")) : Key("blocked");
        step.CondNever();
        var runner = new MacroRunner(new FakeBackend());
        var transitions = new ConcurrentQueue<bool>();
        runner.StepStateChanged += (current, executing) =>
        {
            if (ReferenceEquals(current, step)) transitions.Enqueue(executing);
        };
        Assert.Equal("Done", await RunAndStop(runner, Plan(step)));
        Assert.Equal(new[] { true, false }, transitions.ToArray());
    }

    private static async Task<string> RunAndStop(MacroRunner runner, MacroPlan plan, int timeoutMs = 5000, double jitterMs = 0)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Finished += reason => done.TrySetResult(reason);
        try
        {
            runner.Start(plan, 0, jitterMs);
            return await done.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        finally
        {
            runner.Stop();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
