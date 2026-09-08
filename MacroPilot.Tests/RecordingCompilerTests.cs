using System;
using System.Collections.Generic;
using System.Linq;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

/// <summary>
/// 宏录制归纳器（事件流 → 动作列表）。纯函数、坐标换算注入，因此全部规则可确定性回归——
/// 采集（钩子）层照项目惯例不测。每条归纳规则至少一个用例 + 关键边界。
/// </summary>
public class RecordingCompilerTests
{
    // 单屏假环境：物理坐标 / 2000×1000 归一化
    private static (string, double, double) Map(int x, int y) => ("SCR", x / 2000.0, y / 1000.0);
    private static List<MacroStep> C(params RecEvent[] ev) => RecordingCompiler.Compile(ev, Map);

    private static RecEvent MDown(long t, int x, int y, int b = 0) => new(t, RecKind.MouseDown, x, y, b);
    private static RecEvent MUp(long t, int x, int y, int b = 0) => new(t, RecKind.MouseUp, x, y, b);
    private static RecEvent MMove(long t, int x, int y) => new(t, RecKind.MouseMove, x, y, 0);
    private static RecEvent Wheel(long t, int delta) => new(t, RecKind.Wheel, 0, 0, delta);
    private static RecEvent KDown(long t, int vk) => new(t, RecKind.KeyDown, 0, 0, vk);
    private static RecEvent KUp(long t, int vk) => new(t, RecKind.KeyUp, 0, 0, vk);

    [Fact]
    public void 按下抬起同位置折叠成点击坐标()
    {
        var s = C(MDown(0, 100, 200), MUp(120, 102, 201));
        var click = Assert.Single(s);
        Assert.Equal("MouseClickAt", click.Type);
        Assert.Equal("SCR", click.MoveMonitor);
        Assert.Equal(100 / 2000.0, click.MoveNormX, 6);
        Assert.Equal(200 / 1000.0, click.MoveNormY, 6);
        Assert.Equal(120, click.HoldMs);            // 实测按住时长
        Assert.True(click.Humanize);
    }

    [Fact]
    public void 按住时长低于下限时抬到75毫秒()
    {
        // CH9329 hold<75 部分应用会吞后续键（项目既有结论），录出来的方案要直接可用
        var s = C(MDown(0, 100, 100), MUp(30, 100, 100));
        Assert.Equal(75, s[0].HoldMs);
    }

    [Fact]
    public void 位移超过阈值折叠成拖动()
    {
        var s = C(MDown(0, 100, 100), MMove(50, 300, 400), MUp(200, 300, 400));
        var drag = Assert.Single(s);
        Assert.Equal("MouseDrag", drag.Type);
        Assert.Equal(100 / 2000.0, drag.MoveNormX, 6);       // 起点 = 按下处
        Assert.Equal(300 / 2000.0, drag.DragEndNormX, 6);    // 终点 = 抬起处
        Assert.Equal(400 / 1000.0, drag.DragEndNormY, 6);
    }

    [Fact]
    public void 中途绕远但回到原地仍算拖动不算点击()
    {
        // 位移判定要看轨迹的最大偏移，不能只比按下/抬起两点——画个圈回到原地也是拖动
        var s = C(MDown(0, 100, 100), MMove(50, 400, 100), MUp(200, 101, 101));
        Assert.Equal("MouseDrag", Assert.Single(s).Type);
    }

    [Fact]
    public void 快速连击合并为点击次数N()
    {
        var s = C(
            MDown(0, 100, 100), MUp(80, 100, 100),
            MDown(200, 101, 100), MUp(280, 101, 100),
            MDown(400, 100, 101), MUp(480, 100, 101));
        var click = Assert.Single(s);
        Assert.Equal(3, click.LoopCount);
        Assert.True(click.LoopDelayMs > 0 && click.LoopDelayMs < RecordingCompiler.MergeClickGapMs);
    }

    [Fact]
    public void 隔得久的两次点击不合并且中间生成等待()
    {
        var s = C(MDown(0, 100, 100), MUp(80, 100, 100), MDown(1080, 100, 100), MUp(1160, 100, 100));
        Assert.Equal(3, s.Count);
        Assert.Equal("MouseClickAt", s[0].Type);
        Assert.Equal("Wait", s[1].Type);
        Assert.Equal(1000, s[1].DurationMs);   // 1000ms 间隔取整到 50ms
        Assert.Equal("MouseClickAt", s[2].Type);
    }

    [Fact]
    public void 位置离得远的两次点击不合并()
    {
        var s = C(MDown(0, 100, 100), MUp(80, 100, 100), MDown(180, 500, 500), MUp(260, 500, 500));
        Assert.Equal(2, s.Count(x => x.Type == "MouseClickAt"));
    }

    [Fact]
    public void 小间隔不生成等待()
    {
        var s = C(MDown(0, 100, 100), MUp(80, 100, 100), MDown(180, 500, 500), MUp(260, 500, 500));
        Assert.DoesNotContain(s, x => x.Type == "Wait");   // 100ms < 200ms 阈值
    }

    [Fact]
    public void 连续同向滚轮合并求和()
    {
        var s = C(Wheel(0, 120), Wheel(100, 120), Wheel(200, 240));
        var w = Assert.Single(s);
        Assert.Equal("MouseWheel", w.Type);
        Assert.Equal(4, w.Wheel);
    }

    [Fact]
    public void 换方向的滚轮拆成两条()
    {
        var s = C(Wheel(0, 120), Wheel(100, -120), Wheel(200, -120));
        Assert.Equal(2, s.Count);
        Assert.Equal(1, s[0].Wheel);
        Assert.Equal(-2, s[1].Wheel);
    }

    [Fact]
    public void 普通按键折叠成KeyTap()
    {
        var s = C(KDown(0, 0x41), KUp(90, 0x41));   // A
        var k = Assert.Single(s);
        Assert.Equal("KeyTap", k.Type);
        Assert.Equal("A", k.Key);
        Assert.Equal(0, k.Modifier);
        Assert.Equal(90, k.HoldMs);
    }

    [Fact]
    public void 修饰键组合折叠成带Modifier的按键()
    {
        // 左Ctrl(0xA2) 按住 + A：A 带上 LCtrl 位(0x01)，Ctrl 自己不再单独成键
        var s = C(KDown(0, 0xA2), KDown(50, 0x41), KUp(140, 0x41), KUp(200, 0xA2));
        var k = Assert.Single(s);
        Assert.Equal("A", k.Key);
        Assert.Equal(0x01, k.Modifier);
    }

    [Fact]
    public void 单独按修饰键生成修饰键动作()
    {
        var s = C(KDown(0, 0xA4), KUp(100, 0xA4));   // 左Alt 单独按放
        var k = Assert.Single(s);
        Assert.Equal("KeyTap", k.Type);
        Assert.Equal("", k.Key);
        Assert.Equal(0x04, k.Modifier);
    }

    [Fact]
    public void 长按的自动重复按下只算一次()
    {
        // 系统对长按会连发 KeyDown；即使采集层漏了去重，归纳器也只认第一次
        var s = C(KDown(0, 0x41), KDown(30, 0x41), KDown(60, 0x41), KUp(300, 0x41));
        var k = Assert.Single(s);
        Assert.Equal(300, k.HoldMs);
    }

    [Fact]
    public void 没配对按下的抬起被忽略()
    {
        // 录制开始前就按住的键/键钮，开始后才抬起——不能凭空生成动作
        var s = C(MUp(10, 100, 100), KUp(20, 0x41));
        Assert.Empty(s);
    }

    [Fact]
    public void 纯移动不生成任何动作()
    {
        var s = C(MMove(0, 10, 10), MMove(50, 500, 500), MMove(100, 900, 900));
        Assert.Empty(s);
    }

    [Fact]
    public void 上下互滚抵消为零的滚轮不留空动作()
    {
        var s = C(Wheel(0, 120), Wheel(100, -120));
        // 变向拆成两条 ±1，不合并；但若同向合并出 0（理论路径）不能留空壳——这里验证没有 Wheel=0 的动作
        Assert.DoesNotContain(s, x => x.Type == "MouseWheel" && x.Wheel == 0);
    }

    [Fact]
    public void 综合场景_点击打字滚轮的顺序与等待正确()
    {
        var s = C(
            MDown(0, 100, 100), MUp(80, 100, 100),        // 点击
            KDown(600, 0x48), KUp(690, 0x48),             // 520ms 后按 H → 生成等待
            Wheel(750, -120));                            // 60ms 后滚轮 → 不生成等待
        Assert.Equal(new[] { "MouseClickAt", "Wait", "KeyTap", "MouseWheel" }, s.Select(x => x.Type).ToArray());
        Assert.Equal("H", s[2].Key);
        Assert.Equal(-1, s[3].Wheel);
    }
}
