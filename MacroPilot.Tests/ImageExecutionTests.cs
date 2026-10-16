using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

public class ImageExecutionTests
{
    [Fact]
    public void FullScreenImageConditionUsesWholeBoundScreen()
    {
        using var template = new Bitmap(2, 2);
        using var shot = new Bitmap(5, 4);
        var condition = new ConditionItem { Type = "ImageMatch", Image = "reference" };
        Assert.True(condition.IsValid);
        var screen = new Rectangle(-5, 12, 5, 4);
        var observed = ImageConditionEvaluator.Observe(condition, template, screen, actual =>
        { Assert.Equal(screen, actual); return shot; }, default);
        Assert.True(observed.Found);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(10, 20, true)]
    [InlineData(0, 20, false)]
    [InlineData(20, 0, false)]
    [InlineData(-1, -1, false)]
    public void ImageRegionValidityAgreesWithWholeScreenConvention(int width, int height, bool valid)
    {
        var condition = new ConditionItem { Type = "ImageMatch", Image = "image", RectW = width, RectH = height };
        Assert.Equal(valid, condition.IsValid);
    }

    [Fact]
    public void MissingTemplateOutOfScreenAndCaptureFailureAreObservationErrors()
    {
        var item = new ConditionItem { Type = "ImageMatch", Image = "image", Invert = true };
        var screen = new Rectangle(0, 0, 10, 10);
        Assert.Throws<ConditionObservationException>(() => ImageConditionEvaluator.Observe(item, null, screen, _ => null, default));
        using var template = new Bitmap(2, 2);
        Assert.Throws<ConditionObservationException>(() => ImageConditionEvaluator.Observe(item, template, screen, _ => null, default));
        item.RectX = 20; item.RectW = item.RectH = 10;
        Assert.Throws<ConditionObservationException>(() => ImageConditionEvaluator.Observe(item, template, screen,
            _ => throw new Exception("Out-of-bounds regions must not be captured"), default));
    }

    [Fact]
    public void SourceCopyBoundsFollowNewMonitorPositionAndRejectStaleOrOversizedRegions()
    {
        var moved = new Rectangle(-1920, 100, 1920, 1080);
        Assert.True(ScreenCapture.TrySourceRegion(moved, new Rectangle(-1910, 120, 100, 50), out var relative));
        Assert.Equal(new Rectangle(10, 20, 100, 50), relative);
        Assert.False(ScreenCapture.TrySourceRegion(new Rectangle(0, 0, 1920, 1080), new Rectangle(-1910, 120, 100, 50), out _));
        Assert.False(ScreenCapture.TrySourceRegion(moved, new Rectangle(-1910, 120, 3000, 50), out _));
        Assert.False(ScreenCapture.TrySourceRegion(moved, new Rectangle(int.MaxValue - 10, 120, 100, 50), out _));
    }

    [Fact]
    public void SpatialNmsPreservesOriginalGreedyResultsAndReadingOrder()
    {
        var random = new Random(4812);
        var input = Enumerable.Range(0, 2500).Select(_ => (x: random.Next(300), y: random.Next(200), score: random.Next(4) / 4.0)).ToList();
        var expected = input.OrderByDescending(p => p.score).ThenBy(p => p.y).ThenBy(p => p.x).ToList();
        var kept = new List<(int x, int y, double score)>();
        foreach (var point in expected)
            if (!kept.Any(other => Math.Abs(point.x - other.x) < 8 && Math.Abs(point.y - other.y) < 6)) kept.Add(point);
        kept = kept.OrderBy(p => p.y).ThenBy(p => p.x).ToList();
        Assert.Equal(kept, ScreenMatch.SuppressOverlaps(input, 14, 10, default));
    }

    [Fact]
    public void DenseOnePixelMatchesRemainDistinctWithoutQuadraticSuppression()
    {
        var input = Enumerable.Range(0, 40_000).Select(i => (x: i % 200, y: i / 200, score: 1.0)).ToList();
        Assert.Equal(40_000, ScreenMatch.SuppressOverlaps(input, 1, 1, default).Count);
    }

    [Fact]
    public void CancellationDuringDenseSuppressionFinishesPromptly()
    {
        // Cancel from inside each phase, independent of thread-pool scheduling or machine speed.
        using var sortCancellation = new CancellationTokenSource();
        var values = Enumerable.Range(0, 10_000).Reverse().ToList();
        int comparisons = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => ScreenMatch.SortCancelable(values, (a, b) =>
        {
            if (++comparisons == 300) sortCancellation.Cancel();
            return a.CompareTo(b);
        }, sortCancellation.Token));
        Assert.InRange(comparisons, 300, 511); // next periodic checkpoint, not only after all sorting

        using var suppressionCancellation = new CancellationTokenSource();
        int enumerated = 0;
        IEnumerable<(int x, int y, double score)> Candidates()
        {
            for (int i = 0; i < 10_000; i++)
            {
                if (++enumerated == 100) suppressionCancellation.Cancel();
                yield return (i % 100, i / 100, 1.0);
            }
        }
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ScreenMatch.SuppressSortedOverlaps(Candidates(), 1, 1, suppressionCancellation.Token));
        Assert.Equal(100, enumerated); // the current candidate's checkpoint must stop enumeration
    }
}
