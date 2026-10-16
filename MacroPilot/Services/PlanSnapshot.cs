using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MacroPilot.Models;

namespace MacroPilot.Services;

/// <summary>方案快照的唯一 JSON 配置；保存基线、撤销与放弃修改采用相同深度和内容。</summary>
public static class PlanSnapshot
{
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 256 };

    public static string Capture(MacroPlan plan) => JsonSerializer.Serialize(plan, Options);

    public static MacroPlan Read(string snapshot) =>
        JsonSerializer.Deserialize<MacroPlan>(snapshot, Options)
        ?? throw new JsonException("方案快照为空，无法恢复。");

    /// <summary>读取成功后才修改对象。返回定时目标名称是否随恢复发生了变化。</summary>
    public static bool Restore(MacroPlan target, string snapshot, MacroDocument? document = null,
        IEnumerable<MacroPlan>? availablePlans = null)
    {
        var restored = Read(snapshot);
        string previousName = target.Name;
        // 定时按首个同名方案解析。导入/重命名可出现同名，不能替另一个对象移动定时目标。
        bool follows = document != null && IsScheduledTarget(document, target, availablePlans ?? document.Plans);
        target.CopyFrom(restored);
        return follows && FollowScheduledRename(document!, previousName, target.Name);
    }

    public static bool IsScheduledTarget(MacroDocument document, MacroPlan plan, IEnumerable<MacroPlan> availablePlans) =>
        document.ScheduleMode != "" && ReferenceEquals(plan, availablePlans.FirstOrDefault(p => p.Name == document.ScheduledPlan));

    public static bool FollowScheduledRename(MacroDocument document, string oldName, string newName)
    {
        if (oldName == newName || document.ScheduleMode == "" || document.ScheduledPlan != oldName) return false;
        document.ScheduledPlan = newName;
        return true;
    }
}
