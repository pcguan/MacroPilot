using System;
using System.Collections.ObjectModel;
using System.Linq;
using MacroPilot.Models;

namespace MacroPilot.Services;

/// <summary>编辑命令的身份、树归属及试运行转换；不依赖控件或输入设备。</summary>
public static class PlanEditing
{
    public static MacroStep CompleteEdit(MacroStep? source, MacroStep edited)
    {
        if (source != null) { edited.Id = source.Id; edited.Disabled = source.Disabled; }
        return edited;
    }

    public static ObservableCollection<MacroStep>? Container(ObservableCollection<MacroStep> roots, MacroStep target)
    {
        if (roots.Contains(target)) return roots;
        foreach (var group in roots.Where(s => s.IsGroup))
            if (Container(group.Children, target) is { } found) return found;
        return null;
    }

    public static MacroStep? ResolveTarget(ObservableCollection<MacroStep> roots, MacroStep? focused, MacroStep? selected)
    {
        var target = focused ?? selected;
        return target != null && Container(roots, target) != null ? target : null;
    }

    public static bool Remove(ObservableCollection<MacroStep> roots, MacroStep target)
    {
        var owner = Container(roots, target);
        if (owner == null) return false;
        owner.Remove(target);
        while (owner.Count == 0 && !ReferenceEquals(owner, roots))
        {
            var parent = MacroStep.Flatten(roots).FirstOrDefault(s => s.IsGroup && ReferenceEquals(s.Children, owner));
            if (parent == null || Container(roots, parent) is not { } siblings) break;
            siblings.Remove(parent);
            owner = siblings;
        }
        return true;
    }

    // 移到父组合的同级，保留一层一层移出的语义。先定位所有集合，不能删到一半才发现索引无效。
    public static bool Promote(ObservableCollection<MacroStep> roots, MacroStep child)
    {
        var parent = MacroStep.Flatten(roots).FirstOrDefault(s => s.IsGroup && s.Children.Contains(child));
        if (parent == null || Container(roots, parent) is not { } siblings) return false;
        int at = siblings.IndexOf(parent);
        if (at < 0) return false;
        parent.Children.Remove(child);
        // 即使只剩一项，也保留组合的条件、循环与监听语义。
        if (parent.Children.Count > 0) at++;
        else
        {
            siblings.RemoveAt(at);
        }
        siblings.Insert(at, child);
        return true;
    }

    public static MacroPlan BuildTestPlan(MacroPlan source, int start, bool single)
    {
        if (start < 0 || start >= source.Steps.Count) throw new ArgumentOutOfRangeException(nameof(start));
        var result = source.Clone();
        result.Name = single ? $"单次测试：第 {start + 1} 步" : $"{source.Name}（从第 {start + 1} 步）";
        result.LoopCount = 1;
        result.Steps = new(source.Steps.Skip(start).Take(single ? 1 : source.Steps.Count).Select(s => s.Clone()));
        foreach (var jump in MacroStep.Flatten(result.Steps).Where(s => s.Type == "Jump"))
        {
            // 先在原方案解析旧序号/Id，再映射到子计划；有效跳转保留次数上限。
            var target = jump.JumpTargetAlias.Length > 0 ? source.Steps.FirstOrDefault(s => s.Alias == jump.JumpTargetAlias)
                : jump.JumpTargetId.Length > 0 ? source.Steps.FirstOrDefault(s => s.Id == jump.JumpTargetId)
                : source.Steps.ElementAtOrDefault(jump.JumpTarget - 1);
            int mapped = target == null ? -1 : result.Steps.ToList().FindIndex(s => s.Id == target.Id);
            if (single || mapped < 0) { jump.JumpTargetAlias = ""; jump.JumpTargetId = ""; jump.JumpTarget = 0; }
            else if (jump.JumpTargetAlias.Length == 0) { jump.JumpTargetId = result.Steps[mapped].Id; jump.JumpTarget = mapped + 1; }
        }
        return result;
    }
}
