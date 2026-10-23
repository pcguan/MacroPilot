using System;
using System.Collections.Generic;
using System.Linq;
using MacroPilot.Input;
using Xunit;
using Point = MacroPilot.Input.MouseRoutePlanner.Point;

namespace MacroPilot.Tests;

public sealed class MouseRouteTests
{
    private static ScreenInfo.Monitor M(string name, int l, int t, int r, int b, bool primary = false)
        => new(name, l, t, r, b, primary);
    public static IEnumerable<object[]> Layouts()
    {
        yield return new object[] { new[] { M("A", 0, 0, 1920, 1080, true), M("B", 1920, 0, 3840, 1080), M("C", 3000, -1080, 4920, 0) } };
        yield return new object[] { new[] { M("A", 0, 0, 2560, 1440, true), M("B", 2560, -1076, 4480, 4) } };
        yield return new object[] { new[] { M("A", 0, 0, 1920, 1080, true), M("B", -1080, -840, 0, 1080), M("C", -3000, -840, -1080, 240) } };
        yield return new object[] { new[] { M("A", 0, 0, 800, 600, true), M("B", 800, -599, 1600, 1) } };
        yield return new object[] { new[] { M("A", -800, -600, 0, 0), M("B", 0, -1, 800, 599, true) } };
        yield return new object[] { new[] { M("A", 0, 0, 800, 600, true), M("B", 400, 600, 1200, 1200), M("C", 1200, 800, 2000, 1400) } };
    }

    [Theory, MemberData(nameof(Layouts))]
    public void EveryDirectedPairAndBoundaryRouteStaysOnPhysicalScreens(ScreenInfo.Monitor[] screens)
    {
        foreach (var from in screens)
        foreach (var to in screens)
        foreach (var start in new[] { new Point(from.Left, from.Top), new Point(from.Right - 1, from.Bottom - 1) })
        foreach (var end in new[] { new Point(to.Left, to.Top), new Point(to.Right - 1, to.Bottom - 1) })
        {
            var route = MouseRoutePlanner.Plan(screens, start, end);
            var position = start; long now = 0;
            var controller = new RelativeMoveController(() => (position.X, position.Y), (dx, dy) =>
            {
                position = new(position.X + dx, position.Y + dy);
                Assert.Contains(screens, m => m.Contains(position.X, position.Y));
            }, _ => { }, _ => now++, () => now);
            foreach (var leg in route)
            {
                Assert.Equal(position, leg.From);
                if (leg.Screen != null)
                {
                    Assert.True(leg.Screen.Contains(leg.From.X, leg.From.Y));
                    Assert.True(leg.Screen.Contains(leg.To.X, leg.To.Y));
                }
                else
                {
                    Assert.True(leg.From.X == leg.To.X || leg.From.Y == leg.To.Y);
                    Assert.InRange(Math.Abs(leg.From.X - leg.To.X) + Math.Abs(leg.From.Y - leg.To.Y), 1, 3);
                }
                controller.MoveTo(leg.To.X, leg.To.Y, default);
            }
            Assert.Equal(end, position);
        }
    }

    [Fact]
    public void LegacyDiagonalHopHitsGapButPairedExitRouteArrives()
    {
        var screens = new[] { M("A", 0, 0, 1920, 1080, true), M("B", 1920, 0, 3840, 1080), M("C", 3000, -1080, 4920, 0) };
        var start = new Point(1921, 540); var end = new Point(3420, -2);
        var p = start; long now = 0;
        var controller = new RelativeMoveController(() => (p.X, p.Y), (dx, dy) =>
        {
            var proposed = new Point(p.X + dx, p.Y + dy);
            // Model a desktop edge that blocks a packet directed into the gap.
            if (screens.Any(m => m.Contains(proposed.X, proposed.Y))) p = proposed;
        }, _ => { }, _ => now++, () => now);
        Assert.Throws<InputSafetyException>(() => controller.MoveTo(end.X, end.Y, default));
        p = start; now = 0;
        controller = new RelativeMoveController(() => (p.X, p.Y), (dx, dy) => p = new(p.X + dx, p.Y + dy), _ => { }, _ => now++, () => now);
        foreach (var leg in MouseRoutePlanner.Plan(screens, start, end)) controller.MoveTo(leg.To.X, leg.To.Y, default);
        Assert.Equal(end, p);
    }

    [Theory]
    [InlineData(800, 600)] // corner contact
    [InlineData(801, 0)]   // gap
    public void DisconnectedLayoutIsRejectedBeforeExecution(int left, int top)
    {
        var screens = new[] { M("A", 0, 0, 800, 600, true), M("B", left, top, left + 800, top + 600) };
        Assert.Throws<InputSafetyException>(() => MouseRoutePlanner.Plan(screens, new(100, 100), new(left + 100, top + 100)));
    }

    [Fact]
    public void AdjacentSecondaryScreensDoNotRouteThroughPrimary()
    {
        var screens = new[] { M("A", 0, 0, 800, 600, true), M("B", 800, 0, 1600, 600), M("C", 1600, 0, 2400, 600) };
        var route = MouseRoutePlanner.Plan(screens, new(900, 200), new(1700, 200));
        Assert.All(route, leg => { Assert.True(leg.From.X >= 800); Assert.True(leg.To.X >= 800); });
    }

    [Fact]
    public void OffscreenTargetAndChangedTopologyAreDetected()
    {
        var a = M("A", 0, 0, 800, 600, true);
        Assert.Throws<InputSafetyException>(() => MouseRoutePlanner.Plan(new[] { a }, new(10, 10), new(800, 0)));
        Assert.True(MouseRoutePlanner.SameTopology(new[] { a }, new[] { a with { Number = 2 } }));
        Assert.False(MouseRoutePlanner.SameTopology(new[] { a }, new[] { a with { Right = 801 } }));
        Assert.False(MouseRoutePlanner.SameTopology(new[] { a }, Array.Empty<ScreenInfo.Monitor>()));
    }
}
