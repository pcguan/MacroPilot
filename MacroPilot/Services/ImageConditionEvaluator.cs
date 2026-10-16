using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using MacroPilot.Models;
using MacroPilot.Input;

namespace MacroPilot.Services;

/// <summary>观测失败不能被 ! / 取反变成成立；异常穿过布尔组合，结束本次执行。</summary>
internal sealed class ConditionObservationException : InvalidOperationException
{
    public ConditionObservationException(string message, Exception? inner = null) : base(message, inner) { }
}

internal static class ImageConditionEvaluator
{
    // capture 返回的位图归调用方的本轮缓存所有，本方法只读，不释放它。
    internal static (bool Found, string Text) Observe(ConditionItem item, Bitmap? template, Rectangle screen,
        Func<Rectangle, Bitmap?> capture, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (template == null) throw new ConditionObservationException("图片条件无法观测：模板缺失或损坏。");
        if (!ConditionItem.IsImageRegionValid(item.RectW, item.RectH))
            throw new ConditionObservationException("图片条件无法观测：限制区域宽高无效。");
        var region = screen;
        if (item.RectW > 0)
        {
            // 用 long 算边界，避免导入的极端坐标溢出后落到另一块区域。
            long left = Math.Max((long)screen.Left + item.RectX, screen.Left);
            long top = Math.Max((long)screen.Top + item.RectY, screen.Top);
            long right = Math.Min((long)screen.Left + item.RectX + item.RectW, screen.Right);
            long bottom = Math.Min((long)screen.Top + item.RectY + item.RectH, screen.Bottom);
            if (right <= left || bottom <= top)
                throw new ConditionObservationException("图片条件无法观测：限制区域不在当前屏幕范围内。");
            region = new Rectangle((int)left, (int)top, (int)(right - left), (int)(bottom - top));
        }
        if (region.Width <= 0 || region.Height <= 0)
            throw new ConditionObservationException("图片条件无法观测：显示器范围无效。");
        var sw = Stopwatch.StartNew();
        try
        {
            var shot = capture(region) ?? throw new ConditionObservationException("图片条件无法观测：抓屏失败。");
            double threshold = Math.Clamp(item.Threshold, 0.5, 1.0);
            var hits = ScreenMatch.FindIn(shot, template, threshold, out double best, ct, region.X, region.Y);
            bool found = hits.Count > 0;
            string text = found
                ? $"目标图片已出现（命中 {hits.Count} 个，最高相似度 {best:0.00} / 阈值 {threshold:0.00}，耗时 {sw.ElapsedMilliseconds}ms）"
                : $"目标图片未出现（区域内最高相似度 {(best > 0 ? best.ToString("0.00") : "低于 " + Math.Max(0, threshold - 0.15).ToString("0.00"))} / 阈值 {threshold:0.00}，耗时 {sw.ElapsedMilliseconds}ms）";
            return (found, text);
        }
        catch (OperationCanceledException) { throw; }
        catch (InputSafetyException) { throw; }
        catch (ConditionObservationException) { throw; }
        catch (Exception ex) { throw new ConditionObservationException("图片条件无法观测：" + ex.Message, ex); }
    }
}
