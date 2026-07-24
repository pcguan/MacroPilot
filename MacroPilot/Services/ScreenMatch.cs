using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace MacroPilot.Services;

/// <summary>
/// 屏幕图片条件：抓取虚拟桌面指定区域，与目标图逐像素比对（固定位置匹配）。
/// 纯 GDI（System.Drawing），无第三方依赖；可在后台线程调用。
/// </summary>
public static class ScreenMatch
{
    private const int Tolerance = 28;   // 单通道容差，抗轻微色差/抗锯齿

    /// <summary>抓取虚拟桌面某区域（虚拟像素）为 Bitmap（调用方负责 Dispose）。</summary>
    public static Bitmap CaptureRegion(int vx, int vy, int w, int h)
    {
        var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(vx, vy, 0, 0, new Size(bmp.Width, bmp.Height), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    public static Bitmap FromPng(byte[] png)
    {
        using var ms = new MemoryStream(png);
        // 复制一份，脱离流生命周期
        using var loaded = new Bitmap(ms);
        return new Bitmap(loaded);
    }

    /// <summary>目标图与当前屏幕同位置区域比对：每通道差 ≤ 容差算相同，相同占比 ≥ threshold 即命中。</summary>
    public static bool Matches(byte[]? targetPng, int vx, int vy, int w, int h, double threshold)
    {
        if (targetPng == null || targetPng.Length == 0 || w <= 0 || h <= 0) return false;
        try { using var target = FromPng(targetPng); return Matches(target, vx, vy, threshold); }
        catch { return false; }
    }

    /// <summary>模板已解码好时的重载：循环里挂图片条件不必每圈都 base64+PNG 解码（调用方缓存 Bitmap）。</summary>
    public static bool Matches(Bitmap? target, int vx, int vy, double threshold)
        => MatchScore(target, vx, vy) >= threshold;

    /// <summary>返回同位置区域与模板的匹配度 0..1（无模板/异常返回 -1）。供日志显示实际匹配度、便于调阈值。</summary>
    public static double MatchScore(Bitmap? target, int vx, int vy)
    {
        if (target == null || target.Width <= 0 || target.Height <= 0) return -1;
        try
        {
            using var shot = CaptureRegion(vx, vy, target.Width, target.Height);
            return MatchRatio(target, shot);
        }
        catch { return -1; }
    }

    /// <summary>
    /// 在指定屏幕区域内滑窗搜索模板，返回所有相似度 ≥ threshold 的命中【中心点】（虚拟像素），
    /// 经非极大值抑制去重、并按从上到下·从左到右排序（供「点击第几个」索引）。
    /// 早退优化：某位置差异像素超过阈值允许的上限就立刻放弃，非命中位置很便宜。
    /// </summary>
    public static List<(int cx, int cy, double score)> FindMatches(
        Bitmap? template, int regionVx, int regionVy, int regionW, int regionH, double threshold, int step = 2)
    {
        var result = new List<(int, int, double)>();
        if (template == null || template.Width <= 0 || template.Height <= 0) return result;
        int tw = template.Width, th = template.Height;
        if (regionW < tw || regionH < th) return result;

        Bitmap shot;
        try { shot = CaptureRegion(regionVx, regionVy, regionW, regionH); }
        catch { return result; }

        var rt = new Rectangle(0, 0, tw, th);
        var rs = new Rectangle(0, 0, regionW, regionH);
        var dt = template.LockBits(rt, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var ds = shot.LockBits(rs, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int tStride = dt.Stride, sStride = ds.Stride;
        var tbuf = new byte[tStride * th];
        var sbuf = new byte[sStride * regionH];
        Marshal.Copy(dt.Scan0, tbuf, 0, tbuf.Length);
        Marshal.Copy(ds.Scan0, sbuf, 0, sbuf.Length);
        template.UnlockBits(dt); shot.UnlockBits(ds); shot.Dispose();

        var raw = new List<(int x, int y, double score)>();
        long total = (long)tw * th;
        long allowedMiss = (long)(total * (1.0 - threshold));   // 差异超过它即不可能达标
        for (int oy = 0; oy + th <= regionH; oy += step)
        {
            for (int ox = 0; ox + tw <= regionW; ox += step)
            {
                long miss = 0;
                bool ok = true;
                for (int y = 0; y < th && ok; y++)
                {
                    int trow = y * tStride;
                    int srow = (oy + y) * sStride + ox * 4;
                    for (int x = 0; x < tw; x++)
                    {
                        int ti = trow + x * 4, si = srow + x * 4;
                        if (Math.Abs(tbuf[ti] - sbuf[si]) > Tolerance ||
                            Math.Abs(tbuf[ti + 1] - sbuf[si + 1]) > Tolerance ||
                            Math.Abs(tbuf[ti + 2] - sbuf[si + 2]) > Tolerance)
                        {
                            if (++miss > allowedMiss) { ok = false; break; }
                        }
                    }
                }
                if (ok) raw.Add((ox, oy, 1.0 - (double)miss / total));
            }
        }

        // 非极大值抑制：按分数降序贪心接受，抑制与已接受项中心距离在半个模板内的其它候选（同一目标的邻近位置）。
        raw.Sort((p, q) => q.score.CompareTo(p.score));
        var kept = new List<(int x, int y, double score)>();
        foreach (var c in raw)
        {
            bool near = false;
            foreach (var k in kept)
                if (Math.Abs(c.x - k.x) < tw / 2 + 1 && Math.Abs(c.y - k.y) < th / 2 + 1) { near = true; break; }
            if (!near) kept.Add(c);
        }
        // 阅读顺序（上→下、左→右）排序，供「第几个」稳定索引。
        kept.Sort((p, q) => p.y != q.y ? p.y.CompareTo(q.y) : p.x.CompareTo(q.x));
        foreach (var k in kept)
            result.Add((regionVx + k.x + tw / 2, regionVy + k.y + th / 2, k.score));
        return result;
    }

    private static double MatchRatio(Bitmap a, Bitmap b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        if (w <= 0 || h <= 0) return 0;
        var ra = new Rectangle(0, 0, w, h);
        var da = a.LockBits(ra, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(ra, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int strideA = da.Stride, strideB = db.Stride;
            var rowA = new byte[strideA];
            var rowB = new byte[strideB];
            long same = 0, total = (long)w * h;
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(da.Scan0 + y * strideA, rowA, 0, strideA);
                Marshal.Copy(db.Scan0 + y * strideB, rowB, 0, strideB);
                for (int x = 0; x < w; x++)
                {
                    int i = x * 4;
                    if (Math.Abs(rowA[i] - rowB[i]) <= Tolerance &&
                        Math.Abs(rowA[i + 1] - rowB[i + 1]) <= Tolerance &&
                        Math.Abs(rowA[i + 2] - rowB[i + 2]) <= Tolerance)
                        same++;
                }
            }
            return total == 0 ? 0 : (double)same / total;
        }
        finally { a.UnlockBits(da); b.UnlockBits(db); }
    }
}
