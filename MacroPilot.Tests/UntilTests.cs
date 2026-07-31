using MacroPilot.Models;
using Xunit;
using static MacroPilot.Tests.Harness;

namespace MacroPilot.Tests;

/// <summary>「重复直到条件满足」（do-while）：每趟执行完判定停止条件，满足即止；趟数/时长上限保护。</summary>
public class UntilTests
{
    /// <summary>给动作挂"直到条件满足"模式：cond 为停止条件，max/timeout 为保护上限（0=不限）。</summary>
    private static MacroStep Until(MacroStep s, ConditionItem cond, int max = 0, int timeoutMs = 0, int delayMs = 0)
    {
        s.RepeatUntil = true;
        s.UntilConditions.Add(cond);
        s.UntilMaxCount = max;
        s.UntilTimeoutMs = timeoutMs;
        s.RepeatDelayMs = delayMs;
        return s;
    }

    private static void MakeUntilSatisfied(MacroStep s)
    {
        foreach (var it in s.UntilConditions) it.Invert = false;   // Never（全天取反）→ 满足
    }

    [Fact]
    public void 停止条件一开始就满足则只执行一趟()
    {
        var s = Until(Key("a"), Always());
        var r = Run(Plan(s));
        Assert.Equal(new[] { "a" }, r.Calls);
        Assert.True(r.LogHas("停止条件已满足"));
    }

    [Fact]
    public void 停止条件不满足时执行到趟数上限()
    {
        var s = Until(Key("a"), Never(), max: 3);
        var r = Run(Plan(s));
        Assert.Equal(new[] { "a", "a", "a" }, r.Calls);
        Assert.True(r.LogHas("已达 3 趟上限"));
    }

    [Fact]
    public void 中途满足即停()
    {
        var s = Until(Key("a"), Never(), max: 10, delayMs: 30);
        var r = Run(Plan(s), onLog: (_, _, msg) =>
        {
            if (msg.Contains("停止条件检查 第 2 趟")) MakeUntilSatisfied(s);
        });
        Assert.Equal(new[] { "a", "a", "a" }, r.Calls);   // 第 3 趟末检查时已满足
        Assert.True(r.LogHas("停止条件已满足"));
    }

    [Fact]
    public void 时长上限到点后结束()
    {
        var s = Until(Key("a"), Never(), timeoutMs: 250, delayMs: 50);
        var r = Run(Plan(s), timeoutMs: 8000);
        Assert.True(r.Calls.Count >= 1);
        Assert.True(r.LogHas("毫秒时限"));
    }

    [Fact]
    public void 模式开启但无有效停止条件时退回固定趟数()
    {
        var s = Key("a");
        s.RepeatUntil = true;        // 有开关无条件：不该变成无限循环
        s.RepeatCount = 2; s.RepeatDelayMs = 0;
        var r = Run(Plan(s));
        Assert.Equal(new[] { "a", "a" }, r.Calls);
    }

    [Fact]
    public void 每趟照常重新判定运行条件()
    {
        var s = Until(Key("a"), Never(), max: 2);
        s.CondNever();               // 运行条件永不满足 → 每趟都跳过本体，但停止条件仍在趟末判定
        var r = Run(Plan(s));
        Assert.Empty(r.Calls);
        Assert.True(r.LogHas("已达 2 趟上限"));
    }

    [Fact]
    public void 组合动作同样支持直到条件满足()
    {
        var g = Until(Group(Key("a"), Key("b")), Never(), max: 2);
        var r = Run(Plan(g));
        Assert.Equal(new[] { "a", "b", "a", "b" }, r.Calls);
    }

    [Fact]
    public void 趟内产生跳转立即结束重复()
    {
        var c = Key("c");
        var j = Until(JumpTo(c), Never(), max: 5);
        var r = Run(Plan(j, Key("b"), c));
        Assert.Equal(new[] { "c" }, r.Calls);   // 跳转只执行一趟就生效，b 被跳过
        Assert.False(r.LogHas("已达 5 趟上限"));
    }

    [Fact]
    public void 克隆深拷贝停止条件()
    {
        var s = Until(Key("a"), Never(), max: 4, timeoutMs: 9000);
        var c = s.Clone();
        Assert.NotSame(s.UntilConditions, c.UntilConditions);
        Assert.NotSame(s.UntilConditions[0], c.UntilConditions[0]);
        Assert.Single(c.UntilConditions);
        Assert.True(c.HasUntilCondition);
    }
}
