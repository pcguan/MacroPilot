using System;
using System.Threading;
using System.Threading.Tasks;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

public class UpdatePreparationTests
{
    [Fact]
    public async Task 下载期间新编辑必须经最终保存检查()
    {
        var download = new TaskCompletionSource<bool>();
        bool dirty = false, applied = false, checkedDirty = false;
        var task = UpdatePreparation.ApplyWhenReadyAsync(() => download.Task,
            () => { checkedDirty = dirty; return !dirty; }, () => applied = true, default);
        dirty = true;
        download.SetResult(true);
        Assert.False(await task);
        Assert.True(checkedDirty);
        Assert.False(applied);
    }

    [Fact]
    public async Task 取消下载不会进入保存或安装且可再次更新()
    {
        using var cancel = new CancellationTokenSource();
        bool applied = false, checkedSave = false;
        var task = UpdatePreparation.ApplyWhenReadyAsync(() => Task.Delay(10000, cancel.Token),
            () => { checkedSave = true; return true; }, () => applied = true, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(checkedSave); Assert.False(applied);
        Assert.True(await UpdatePreparation.ApplyWhenReadyAsync(() => Task.CompletedTask, () => true, () => applied = true, default));
        Assert.True(applied);
    }

    [Fact]
    public async Task 安装启动异常不报告交接成功()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdatePreparation.ApplyWhenReadyAsync(
            () => Task.CompletedTask, () => true, () => throw new InvalidOperationException("launch failed"), default));
    }
}
