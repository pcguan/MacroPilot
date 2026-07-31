using MacroPilot.Models;
using Xunit;
using static MacroPilot.Tests.Harness;

namespace MacroPilot.Tests;

/// <summary>
/// 事件监听 7 个挂点（v0.3.1）：触发时机、顺序、以及"条件类挂点只在设了运行条件时才触发"。
/// 这些是肉眼最难查的部分——挂点漏触发/多触发/顺序错都不会报错，只会让方案行为微妙地不对。
/// </summary>
public class HookTests
{
    [Fact]
    public void 无运行条件时只跑运行类挂点且顺序正确()
    {
        var s = Key("main");
        s.PreCondAction = Key("preCond");
        s.CondSuccessAction = Key("condOk");
        s.CondFailAction = Key("condFail");
        s.PreRunAction = Key("preRun");
        s.SuccessAction = Key("ok");
        s.FailAction = Key("fail");
        s.CompleteAction = Key("done");

        var r = Run(Plan(s));
        // 没设运行条件 → 条件类三个挂点一律不触发（判定恒过，触发只会刷屏）
        Assert.Equal(new[] { "preRun", "main", "ok", "done" }, r.Calls);
    }

    [Fact]
    public void 设了条件且判定通过时的完整顺序()
    {
        var s = Key("main"); s.CondAlways();
        s.PreCondAction = Key("preCond");
        s.CondSuccessAction = Key("condOk");
        s.CondFailAction = Key("condFail");
        s.PreRunAction = Key("preRun");
        s.SuccessAction = Key("ok");
        s.CompleteAction = Key("done");

        var r = Run(Plan(s));
        Assert.Equal(new[] { "preCond", "condOk", "preRun", "main", "ok", "done" }, r.Calls);
    }

    [Fact]
    public void 条件判定失败时跑条件前判断失败后与结束后()
    {
        var s = Key("main"); s.CondNever();
        s.PreCondAction = Key("preCond");
        s.CondSuccessAction = Key("condOk");
        s.CondFailAction = Key("condFail");
        s.PreRunAction = Key("preRun");
        s.SuccessAction = Key("ok");
        s.CompleteAction = Key("done");

        var r = Run(Plan(s));
        // 动作本体、运行前、成功 不该跑；「运行结束后」按定夺的语义：条件跳过也算一次结束，照样触发
        Assert.Equal(new[] { "preCond", "condFail", "done" }, r.Calls);
    }

    [Fact]
    public void 动作执行失败时跑失败后与结束后而不跑成功后()
    {
        var s = Key("boom");
        s.PreRunAction = Key("preRun");
        s.SuccessAction = Key("ok");
        s.FailAction = Key("fail");
        s.CompleteAction = Key("done");

        var r = Run(Plan(s, Key("next")), failOnKey: "boom");
        Assert.Equal(new[] { "preRun", "boom", "fail", "done", "next" }, r.Calls);
        Assert.Equal("Done", r.Reason);   // 单个动作失败不该中断整个方案
    }

    [Fact]
    public void 运行前挂点在动作自身循环之外只触发一次()
    {
        var s = Key("main");
        s.LoopCount = 3;
        s.LoopDelayMs = 0;
        s.PreRunAction = Key("preRun");
        s.CompleteAction = Key("done");

        var r = Run(Plan(s));
        Assert.Equal(new[] { "preRun", "main", "main", "main", "done" }, r.Calls);
    }

    [Fact]
    public void 组合也支持运行前与成功结束挂点()
    {
        var g = Group(Key("x"), Key("y"));
        g.PreRunAction = Key("preRun");
        g.SuccessAction = Key("ok");
        g.CompleteAction = Key("done");

        var r = Run(Plan(g));
        Assert.Equal(new[] { "preRun", "x", "y", "ok", "done" }, r.Calls);
    }

    [Fact]
    public void 监听动作自身可以是组合并能再挂自己的监听()
    {
        var inner = Group(Key("h1"), Key("h2"));
        inner.CompleteAction = Key("h3");           // 监听里再挂监听（递归）
        var s = Key("main");
        s.SuccessAction = inner;

        var r = Run(Plan(s));
        Assert.Equal(new[] { "main", "h1", "h2", "h3" }, r.Calls);
    }

    [Fact]
    public void 监听动作自身也有运行条件不满足则跳过它()
    {
        var hook = Key("hook"); hook.CondNever();
        var s = Key("main");
        s.SuccessAction = hook;

        var r = Run(Plan(s));
        Assert.Equal(new[] { "main" }, r.Calls);
    }

    [Fact]
    public void 监听动作支持自身循环()
    {
        var hook = Key("hook");
        hook.LoopCount = 2; hook.LoopDelayMs = 0;
        var s = Key("main");
        s.CompleteAction = hook;

        var r = Run(Plan(s));
        Assert.Equal(new[] { "main", "hook", "hook" }, r.Calls);
    }

    [Fact]
    public void 被禁用的动作整步跳过且不触发其任何监听()
    {
        var s = Key("main");
        s.Disabled = true;
        s.PreRunAction = Key("preRun");
        s.CompleteAction = Key("done");

        var r = Run(Plan(Key("a"), s, Key("b")));
        Assert.Equal(new[] { "a", "b" }, r.Calls);
    }

    [Fact]
    public void 条件重复检查到上限后走的是判断失败后挂点()
    {
        var s = Key("main"); s.CondNever();
        s.RunConditionRetry = true;
        s.RunConditionRetryIntervalMs = 30;
        s.RunConditionRetryMax = 2;
        s.CondSuccessAction = Key("condOk");
        s.CondFailAction = Key("condFail");

        var r = Run(Plan(s));
        Assert.Equal(new[] { "condFail" }, r.Calls);
    }

    [Fact]
    public void 挂点列表按生命周期顺序枚举()
    {
        var s = Key("main");
        s.CompleteAction = Key("done");     // 故意乱序赋值
        s.PreRunAction = Key("preRun");
        s.CondFailAction = Key("condFail");
        s.PreCondAction = Key("preCond");
        s.FailAction = Key("fail");
        s.CondSuccessAction = Key("condOk");
        s.SuccessAction = Key("ok");

        var kinds = new System.Collections.Generic.List<string>();
        foreach (var (kind, _) in s.HookList()) kinds.Add(kind);
        Assert.Equal(new[] { "条件前", "条件成立", "条件不成立", "运行前", "成功", "失败", "结束" }, kinds);
    }
}
