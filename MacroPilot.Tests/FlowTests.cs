using System.Threading;
using System.Threading.Tasks;
using MacroPilot.Input;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;
using static MacroPilot.Tests.Harness;

namespace MacroPilot.Tests;

/// <summary>方案内的动作交互：循环、跳转、组合嵌套、暂停/停止。</summary>
public class FlowTests
{
    // ---- 循环 ----

    [Fact]
    public void 动作自身循环执行指定次数()
    {
        var s = Key("a"); s.LoopCount = 3; s.LoopDelayMs = 0;
        var r = Run(Plan(s));
        Assert.Equal(new[] { "a", "a", "a" }, r.Calls);
    }

    [Fact]
    public void 方案级循环把整串动作重复若干轮()
    {
        var p = Plan(Key("a"), Key("b"));
        p.LoopCount = 2; p.LoopDelayMs = 0;
        var r = Run(p);
        Assert.Equal(new[] { "a", "b", "a", "b" }, r.Calls);
    }

    [Fact]
    public void 组合自身循环()
    {
        var g = Group(Key("x"), Key("y"));
        g.LoopCount = 2; g.LoopDelayMs = 0;
        var r = Run(Plan(g));
        Assert.Equal(new[] { "x", "y", "x", "y" }, r.Calls);
    }

    [Fact]
    public void 嵌套组合按深度优先顺序执行()
    {
        var inner = Group(Key("i1"), Key("i2"));
        var outer = Group(Key("o1"), inner, Key("o2"));
        var r = Run(Plan(Key("a"), outer, Key("b")));
        Assert.Equal(new[] { "a", "o1", "i1", "i2", "o2", "b" }, r.Calls);
    }

    [Fact]
    public void 组合内被禁用的子动作被跳过()
    {
        var bad = Key("skip"); bad.Disabled = true;
        var g = Group(Key("x"), bad, Key("y"));
        var r = Run(Plan(g));
        Assert.Equal(new[] { "x", "y" }, r.Calls);
    }

    // ---- 跳转 ----

    [Fact]
    public void 跳转是纯goto回到指定序号()
    {
        // a b jump(1,最多2次) c  →  a b a b a b c
        var r = Run(Plan(Key("a"), Key("b"), Jump(1, 2), Key("c")));
        Assert.Equal(new[] { "a", "b", "a", "b", "a", "b", "c" }, r.Calls);
    }

    [Fact]
    public void 跳转的最大重复次数是防死循环上限()
    {
        var r = Run(Plan(Key("a"), Jump(1, 3)));
        // 首次 a + 3 次回跳各一次 a = 4 次，之后跳转失效、顺序结束
        Assert.Equal(new[] { "a", "a", "a", "a" }, r.Calls);
        Assert.Equal("Done", r.Reason);
    }

    [Fact]
    public void 跳转次数按方案的每一轮各自重置()
    {
        var p = Plan(Key("a"), Jump(1, 1));
        p.LoopCount = 2; p.LoopDelayMs = 0;
        var r = Run(p);
        // 每轮：a + 回跳一次 a = 2 次；两轮共 4 次
        Assert.Equal(new[] { "a", "a", "a", "a" }, r.Calls);
    }

    [Fact]
    public void 跳转目标越界时忽略()
    {
        var r = Run(Plan(Key("a"), Jump(99, 1), Key("b")));
        Assert.Equal(new[] { "a", "b" }, r.Calls);
    }

    [Fact]
    public void 组合内部的跳转在顶层生效()
    {
        // 组合里放一个跳转：应在当前顶层步骤结束后跳回顶层第 1 个
        var g = Group(Key("x"), Jump(1, 1));
        var r = Run(Plan(Key("a"), g, Key("b")));
        Assert.Equal(new[] { "a", "x", "a", "x", "b" }, r.Calls);
    }

    [Fact]
    public void 监听里的跳转同样上报到顶层()
    {
        var s = Key("a");
        s.CompleteAction = Jump(1, 1);
        var r = Run(Plan(s, Key("b")));
        Assert.Equal(new[] { "a", "a", "b" }, r.Calls);
    }

    [Fact]
    public void 被禁用的跳转不生效()
    {
        var j = Jump(1, 5); j.Disabled = true;
        var r = Run(Plan(Key("a"), j, Key("b")));
        Assert.Equal(new[] { "a", "b" }, r.Calls);
    }

    // ---- 停止 / 暂停 ----

    [Fact]
    public void 停止后结束原因为Stopped()
    {
        var p = Plan(Wait(3000), Key("never"));
        var fake = new FakeBackend();
        var runner = new MacroRunner(fake);
        string reason = "";
        var done = new ManualResetEventSlim();
        runner.Finished += r => { reason = r; done.Set(); };
        runner.Start(p, 0);
        Thread.Sleep(200);
        runner.Stop();
        Assert.True(done.Wait(5000), "停止后应尽快结束");
        Assert.Equal("Stopped", reason);
        Assert.Empty(fake.Calls);            // 后面的动作不该再执行
    }

    [Fact]
    public void 无限循环可以被停止()
    {
        var s = Key("a"); s.LoopCount = 0; s.LoopDelayMs = 10;   // 0 = 无限
        var fake = new FakeBackend();
        var runner = new MacroRunner(fake);
        var done = new ManualResetEventSlim();
        string reason = "";
        runner.Finished += r => { reason = r; done.Set(); };
        runner.Start(Plan(s), 0);
        Thread.Sleep(200);
        runner.Stop();
        Assert.True(done.Wait(5000));
        Assert.Equal("Stopped", reason);
        Assert.NotEmpty(fake.Calls);
    }

    [Fact]
    public void 暂停期间等待动作不会被跳过()
    {
        // 历史 bug：暂停时秒表照走，恢复后剩余时间变负 → 等待被"跳过"。
        // 这里等待 900ms，中途暂停 700ms，总耗时必须显著大于 900ms 才说明暂停期间没在计时。
        var p = Plan(Wait(900), Key("after"));
        var fake = new FakeBackend();
        var runner = new MacroRunner(fake);
        var done = new ManualResetEventSlim();
        runner.Finished += _ => done.Set();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        runner.Start(p, 0);
        Thread.Sleep(150);
        runner.Pause();
        Thread.Sleep(700);
        runner.Resume();
        Assert.True(done.Wait(8000));
        sw.Stop();

        Assert.Equal(new[] { "after" }, fake.Calls);
        Assert.True(sw.ElapsedMilliseconds >= 1500,
            $"暂停期间不应计时：总耗时 {sw.ElapsedMilliseconds}ms，应 ≥1500ms(900 等待 + 700 暂停)");
    }

    [Fact]
    public void 方案级条件不满足时空转等待且可停止()
    {
        var p = Plan(Key("a"));
        p.CondNever();
        p.RunConditionRetryIntervalMs = 50;
        var fake = new FakeBackend();
        var runner = new MacroRunner(fake);
        var done = new ManualResetEventSlim();
        runner.Finished += _ => done.Set();
        runner.Start(p, 0);
        Thread.Sleep(300);
        runner.Stop();
        Assert.True(done.Wait(5000));
        Assert.Empty(fake.Calls);            // 条件一直不满足 → 一个动作都没跑
    }

    [Fact]
    public void 方案级条件的重复检查次数上限会结束运行()
    {
        var p = Plan(Key("a"));
        p.CondNever();
        p.RunConditionRetryIntervalMs = 30;
        p.RunConditionRetryMax = 3;          // 到上限即结束，不再无限等
        var r = Run(p, timeoutMs: 8000);
        Assert.Empty(r.Calls);
        Assert.True(r.LogHas("仍未满足"));
    }
}
