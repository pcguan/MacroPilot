using System;
using System.Threading;
using System.Threading.Tasks;

namespace MacroPilot.Services;

/// <summary>最终保存检查必须在全部异步准备之后；检查与交接之间不再让出 UI 线程。</summary>
public static class UpdatePreparation
{
    public static async Task<bool> ApplyWhenReadyAsync(Func<Task> prepare, Func<bool> ready, Action apply, CancellationToken ct)
    {
        await prepare();
        ct.ThrowIfCancellationRequested();
        if (!ready()) return false;
        ct.ThrowIfCancellationRequested();
        apply();
        return true;
    }
}
