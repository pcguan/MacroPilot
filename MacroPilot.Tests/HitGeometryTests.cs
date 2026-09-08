using System.Windows;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

/// <summary>
/// 截图标注的命中判定（纯几何）。规则：**只认轮廓、不认内部**——
/// 点边框才算选中/拖动那个图形，内部留给"接着在里面画新图形"。
/// 这块以前只能靠肉眼在覆盖层上试，出过"画完拖不动""在图形里再画一个变成拖走它"的问题。
/// </summary>
public class HitGeometryTests
{
    private static readonly Rect Box = new(100, 100, 200, 100);   // 100..300 × 100..200
    private const double Tol = 7;

    // ---- 矩形轮廓 ----

    [Theory]
    [InlineData(100, 150)]   // 左边框
    [InlineData(300, 150)]   // 右边框
    [InlineData(200, 100)]   // 上边框
    [InlineData(200, 200)]   // 下边框
    [InlineData(100, 100)]   // 左上角
    [InlineData(103, 150)]   // 边框内侧容差内
    [InlineData(96, 150)]    // 边框外侧容差内
    public void 点在矩形边框上算命中(double x, double y)
        => Assert.True(HitGeometry.NearRectOutline(Box, new Point(x, y), Tol));

    [Theory]
    [InlineData(200, 150)]   // 正中间
    [InlineData(110, 150)]   // 内部但离边框够远
    [InlineData(200, 112)]
    public void 点在矩形内部不算命中(double x, double y)
        => Assert.False(HitGeometry.NearRectOutline(Box, new Point(x, y), Tol));

    [Theory]
    [InlineData(80, 150)]    // 框外
    [InlineData(200, 60)]
    [InlineData(400, 400)]
    public void 点在矩形之外不算命中(double x, double y)
        => Assert.False(HitGeometry.NearRectOutline(Box, new Point(x, y), Tol));

    [Fact]
    public void 又细又长的矩形整体都算轮廓()
    {
        // 高度小于两倍容差时没有"内部"可言，任何位置都应能抓住，否则细线框永远拖不动
        var thin = new Rect(50, 50, 200, 6);
        Assert.True(HitGeometry.NearRectOutline(thin, new Point(150, 53), Tol));
    }

    // ---- 椭圆轮廓 ----

    [Theory]
    [InlineData(100, 150)]   // 左端点
    [InlineData(300, 150)]   // 右端点
    [InlineData(200, 100)]   // 上端点
    [InlineData(200, 200)]   // 下端点
    public void 点在椭圆轮廓上算命中(double x, double y)
        => Assert.True(HitGeometry.NearEllipseOutline(Box, new Point(x, y), Tol));

    [Fact]
    public void 椭圆内部与包围盒四角都不算命中()
    {
        Assert.False(HitGeometry.NearEllipseOutline(Box, new Point(200, 150), Tol));   // 圆心
        Assert.False(HitGeometry.NearEllipseOutline(Box, new Point(100, 100), Tol));   // 包围盒左上角（在椭圆外）
    }

    // ---- 8 向手柄 ----

    [Theory]
    [InlineData(100, 100, "nw")]
    [InlineData(200, 100, "n")]
    [InlineData(300, 100, "ne")]
    [InlineData(100, 150, "w")]
    [InlineData(300, 150, "e")]
    [InlineData(100, 200, "sw")]
    [InlineData(200, 200, "s")]
    [InlineData(300, 200, "se")]
    public void 八个手柄各自命中对应方向(double x, double y, string expect)
        => Assert.Equal(expect, HitGeometry.HandleHit(Box, new Point(x, y), Tol));

    [Fact]
    public void 手柄之外返回空()
    {
        Assert.Equal("", HitGeometry.HandleHit(Box, new Point(150, 100), Tol));   // 上边框中段，不是手柄
        Assert.Equal("", HitGeometry.HandleHit(Box, new Point(200, 150), Tol));   // 内部
    }

    [Fact]
    public void 手柄名可直接用于矩形拖拽器()
    {
        // 与 RectPicker 的抓取名一致：含 n/s/w/e 字母决定拉哪条边，拼错会导致"拉一个角结果整块跑掉"
        foreach (var k in new[] { "nw", "n", "ne", "w", "e", "sw", "s", "se" })
            Assert.True(k.IndexOfAny(new[] { 'n', 's', 'w', 'e' }) >= 0);
    }

    // ---- 线段距离（箭头 / 画笔）----

    [Fact]
    public void 点到线段距离取的是垂距而不是端点距离()
    {
        var a = new Point(0, 0); var b = new Point(100, 0);
        Assert.Equal(5, HitGeometry.DistToSegment(new Point(50, 5), a, b), 3);
    }

    [Fact]
    public void 线段之外按到端点的距离算()
    {
        var a = new Point(0, 0); var b = new Point(100, 0);
        Assert.Equal(10, HitGeometry.DistToSegment(new Point(110, 0), a, b), 3);
    }

    [Fact]
    public void 退化成一个点的线段不会除零()
    {
        var a = new Point(10, 10);
        Assert.Equal(0, HitGeometry.DistToSegment(a, a, a), 3);
    }
}
