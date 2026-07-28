using System.Drawing;
using System.Threading;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

/// <summary>
/// 图片搜索算法本体（<see cref="ScreenMatch.FindIn"/>：只搜给定位图，不碰屏幕，故可确定性回归）。
/// 这套算法历史上出过两类事故：等权比对导致满屏假命中、隔点扫描漏掉真目标——都得用真实几何来把关。
/// </summary>
public class ScreenMatchTests
{
    // 造一个"平坦底 + 有结构"的模板：与真实场景一致（图标/文字周围大片同色），也正是等权算法会翻车的形状
    private static Bitmap MakeTemplate(int w = 40, int h = 20)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        using var f = new Font("Arial", 10);
        g.DrawString("Ab7", f, Brushes.Black, 1, 1);
        g.DrawRectangle(Pens.Red, 0, 0, w - 1, h - 1);
        return bmp;
    }

    private static Bitmap MakeScene(int w = 600, int h = 400)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);                                  // 大片平坦白底：等权比对的经典陷阱
        using var f = new Font("Arial", 10);
        for (int i = 0; i < 12; i++) g.DrawString("xyz", f, Brushes.Gray, 20 + i * 40, 300);
        return bmp;
    }

    private static void Paste(Bitmap scene, Bitmap tpl, int x, int y)
    {
        using var g = Graphics.FromImage(scene);
        g.DrawImage(tpl, x, y, tpl.Width, tpl.Height);
    }

    [Fact]
    public void 能在场景里找到模板且中心点正确()
    {
        using var tpl = MakeTemplate();
        using var scene = MakeScene();
        Paste(scene, tpl, 137, 91);
        var hits = ScreenMatch.FindIn(scene, tpl, 0.9, out double best);
        Assert.Single(hits);
        Assert.Equal(137 + tpl.Width / 2, hits[0].cx);
        Assert.Equal(91 + tpl.Height / 2, hits[0].cy);
        Assert.True(best > 0.99, $"贴进去的应当接近满分，实际 {best}");
    }

    [Fact]
    public void 模板不在场景里就不该有命中()
    {
        using var tpl = MakeTemplate();
        using var scene = MakeScene();
        var hits = ScreenMatch.FindIn(scene, tpl, 0.9, out _);
        Assert.Empty(hits);   // 大片白底不能被算成"90% 像素一致"
    }

    [Fact]
    public void 多处出现时按从上到下从左到右排序()
    {
        using var tpl = MakeTemplate();
        using var scene = MakeScene();
        Paste(scene, tpl, 300, 200);
        Paste(scene, tpl, 60, 200);    // 同一行更靠左
        Paste(scene, tpl, 150, 40);    // 更靠上
        var hits = ScreenMatch.FindIn(scene, tpl, 0.9, out _);
        Assert.Equal(3, hits.Count);
        Assert.Equal(150 + tpl.Width / 2, hits[0].cx);   // 最上面那个
        Assert.True(hits[1].cx < hits[2].cx, "同一行按从左到右");
    }

    [Fact]
    public void 偏移量会加到命中坐标上()
    {
        using var tpl = MakeTemplate();
        using var scene = MakeScene();
        Paste(scene, tpl, 100, 100);
        var hits = ScreenMatch.FindIn(scene, tpl, 0.9, out _, CancellationToken.None, offsetX: 1920, offsetY: -50);
        Assert.Single(hits);
        Assert.Equal(1920 + 100 + tpl.Width / 2, hits[0].cx);
        Assert.Equal(-50 + 100 + tpl.Height / 2, hits[0].cy);
    }

    [Fact]
    public void 重叠命中会被非极大值抑制去重()
    {
        using var tpl = MakeTemplate();
        using var scene = MakeScene();
        Paste(scene, tpl, 200, 150);
        Paste(scene, tpl, 204, 152);   // 几乎重叠：同一个目标，不该报成两个
        var hits = ScreenMatch.FindIn(scene, tpl, 0.9, out _);
        Assert.Single(hits);
    }

    [Fact]
    public void 已取消的令牌会立刻抛出()
    {
        using var tpl = MakeTemplate();
        using var scene = MakeScene();
        Paste(scene, tpl, 100, 100);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<System.OperationCanceledException>(
            () => ScreenMatch.FindIn(scene, tpl, 0.9, out _, cts.Token));
    }

    [Fact]
    public void 区域比模板还小时直接返回空()
    {
        using var tpl = MakeTemplate(80, 60);
        using var scene = MakeScene(50, 50);
        Assert.Empty(ScreenMatch.FindIn(scene, tpl, 0.9, out _));
    }
}
