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

    /// <summary>
    /// 图片搜索允许占用的核数：0 或负数＝不限（用满所有逻辑核，默认）。
    /// 搜索期间被选中的核基本是满载的——本工具常与游戏同时跑，需要时可以留几个核给前台。
    /// 由配置页写入（<see cref="Models.MacroDocument.MatchMaxCores"/>），运行期读取。
    /// </summary>
    public static int MaxParallelism { get; set; }

    private static System.Threading.Tasks.ParallelOptions Po(System.Threading.CancellationToken ct)
    {
        int cores = Environment.ProcessorCount;
        int dop = MaxParallelism > 0 ? Math.Min(MaxParallelism, cores) : -1;   // 超过本机核数没有意义
        return new System.Threading.Tasks.ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = dop };
    }
    private const double DiagSlack = 0.15;   // 统计"最高相似度"时相对阈值放宽的幅度（仅供诊断，不影响命中判定）

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
        => FindMatches(template, regionVx, regionVy, regionW, regionH, threshold, out _);

    /// <summary>
    /// 同上，另外给出区域内的【最高相似度】bestScore——未命中时用它判断"是画面真变了还是只差一点"。
    /// 为算得出这个值，早退预算比阈值放宽 <see cref="DiagSlack"/>；低于 (阈值-DiagSlack) 的位置照旧早早放弃，
    /// 此时 bestScore 返回 0，表示"远低于阈值"。
    /// </summary>
    public static List<(int cx, int cy, double score)> FindMatches(
        Bitmap? template, int regionVx, int regionVy, int regionW, int regionH, double threshold, out double bestScore)
        => FindMatches(template, regionVx, regionVy, regionW, regionH, threshold, out bestScore, System.Threading.CancellationToken.None);

    /// <summary>
    /// 同上，另外接受取消令牌：**逐行检查取消**。一次搜索在大区域上可能要几百毫秒到数秒，
    /// 期间不理会取消的话，用户按下停止（F11）要等这一整轮搜完才生效，手感就是"按了没反应"。
    /// 取消时抛 <see cref="OperationCanceledException"/>，与引擎其它可取消点一致。
    /// </summary>
    public static List<(int cx, int cy, double score)> FindMatches(
        Bitmap? template, int regionVx, int regionVy, int regionW, int regionH, double threshold,
        out double bestScore, System.Threading.CancellationToken ct)
    {
        bestScore = 0;
        var empty = new List<(int cx, int cy, double score)>();
        ct.ThrowIfCancellationRequested();   // 抓屏也要时间，已取消就别开工了
        if (template == null || template.Width <= 0 || template.Height <= 0) return empty;
        if (regionW < template.Width || regionH < template.Height) return empty;

        Bitmap shot;
        try { shot = CaptureRegion(regionVx, regionVy, regionW, regionH); }
        catch { return empty; }
        try { return FindIn(shot, template, threshold, out bestScore, ct, regionVx, regionVy); }
        finally { shot.Dispose(); }
    }

    /// <summary>
    /// 在【给定位图】里搜模板——<see cref="FindMatches"/> 的实际实现，把"抓屏"和"搜索"分开：
    /// 于是能拿真实截图离线压测 / 回归这套算法（改它之前照例先离线验一遍），单测也不必依赖屏幕。
    /// 返回的命中中心点已加上 offsetX/offsetY（调用方传区域在虚拟桌面上的左上角）。
    /// </summary>
    public static List<(int cx, int cy, double score)> FindIn(
        Bitmap shot, Bitmap template, double threshold, out double bestScore,
        System.Threading.CancellationToken ct = default, int offsetX = 0, int offsetY = 0)
    {
        bestScore = 0;
        var result = new List<(int, int, double)>();
        int tw = template.Width, th = template.Height;
        int regionW = shot.Width, regionH = shot.Height;
        if (tw <= 0 || th <= 0 || regionW < tw || regionH < th) return result;

        var dt = template.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var ds = shot.LockBits(new Rectangle(0, 0, regionW, regionH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var tbuf = new byte[dt.Stride * th];
        var sbuf = new byte[ds.Stride * regionH];
        Marshal.Copy(dt.Scan0, tbuf, 0, tbuf.Length);
        Marshal.Copy(ds.Scan0, sbuf, 0, sbuf.Length);
        int tStride = dt.Stride, sStride = ds.Stride;
        template.UnlockBits(dt); shot.UnlockBits(ds);   // shot 由调用方释放（FindMatches 用完即弃，离线验证要复用）

        var full = new Matcher(tbuf, tStride, tw, th, sbuf, sStride, regionW, regionH);
        var raw = new List<(int x, int y, double score)>();
        double best = 0;

        // ---- 先粗后精 ----
        // 全分辨率直扫的代价 = 位置数 × 模板像素数：整屏 2560×1440 配 364×41 的模板就是 307 万个位置 ×
        // 上千次比较，14 核并行也要 0.9 秒。先在 1/k 缩略图上筛一遍，每个位置的比较量降 k² 倍，
        // 候选再回原图用【原来那套一模一样的判定】精验 → 命中与分数完全不变。
        //
        // 【为什么要遍历 k² 个相位】缩略图是按 k×k 分块平均出来的，块的边界固定在 0,k,2k…；
        // 而目标在屏幕上的位置是任意的，只要它没落在块边界上，块里就混进了周围的背景，
        // 粗筛分数会掉下去 → 真目标被漏掉（实测：贴在 4 的倍数坐标上能找到，贴在 x%4=2 上就丢了）。
        // 把区域按 (px,py) 各偏移 0..k-1 各缩一次，就一定有一个相位与目标严丝合缝，于是不可能漏。
        // 相位共 k² 个、每个位置数是原来的 1/k²，位置总数不变，但每个位置的比较量降到 1/k²。
        int scale = ChooseScale(tw, th, regionW, regionH);
        bool coarseUsed = false;
        if (scale > 1)
        {
            var (ctb, ctStride, ctw, cth) = Decimate(null, tbuf, tStride, tw, th, 0, 0, scale);
            if (ctw >= 6 && cth >= 4)
            {
                var sat = new Sat(sbuf, sStride, regionW, regionH);
                double coarseThr = Math.Max(0.5, threshold - CoarseSlack);
                var candidates = new List<(int x, int y)>();
                bool overflow = false;
                for (int py = 0; py < scale && !overflow; py++)
                    for (int px = 0; px < scale && !overflow; px++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var (cs, csStride, cw, chh) = Decimate(sat, null, 0, regionW, regionH, px, py, scale);
                        if (cw < ctw || chh < cth) continue;
                        var coarse = new Matcher(ctb, ctStride, ctw, cth, cs, csStride, cw, chh);
                        var cand = coarse.Scan(0, cw - ctw, 0, chh - cth, coarseThr, coarseThr, ct, out _);
                        foreach (var c in cand)
                        {
                            candidates.Add((c.x * scale + px, c.y * scale + py));
                            if (candidates.Count > MaxCoarseCandidates) { overflow = true; break; }   // 粗筛没起到过滤作用，回退全量扫
                        }
                    }
                if (!overflow)
                {
                    coarseUsed = true;
                    var seen = new HashSet<long>();
                    foreach (var (cx, cy) in candidates)
                    {
                        // 相位已经把对齐钉死了，精验只需在 ±1 的邻域内兜一下取整误差
                        int x0 = Math.Max(0, cx - 1), x1 = Math.Min(regionW - tw, cx + 1);
                        int y0 = Math.Max(0, cy - 1), y1 = Math.Min(regionH - th, cy + 1);
                        if (x1 < x0 || y1 < y0) continue;
                        if (!seen.Add(((long)x0 << 32) | (uint)y0)) continue;   // 邻域重叠，去重省一次扫描
                        var hits = full.Scan(x0, x1, y0, y1, threshold, Math.Max(0, threshold - DiagSlack), ct, out double b);
                        if (b > best) best = b;
                        raw.AddRange(hits);
                    }
                }
            }
        }
        if (!coarseUsed)
            raw = full.Scan(0, regionW - tw, 0, regionH - th, threshold, Math.Max(0, threshold - DiagSlack), ct, out best);

        bestScore = best;

        // 非极大值抑制：按分数降序贪心接受，抑制与已接受项中心距离在半个模板内的其它候选（同一目标的邻近位置）。
        // 同分时再按位置排（上→下、左→右）：并行扫描回来的顺序本身不确定，只按分数排的话，
        // 一堆同分候选进 NMS 的先后每次都可能不同 → 同一屏幕两次搜索的结果个数/位置会飘，
        // 「点击第 N 个」就不稳。加上位置这一级比较，结果与线程调度彻底无关。
        raw.Sort((p, q) =>
        {
            int c = q.score.CompareTo(p.score);
            if (c != 0) return c;
            c = p.y.CompareTo(q.y);
            return c != 0 ? c : p.x.CompareTo(q.x);
        });
        var kept = new List<(int x, int y, double score)>();
        foreach (var c in raw)
        {
            bool near = false;
            foreach (var k2 in kept)
                if (Math.Abs(c.x - k2.x) < tw / 2 + 1 && Math.Abs(c.y - k2.y) < th / 2 + 1) { near = true; break; }
            if (!near) kept.Add(c);
        }
        // 阅读顺序（上→下、左→右）排序，供「第几个」稳定索引。
        kept.Sort((p, q) => p.y != q.y ? p.y.CompareTo(q.y) : p.x.CompareTo(q.x));
        foreach (var k3 in kept)
            result.Add((offsetX + k3.x + tw / 2, offsetY + k3.y + th / 2, k3.score));
        return result;
    }

    // 缩略图上放宽的阈值幅度。相位对齐后，真目标在缩略图上的块均值与模板缩略图【逐块一致】，
    // 分数本应接近满分；放宽是为了容忍屏幕上的轻微差异（抗锯齿/动画），宁多勿漏——多出来的候选精验会淘汰。
    private const double CoarseSlack = 0.15;
    private const int MaxCoarseCandidates = 4000; // 粗筛没起到过滤作用时（候选过多）回退全量扫
    private const int MinCoarseArea = 250_000;    // 位置数低于此值时全量扫本来就很快，不值得再缩一遍图
    // 模板太小也不值得：缩略图省下的是"每个位置的比较量"，而准备工作（积分图 + k² 张缩略图）
    // 的开销只跟【区域面积】有关。实测 32×32 及以下时准备工作反而占大头（快 1.0 倍甚至倒退到 0.4 倍），
    // 64×48（≈3000 像素）起才稳定获益。
    private const int MinCoarseTemplatePixels = 3000;
    private const int MinCoarseTemplateKept = 250;   // 缩略图模板至少要保留这么多像素，否则筛不准也摊不平

    /// <summary>选缩放倍数：模板缩完不能太小（细节全糊就筛不准），扫描量太小则不缩。</summary>
    private static int ChooseScale(int tw, int th, int rw, int rh)
    {
        long positions = (long)Math.Max(0, rw - tw + 1) * Math.Max(0, rh - th + 1);
        if (positions < MinCoarseArea) return 1;
        if ((long)tw * th < MinCoarseTemplatePixels) return 1;
        // k 越大越省：相位共 k² 个、每相位位置数 1/k²（位置总数不变），但每个位置的比较量降到 1/k²。
        // 上限由"缩完还认得出结构"决定——模板缩到 10×6 以下就只剩几个色块，粗筛会放过一大片。
        // k 越大，每个位置的比较量越省（1/k²）；但缩略图模板本身也在变小，小到只剩几十个像素时
        // 既筛不准（候选暴涨、精验白跑）又摊不平 k² 个相位的固定开销——实测 64×48 用 k=6（36 个相位、
        // 缩略图只剩 10×8）反而比全量扫还慢。所以再加一条：缩完至少要留住 ~250 个像素。
        foreach (int k in new[] { 8, 6, 5, 4, 3, 2 })
            if (tw / k >= 10 && th / k >= 6 && (tw / k) * (th / k) >= MinCoarseTemplateKept) return k;
        return 1;
    }

    /// <summary>
    /// 积分图（每通道一张）：算任意矩形的像素和都是 O(1)。相位缩略图要算 k²×(W/k)×(H/k) 个块均值，
    /// 逐块累加会重复扫整幅图 k² 遍，有了它就只扫一遍。
    /// </summary>
    private sealed class Sat
    {
        private readonly int[] _b, _g, _r;
        private readonly int _w;
        public Sat(byte[] src, int stride, int w, int h)
        {
            _w = w + 1;
            _b = new int[_w * (h + 1)]; _g = new int[_w * (h + 1)]; _r = new int[_w * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride, cur = (y + 1) * _w, prev = y * _w;
                int rb = 0, rg = 0, rr = 0;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    rb += src[i]; rg += src[i + 1]; rr += src[i + 2];
                    _b[cur + x + 1] = _b[prev + x + 1] + rb;
                    _g[cur + x + 1] = _g[prev + x + 1] + rg;
                    _r[cur + x + 1] = _r[prev + x + 1] + rr;
                }
            }
        }
        public (int b, int g, int r) Sum(int x, int y, int w, int h)
        {
            int a = y * _w + x, bb = y * _w + x + w, c = (y + h) * _w + x, d = (y + h) * _w + x + w;
            return (_b[d] - _b[bb] - _b[c] + _b[a], _g[d] - _g[bb] - _g[c] + _g[a], _r[d] - _r[bb] - _r[c] + _r[a]);
        }
    }

    /// <summary>
    /// k×k 盒式平均缩小（BGRA），从 (px,py) 起分块——(px,py) 即"相位"。
    /// 传 sat 就走积分图（区域用），否则直接逐块累加（模板用，只缩一次）。
    /// 平均比抽样稳：轻微位移/抗锯齿差异会被抹平，粗筛才不会误杀真目标。
    /// </summary>
    private static (byte[] buf, int stride, int w, int h) Decimate(
        Sat? sat, byte[]? src, int stride, int w, int h, int px, int py, int k)
    {
        int nw = (w - px) / k, nh = (h - py) / k;
        if (nw <= 0 || nh <= 0) return (Array.Empty<byte>(), 0, 0, 0);
        int nstride = nw * 4;
        var dst = new byte[nstride * nh];
        int m = k * k;
        for (int y = 0; y < nh; y++)
        {
            int drow = y * nstride;
            for (int x = 0; x < nw; x++)
            {
                int sb, sg, sr;
                if (sat != null) (sb, sg, sr) = sat.Sum(px + x * k, py + y * k, k, k);
                else
                {
                    sb = sg = sr = 0;
                    for (int dy = 0; dy < k; dy++)
                    {
                        int srow = (py + y * k + dy) * stride + (px + x * k) * 4;
                        for (int dx = 0; dx < k; dx++)
                        { int i = srow + dx * 4; sb += src![i]; sg += src[i + 1]; sr += src[i + 2]; }
                    }
                }
                int o = drow + x * 4;
                dst[o] = (byte)(sb / m); dst[o + 1] = (byte)(sg / m); dst[o + 2] = (byte)(sr / m); dst[o + 3] = 255;
            }
        }
        return (dst, nstride, nw, nh);
    }

    /// <summary>
    /// 一次匹配所需的预处理与扫描：模板像素按【梯度权重降序】排好，配合预算早退。
    /// 抽成类是为了让"缩略图粗筛"和"原图精验"共用同一套判定——精度不打折的前提就在这里。
    /// </summary>
    private sealed class Matcher
    {
        private readonly byte[] _s;
        private readonly int _sStride, _rw, _rh, _tw, _th, _n;
        private readonly int[] _wS, _sOff;
        private readonly byte[] _tB, _tG, _tR;
        private readonly long _totalW;

        public Matcher(byte[] tbuf, int tStride, int tw, int th, byte[] sbuf, int sStride, int rw, int rh)
        {
            _s = sbuf; _sStride = sStride; _rw = rw; _rh = rh; _tw = tw; _th = th; _n = tw * th;
            // 权重 = 1 + min(15, 亮度梯度/6)：梯度取与右/下邻的亮度差绝对值的较大者。
            var weight = new int[_n];
            var lum = new float[_n];
            for (int y = 0; y < th; y++)
            {
                int row = y * tStride;
                for (int x = 0; x < tw; x++)
                {
                    int i = row + x * 4;
                    lum[y * tw + x] = 0.114f * tbuf[i] + 0.587f * tbuf[i + 1] + 0.299f * tbuf[i + 2];   // BGRA
                }
            }
            long total = 0;
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    int i = y * tw + x;
                    float gx = x + 1 < tw ? Math.Abs(lum[i + 1] - lum[i]) : 0;
                    float gy = y + 1 < th ? Math.Abs(lum[i + tw] - lum[i]) : 0;
                    int w = 1 + Math.Min(15, (int)(Math.Max(gx, gy) / 6));
                    weight[i] = w; total += w;
                }
            _totalW = total;
            // 结构像素优先：按权重降序排列（同时预取排好序的模板 BGR 与屏幕偏移，避免内层反查）。
            var order = new int[_n];
            for (int i = 0; i < _n; i++) order[i] = i;
            Array.Sort(order, (a, b) => weight[b].CompareTo(weight[a]));
            _wS = new int[_n]; _tB = new byte[_n]; _tG = new byte[_n]; _tR = new byte[_n]; _sOff = new int[_n];
            for (int k = 0; k < _n; k++)
            {
                int i = order[k]; int y = i / tw, x = i % tw;
                _wS[k] = weight[i];
                int ti = y * tStride + x * 4;
                _tB[k] = tbuf[ti]; _tG[k] = tbuf[ti + 1]; _tR[k] = tbuf[ti + 2];
                _sOff[k] = y * _sStride + x * 4;   // 相对滑窗左上角的屏幕缓冲偏移
            }
        }

        /// <summary>
        /// 扫描 [x0,x1]×[y0,y1] 这些左上角位置。hitThr=判为命中的阈值；diagThr=早退门槛（比 hitThr 低，
        /// 用于统计"最高相似度"这个诊断值）。返回命中列表，bestScore 给出扫到的最高分。
        /// </summary>
        public List<(int x, int y, double score)> Scan(
            int x0, int x1, int y0, int y1, double hitThr, double diagThr,
            System.Threading.CancellationToken ct, out double bestScore)
        {
            var raw = new List<(int x, int y, double score)>();
            bestScore = 0;
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(x1, _rw - _tw); y1 = Math.Min(y1, _rh - _th);
            if (x1 < x0 || y1 < y0) return raw;
            long budget = (long)(_totalW * (1.0 - hitThr));
            long diagBudget = (long)(_totalW * (1.0 - diagThr));
            double best = 0;
            var lk = new object();

            void ScanRow(int oy, List<(int x, int y, double score)> hits, ref double localBest)
            {
                // 先把字段抓成局部：内层是几十亿次的热循环，字段访问会挡住 JIT 的边界检查消除，
                // 实测（不走粗筛的小模板）差 10~20%。
                byte[] sb = _s, tb = _tB, tg = _tG, tr = _tR;
                int[] off = _sOff, wS = _wS;
                int n = _n; long total = _totalW;
                int rowBase = oy * _sStride;
                for (int ox = x0; ox <= x1; ox++)
                {
                    int baseOff = rowBase + ox * 4;
                    long pen = 0;
                    bool ok = true;
                    for (int k = 0; k < n; k++)
                    {
                        int si = baseOff + off[k];
                        if (Math.Abs(tb[k] - sb[si]) > Tolerance ||
                            Math.Abs(tg[k] - sb[si + 1]) > Tolerance ||
                            Math.Abs(tr[k] - sb[si + 2]) > Tolerance)
                        {
                            pen += wS[k];
                            if (pen > diagBudget) { ok = false; break; }   // 连"接近"都算不上，放弃该位置
                        }
                    }
                    if (!ok) continue;
                    double score = 1.0 - (double)pen / total;
                    if (score > localBest) localBest = score;
                    if (pen <= budget) hits.Add((ox, oy, score));          // 达阈值才算命中
                }
            }

            // 按行并行：位置之间互不相干，共享缓冲全只读。行数很少时（精验小窗口）并行反而是负担，直接串行。
            if (y1 - y0 < 8)
            {
                for (int oy = y0; oy <= y1; oy++) { ct.ThrowIfCancellationRequested(); ScanRow(oy, raw, ref best); }
            }
            else
            {
                var po = Po(ct);
                try
                {
                    System.Threading.Tasks.Parallel.For(y0, y1 + 1, po,
                        () => (hits: new List<(int x, int y, double score)>(), best: 0.0),
                        (oy, _, local) => { double lb = local.best; ScanRow(oy, local.hits, ref lb); return (local.hits, lb); },
                        local => { lock (lk) { raw.AddRange(local.hits); if (local.best > best) best = local.best; } });
                }
                catch (AggregateException ex) when (ex.InnerException is OperationCanceledException oce) { throw oce; }
            }
            bestScore = best;
            return raw;
        }
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
