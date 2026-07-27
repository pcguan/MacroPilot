using System;
using System.Collections.Generic;
using System.Threading;
using MacroPilot.Input;
using MacroPilot.Models;
using MacroPilot.Services;

namespace MacroPilot.Tests;

/// <summary>
/// 假输入后端：不产生任何真实键鼠输出，只把调用按顺序记下来。
/// 于是"方案跑出来的动作序列"变成可断言的字符串列表，业务流程（循环 / 跳转 / 组合 / 监听 / 条件）
/// 全部可测，而实际注入那一层（SendInput、CH9329 串口帧）本就不该在单测里验证。
/// </summary>
public sealed class FakeBackend : IInputBackend
{
    public readonly List<string> Calls = new();
    /// <summary>让指定按键抛异常，用来构造"动作执行失败"的分支。</summary>
    public string? FailOnKey;

    private void Rec(string s) { lock (Calls) Calls.Add(s); }

    public bool IsOpen => true;
    public string Describe => "Fake";
    public bool Open() => true;
    public void Close() { }

    public void KeyTap(string key, byte modifier, double holdMs, CancellationToken ct = default)
    {
        Rec(key);
        if (FailOnKey != null && key == FailOnKey) throw new InvalidOperationException("fake failure: " + key);
    }

    public void MouseClick(string button, double holdMs, CancellationToken ct = default) => Rec("click:" + button);
    public void MouseMove(int x, int y) => Rec($"move:{x},{y}");
    public void MouseDown(string button) => Rec("down:" + button);
    public void MouseUp(string button) => Rec("up:" + button);
    public void MouseWheel(int amount) => Rec("wheel:" + amount);
    public void ReleaseAll() { }
    public void Dispose() { }
}

/// <summary>跑一个方案并收集结果（动作序列 / 日志 / 结束原因）。</summary>
public sealed class RunResult
{
    public string Reason = "";
    public List<string> Calls = new();
    public List<string> Logs = new();
    /// <summary>日志里是否出现过含该片段的行。</summary>
    public bool LogHas(string fragment) => Logs.Exists(l => l.Contains(fragment, StringComparison.Ordinal));
}

public static class Harness
{
    /// <summary>
    /// 同步跑完一个方案。runner 内部是后台线程，这里用 Finished 事件等它结束。
    /// onLog 可用来在运行途中改动模型（例如让"重复检查"的条件中途变成满足）。
    /// </summary>
    public static RunResult Run(MacroPlan plan, int timeoutMs = 10000,
                                Action<MacroRunner, string, string>? onLog = null,
                                string? failOnKey = null)
    {
        var fake = new FakeBackend { FailOnKey = failOnKey };
        var runner = new MacroRunner(fake);
        var res = new RunResult();
        var done = new ManualResetEventSlim(false);

        runner.Log += (lvl, msg) =>
        {
            lock (res.Logs) res.Logs.Add(msg);
            onLog?.Invoke(runner, lvl, msg);
        };
        runner.Finished += r => { res.Reason = r; done.Set(); };

        runner.Start(plan, 0);
        if (!done.Wait(timeoutMs))
        {
            runner.Stop();
            done.Wait(3000);
            throw new TimeoutException("方案未在预期时间内结束，可能陷入死循环：已执行 " + string.Join(",", fake.Calls));
        }
        res.Calls = new List<string>(fake.Calls);
        return res;
    }

    // ---- 构造用的小工具：用 KeyTap 当"可观测动作"，键名即标记 ----

    /// <summary>一个只会在假后端留下 name 记录的动作。</summary>
    public static MacroStep Key(string name) => new() { Type = "KeyTap", Key = name, HoldMs = 0, LoopCount = 1 };

    /// <summary>等待动作（毫秒），用于测暂停/停止时序。</summary>
    public static MacroStep Wait(int ms) => new() { Type = "Wait", DurationMs = ms, LoopCount = 1 };

    /// <summary>跳转到第 target 个顶层动作（1 起）；times=最大重复次数，0 为不限。</summary>
    public static MacroStep Jump(int target, int times = 0) => new() { Type = "Jump", JumpTarget = target, JumpTimes = times, LoopCount = 1 };

    public static MacroStep Group(params MacroStep[] children)
    {
        var g = new MacroStep { Type = "Group", LoopCount = 1 };
        foreach (var c in children) g.Children.Add(c);
        return g;
    }

    public static MacroPlan Plan(params MacroStep[] steps)
    {
        var p = new MacroPlan { Name = "T", LoopCount = 1, LoopDelayMs = 0 };
        foreach (var s in steps) p.Steps.Add(s);
        return p;
    }

    /// <summary>一条"当前一定满足"的时间段条件（全天 00:00-23:59）。</summary>
    public static ConditionItem Always() => new() { Type = "TimeRange", StartMinute = 0, EndMinute = 1439, Invert = false };

    /// <summary>一条"当前一定不满足"的条件（全天区间取反）。</summary>
    public static ConditionItem Never() => new() { Type = "TimeRange", StartMinute = 0, EndMinute = 1439, Invert = true };

    /// <summary>给动作/方案挂若干条件；logic 为 "And"（默认）或 "Or"。</summary>
    public static T Cond<T>(this T c, string logic, params ConditionItem[] items) where T : IRunCondition
    {
        c.RunConditions.Clear();
        foreach (var i in items) c.RunConditions.Add(i);
        c.RunConditionLogic = logic;
        return c;
    }

    public static T CondAlways<T>(this T c) where T : IRunCondition => c.Cond("And", Always());
    public static T CondNever<T>(this T c) where T : IRunCondition => c.Cond("And", Never());

    /// <summary>把"永不满足"就地改成"总是满足"（配合 onLog 测试重复检查中途转为满足）。</summary>
    public static void MakeSatisfied(IRunCondition c)
    {
        foreach (var it in c.RunConditions) it.Invert = false;
    }
}
