using System.Drawing;
using System.Threading;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

/// <summary>
/// 图片搜索的可取消性。一次搜索在大区域上可能要几百毫秒到数秒，期间不理会取消的话，
/// 用户按下停止（F11）得等这一整轮搜完才生效——手感就是"按了没反应"。
/// </summary>
public class ScreenMatchCancelTests
{
    [Fact]
    public void 已取消的令牌会让搜索立刻抛出而不是搜完整块区域()
    {
        using var tpl = new Bitmap(16, 16);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<System.OperationCanceledException>(
            () => ScreenMatch.FindMatches(tpl, 0, 0, 600, 600, 0.9, out _, cts.Token));
    }

    [Fact]
    public void 不传令牌时行为不变()
    {
        // 老调用点（不关心取消）照常返回结果，不应因为新增重载而抛异常
        using var tpl = new Bitmap(16, 16);
        var hits = ScreenMatch.FindMatches(tpl, 0, 0, 200, 200, 0.9);
        Assert.NotNull(hits);
    }
}
