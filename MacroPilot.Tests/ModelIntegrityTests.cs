using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MacroPilot.Models;
using Xunit;

namespace MacroPilot.Tests;

/// <summary>
/// 模型完整性：用反射逐属性核对，专门拦"加了新字段却忘了同步到 Clone / Copy / 遍历"的老毛病。
/// 这个项目已经因为这类疏漏踩过坑（BuildRunPlan 手写复制漏掉整组图片条件字段，导致方案级
/// 「图片出现」在运行副本里退化成无条件）。有了这几个测试，以后加字段忘了同步会直接红。
/// </summary>
public class ModelIntegrityTests
{
    // 只在 UI 层使用、不该参与克隆/持久化的属性
    private static readonly HashSet<string> StepIgnored = new()
    {
        "Children",        // 单独递归比对
        "IsChecked", "IsExpanded", "IsExecuting", "IsFocused",   // 仅 UI 瞬时状态
        "Display", "Brief", "IsGroup", "HasJump", "HasListener", "HasRunCondition", "Enabled",
        "PreCondAction", "CondSuccessAction", "CondFailAction", "PreRunAction",
        "SuccessAction", "CompleteAction", "FailAction",         // 监听单独递归比对
    };

    /// <summary>给对象的每个可写属性填一个"非默认"值，用来暴露漏拷的字段。</summary>
    private static void FillDistinct(object o, int seed)
    {
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || !p.CanRead) continue;
            var t = p.PropertyType;
            if (t == typeof(string)) p.SetValue(o, $"v{seed}_{p.Name}");
            else if (t == typeof(int)) p.SetValue(o, seed * 7 + p.Name.Length);
            else if (t == typeof(int?)) p.SetValue(o, seed * 3 + p.Name.Length);
            else if (t == typeof(double)) p.SetValue(o, 0.13 + seed * 0.01 + p.Name.Length * 0.001);
            else if (t == typeof(bool)) p.SetValue(o, true);
            else if (t == typeof(byte)) p.SetValue(o, (byte)(seed % 200 + 1));
        }
    }

    private static void AssertSameProps(object a, object b, HashSet<string> ignore, string ctx)
    {
        foreach (var p in a.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (ignore.Contains(p.Name) || !p.CanRead || !p.CanWrite) continue;
            var t = p.PropertyType;
            if (!(t == typeof(string) || t == typeof(int) || t == typeof(int?) || t == typeof(double)
                  || t == typeof(bool) || t == typeof(byte))) continue;
            Assert.True(Equals(p.GetValue(a), p.GetValue(b)),
                $"{ctx}：属性 {p.Name} 未被同步（期望 {p.GetValue(a)}，实际 {p.GetValue(b)}）——" +
                "新增字段后请同步到对应的 Clone / Copy 逻辑。");
        }
    }

    [Fact]
    public void 动作克隆覆盖全部可持久化字段()
    {
        var s = new MacroStep();
        FillDistinct(s, 3);
        s.Type = "KeyTap";                       // 保持是有效类型，避免 Display 逻辑异常
        var c = s.Clone();
        AssertSameProps(s, c, StepIgnored, "MacroStep.Clone");
    }

    [Fact]
    public void 动作克隆是深拷贝含子动作与全部监听挂点()
    {
        var s = new MacroStep { Type = "Group" };
        s.Children.Add(new MacroStep { Type = "KeyTap", Key = "child" });
        s.PreCondAction = new MacroStep { Type = "KeyTap", Key = "h1" };
        s.CondSuccessAction = new MacroStep { Type = "KeyTap", Key = "h2" };
        s.CondFailAction = new MacroStep { Type = "KeyTap", Key = "h3" };
        s.PreRunAction = new MacroStep { Type = "KeyTap", Key = "h4" };
        s.SuccessAction = new MacroStep { Type = "KeyTap", Key = "h5" };
        s.FailAction = new MacroStep { Type = "KeyTap", Key = "h6" };
        s.CompleteAction = new MacroStep { Type = "KeyTap", Key = "h7" };

        var c = s.Clone();
        Assert.NotSame(s.Children[0], c.Children[0]);
        Assert.Equal("child", c.Children[0].Key);
        var kinds = c.HookList().Select(h => h.Hook.Key).ToArray();
        Assert.Equal(new[] { "h1", "h2", "h3", "h4", "h5", "h6", "h7" }, kinds);
        foreach (var (_, hook) in c.HookList()) Assert.DoesNotContain(hook, s.HookList().Select(x => x.Hook));
    }

    [Fact]
    public void 运行条件整体拷贝覆盖接口的每个属性()
    {
        // RunCondition.Copy 是方案级/动作级共用的唯一拷贝入口，必须覆盖 IRunCondition 全部属性。
        var src = new MacroStep();
        foreach (var p in typeof(IRunCondition).GetProperties())
        {
            var t = p.PropertyType;
            if (t == typeof(string)) p.SetValue(src, "x_" + p.Name);
            else if (t == typeof(int)) p.SetValue(src, 40 + p.Name.Length);
            else if (t == typeof(int?)) p.SetValue(src, 20 + p.Name.Length);
            else if (t == typeof(double)) p.SetValue(src, 0.42);
            else if (t == typeof(bool)) p.SetValue(src, true);
        }
        var dst = new MacroPlan();
        RunCondition.Copy(src, dst);
        foreach (var p in typeof(IRunCondition).GetProperties())
            Assert.True(Equals(p.GetValue(src), p.GetValue(dst)),
                $"RunCondition.Copy 漏了属性 {p.Name}——新增运行条件字段后要同步 Copy/Clear。");
    }

    [Fact]
    public void 清空运行条件后判定为未设条件()
    {
        var s = new MacroStep();
        s.RunConditionType = "TimeRange";
        s.RunConditionStartMinute = 100;
        s.RunConditionRetry = true;
        s.RunConditionRetryMax = 9;
        RunCondition.Clear(s);
        Assert.False(RunCondition.Has(s));
        Assert.False(s.RunConditionRetry);
        Assert.Equal(1000, s.RunConditionRetryIntervalMs);   // 回到默认间隔
        Assert.Equal(0, s.RunConditionRetryMax);
    }

    [Fact]
    public void 运行条件有效性判定的边界()
    {
        var s = new MacroStep();
        Assert.False(RunCondition.Has(s));                                  // 空

        s.RunConditionType = "TimeRange";
        Assert.False(RunCondition.Has(s));                                  // 有类型无时间

        s.RunConditionStartMinute = 60;
        Assert.True(RunCondition.Has(s));                                   // 单侧即有效

        RunCondition.Clear(s);
        s.RunConditionType = "ImageMatch";
        s.RunConditionImage = "file:abc";
        Assert.False(RunCondition.Has(s));                                  // 有图无区域

        s.RunConditionRectW = 10; s.RunConditionRectH = 10;
        Assert.True(RunCondition.Has(s));
    }

    [Fact]
    public void 监听挂点为空时不算有监听()
    {
        var s = new MacroStep();
        Assert.False(s.HasListener);
        Assert.Empty(s.HookList());
        s.PreCondAction = new MacroStep();
        Assert.True(s.HasListener);
        Assert.Single(s.HookList());
    }
}
