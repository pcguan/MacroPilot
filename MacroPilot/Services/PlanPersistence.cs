using System;
using System.Collections.Generic;
using System.Linq;
using MacroPilot.Models;

namespace MacroPilot.Services;

/// <summary>
/// 先在副本上准备图片与保存基线，再写入；失败保留当前对象、脏标记和旧基线。
/// 写入成功只提交外置图片引用与条件迁移，保留所有活对象和 UI 状态。
/// </summary>
public static class PlanPersistence
{
    public static bool TrySave(IReadOnlyList<MacroPlan> plans, Action<MacroPlan> prepare,
        Func<IReadOnlyList<MacroPlan>, bool> write, out string error)
    {
        error = "";
        List<MacroPlan> prepared;
        List<string> snapshots;
        try
        {
            prepared = plans.Select(plan => plan.Clone()).ToList();
            foreach (var plan in prepared) prepare(plan);
            // 序列化可能因损坏数据/过深嵌套失败；必须在写盘前确认所有基线都可建立。
            snapshots = prepared.Select(PlanSnapshot.Capture).ToList();
            if (!write(prepared))
            {
                error = "无法写入数据文件，请检查目录权限和磁盘空间。";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        for (int i = 0; i < plans.Count; i++)
        {
            ImageStore.ApplyPreparedReferences(plans[i], prepared[i]);
            plans[i].SavedSnapshot = snapshots[i];
            plans[i].Dirty = false;
        }
        return true;
    }
}
