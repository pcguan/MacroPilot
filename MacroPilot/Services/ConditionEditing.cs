using System;
using System.Collections.Generic;
using MacroPilot.Models;

namespace MacroPilot.Services;

public static class ConditionEditing
{
    // 不过滤条目：删掉任意一条会改变 AND/OR 或表达式序号的语义。
    public static IReadOnlyList<ConditionItem> Validate(IReadOnlyList<ConditionItem> items)
    {
        for (int i = 0; i < items.Count; i++)
            if (!items[i].IsValid) throw new InvalidOperationException($"第 {i + 1} 条条件配置不完整，请编辑或删除该条条件。");
        return items;
    }
}
