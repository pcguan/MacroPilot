using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MacroPilot.Services;

/// <summary>
/// 抓屏服务：现代路径（DXGI 桌面复制，直接从系统合成器拿帧，全屏 1~5ms）+ 兼容回退（GDI
/// CopyFromScreen，全屏 20~40ms）。**两条路径都保留**：
///   · 进程启动时 <see cref="Probe"/> 探测一次——老驱动 / 老系统 / 远程会话建不出桌面复制就整场用 GDI，
///     不在每次方案运行时反复试探；
///   · 即便探测通过，单次抓取仍可能失败（换分辨率 AccessLost、UAC 安全桌面、区域跨屏、竖屏旋转），
///     失败就该次回退 GDI——正确性永远优先于速度。
/// 会话按显示器各建一份并复用；staging 纹理保留上一帧，屏幕没变化（AcquireNextFrame 超时）时直接用它。
/// </summary>
public static class ScreenCapture
{
    /// <summary>现代抓屏是否可用（启动时 Probe 的结论，整个进程生命周期内不变）。</summary>
    public static bool ModernAvailable { get; private set; }
    /// <summary>供日志/界面显示："DXGI" 或 "GDI"。</summary>
    public static string ModeDesc => ModernAvailable ? "DXGI" : "GDI";
    /// <summary>基准测试/测试用：强制走 GDI 路径。</summary>
    public static bool ForceLegacy { get; set; }

    private static readonly object _lock = new();
    private static ID3D11Device? _device;
    private static ID3D11DeviceContext? _context;
    private static readonly Dictionary<int, OutputSession> _sessions = new();   // 键 = 输出下标

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void RtlMoveMemory(IntPtr dst, IntPtr src, UIntPtr len);

    private sealed class OutputSession
    {
        public IDXGIOutputDuplication? Dup;
        public ID3D11Texture2D? Staging;
        public int Left, Top, Width, Height;   // 该输出在虚拟桌面上的矩形（物理像素）
        public bool HasFrame;                  // staging 里是否已有一帧（超时=画面没变，直接复用）
        public bool Broken;                    // 建复制失败（旋转屏等）：本输出恒走 GDI
    }

    /// <summary>
    /// 启动时探测一次：能建出 D3D11 硬件设备 + 主输出的桌面复制即认为现代路径可用。
    /// 失败静默回退 GDI（老驱动/虚拟机/远程会话都属正常情况，不是错误）。
    /// </summary>
    public static void Probe()
    {
        try
        {
            var r = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out var dev);
            if (r.Failure || dev == null) { ModernAvailable = false; return; }
            using var dxgi = dev.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            if (adapter.EnumOutputs(0u, out var output0).Failure || output0 == null) { dev.Dispose(); ModernAvailable = false; return; }
            using (output0)
            using (var o1 = output0.QueryInterface<IDXGIOutput1>())
            using (var dup = o1.DuplicateOutput(dev))
            {
                ModernAvailable = dup != null;
            }
            _device = dev;
            _context = dev.ImmediateContext;
        }
        catch { ModernAvailable = false; }
    }

    /// <summary>
    /// 抓取虚拟桌面矩形（物理像素）为 32bppArgb Bitmap（调用方负责 Dispose）。
    /// 现代路径要求区域完整落在单个未旋转的输出内，否则该次自动走 GDI；GDI 也失败则抛出。
    /// </summary>
    public static Bitmap Capture(int vx, int vy, int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (ModernAvailable && !ForceLegacy)
        {
            try
            {
                lock (_lock)
                {
                    var bmp = TryModern(vx, vy, w, h);
                    if (bmp != null) return bmp;
                }
            }
            catch { /* 任何现代路径异常都落到 GDI */ }
        }
        return Legacy(vx, vy, w, h);
    }

    private static Bitmap Legacy(int vx, int vy, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(vx, vy, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    private static Bitmap? TryModern(int vx, int vy, int w, int h)
    {
        if (_device == null || _context == null) return null;
        int idx = FindOutput(vx, vy, w, h, out var sess);
        if (idx < 0 || sess == null || sess.Broken) return null;

        if (sess.Dup == null && !CreateSession(idx, sess)) return null;

        // 排空到最新帧：每帧 CopyResource 进 staging（staging 因此始终保有最近一帧）
        bool got = DrainFrames(sess);
        if (!got && !sess.HasFrame)
        {
            // 刚建会话还没拿到过帧：稍等一帧的时间再试一次，仍没有就 GDI（比如安全桌面期间）
            got = DrainFrames(sess, 100);
            if (!got && !sess.HasFrame) return null;
        }

        // 从 staging 拷出请求的区域
        var mapped = _context.Map(sess.Staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int srcX = vx - sess.Left, srcY = vy - sess.Top;
                for (int row = 0; row < h; row++)
                {
                    IntPtr src = mapped.DataPointer + (srcY + row) * (int)mapped.RowPitch + srcX * 4;
                    RtlMoveMemory(bd.Scan0 + row * bd.Stride, src, (UIntPtr)(w * 4));
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
        finally { _context.Unmap(sess.Staging!, 0); }
    }

    // 找到完整包含请求区域的输出；跨屏/屏外 → -1（走 GDI）。
    private static int FindOutput(int vx, int vy, int w, int h, out OutputSession? sess)
    {
        sess = null;
        using var dxgi = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        for (int i = 0; ; i++)
        {
            if (adapter.EnumOutputs((uint)i, out var output).Failure || output == null) break;
            using (output)
            {
                var rc = output.Description.DesktopCoordinates;
                if (vx >= rc.Left && vy >= rc.Top && vx + w <= rc.Right && vy + h <= rc.Bottom)
                {
                    if (!_sessions.TryGetValue(i, out var s))
                    {
                        s = new OutputSession { Left = rc.Left, Top = rc.Top, Width = rc.Right - rc.Left, Height = rc.Bottom - rc.Top };
                        _sessions[i] = s;
                    }
                    sess = s;
                    return i;
                }
            }
        }
        return -1;
    }

    private static bool CreateSession(int idx, OutputSession sess)
    {
        try
        {
            using var dxgi = _device!.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            if (adapter.EnumOutputs((uint)idx, out var output).Failure || output == null) { sess.Broken = true; return false; }
            using (output)
            using (var o1 = output.QueryInterface<IDXGIOutput1>())
            {
                var dup = o1.DuplicateOutput(_device);
                // 竖屏/旋转输出的帧数据是转过的，拷出来还要转回去——不值得，这类输出恒走 GDI
                if (dup.Description.Rotation != ModeRotation.Identity)
                { dup.Dispose(); sess.Broken = true; return false; }
                sess.Dup = dup;
            }
            sess.Staging?.Dispose();
            sess.Staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)sess.Width, Height = (uint)sess.Height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
            });
            sess.HasFrame = false;
            return true;
        }
        catch { sess.Broken = true; return false; }
    }

    // 把可用帧全部取出、最后一帧留在 staging；返回是否取到过新帧。AccessLost（换分辨率/安全桌面）时重建一次会话。
    private static bool DrainFrames(OutputSession sess, int firstTimeoutMs = 0)
    {
        bool got = false;
        int timeout = firstTimeoutMs;
        while (true)
        {
            var r = sess.Dup!.AcquireNextFrame((uint)timeout, out _, out var res);
            timeout = 0;
            if (r.Success && res != null)
            {
                using (res)
                using (var tex = res.QueryInterface<ID3D11Texture2D>())
                    _context!.CopyResource(sess.Staging!, tex);
                sess.Dup.ReleaseFrame();
                sess.HasFrame = true; got = true;
                continue;
            }
            if (r == Vortice.DXGI.ResultCode.WaitTimeout) return got;
            // AccessLost 等：会话作废，重建一次；再失败本次放弃（外层回退 GDI），下次调用还会重试重建
            sess.Dup.Dispose(); sess.Dup = null; sess.HasFrame = false;
            return got;
        }
    }
}
