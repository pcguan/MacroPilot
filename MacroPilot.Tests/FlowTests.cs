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

    // ---- 重复次数 vs 执行次数（两个概念，别混）----
    // 执行次数(LoopCount) = 重复动作【本体】，条件判一次、监听走一遍；
    // 重复次数(RepeatCount) = 重复【整趟】，每趟都重新判条件、重新触发监听。

    [Fact]
    public void 重复次数把整趟重复若干遍()
    {
        var s = Key("a"); s.RepeatCount = 3; s.RepeatDelayMs = 0;
        var r = Run(Plan(s));
        Assert.Equal(new[] { "a", "a", "a" }, r.Calls);
    }

    [Fact]
    public void 执行次数与重复次数相乘()
    {
        var s = Key("a");
        s.LoopCount = 2; s.LoopDelayMs = 0;      // 本体做 2 遍
        s.RepeatCount = 3; s.RepeatDelayMs = 0;  // 整趟来 3 趟
        var r = Run(Plan(s));
        Assert.Equal(6, r.Calls.Count);
    }

    [Fact]
    public void 重复次数每趟都会重新判定运行条件()
    {
        // 条件恒不满足 + 不重试：每趟都判、每趟都跳过，动作一次都不执行
        var s = Key("a"); s.CondNever();
        s.RepeatCount = 3; s.RepeatDelayMs = 0;
        var r = Run(Plan(s));
        Assert.Empty(r.Calls);
        int skipped = r.Logs.FindAll(l => l.Contains("条件不满足，跳过动作")).Count;
        Assert.Equal(3, skipped);   // 判了 3 次，而不是 1 次
    }

    [Fact]
    public void 执行次数只判一次条件()
    {
        // 对照组：本体重复 3 遍，条件只判一次 → 只有一条跳过日志
        var s = Key("a"); s.CondNever();
        s.LoopCount = 3; s.LoopDelayMs = 0;
        var r = Run(Plan(s));
        Assert.Empty(r.Calls);
        Assert.Single(r.Logs.FindAll(l => l.Contains("条件不满足，跳过动作")));
    }

    [Fact]
    public void 重复次数每趟都触发监听()
    {
        var s = Key("a");
        s.PreRunAction = Key("pre");
        s.CompleteAction = Key("done");
        s.RepeatCount = 2; s.RepeatDelayMs = 0;
        var r = Run(Plan(s));
        Assert.Equal(new[] { "pre", "a", "done", "pre", "a", "done" }, r.Calls);
    }

    [Fact]
    public void 执行次数不会重复触发监听()
    {
        var s = Key("a");
        s.PreRunAction = Key("pre");
        s.CompleteAction = Key("done");
        s.LoopCount = 2; s.LoopDelayMs = 0;
        var r = Run(Plan(s));
        Assert.Equal(new[] { "pre", "a", "a", "done" }, r.Calls);   // 监听各一次，本体两次
    }

    [Fact]
    public void 组合也支持整趟重复()
    {
        var g = Group(Key("x"), Key("y"));
        g.RepeatCount = 2; g.RepeatDelayMs = 0;
        var r = Run(Plan(g));
        Assert.Equal(new[] { "x", "y", "x", "y" }, r.Calls);
    }

    [Fact]
    public void 重复期间产生跳转就不再重复()
    {
        // 跳转优先：否则"重复 5 次"会把跳转困在原地
        var a = Key("a");
        var j = JumpTo(a, 1); j.RepeatCount = 5; j.RepeatDelayMs = 0;
        var r = Run(Plan(a, j));
        Assert.Equal(new[] { "a", "a" }, r.Calls);
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

    // ---- 跳转绑定动作身份（v0.4.1）----

    [Fact]
    public void 在目标之前插入动作后跳转仍指向同一个动作()
    {
        // 用户场景：原本"跳转到动作 3"，之后在动作 3 前面插了一个新动作，
        // 旧的动作 3 变成了动作 4——跳转应当自动跟到 4，而不是还指着第 3 位。
        var a = Key("a"); var b = Key("b"); var c = Key("c");
        var plan = Plan(a, b, c, JumpTo(c, 1));
        plan.Steps.Insert(1, Key("x"));       // 在 b 之前插入 → c 从第 3 位变成第 4 位

        var r = Run(plan);
        // 期望：a x b c → 跳回 c → c 再跑一次 → 结束
        Assert.Equal(new[] { "a", "x", "b", "c", "c" }, r.Calls);
    }

    [Fact]
    public void 删除目标之前的动作后跳转仍指向同一个动作()
    {
        var a = Key("a"); var b = Key("b"); var c = Key("c");
        var plan = Plan(a, b, c, JumpTo(c, 1));
        plan.Steps.Remove(b);                 // c 从第 3 位变成第 2 位

        var r = Run(plan);
        Assert.Equal(new[] { "a", "c", "c" }, r.Calls);
    }

    [Fact]
    public void 重新排序后跳转仍指向同一个动作()
    {
        var a = Key("a"); var b = Key("b"); var c = Key("c");
        var plan = Plan(a, b, c, JumpTo(a, 1));
        plan.Steps.Remove(a); plan.Steps.Insert(2, a);   // a 挪到第 3 位：b c a Jump

        var r = Run(plan);
        Assert.Equal(new[] { "b", "c", "a", "a" }, r.Calls);
    }

    [Fact]
    public void 目标被删除后跳转不生效也不乱跳()
    {
        var a = Key("a"); var c = Key("c");
        var plan = Plan(a, c, JumpTo(c, 5));
        plan.Steps.Remove(c);                 // 目标没了

        var r = Run(plan);
        Assert.Equal(new[] { "a" }, r.Calls);
        Assert.Equal("Done", r.Reason);
    }

    [Fact]
    public void 组合内的跳转同样按身份绑定()
    {
        var a = Key("a");
        var g = Group(Key("x"), JumpTo(a, 1));
        var plan = Plan(a, g);
        plan.Steps.Insert(0, Key("head"));    // a 从第 1 位变成第 2 位

        var r = Run(plan);
        Assert.Equal(new[] { "head", "a", "x", "a", "x" }, r.Calls);
    }

    [Fact]
    public void 只有序号的旧存档跳转仍可用()
    {
        // 旧数据没有 JumpTargetId，运行时回退按序号定位
        var r = Run(Plan(Key("a"), Key("b"), Jump(1, 1)));
        Assert.Equal(new[] { "a", "b", "a", "b" }, r.Calls);
    }

    [Fact]
    public void 克隆保留身份于是运行副本的跳转仍然有效()
    {
        // 方案运行的是克隆副本：Id 必须一起克隆，否则跳转在副本里找不到目标
        var a = Key("a");
        var plan = Plan(a, JumpTo(a, 1));
        var copy = Plan(plan.Steps[0].Clone(), plan.Steps[1].Clone());
        var r = Run(copy);
        Assert.Equal(new[] { "a", "a" }, r.Calls);
    }

    [Fact]
    public void 换身份后副本与原件互不影响()
    {
        var a = Key("a");
        var dup = a.Clone();
        Assert.Equal(a.Id, dup.Id);      // 克隆保持身份
        dup.RenewId();
        Assert.NotEqual(a.Id, dup.Id);   // 粘贴时换新身份，跳转不会同时指向两个
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
