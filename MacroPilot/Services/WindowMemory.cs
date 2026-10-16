using System;
using System.Windows;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using MacroPilot.Input;
using MacroPilot.Models;

namespace MacroPilot.Services;

/// <summary>
/// 窗口几何记忆：位置 / 大小 / 最大化状态按 key 存进 settings.json，下次打开还原。
/// **主窗口和所有对话框走同一套**（对话框由 MainWindow.MakeDialog 统一挂上，key = 标题）。
/// 保存坐标使用 WPF DIP；恢复时把显示器物理工作区换算为系统 DIP 再逐屏验证。
/// </summary>
public static class WindowMemory
{
    private static MacroDocument? _doc;
    private static Action? _persist;

    /// <summary>启动时接上存储（doc 与"写盘"动作），未初始化时所有调用都是空操作。</summary>
    public static void Init(MacroDocument doc, Action persist)
    {
        _doc = doc; _persist = persist;
    }

    /// <summary>
    /// 给窗口挂上记忆：Show 前还原，关闭时保存。必须在窗口显示之前调用，
    /// 否则会看到窗口先按默认位置弹出再跳一下。
    /// </summary>
    public static void Attach(Window w, string key)
    {
        if (_doc == null || string.IsNullOrEmpty(key)) return;
        Restore(w, key);
        // 用 Closing 而不是 Closed：Closed 时 Left/Top/RestoreBounds 可能已失效。
        w.Closing += (_, _) => Save(w, key);
    }

    private static void Restore(Window w, string key)
    {
        if (_doc == null || !_doc.Windows.TryGetValue(key, out var g)) return;
        // 尊重窗口自己的最小尺寸；记录不合法（老数据 / 手改坏了）就当没记过。
        double minW = double.IsNaN(w.MinWidth) || w.MinWidth <= 0 ? 120 : w.MinWidth;
        double minH = double.IsNaN(w.MinHeight) || w.MinHeight <= 0 ? 80 : w.MinHeight;
        if (!double.IsFinite(g.Left) || !double.IsFinite(g.Top) || !double.IsFinite(g.Width) || !double.IsFinite(g.Height)) return;
        bool sizeOk = g.Width >= minW && g.Height >= minH;
        var rect = new Rect(g.Left, g.Top, sizeOk ? g.Width : 0, sizeOk ? g.Height : 0);

        if (sizeOk && IsUsablyOnScreen(rect))
        {
            // 对话框默认按内容自适应高度（SizeToContent），恢复固定尺寸前必须先关掉，
            // 否则设置的 Height 会立刻被内容测量覆盖回去。
            w.SizeToContent = SizeToContent.Manual;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = g.Left; w.Top = g.Top; w.Width = g.Width; w.Height = g.Height;
            w.Tag = RestoredTag;   // 告诉 MakeDialog 的 Loaded 逻辑：别再按内容重算尺寸/挪到鼠标处
        }
        if (g.Maximized && w.ResizeMode != ResizeMode.NoResize) w.WindowState = WindowState.Maximized;
    }

    /// <summary>标记该窗口的尺寸/位置来自记忆，调用方据此跳过自动定位与自适应高度。</summary>
    public const string RestoredTag = "GeometryRestored";

    public static bool WasRestored(Window w) => ReferenceEquals(w.Tag, RestoredTag) || (w.Tag as string) == RestoredTag;

    private static void Save(Window w, string key)
    {
        if (_doc == null) return;
        // 最大化/最小化时 Left/Width 是当前显示状态的值，要取 RestoreBounds（还原后的大小），
        // 否则下次取消最大化会得到一个全屏尺寸的普通窗口。
        var r = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight) : w.RestoreBounds;
        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0 || double.IsNaN(r.Width) || double.IsNaN(r.Height)) return;
        var g = new WindowGeometry
        {
            Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height,
            Maximized = w.WindowState == WindowState.Maximized,
        };
        if (_doc.Windows.TryGetValue(key, out var old) && old.Equals(g)) return;   // 没变就别写盘
        _doc.Windows[key] = g;
        _persist?.Invoke();
    }

    // 逐个实际工作区检查，不能把多屏之间的空洞当成可见桌面。
    private static bool IsUsablyOnScreen(Rect r)
    {
        double scale = 1;
        try { scale = Math.Max(1, GetDpiForSystem()) / 96.0; } catch { }
        return IsUsablyOnScreen(r, ScreenInfo.All().Select(m => new Rect(
            m.WorkLeft / scale, m.WorkTop / scale,
            (m.WorkRight - m.WorkLeft) / scale, (m.WorkBottom - m.WorkTop) / scale)));
    }

    public static bool IsUsablyOnScreen(Rect window, IEnumerable<Rect> workAreas)
    {
        if (window.IsEmpty || window.Width < 160 || window.Height < 80) return false;
        var title = new Rect(window.Left, window.Top, window.Width, Math.Min(32, window.Height));
        foreach (var area in workAreas)
        {
            var body = Rect.Intersect(window, area);
            var handle = Rect.Intersect(title, area);
            if (!body.IsEmpty && body.Width >= 160 && body.Height >= 80 &&
                !handle.IsEmpty && handle.Width >= 160 && handle.Height >= 24) return true;
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}
