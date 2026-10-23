using System;
using System.Collections.Generic;
using System.Linq;

namespace MacroPilot.Input;

/// <summary>纯几何规划：每条跨屏边必须先到出口，再进入相邻屏；不发送输入。</summary>
internal static class MouseRoutePlanner
{
    internal readonly record struct Point(int X, int Y);
    internal sealed record Leg(Point From, Point To, ScreenInfo.Monitor? Screen);

    internal static InputSafetyException CrossingFailed(Leg leg, InputSafetyException cause)
        => new($"跨屏移动受阻（{leg.From.X},{leg.From.Y} → {leg.To.X},{leg.To.Y}），已停止方案。" +
            "请检查共享边是否过窄、系统边缘限制或外部鼠标干预。" + cause.Message, cause);

    internal static void VerifyStart(Point expected)
    {
        var actual = ScreenInfo.CursorPos();
        if (Math.Abs((long)actual.vx - expected.X) > 3 || Math.Abs((long)actual.vy - expected.Y) > 3)
            throw new InputSafetyException("规划鼠标路径后检测到非预期位移，已停止移动。");
    }

    internal static void VerifyPosition(int x, int y)
    {
        var p = ScreenInfo.CursorPos();
        if (Math.Abs((long)p.vx - x) > 6 || Math.Abs((long)p.vy - y) > 6)
            throw new InputSafetyException("点击或拖动前鼠标位置已改变，已安全停止方案。");
    }

    internal static ScreenInfo.Monitor RequireScreen(IReadOnlyList<ScreenInfo.Monitor> screens, Point point)
        => screens.FirstOrDefault(m => m.Contains(point.X, point.Y))
            ?? throw new InputSafetyException($"鼠标位置 ({point.X},{point.Y}) 不在任何显示器内，已停止移动。");

    internal static bool SameTopology(IReadOnlyList<ScreenInfo.Monitor> expected, IReadOnlyList<ScreenInfo.Monitor> actual)
        => expected.Count == actual.Count && expected.All(a => actual.Any(b =>
            a.Device == b.Device && a.Left == b.Left && a.Top == b.Top && a.Right == b.Right &&
            a.Bottom == b.Bottom && a.Primary == b.Primary));

    internal static List<Leg> Plan(IReadOnlyList<ScreenInfo.Monitor> screens, Point start, Point end)
    {
        var source = RequireScreen(screens, start);
        var target = RequireScreen(screens, end);
        var previous = new Dictionary<ScreenInfo.Monitor, ScreenInfo.Monitor>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<ScreenInfo.Monitor>(ReferenceEqualityComparer.Instance) { source };
        var queue = new Queue<ScreenInfo.Monitor>();
        queue.Enqueue(source);
        while (queue.Count > 0 && !seen.Contains(target))
        {
            var current = queue.Dequeue();
            foreach (var next in screens)
                if (!seen.Contains(next) && Adjacent(current, next))
                { seen.Add(next); previous[next] = current; queue.Enqueue(next); }
        }
        if (!seen.Contains(target))
            throw new InputSafetyException("显示器之间没有可通行的共享边，无法规划鼠标路径，请检查屏幕排列。");
        var path = new List<ScreenInfo.Monitor> { target };
        while (!ReferenceEquals(path[^1], source)) path.Add(previous[path[^1]]);
        path.Reverse();
        var result = new List<Leg>();
        var position = start;
        for (int i = 0; i + 1 < path.Count; i++)
        {
            var exit = CrossPoint(path[i + 1], path[i]);
            var entry = CrossPoint(path[i], path[i + 1]);
            if (position != exit) result.Add(new(position, exit, path[i]));
            result.Add(new(exit, entry, null));
            position = entry;
        }
        if (position != end) result.Add(new(position, end, target));
        return result;
    }

    private static bool Adjacent(ScreenInfo.Monitor a, ScreenInfo.Monitor b)
        => ((a.Right == b.Left || a.Left == b.Right) && Math.Min(a.Bottom, b.Bottom) > Math.Max(a.Top, b.Top)) ||
           ((a.Bottom == b.Top || a.Top == b.Bottom) && Math.Min(a.Right, b.Right) > Math.Max(a.Left, b.Left));

    private static Point CrossPoint(ScreenInfo.Monitor a, ScreenInfo.Monitor b)
    {
        int x, y;
        if (a.Right == b.Left || a.Left == b.Right)
        {
            y = (int)(((long)Math.Max(a.Top, b.Top) + Math.Min(a.Bottom, b.Bottom)) / 2);
            // Use floor midpoint for negative coordinates and one-pixel overlaps.
            y = Math.Clamp(y, Math.Max(a.Top, b.Top), Math.Min(a.Bottom, b.Bottom) - 1);
            x = a.Right == b.Left ? b.Left + 1 : b.Right - 2;
        }
        else
        {
            x = (int)(((long)Math.Max(a.Left, b.Left) + Math.Min(a.Right, b.Right)) / 2);
            x = Math.Clamp(x, Math.Max(a.Left, b.Left), Math.Min(a.Right, b.Right) - 1);
            y = a.Bottom == b.Top ? b.Top + 1 : b.Bottom - 2;
        }
        return new(Math.Clamp(x, b.Left, b.Right - 1), Math.Clamp(y, b.Top, b.Bottom - 1));
    }
}
