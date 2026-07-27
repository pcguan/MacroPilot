using MacroPilot.Models;
using Xunit;
using static MacroPilot.Tests.Harness;

namespace MacroPilot.Tests;

/// <summary>运行条件：判定本身、取反、开放边界、以及"不满足时重复检查"（v0.3.2）。</summary>
public class RunConditionTests
{
    [Fact]
    public void 无条件的动作照常执行()
    {
        var r = Run(Plan(Key("a")));
        Assert.Equal(new[] { "a" }, r.Calls);
    }

    [Fact]
    public void 条件满足则执行()
    {
        var s = Key("a"); s.CondAlways();
        var r = Run(Plan(s));
        Assert.Equal(new[] { "a" }, r.Calls);
    }

    [Fact]
    public void 条件不满足则跳过该动作但方案继续()
    {
        var s = Key("b"); s.CondNever();
        var r = Run(Plan(Key("a"), s, Key("c")));
        Assert.Equal(new[] { "a", "c" }, r.Calls);
        Assert.Equal("Done", r.Reason);
        Assert.True(r.LogHas("条件不满足"));
    }

    [Fact]
    public void 只设开始时间是开放边界()
    {
        // 只设"00:00 之后"→ 任何时刻都满足
        var s = Key("a");
        s.RunConditionType = "TimeRange";
        s.RunConditionStartMinute = 0;
        s.RunConditionEndMinute = null;
        var r = Run(Plan(s));
        Assert.Equal(new[] { "a" }, r.Calls);
    }

    [Fact]
    public void 类型有值但时间两侧都为空视为未设条件()
    {
        var s = Key("a");
        s.RunConditionType = "TimeRange";
        s.RunConditionStartMinute = null;
        s.RunConditionEndMinute = null;
        Assert.False(RunCondition.Has(s));   // 判定口径：两级共用，避免一边认为有一边认为无
        var r = Run(Plan(s));
        Assert.Equal(new[] { "a" }, r.Calls);
    }

    // ---- 重复检查（v0.3.2）----

    [Fact]
    public void 未勾选重复检查时不满足即跳过不重试()
    {
        var s = Key("a"); s.CondNever();
        s.RunConditionRetry = false;
        var r = Run(Plan(s));
        Assert.Empty(r.Calls);
        Assert.False(r.LogHas("重新检查"));
    }

    [Fact]
    public void 勾选重复检查达到次数上限后跳过()
    {
        var s = Key("a"); s.CondNever();
        s.RunConditionRetry = true;
        s.RunConditionRetryIntervalMs = 50;
        s.RunConditionRetryMax = 3;
        var r = Run(Plan(s));
        Assert.Empty(r.Calls);                                   // 始终不满足 → 最终跳过
        Assert.True(r.LogHas("重新检查"));
        Assert.True(r.LogHas("重复检查 3 次仍未满足"));           // 次数用满
    }

    [Fact]
    public void 重复检查期间条件转为满足则继续执行()
    {
        var s = Key("a"); s.CondNever();
        s.RunConditionRetry = true;
        s.RunConditionRetryIntervalMs = 50;
        s.RunConditionRetryMax = 20;
        // 看到"开始重新检查"的日志后，把条件就地改成满足——模拟"等到目标出现"。
        var r = Run(Plan(s), onLog: (_, _, msg) =>
        {
            if (msg.Contains("重新检查")) MakeSatisfied(s);
        });
        Assert.Equal(new[] { "a" }, r.Calls);
        Assert.True(r.LogHas("条件已满足"));
    }

    [Fact]
    public void 重复检查的次数上限为零表示不限次()
    {
        var s = Key("a"); s.CondNever();
        s.RunConditionRetry = true;
        s.RunConditionRetryIntervalMs = 30;
        s.RunConditionRetryMax = 0;   // 不限 → 必须靠条件转满足才能结束，否则测试会超时
        var r = Run(Plan(s), timeoutMs: 8000, onLog: (_, _, msg) =>
        {
            if (msg.Contains("重新检查")) MakeSatisfied(s);
        });
        Assert.Equal(new[] { "a" }, r.Calls);
    }

    [Fact]
    public void 组合的条件不满足则整组跳过()
    {
        var g = Group(Key("x"), Key("y")); g.CondNever();
        var r = Run(Plan(Key("a"), g, Key("b")));
        Assert.Equal(new[] { "a", "b" }, r.Calls);
    }
}
