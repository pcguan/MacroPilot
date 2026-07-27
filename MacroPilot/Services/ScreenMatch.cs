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
    ///
    /// 相似度＝【模板梯度加权】的一致像素占比：结构像素（文字/图标边缘）权重高、平坦背景权重低。
    /// 等权的"90% 像素一致"对"平坦底+小特征"的模板会失效——特征只占 ~15% 面积时，任何一块
    /// 颜色相近的平坦区域都能"命中"（corp-win 实测 33 个假命中）；加权后结构必须对上才算相似，
    /// 同一截图实测恰好只命中真目标 1 个。
    /// 扫描为逐像素（step=1）：加权指标下偏 1px 分数会跌破阈值，隔点扫描会把真目标整个漏掉
    /// （实测真命中就落在奇数 x 上）。性能靠【结构像素优先】早退：按权重降序检查，假位置在前
    /// 一百来个结构像素上就爆掉预算提前放弃，反而比旧的逐行扫更快。
    /// </summary>
    public static List<(int cx, int cy, double score)> FindMatches(
        Bitmap? template, int regionVx, int regionVy, int regionW, int regionH, double threshold)
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

        int n = tw * th;
        // 权重 = 1 + min(15, 亮度梯度/6)：梯度取与右/下邻的亮度差绝对值的较大者。
        var weight = new int[n];
        long totalW = 0;
        {
            var lum = new float[n];
            for (int y = 0; y < th; y++)
            {
                int row = y * tStride;
                for (int x = 0; x < tw; x++)
                {
                    int i = row + x * 4;
                    lum[y * tw + x] = 0.114f * tbuf[i] + 0.587f * tbuf[i + 1] + 0.299f * tbuf[i + 2];   // BGRA
                }
            }
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    int i = y * tw + x;
                    float gx = x + 1 < tw ? Math.Abs(lum[i + 1] - lum[i]) : 0;
                    float gy = y + 1 < th ? Math.Abs(lum[i + tw] - lum[i]) : 0;
                    int w = 1 + Math.Min(15, (int)(Math.Max(gx, gy) / 6));
                    weight[i] = w; totalW += w;
                }
        }
        // 结构像素优先：模板像素按权重降序排列（同时预取排好序的模板 BGR 与屏幕偏移，避免内层反查）。
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => weight[b].CompareTo(weight[a]));
        var wS = new int[n]; var tB = new byte[n]; var tG = new byte[n]; var tR = new byte[n]; var sOff = new int[n];
        for (int k = 0; k < n; k++)
        {
            int i = order[k]; int y = i / tw, x = i % tw;
            wS[k] = weight[i];
            int ti = y * tStride + x * 4;
            tB[k] = tbuf[ti]; tG[k] = tbuf[ti + 1]; tR[k] = tbuf[ti + 2];
            sOff[k] = y * sStride + x * 4;   // 相对滑窗左上角的屏幕缓冲偏移
        }

        var raw = new List<(int x, int y, double score)>();
        long budget = (long)(totalW * (1.0 - threshold));   // 加权差异超过它即不可能达标
        for (int oy = 0; oy + th <= regionH; oy++)
        {
            int rowBase = oy * sStride;
            for (int ox = 0; ox + tw <= regionW; ox++)
            {
                int baseOff = rowBase + ox * 4;
                long pen = 0;
                bool ok = true;
                for (int k = 0; k < n; k++)
                {
                    int si = baseOff + sOff[k];
                    if (Math.Abs(tB[k] - sbuf[si]) > Tolerance ||
                        Math.Abs(tG[k] - sbuf[si + 1]) > Tolerance ||
                        Math.Abs(tR[k] - sbuf[si + 2]) > Tolerance)
                    {
                        pen += wS[k];
                        if (pen > budget) { ok = false; break; }
                    }
                }
                if (ok) raw.Add((ox, oy, 1.0 - (double)pen / totalW));
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
