using MacroPilot.Models;
using Xunit;
using static MacroPilot.Tests.Harness;

namespace MacroPilot.Tests;

/// <summary>方案配置「动作执行失败时立即暂停」（默认开）；动作设了「运行失败后」监听则失败交由监听处理、不暂停。</summary>
public class PauseOnFailTests
{
    [Fact]
    public void 失败且无失败监听时自动暂停_恢复后继续()
    {
        var s = Key("boom");
        var p = Plan(s, Key("next"));   // 默认 PauseOnFail = true
        var r = Run(p, failOnKey: "boom", onLog: (runner, _, msg) =>
        {
            if (msg.Contains("自动暂停")) runner.Resume();   // 模拟用户按 F9 继续
        });
        Assert.True(r.LogHas("自动暂停"));
        Assert.Contains("next", r.Calls);   // 恢复后照常走完
        Assert.Equal("Done", r.Reason);
    }

    [Fact]
    public void 关闭失败暂停后失败直接继续()
    {
        var s = Key("boom");
        var p = Plan(s, Key("next"));
        p.PauseOnFail = false;
        var r = Run(p, failOnKey: "boom");
        Assert.False(r.LogHas("自动暂停"));
        Assert.Contains("next", r.Calls);
    }

    [Fact]
    public void 设置了失败监听则不暂停()
    {
        var s = Key("boom");
        s.FailAction = Key("handler");
        var p = Plan(s, Key("next"));   // PauseOnFail = true，但失败交由监听处理
        var r = Run(p, failOnKey: "boom");
        Assert.False(r.LogHas("自动暂停"));
        Assert.Equal(new[] { "boom", "handler", "next" }, r.Calls);
    }
}
