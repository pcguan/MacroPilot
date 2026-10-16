using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

public class PlanPersistenceTests
{
    private static MacroPlan EditedPlan()
    {
        var plan = new MacroPlan
        {
            Name = "已编辑方案", LoopCount = 3, LoopDelayMs = 120000, LoopDelayUnit = 2, PauseOnFail = false,
            RunConditionLogic = "Expr:@1", RunConditionRetry = true, RunConditionRetryIntervalMs = 17,
            RunConditionRetryIntervalUnit = 0, RunConditionRetryMax = 9,
            RunConditionRetryTimeoutMs = 45000, RunConditionRetryTimeoutUnit = 1,
            Dirty = true, IsScheduled = true, SavedSnapshot = "原保存基线",
        };
        plan.RunConditions.Add(new ConditionItem { Type = "ImageMatch", Image = "plan-image", RectW = 20, RectH = 30 });
        var group = new MacroStep { Type = "Group", Id = "group", DisplayIndex = 4, IsExpanded = true, IsChecked = true };
        var child = new MacroStep { Type = "MouseClickImage", Id = "child", ClickImage = "click-image", IsFocused = true };
        child.RunConditions.Add(new ConditionItem { Type = "ImageMatch", Image = "condition-image", RectW = 4, RectH = 5 });
        child.UntilConditions.Add(new ConditionItem { Type = "ImageMatch", Image = "until-image", RectW = 6, RectH = 7 });
        child.CompleteAction = new MacroStep { Type = "MouseClickImage", Id = "hook", ClickImage = "hook-image" };
        group.Children.Add(child);
        plan.Steps.Add(group);
        return plan;
    }

    [Fact]
    public void 方案克隆覆盖全部持久化标量且不复制界面状态()
    {
        var source = EditedPlan();
        // 加字段后自动给出非默认值，避免新增字段漏进 Clone/CopyFrom 而测试仍然通过。
        foreach (var property in typeof(MacroPlan).GetProperties())
        {
            if (!property.CanWrite || property.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
            var type = property.PropertyType;
            if (type == typeof(string)) property.SetValue(source, "value_" + property.Name);
            else if (type == typeof(int)) property.SetValue(source, 12 + property.Name.Length);
            else if (type == typeof(int?)) property.SetValue(source, 37);
            else if (type == typeof(double)) property.SetValue(source, 0.73);
            else if (type == typeof(bool)) property.SetValue(source, !(bool)property.GetValue(new MacroPlan())!);
        }
        var copy = source.Clone();
        Assert.Equal(PlanSnapshot.Capture(source), PlanSnapshot.Capture(copy));
        Assert.False(copy.Dirty);
        Assert.False(copy.IsScheduled);
        Assert.Null(copy.SavedSnapshot);
    }

    [Fact]
    public void 运行与剪贴板副本保留编号身份但嵌套内容独立()
    {
        var source = EditedPlan();
        var copy = source.Clone();
        Assert.Equal(4, copy.Steps[0].DisplayIndex);
        Assert.Equal(source.Steps[0].Id, copy.Steps[0].Id);
        Assert.False(copy.Steps[0].IsChecked);
        Assert.False(copy.Steps[0].IsExpanded);
        copy.RunConditions[0].Image = "changed";
        copy.Steps[0].Children[0].RunConditions[0].Image = "changed";
        copy.Steps[0].Children[0].UntilConditions[0].Image = "changed";
        copy.Steps[0].Children[0].CompleteAction!.ClickImage = "changed";
        Assert.Equal("plan-image", source.RunConditions[0].Image);
        Assert.Equal("condition-image", source.Steps[0].Children[0].RunConditions[0].Image);
        Assert.Equal("until-image", source.Steps[0].Children[0].UntilConditions[0].Image);
        Assert.Equal("hook-image", source.Steps[0].Children[0].CompleteAction!.ClickImage);
    }

    [Fact]
    public void 放弃和撤销恢复全部设置条件步骤并同步定时名称()
    {
        var plan = EditedPlan();
        string snapshot = PlanSnapshot.Capture(plan);
        var stepsCollection = plan.Steps;
        plan.CopyFrom(new MacroPlan { Name = "后来改名", PauseOnFail = true });
        var document = new MacroDocument { ScheduleMode = "Daily", ScheduledPlan = plan.Name, Plans = new() { plan } };

        Assert.True(PlanSnapshot.Restore(plan, snapshot, document));
        Assert.Equal(snapshot, PlanSnapshot.Capture(plan));
        Assert.Equal("已编辑方案", document.ScheduledPlan);
        Assert.Same(stepsCollection, plan.Steps);
        // 快照仅包含持久化内容，恢复不能把其他 UI 状态或旧保存基线冒充成此次基线。
        Assert.True(plan.Dirty);
        Assert.Equal("原保存基线", plan.SavedSnapshot);
        Assert.True(plan.IsScheduled);
    }

    [Fact]
    public void 深层组合快照可往返且嵌套恢复保持独立()
    {
        var plan = new MacroPlan();
        var root = new MacroStep { Type = "Group" };
        var leaf = root;
        for (int i = 0; i < 70; i++)
        {
            var child = new MacroStep { Type = "Group" };
            leaf.Children.Add(child);
            leaf = child;
        }
        leaf.Type = "KeyTap"; leaf.Key = "saved";
        plan.Steps.Add(root);
        string snapshot = PlanSnapshot.Capture(plan);   // Children 数组 + 对象，明显超过默认 64 层
        leaf.Key = "edited";
        PlanSnapshot.Restore(plan, snapshot);
        Assert.Equal(snapshot, PlanSnapshot.Capture(plan));
        Assert.Equal("edited", leaf.Key);   // 恢复得到独立对象，不是把旧对象偷偷改回去
    }

    [Fact]
    public void 损坏恢复快照不修改当前方案或定时目标()
    {
        var plan = EditedPlan();
        string before = PlanSnapshot.Capture(plan);
        var document = new MacroDocument { ScheduleMode = "Daily", ScheduledPlan = plan.Name };
        Assert.ThrowsAny<Exception>(() => PlanSnapshot.Restore(plan, "{ broken", document));
        Assert.Equal(before, PlanSnapshot.Capture(plan));
        Assert.Equal(plan.Name, document.ScheduledPlan);
        Assert.True(plan.Dirty);
        Assert.Equal("原保存基线", plan.SavedSnapshot);
    }

    [Fact]
    public void 恢复同名的非定时方案不会转移真正定时目标()
    {
        var scheduled = new MacroPlan { Name = "A" };
        var edited = new MacroPlan { Name = "B" };
        string snapshot = PlanSnapshot.Capture(edited);
        edited.Name = "A";
        var document = new MacroDocument { ScheduleMode = "Daily", ScheduledPlan = "A", Plans = new() { scheduled, edited } };
        Assert.False(PlanSnapshot.Restore(edited, snapshot, document));
        Assert.Equal("B", edited.Name);
        Assert.Equal("A", document.ScheduledPlan);
        Assert.True(PlanSnapshot.IsScheduledTarget(document, scheduled, document.Plans));
    }

    [Fact]
    public void 写入失败保留内容对象脏标记和保存基线()
    {
        var plan = EditedPlan();
        string before = PlanSnapshot.Capture(plan);
        var group = plan.Steps[0];
        var condition = plan.RunConditions[0];
        bool called = false;
        bool saved = PlanPersistence.TrySave(new[] { plan }, PrepareImageReferences, prepared =>
        {
            called = true;
            Assert.StartsWith("file:", prepared[0].RunConditions[0].Image);
            Assert.NotSame(plan, prepared[0]);
            return false;
        }, out var error);
        Assert.True(called);
        Assert.False(saved);
        Assert.NotEmpty(error);
        Assert.Equal(before, PlanSnapshot.Capture(plan));
        Assert.Same(group, plan.Steps[0]);
        Assert.Same(condition, plan.RunConditions[0]);
        Assert.True(plan.Dirty);
        Assert.Equal("原保存基线", plan.SavedSnapshot);
    }

    [Fact]
    public void 准备或写入抛异常均不提交当前内容()
    {
        foreach (bool failInPrepare in new[] { true, false })
        {
            var plan = EditedPlan();
            string before = PlanSnapshot.Capture(plan);
            Assert.False(PlanPersistence.TrySave(new[] { plan }, prepared =>
            {
                PrepareImageReferences(prepared);
                if (failInPrepare) throw new IOException("图片目录不可写");
            }, _ => throw new IOException("方案目录不可写"), out var error));
            Assert.Contains("不可写", error);
            Assert.Equal(before, PlanSnapshot.Capture(plan));
            Assert.True(plan.Dirty);
            Assert.Equal("原保存基线", plan.SavedSnapshot);
        }
    }

    [Fact]
    public void 保存成功后图片引用和基线与写入内容一致且保留全部活对象和状态()
    {
        var plan = EditedPlan();
        var steps = plan.Steps;
        var group = steps[0];
        var children = group.Children;
        var child = children[0];
        var hook = child.CompleteAction;
        var planCondition = plan.RunConditions[0];
        var childCondition = child.RunConditions[0];
        var untilCondition = child.UntilConditions[0];
        string? written = null;
        Assert.True(PlanPersistence.TrySave(new[] { plan }, PrepareImageReferences, prepared =>
        {
            written = PlanSnapshot.Capture(prepared[0]);
            return true;
        }, out _));
        Assert.Equal(written, PlanSnapshot.Capture(plan));
        Assert.Equal(written, plan.SavedSnapshot);
        Assert.False(plan.Dirty);
        Assert.Same(steps, plan.Steps);
        Assert.Same(group, plan.Steps[0]);
        Assert.Same(children, group.Children);
        Assert.Same(child, group.Children[0]);
        Assert.Same(hook, child.CompleteAction);
        Assert.Same(planCondition, plan.RunConditions[0]);
        Assert.Same(childCondition, child.RunConditions[0]);
        Assert.Same(untilCondition, child.UntilConditions[0]);
        Assert.True(group.IsExpanded);
        Assert.True(group.IsChecked);
        Assert.True(child.IsFocused);
        Assert.StartsWith("file:", hook!.ClickImage);
    }

    [Fact]
    public void 旧条件只在保存成功后迁移且保存失败仍可重试()
    {
        var plan = new MacroPlan { RunConditionType = "TimeRange", RunConditionStartMinute = 12, Dirty = true };
        Assert.False(PlanPersistence.TrySave(new[] { plan }, PrepareImageReferences, _ => false, out _));
        Assert.Equal("TimeRange", plan.RunConditionType);
        Assert.Empty(plan.RunConditions);
        Assert.True(PlanPersistence.TrySave(new[] { plan }, PrepareImageReferences, _ => true, out _));
        Assert.Equal("", plan.RunConditionType);
        Assert.Equal(12, Assert.Single(plan.RunConditions).StartMinute);
        Assert.Equal(plan.SavedSnapshot, PlanSnapshot.Capture(plan));
    }

    // 模拟外置存储的准备结果，避免单测向真实用户的 AppData 图片目录写入。
    // 测试重点是准备副本、写盘与提交之间的数据契约；实际哈希/文件 IO 属 ImageStore 的职责。
    private static void PrepareImageReferences(MacroPlan plan)
    {
        void Images(IEnumerable<ConditionItem> items)
        {
            foreach (var item in items)
                if (item.Image.Length > 0) item.Image = "file:" + item.Image;
        }
        RunCondition.Normalize(plan);
        Images(plan.RunConditions);
        foreach (var step in MacroStep.Flatten(plan.Steps))
        {
            RunCondition.Normalize(step);
            Images(step.RunConditions);
            Images(step.UntilConditions);
            if (step.ClickImage.Length > 0) step.ClickImage = "file:" + step.ClickImage;
        }
    }
}
