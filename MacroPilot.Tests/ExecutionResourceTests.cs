using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MacroPilot.Models;
using MacroPilot.Services;
using Xunit;

namespace MacroPilot.Tests;

public class ExecutionResourceTests
{
    [Theory]
    [InlineData("(){2147483647}")]
    [InlineData("((){4096}){4096}")]
    [InlineData("a{4097}")]
    [InlineData("^(?=a)a$")]
    [InlineData("^(?!a)b$")]
    [InlineData(@"(a)\1")]
    public void UnsupportedOrUnboundedRandomPatternsFailWithReadableErrors(string pattern)
    {
        var error = Assert.Throws<FormatException>(() => RandomText.Generate(pattern));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void RandomPatternHasDepthAndCancellationLimits()
    {
        Assert.Throws<FormatException>(() => RandomText.Generate(new string('(', 65) + "a" + new string(')', 65)));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => RandomText.Generate("a{1000}", cts.Token));
        Assert.Equal("abab", RandomText.Generate("(?:ab){2}"));
        // U+FFFF must not wrap the range iterator back to zero forever.
        Assert.Single(RandomText.Generate("[\ufffe-\uffff]"));
    }

    [Theory]
    [InlineData(new int[] { 30, 30, 30, 30 }, 1)]
    [InlineData(new int[] { 180, 180 }, 3)]
    [InlineData(new int[] { -30, -30, -30, -30 }, -1)]
    [InlineData(new int[] { 60, -180 }, -1)]
    [InlineData(new int[] { 60, -60 }, 0)]
    public void RecordedWheelPreservesSubNotchDeltas(int[] deltas, int expected)
    {
        var events = deltas.Select((d, i) => new RecEvent(i * 20, RecKind.Wheel, 0, 0, d)).ToArray();
        var steps = RecordingCompiler.Compile(events, (_, _) => ("screen", 0, 0));
        Assert.Equal(expected, steps.Where(s => s.Type == "MouseWheel").Sum(s => s.Wheel));
        Assert.DoesNotContain(steps, s => s.Type == "MouseWheel" && s.Wheel == 0);
    }

    [Theory]
    [InlineData(nameof(IRunCondition.RunConditionRetryIntervalUnit))]
    [InlineData(nameof(IRunCondition.RunConditionRetryTimeoutUnit))]
    public void ConditionSnapshotIncludesDisplayUnits(string property)
    {
        var plan = new MacroPlan();
        string before = RunCondition.Snapshot(plan);
        var prop = typeof(IRunCondition).GetProperty(property)!;
        prop.SetValue(plan, (int)prop.GetValue(plan)! + 1);
        Assert.NotEqual(before, RunCondition.Snapshot(plan));
    }

    [Fact]
    public void ConditionSnapshotPreservesFullThresholdPrecisionAndSpecialCharacters()
    {
        var plan = new MacroPlan();
        plan.RunConditions.Add(new ConditionItem { Type = "ImageMatch", Image = "image,;|", Threshold = 0.90001 });
        string before = RunCondition.Snapshot(plan);
        plan.RunConditions[0].Threshold = 0.90002;
        Assert.NotEqual(before, RunCondition.Snapshot(plan));
    }

    [Fact]
    public void ClipboardLeaseRestoresNonTextAndAllOriginalFormats()
    {
        var original = new Dictionary<string, object> { ["Bitmap"] = new byte[] { 1, 2 }, ["FileDrop"] = new[] { "file.png" }, ["HTML"] = "<b>hello</b>" };
        var clipboard = new FakeClipboard { Data = original };
        var lease = new ClipboardLease(clipboard, "macro");
        Assert.Equal("macro", clipboard.Data);
        lease.Restore();
        Assert.Same(original, clipboard.Data);
        Assert.Equal(1, clipboard.Restores);
        lease.Restore();
        Assert.Equal(1, clipboard.Restores);
    }

    [Fact]
    public void ClipboardLeaseRestoresEmptyClipboardAndPreservesNewExternalCopy()
    {
        var clipboard = new FakeClipboard();
        new ClipboardLease(clipboard, "macro").Restore();
        Assert.Null(clipboard.Data);
        var lease = new ClipboardLease(clipboard, "macro");
        clipboard.SetText("new user copy");
        lease.Restore();
        Assert.Equal("new user copy", clipboard.Data);
    }

    [Fact]
    public void ClipboardLeaseDoesNotOverwriteWhenBackupFailedOrChangedDuringRead()
    {
        var clipboard = new FakeClipboard { Data = "original", FailCapture = true };
        Assert.Throws<InvalidOperationException>(() => new ClipboardLease(clipboard, "macro"));
        Assert.Equal("original", clipboard.Data);
        clipboard.FailCapture = false; clipboard.ChangeDuringCapture = true;
        Assert.Throws<InvalidOperationException>(() => new ClipboardLease(clipboard, "macro"));
        Assert.Equal("external", clipboard.Data);
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public object? Data;
        public uint Sequence { get; private set; }
        public bool FailCapture, ChangeDuringCapture;
        public int Restores;
        public object? Capture()
        {
            if (FailCapture) throw new InvalidOperationException("Cannot back up");
            var result = Data;
            if (ChangeDuringCapture) SetText("external");
            return result;
        }
        public void SetText(string text) { Data = text; Sequence++; }
        public void Restore(object? snapshot) { Data = snapshot; Sequence++; Restores++; }
    }
}
