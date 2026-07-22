using System;
using System.Windows;
using MacroPilot.Models;

namespace MacroPilot.Services;

/// <summary>
/// 窗口几何记忆：位置 / 大小 / 最大化状态按 key 存进 settings.json，下次打开还原。
/// **主窗口和所有对话框走同一套**（对话框由 MainWindow.MakeDialog 统一挂上，key = 标题）。
/// 坐标单位是 WPF 的 DIP，与 SystemParameters.VirtualScreen* 同一坐标系，不用自己换算 DPI。
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

    // 与虚拟桌面的交集要够大：只露出一条边（比如副屏被拔掉后残留的坐标）不算可用。
    private static bool IsUsablyOnScreen(Rect r)
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                          SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var hit = Rect.Intersect(r, vs);
        return !hit.IsEmpty && hit.Width >= 160 && hit.Height >= 80;
    }
}
