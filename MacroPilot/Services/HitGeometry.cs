using System;
using System.Windows;

namespace MacroPilot.Services;

/// <summary>
/// 覆盖层上"点有没有落在某个图形上"的纯几何判定（DIP 空间，无 UI 依赖，可单测）。
///
/// 标注编辑的核心规则：**只认轮廓，不认内部**——点在图形的边框上才算命中它（选中 / 拖动），
/// 图形内部永远留给"接着画新图形"。这样在一个矩形里再画一个矩形不会变成把外面那个拖走。
/// </summary>
public static class HitGeometry
{
    /// <summary>点到线段的距离。</summary>
    public static double DistToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        double t = len2 <= 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double qx = a.X + t * dx, qy = a.Y + t * dy;
        return Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
    }

    /// <summary>点是否落在矩形的轮廓带上（外扩 tol 之内、且不在内缩 tol 之中）。</summary>
    public static bool NearRectOutline(Rect b, Point p, double tol)
    {
        if (p.X < b.X - tol || p.X > b.Right + tol || p.Y < b.Y - tol || p.Y > b.Bottom + tol) return false;
        return !(p.X > b.X + tol && p.X < b.Right - tol && p.Y > b.Y + tol && p.Y < b.Bottom - tol);
    }

    /// <summary>点是否落在椭圆轮廓上：按半轴归一化后与单位圆比距离，再换算回像素。太扁太小就退化成矩形轮廓。</summary>
    public static bool NearEllipseOutline(Rect b, Point p, double tol)
    {
        double ra = b.Width / 2, rb = b.Height / 2;
        if (ra < 2 || rb < 2) return NearRectOutline(b, p, tol);
        double dx = (p.X - (b.X + ra)) / ra, dy = (p.Y - (b.Y + rb)) / rb;
        return Math.Abs(Math.Sqrt(dx * dx + dy * dy) - 1) * Math.Min(ra, rb) <= tol;
    }

    /// <summary>
    /// 命中包围盒上的 8 个缩放手柄（四角 + 四边中点），返回 nw/n/ne/w/e/sw/s/se；没命中返回 ""。
    /// 名字与 <c>RectPicker</c> 的抓取方式一致，可直接喂给它。
    /// </summary>
    public static string HandleHit(Rect b, Point p, double tol)
    {
        (double x, double y, string k)[] hs =
        {
            (b.X, b.Y, "nw"), (b.X + b.Width / 2, b.Y, "n"), (b.Right, b.Y, "ne"),
            (b.X, b.Y + b.Height / 2, "w"), (b.Right, b.Y + b.Height / 2, "e"),
            (b.X, b.Bottom, "sw"), (b.X + b.Width / 2, b.Bottom, "s"), (b.Right, b.Bottom, "se"),
        };
        foreach (var h in hs)
            if (Math.Abs(p.X - h.x) <= tol && Math.Abs(p.Y - h.y) <= tol) return h.k;
        return "";
    }
}
