using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

public class EditingAndLifecycleTests
{
    [Theory]
    [InlineData("KeyTap")]
    [InlineData("Group")]
    public void 编辑保留禁用状态和身份(string type)
    {
        var source = new MacroStep { Type = type, Disabled = true, Id = "stable", Note = "before" };
        var edited = PlanEditing.CompleteEdit(source, new MacroStep { Type = type, Note = "after" });
        Assert.True(edited.Disabled);
        Assert.Equal("stable", edited.Id);
        Assert.Equal("before", source.Note);
        Assert.Equal("after", edited.Note);
    }

    [Fact]
    public void 旧子项焦点不会回退删除父组()
    {
        var old = Harness.Key("old");
        var group = new MacroStep { Type = "Group", Children = new() { old } };
        var roots = new ObservableCollection<MacroStep> { group };
        var edited = PlanEditing.CompleteEdit(old, Harness.Key("new"));
        group.Children[0] = edited;
        Assert.Null(PlanEditing.ResolveTarget(roots, old, group));
        Assert.Same(edited, PlanEditing.ResolveTarget(roots, edited, group));
        Assert.Single(roots);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void 嵌套移出操作保持全部叶子且不错误移到顶层(int count)
    {
        var children = Enumerable.Range(0, count).Select(i => Harness.Key(i.ToString())).ToArray();
        var inner = new MacroStep { Type = "Group", Children = new(children) };
        var peer = Harness.Key("peer");
        var outer = new MacroStep { Type = "Group", Children = new() { inner, peer } };
        var roots = new ObservableCollection<MacroStep> { outer };
        Assert.True(PlanEditing.Promote(roots, children[0]));
        Assert.Same(outer, Assert.Single(roots));
        Assert.Contains(children[0], outer.Children);
        Assert.Equal(count + 1, MacroStep.Flatten(roots).Count(s => !s.IsGroup));
        Assert.Equal(count >= 2, outer.Children.Contains(inner));
    }

    [Fact]
    public void 移出过期节点不修改原树()
    {
        var plan = new MacroPlan { Steps = new() { Harness.Key("keep") } };
        var before = PlanSnapshot.Capture(plan);
        Assert.False(PlanEditing.Promote(plan.Steps, Harness.Key("stale")));
        Assert.Equal(before, PlanSnapshot.Capture(plan));
    }

    [Fact]
    public void 删除最后一个嵌套子项清理空组()
    {
        var child = Harness.Key("delete");
        var inner = new MacroStep { Type = "Group", Children = new() { child } };
        var outer = new MacroStep { Type = "Group", Children = new() { inner } };
        var keep = Harness.Key("keep");
        var roots = new ObservableCollection<MacroStep> { outer, keep };
        Assert.True(PlanEditing.Remove(roots, child));
        Assert.Same(keep, Assert.Single(roots));
    }

    [Fact]
    public void 从首项试运行保持别名跳转上限并正常结束()
    {
        var first = Harness.Key("A"); first.Alias = "start";
        var plan = new MacroPlan { PauseOnFail = false, Steps = new() { first,
            new MacroStep { Type = "Jump", JumpTargetAlias = "start", JumpTimes = 1 } } };
        var run = PlanEditing.BuildTestPlan(plan, 0, false);
        Assert.False(run.PauseOnFail);
        Assert.Equal(1, run.Steps[1].JumpTimes);
        Assert.Equal(new[] { "A", "A" }, Harness.Run(run, 2000).Calls);
        Assert.Equal(1, plan.Steps[1].JumpTimes);
    }

    [Fact]
    public void 子计划重映射旧序号并移除裁剪掉的目标()
    {
        var a = Harness.Key("A"); a.Alias = "outside";
        var b = Harness.Key("B");
        var old = new MacroStep { Type = "Jump", JumpTarget = 2, JumpTimes = 3 };
        var outside = new MacroStep { Type = "Jump", JumpTargetAlias = "outside", JumpTimes = 4 };
        var plan = new MacroPlan { Steps = new() { a, b, old, outside } };
        var run = PlanEditing.BuildTestPlan(plan, 1, false);
        Assert.Equal(b.Id, run.Steps[1].JumpTargetId);
        Assert.Equal(1, run.Steps[1].JumpTarget);
        Assert.Equal(3, run.Steps[1].JumpTimes);
        Assert.Empty(run.Steps[2].JumpTargetAlias);
        Assert.Equal("outside", outside.JumpTargetAlias);
    }

    [Fact]
    public void 单步清除子项和监听的跳转但不修改原方案()
    {
        var jump = new MacroStep { Type = "Jump", JumpTargetAlias = "self", JumpTimes = 2 };
        var group = new MacroStep { Type = "Group", Alias = "self", Children = new() { jump }, CompleteAction = jump.Clone() };
        var result = PlanEditing.BuildTestPlan(new MacroPlan { Steps = new() { group } }, 0, true);
        Assert.All(MacroStep.Flatten(result.Steps).Where(s => s.Type == "Jump"), s => Assert.Empty(s.JumpTargetAlias));
        Assert.Equal("self", jump.JumpTargetAlias);
    }

    [Theory]
    [InlineData(ActivityKind.Recording)]
    [InlineData(ActivityKind.StartingRun)]
    [InlineData(ActivityKind.Updating)]
    public void 忙碌时拒绝其他入口并在取消后恢复(ActivityKind busy)
    {
        var state = new ActivityState();
        Assert.True(state.TryBegin(busy));
        Assert.False(state.TryBegin(ActivityKind.StartingRun));
        Assert.False(state.TryBegin(ActivityKind.Recording));
        Assert.False(state.TryBegin(ActivityKind.Updating));
        state.Finish(busy);
        Assert.True(state.TryBegin(ActivityKind.StartingRun));
    }

    [Fact]
    public void 关闭后晚到的异步完成不能重新开放运行()
    {
        var state = new ActivityState();
        state.TryBegin(ActivityKind.StartingRun);
        state.Close(); state.RunStarted(); state.Finish(ActivityKind.StartingRun);
        Assert.Equal(ActivityKind.Closing, state.Kind);
        Assert.False(state.TryBegin(ActivityKind.Recording));
    }

    [Fact]
    public void 恢复窗口不能落在屏幕空洞或只露底边()
    {
        var monitors = new[] { new Rect(0, 0, 1920, 1080), new Rect(1920, 1080, 1920, 1080) };
        Assert.False(WindowMemory.IsUsablyOnScreen(new Rect(2000, 100, 500, 400), monitors));
        Assert.False(WindowMemory.IsUsablyOnScreen(new Rect(100, -300, 500, 400), monitors));
        Assert.True(WindowMemory.IsUsablyOnScreen(new Rect(2000, 1200, 500, 400), monitors));
    }

    [Fact]
    public void 条件编辑拒绝无效条目而非静默过滤()
    {
        var items = new[] { new ConditionItem { Type = "TimeRange", StartMinute = 0 }, new ConditionItem() };
        Assert.Contains("第 2 条", Assert.Throws<InvalidOperationException>(() => ConditionEditing.Validate(items)).Message);
        Assert.Equal(2, items.Length);
    }

    [Fact]
    public void 整屏图片条件可保留原序号保存()
    {
        var items = new[] { new ConditionItem { Type = "ImageMatch", Image = "file:template", RectW = 0, RectH = 0 } };
        Assert.Same(items, ConditionEditing.Validate(items));
    }
}
