using System.Threading;
using MacroPilot.Models;
using MacroPilot.Services;
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
        Assert.True(r.LogHas("条件满足"));   // 判断【通过】也要留痕（含实际观测），不再静默
    }

    [Fact]
    public void 方案级条件满足时也记录判定日志()
    {
        var p = Plan(Key("a")); p.CondAlways();
        var r = Run(p);
        Assert.Equal(new[] { "a" }, r.Calls);
        Assert.True(r.LogHas("方案运行条件满足"));
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
    public void 重复检查的时间上限到点后跳过()
    {
        // 次数不限、只设时间上限：到点必须停下来跳过该动作，而不是无限等
        var s = Key("a"); s.CondNever();
        s.RunConditionRetry = true;
        s.RunConditionRetryIntervalMs = 20;
        s.RunConditionRetryMax = 0;            // 不限次数
        s.RunConditionRetryTimeoutMs = 300;    // 只靠时间收口
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Run(Plan(s), timeoutMs: 8000);
        sw.Stop();
        Assert.Empty(r.Calls);
        Assert.True(r.LogHas("毫秒时限"));
        Assert.True(sw.ElapsedMilliseconds >= 250, $"应等够时限才放弃，实际 {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 4000, $"到点就该结束，实际 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void 次数与时间上限同时生效时先到者结束()
    {
        // 次数很快用完、时间上限很长 → 应按次数结束（两者是"先到先算"，不是必须都满足）
        var s = Key("a"); s.CondNever();
        s.RunConditionRetry = true;
        s.RunConditionRetryIntervalMs = 10;
        s.RunConditionRetryMax = 2;
        s.RunConditionRetryTimeoutMs = 60000;
        var r = Run(Plan(s), timeoutMs: 8000);
        Assert.Empty(r.Calls);
        Assert.True(r.LogHas("重复检查 2 次仍未满足"));
        Assert.True(r.LogHas("次上限"));
    }

    [Fact]
    public void 间隔为零表示立刻重判()
    {
        // 间隔 0 = 不等待，直接连着判。用次数上限收口，验证不会因为 0 被当成"没设置"而顶成 1000ms。
        var s = Key("a"); s.CondNever();
        s.RunConditionRetry = true;
        s.RunConditionRetryIntervalMs = 0;
        s.RunConditionRetryMax = 5;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Run(Plan(s), timeoutMs: 8000);
        sw.Stop();
        Assert.Empty(r.Calls);
        Assert.True(r.LogHas("失败后立刻重新检查"));
        Assert.True(r.LogHas("重复检查 5 次仍未满足"));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"0 间隔不该有等待，实际 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void 重复检查等待中按停止要立刻结束()
    {
        // F11 停止：不管是在"等间隔"还是"刚判完"，都必须尽快收工，不能等到次数/时间上限
        var s = Key("a"); s.CondNever();
        s.RunConditionRetry = true;
        s.RunConditionRetryIntervalMs = 1000;   // 间隔较长：验证停止不用等这一轮走完
        s.RunConditionRetryMax = 0;             // 不限次数，只能靠停止结束
        var fake = new FakeBackend();
        var runner = new MacroRunner(fake);
        string reason = "";
        var done = new ManualResetEventSlim();
        runner.Finished += r => { reason = r; done.Set(); };
        runner.Start(Plan(s), 0);
        Thread.Sleep(300);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        runner.Stop();
        Assert.True(done.Wait(3000), "停止后应尽快结束");
        sw.Stop();
        Assert.Equal("Stopped", reason);
        Assert.True(sw.ElapsedMilliseconds < 900, $"停止响应太慢：{sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void 零间隔重复检查也能被立刻停止()
    {
        // 间隔 0 是连着判的紧循环，最容易漏掉取消检查
        var s = Key("a"); s.CondNever();
        s.RunConditionRetry = true;
        s.RunConditionRetryIntervalMs = 0;
        s.RunConditionRetryMax = 0;
        var fake = new FakeBackend();
        var runner = new MacroRunner(fake);
        string reason = "";
        var done = new ManualResetEventSlim();
        runner.Finished += r => { reason = r; done.Set(); };
        runner.Start(Plan(s), 0);
        Thread.Sleep(200);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        runner.Stop();
        Assert.True(done.Wait(3000), "停止后应尽快结束");
        sw.Stop();
        Assert.Equal("Stopped", reason);
        Assert.True(sw.ElapsedMilliseconds < 900, $"停止响应太慢：{sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void 暂停后再停止同样要退出()
    {
        // 用户实际操作顺序：先 F9 暂停看看情况，再 F11 停止
        var s = Key("a"); s.CondNever();
        s.RunConditionRetry = true;
        s.RunConditionRetryIntervalMs = 500;
        s.RunConditionRetryMax = 0;
        var fake = new FakeBackend();
        var runner = new MacroRunner(fake);
        string reason = "";
        var done = new ManualResetEventSlim();
        runner.Finished += r => { reason = r; done.Set(); };
        runner.Start(Plan(s), 0);
        Thread.Sleep(200);
        runner.Pause();
        Thread.Sleep(200);
        runner.Stop();
        Assert.True(done.Wait(3000), "暂停中停止也应结束");
        Assert.Equal("Stopped", reason);
    }

    // ---- 多条件 与/或（v0.4）----

    [Fact]
    public void 与逻辑要求全部满足()
    {
        var ok = Key("ok").Cond("And", Always(), Always());
        var no = Key("no").Cond("And", Always(), Never());
        var r = Run(Plan(ok, no));
        Assert.Equal(new[] { "ok" }, r.Calls);
    }

    [Fact]
    public void 或逻辑只要一条满足即可()
    {
        var ok = Key("ok").Cond("Or", Never(), Always());
        var no = Key("no").Cond("Or", Never(), Never());
        var r = Run(Plan(ok, no));
        Assert.Equal(new[] { "ok" }, r.Calls);
    }

    [Fact]
    public void 三条以上的与或组合()
    {
        var a = Key("a").Cond("And", Always(), Always(), Always());
        var b = Key("b").Cond("And", Always(), Always(), Never());
        var c = Key("c").Cond("Or", Never(), Never(), Always());
        var d = Key("d").Cond("Or", Never(), Never(), Never());
        var r = Run(Plan(a, b, c, d));
        Assert.Equal(new[] { "a", "c" }, r.Calls);
    }

    [Fact]
    public void 单条条件时与或设置不影响结果()
    {
        var a = Key("a").Cond("Or", Always());
        var b = Key("b").Cond("And", Always());
        var c = Key("c").Cond("Or", Never());
        var r = Run(Plan(a, b, c));
        Assert.Equal(new[] { "a", "b" }, r.Calls);
    }

    [Fact]
    public void 无效条目不参与判定()
    {
        // 半成品条目（比如刚添加还没填内容）不该把整组判成不满足
        var s = Key("a").Cond("And", Always(), new ConditionItem());
        var r = Run(Plan(s));
        Assert.Equal(new[] { "a" }, r.Calls);
    }

    [Fact]
    public void 取反作用于单条而不是整组()
    {
        // Never = 全天区间取反；与一条 Always 做「或」→ 满足
        var s = Key("a").Cond("Or", Never(), Always());
        var r = Run(Plan(s));
        Assert.Equal(new[] { "a" }, r.Calls);
    }

    [Fact]
    public void 多条件下的重复检查等到全部满足()
    {
        var s = Key("a").Cond("And", Always(), Never());
        s.RunConditionRetry = true;
        s.RunConditionRetryIntervalMs = 40;
        s.RunConditionRetryMax = 30;
        var r = Run(Plan(s), onLog: (_, _, msg) =>
        {
            if (msg.Contains("重新检查")) MakeSatisfied(s);   // 把那条 Never 改成满足
        });
        Assert.Equal(new[] { "a" }, r.Calls);
    }

    [Fact]
    public void 历史存档的单条条件运行时仍然生效()
    {
        // 模拟旧 plans.json 直接反序列化出来的动作：只有单条字段、列表为空
        var s = Key("a");
        s.RunConditionType = "TimeRange";
        s.RunConditionStartMinute = 0;
        s.RunConditionEndMinute = 1439;
        s.RunConditionInvert = true;        // 永不满足
        var r = Run(Plan(s, Key("b")));
        Assert.Equal(new[] { "b" }, r.Calls);   // 旧条件被识别并生效 → a 被跳过
    }

    [Fact]
    public void 组合的条件不满足则整组跳过()
    {
        var g = Group(Key("x"), Key("y")); g.CondNever();
        var r = Run(Plan(Key("a"), g, Key("b")));
        Assert.Equal(new[] { "a", "b" }, r.Calls);
    }
}
