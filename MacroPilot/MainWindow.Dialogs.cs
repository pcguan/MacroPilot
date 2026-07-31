using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MacroPilot.Input;
using MacroPilot.Models;
using MacroPilot.Services;

namespace MacroPilot;

// 动作 / 组合编辑对话框（代码构建，主题化，与参考标准一致）。
public partial class MainWindow
{
    // 激活窗口下拉项：包住 WinInfo，ToString 返回可搜索/显示的定长文本。
    private sealed class WinPick
    {
        public Services.WindowActivator.WinInfo Info { get; init; } = null!;
        public string Display { get; init; } = "";
        public override string ToString() => Display;
    }

    // ---------- 通用小部件 ----------
    // 字段标签：单个输入项上方的说明文字，比分组标题弱一档。
    private static TextBlock FieldLabel(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

    // 分组卡片：把一组相关设置包进带描边的面板 + 强调色标题，和其它配置组清晰隔开。
    // 对话框内的二级分组：把关联的字段包成一块、与相邻分组明确分开（细描边圆角，不抢外层卡片的层次）。
    // title 传 null 表示这组自带标题控件（如带勾选框的坐标块），不再额外加标题行。
    private Border SubGroup(string? title, params UIElement[] children)
    {
        var sp = new StackPanel();
        if (!string.IsNullOrEmpty(title))
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var c in children) sp.Children.Add(c);
        return new Border
        {
            Background = (Brush)FindResource("Bg"),   // 比外层卡(Panel)深一档，小卡呈内凹层次
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = sp,
        };
    }

    private Border GroupCard(string title, params UIElement[] children)
    {
        var content = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new Border
        {
            Width = 3, Height = 14, CornerRadius = new CornerRadius(2),
            Background = (Brush)FindResource("Accent"),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(head);
        foreach (var c in children) content.Children.Add(c);
        return new Border
        {
            Background = (Brush)FindResource("Panel"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 2),
            Margin = new Thickness(0, 0, 0, 12),
            Child = content,
        };
    }


    // 当前打开着的对话框栈（外层在前）。嵌套弹窗（编辑动作里再编辑监听动作）靠它认出"上一层是谁"：
    // 认亲之后才能把 Owner 挂到上一层（z 序正确）、位置错开（不完全盖住）、几何记忆分层存（不逐层漂移）。
    private readonly System.Collections.Generic.List<Window> _dlgStack = new();

    private Window MakeDialog(string title)
    {
        // 兜底：清掉"建了却没 Show"的残留（构造到 ShowDialog 之间若抛异常就会留下），
        // 否则后续对话框会认一个从没显示过的窗口当父窗口，直接卡住。
        _dlgStack.RemoveAll(w => !w.IsVisible && !w.IsLoaded);
        int depth = _dlgStack.Count;
        var parent = depth > 0 ? _dlgStack[^1] : (Window)this;
        var win = new Window
        {
            Title = title, Owner = parent, Width = 460, SizeToContent = SizeToContent.Height,
            MaxHeight = 640,   // 初始按内容自适应，超出则内容区滚动
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize,
            Background = Background,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI"),
        };
        win.SetResourceReference(ForegroundProperty, "Ink");
        win.SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBar(win, ThemeManager.EffectiveDark);
        // 最小宽度必须盖得住内容真实的最小需求，否则窗口能被拖到内容根本排不下的宽度，
        // 只能靠裁剪收场——「限制区域」那一行尤其脆弱：四个边输入格的标签是浮在边框【外面】画的，
        // 一旦挤压变形就会和上一行糊在一起。这一行的需求 = 标签 52 + 四格 ×86 + 卡片/对话框内边距 + 滚动条 ≈ 560。
        win.MinWidth = 580;
        win.MinHeight = 320;
        // 记住每个对话框各自调整后的位置与大小（key = 标题），与主窗口同一套机制。
        // 【嵌套层各记各的】：同名对话框套开时若共用一个 key，内层关时会把"错开后的位置"写回去，
        // 下次外层就从那个位置开，再错开一次……每套一层漂 36px，越用越偏。
        WindowMemory.Attach(win, "Dlg:" + title + (depth > 0 ? "#" + depth : ""));
        bool restored = WindowMemory.WasRestored(win);
        // 记住的高度可能超过初始 MaxHeight(640)，要立刻解除上限，否则窗口会被夹到 640。
        if (restored) { win.MaxWidth = double.PositiveInfinity; win.MaxHeight = double.PositiveInfinity; }
        _dlgStack.Add(win);
        win.Closed += (_, _) => _dlgStack.Remove(win);
        bool nested = depth > 0;
        win.Loaded += (_, _) =>
        {
            // 用户上次调整过：尊重记住的尺寸/位置，别再按内容重算高度、也别挪到鼠标处。
            // 但嵌套层的【位置】一律重算成相对上一层错开——否则两层同名对话框正好完全重叠，
            // 看起来像"上一层不见了"。
            if (restored) { if (nested) CascadeFromOwner(win, parent); return; }
            if (nested) CascadeFromOwner(win, parent);
            else PositionWindowAtCursor(win, this);
            // 先按内容自适应出初始高度，加载后冻结为手动尺寸；宽高都可拖动调整
            // （嵌套监听等内容多时能拉大看全，不再锁死宽度导致文本被截断）。
            win.Height = win.ActualHeight;
            win.SizeToContent = SizeToContent.Manual;
            win.MaxWidth = double.PositiveInfinity;
            win.MaxHeight = double.PositiveInfinity;
        };
        return win;
    }

    // 把对话框内容放进可滚动宿主：内容超高时用滚轮/滑块滚动，配现代细滚动条（仅作用于本宿主子树）。
    private ScrollViewer MakeScrollHost(FrameworkElement content)
    {
        var scroller = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
            Style = (Style)FindResource("ThinScrollViewer"),
        };
        return scroller;
    }

    // 在指定显示器上盖一个透明全屏覆盖层，用户点击选位（带十字准星 + 实时坐标）。
    // 返回 (设备名, nx, ny)；Esc/无选择返回 null。用物理像素精确覆盖，避开 DPI 换算。
    // 跨屏点选：【每块屏一个独立覆盖窗】（复用标识屏幕的多窗模式），任意屏直接点，返回 设备名+屏内百分比。
    // 为什么不用一个铺满虚拟桌面的大窗：AllowsTransparency 的分层窗按整窗面积合成，几千像素宽的大窗
    // 每次挪十字线都要重合成超大表面——十字线肉眼可见地跟不上鼠标。拆成每屏一窗后各窗只有单屏大小，
    // 十字线只在光标所在屏渲染（离开即隐藏），恢复到旧单屏点选的流畅度。
    private (string dev, double nx, double ny)? PickAnywhere(Window dialog)
    {
        (string dev, double nx, double ny)? result = null;
        var mainH = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dlgH = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
        // 拾取期间把编辑窗口与本体下沉到底层，让目标屏上的应用透过透明覆盖层清晰可见。
        SetWindowPos(dlgH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);  // SWP_NOSIZE|NOMOVE|NOACTIVATE
        SetWindowPos(mainH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);

        var accent = (Brush)FindResource("Accent");
        var overlays = new List<Window>();
        // 每屏的十字线部件：可见性与位置由【每帧】驱动（见 OnFrame），不走鼠标事件。
        var parts = new List<(ScreenInfo.Monitor m, Canvas canvas, Action<double, double> update)>();
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.EventHandler? onFrame = null;
        void Done()
        {
            if (onFrame != null) { System.Windows.Media.CompositionTarget.Rendering -= onFrame; onFrame = null; }
            foreach (var w in overlays) try { w.Close(); } catch { }
            frame.Continue = false;
        }

        foreach (var mon in ScreenInfo.All())
        {
            var m = mon;
            var overlay = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
                // 藏掉系统光标：软件绘制永远落后硬件光标 1-2 帧，两者同屏可比就永远"追着跑"——
                // 十字线+中心点自己就是光标（截图工具的通行做法），没有参照物就没有可感知延迟。
                ShowInTaskbar = false, Topmost = true, Cursor = Cursors.None,
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)),
            };
            var root = new Grid();
            var canvas = new Canvas { Visibility = Visibility.Collapsed };   // 光标在本屏才显示
            // 虚线十字贯穿本屏 + 外圈光环 + 白描边中心点（与"预览位置"同一族视觉）。
            // 全部用 RenderTransform 移动：不触发布局（measure/arrange），每帧只重渲染。
            var vt = new TranslateTransform(); var ht = new TranslateTransform();
            var ct = new TranslateTransform(); var lt = new TranslateTransform();
            var vLine = new Line { Stroke = accent, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, RenderTransform = vt, X1 = 0, X2 = 0, Y1 = 0 };
            var hLine = new Line { Stroke = accent, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, RenderTransform = ht, Y1 = 0, Y2 = 0, X1 = 0 };
            var ring = new Ellipse { Width = 28, Height = 28, Stroke = accent, StrokeThickness = 1.5, RenderTransform = ct };
            var dot = new Ellipse { Width = 8, Height = 8, Fill = accent, Stroke = Brushes.White, StrokeThickness = 1.5, RenderTransform = ct };
            var coord = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 12, RenderTransform = lt,
                Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(6, 3, 6, 3),
            };
            Canvas.SetLeft(ring, -14); Canvas.SetTop(ring, -14);   // 元素锚在原点，位置全靠 transform
            Canvas.SetLeft(dot, -4); Canvas.SetTop(dot, -4);
            canvas.Children.Add(vLine); canvas.Children.Add(hLine); canvas.Children.Add(ring); canvas.Children.Add(dot); canvas.Children.Add(coord);
            root.Children.Add(canvas);
            if (m.Primary)   // 提示条只放主屏，别每块屏都糊一条
                root.Children.Add(new TextBlock
                {
                    Text = "点击选择位置（可跨屏，自动识别屏幕）· 方向键微调（Shift 加速）· 回车确认 · Esc 取消",
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 28, 0, 0),
                    Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold,
                    Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(12, 6, 12, 6),
                });
            overlay.Content = root;

            overlay.SourceInitialized += (_, _) =>
            {
                var h = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
                SetWindowPos(h, HWND_TOPMOST, m.Left, m.Top, m.Width, m.Height, 0x0040); // SWP_SHOWWINDOW
            };
            overlay.MouseLeftButtonDown += (_, _) =>
            {
                var (cx, cy) = ScreenInfo.CursorPos();   // 落点取物理光标坐标（精确，不受 DIP 换算影响）
                result = ScreenInfo.FromPoint(cx, cy);
                Done();
            };
            overlay.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; Done(); return; }
                if (e.Key == Key.Enter || e.Key == Key.Space)   // 微调后用键盘确认，避免点击时手抖挪位
                {
                    e.Handled = true;
                    var (cx, cy) = ScreenInfo.CursorPos();
                    result = ScreenInfo.FromPoint(cx, cy);
                    Done();
                    return;
                }
                // 方向键微调：直接挪【物理光标】1px（Shift 10px）——十字线每帧读光标位置，自然跟着走。
                int dx = 0, dy = 0;
                switch (e.Key)
                {
                    case Key.Left: dx = -1; break;
                    case Key.Right: dx = 1; break;
                    case Key.Up: dy = -1; break;
                    case Key.Down: dy = 1; break;
                    default: return;
                }
                e.Handled = true;
                int stepPx = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                var (px, py) = ScreenInfo.CursorPos();
                ScreenInfo.MoveCursor(px + dx * stepPx, py + dy * stepPx);
            };
            overlays.Add(overlay);
            parts.Add((m, canvas, (dipX, dipY) =>
            {
                double cw = Math.Max(1.0, canvas.ActualWidth), chh = Math.Max(1.0, canvas.ActualHeight);
                if (vLine.Y2 != chh) vLine.Y2 = chh;   // 线长只在尺寸变化时改
                if (hLine.X2 != cw) hLine.X2 = cw;
                vt.X = dipX; ht.Y = dipY; ct.X = dipX; ct.Y = dipY;
                coord.Text = $"{m.Label}  {dipX / cw * 100:0.#}% , {dipY / chh * 100:0.#}%";
                lt.X = Math.Min(dipX + 18, Math.Max(0, cw - 170));
                lt.Y = Math.Min(dipY + 18, Math.Max(0, chh - 26));
            }));
        }

        foreach (var w in overlays) w.Show();
        // 把键盘焦点给光标所在屏的覆盖窗（Esc 才有效）；点选不依赖焦点，哪屏点了都算。
        {
            var (cx, cy) = ScreenInfo.CursorPos();
            var dev = ScreenInfo.FromPoint(cx, cy).device;
            var mons = ScreenInfo.All();
            int idx = mons.FindIndex(mm => string.Equals(mm.Device, dev, StringComparison.OrdinalIgnoreCase));
            var focusWin = overlays[Math.Clamp(idx, 0, overlays.Count - 1)];
            focusWin.Activate(); focusWin.Focus();
        }
        // 每帧直读物理光标驱动十字线：绕过鼠标事件队列（事件是"过去的位置"，渲染时又晚一拍），
        // 在渲染前一刻取最新位置，把可感知延迟压到最低。
        int lastX = int.MinValue, lastY = int.MinValue;
        bool primed = false;   // 首帧窗口还没排版完（ActualWidth=0），此时算出的十字线位置是错的：
                               // 必须等真正画对一次再启用"没动就跳过"的短路，否则不挪鼠标就一直不渲染。
        onFrame = (_, _) =>
        {
            var (cx, cy) = ScreenInfo.CursorPos();
            if (primed && cx == lastX && cy == lastY) return;   // 没动就不碰视觉树
            lastX = cx; lastY = cy;
            foreach (var pt in parts)
            {
                bool on = pt.m.Contains(cx, cy);
                if (pt.canvas.Visibility != (on ? Visibility.Visible : Visibility.Collapsed))
                    pt.canvas.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (!on) continue;
                double cw = pt.canvas.ActualWidth, chh = pt.canvas.ActualHeight;
                if (cw < 1 || chh < 1) continue;   // 尺寸未就绪，下一帧再试（primed 仍为 false）
                pt.update((cx - pt.m.Left) * cw / pt.m.Width, (cy - pt.m.Top) * chh / pt.m.Height);
                primed = true;
            }
        };
        System.Windows.Media.CompositionTarget.Rendering += onFrame;
        System.Windows.Threading.Dispatcher.PushFrame(frame);   // 阻塞到点选/Esc（替代单窗 ShowDialog 的模态）

        // 拾取结束：把本体与编辑窗口切回前台。
        Services.WindowActivator.ActivateHwnd(mainH);
        Services.WindowActivator.ActivateHwnd(dlgH);
        return result;
    }

    // 在目标显示器上以高亮标记回显当前选中的位置（归一化 nx/ny）。点击任意处 / Esc 关闭。
    private void PreviewPositionOnMonitor(ScreenInfo.Monitor mon, double nx, double ny, Window dialog)
    {
        var mainH = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dlgH = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
        // 与点选一致：预览期间把编辑窗口与本体下沉，露出目标屏内容。
        SetWindowPos(dlgH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);
        SetWindowPos(mainH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);
        var (ox, oy, vw, vh) = VirtualBounds();
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Cursor = Cursors.Arrow,
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)),
        };
        var accent = (Brush)FindResource("Accent");
        var root = new Grid();
        var canvas = new Canvas();
        // 十字准星贯穿全屏 + 中心实心点 + 外圈光环，突出位置。
        var vLine = new Line { Stroke = accent, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 } };
        var hLine = new Line { Stroke = accent, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 } };
        var ring = new Ellipse { Width = 40, Height = 40, Stroke = accent, StrokeThickness = 2 };
        var dot = new Ellipse { Width = 14, Height = 14, Fill = accent, Stroke = Brushes.White, StrokeThickness = 2 };
        var coord = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 12,
            Text = $"{mon.Label}（{nx * 100:0.#}%, {ny * 100:0.#}%）",
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(6, 3, 6, 3),
        };
        canvas.Children.Add(vLine); canvas.Children.Add(hLine); canvas.Children.Add(ring); canvas.Children.Add(dot); canvas.Children.Add(coord);
        var hint = new TextBlock
        {
            Text = $"这是选中的位置（点击任意处或按 Esc 关闭）",
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 28, 0, 0),
            Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(12, 6, 12, 6),
        };
        root.Children.Add(canvas); root.Children.Add(hint);
        overlay.Content = root;

        // 覆盖【所有屏幕】（与点选/截图一致的"全屏锁定"）：这样在任意一块屏点击都能退出，
        // 而不是只有目标屏那一块有效。标记按虚拟像素→DIP 换算落到目标屏上，十字线贯穿目标屏。
        void PlaceMarker()
        {
            double cw = canvas.ActualWidth, chh = canvas.ActualHeight;
            if (cw < 1 || chh < 1) return;   // 尺寸未就绪（SetWindowPos 撑大在布局之前），等 SizeChanged 再来
            double r = vw / cw;              // 虚拟像素 / DIP
            double px = (mon.Left + nx * mon.Width - ox) / r, py = (mon.Top + ny * mon.Height - oy) / r;
            double mx = (mon.Left - ox) / r, my = (mon.Top - oy) / r, mw = mon.Width / r, mh = mon.Height / r;
            vLine.X1 = vLine.X2 = px; vLine.Y1 = my; vLine.Y2 = my + mh;
            hLine.Y1 = hLine.Y2 = py; hLine.X1 = mx; hLine.X2 = mx + mw;
            Canvas.SetLeft(ring, px - ring.Width / 2); Canvas.SetTop(ring, py - ring.Height / 2);
            Canvas.SetLeft(dot, px - dot.Width / 2); Canvas.SetTop(dot, py - dot.Height / 2);
            Canvas.SetLeft(coord, Math.Min(px + 26, Math.Max(0, cw - 160)));
            Canvas.SetTop(coord, Math.Min(py + 14, Math.Max(0, chh - 26)));
        }

        overlay.SourceInitialized += (_, _) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            SetWindowPos(h, HWND_TOPMOST, ox, oy, vw, vh, 0x0040);   // 铺满虚拟桌面
        };
        canvas.SizeChanged += (_, _) => PlaceMarker();
        overlay.Loaded += (_, _) => { PlaceMarker(); overlay.Activate(); overlay.Focus(); };
        overlay.MouseLeftButtonDown += (_, _) => overlay.Close();
        // e.Handled=true：吞掉这次 Esc，否则会继续传到编辑窗口触发 IsCancel 按钮把编辑窗口也关掉。
        overlay.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; overlay.Close(); } };
        overlay.ShowDialog();
        Services.WindowActivator.ActivateHwnd(mainH);
        Services.WindowActivator.ActivateHwnd(dlgH);
    }

    private static (int ox, int oy, int w, int h) VirtualBounds()
    {
        var all = ScreenInfo.All();
        int minL = all.Min(m => m.Left), minT = all.Min(m => m.Top);
        int maxR = all.Max(m => m.Right), maxB = all.Max(m => m.Bottom);
        return (minL, minT, Math.Max(1, maxR - minL), Math.Max(1, maxB - minT));
    }

    // 在每块屏顶部显示编号徽标(类似 Windows"标识")。点击穿透、不抢焦点。返回窗口列表，由调用方在退出编辑窗口时关闭（不自动消失）。
    // 屏幕序号标签按【所属对话框】分别管理，嵌套对话框各自独立、互不干扰；
    // 也让"真实截图"(CaptureTargetImage) 能先把当前对话框的标签藏起来，别被拍进目标图。
    private readonly Dictionary<Window, List<Window>> _idScreensByDialog = new();
    private void ShowIdScreens(Window owner)
    {
        if (_idScreensByDialog.TryGetValue(owner, out var cur) && cur.Count > 0) return;  // 已显示
        _idScreensByDialog[owner] = IdentifyScreens();
    }
    private void HideIdScreens(Window owner)
    {
        if (!_idScreensByDialog.TryGetValue(owner, out var wins)) return;
        foreach (var w in wins) { try { w.Close(); } catch { } }
        _idScreensByDialog.Remove(owner);
    }

    private List<Window> IdentifyScreens()
    {
        var wins = new List<Window>();
        foreach (var m in ScreenInfo.All())
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = m.Number > 0 ? m.Number.ToString() : "?", Foreground = Brushes.White, FontSize = 84, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center });
            stack.Children.Add(new TextBlock { Text = (m.Primary ? "主屏 · " : "") + $"{m.Width}×{m.Height}", Foreground = Brushes.White, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center });
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x14, 0x14, 0x14)),
                BorderBrush = Brushes.White, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(16),
                Padding = new Thickness(32, 16, 32, 16), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(30, 30, 0, 0), Child = stack,
            };
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, Topmost = true, ShowActivated = false, Background = Brushes.Transparent, Content = badge,
            };
            var mm = m;
            w.SourceInitialized += (_, _) =>
            {
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                int ex = GetWindowLongPtr(h, -20).ToInt32();                        // GWL_EXSTYLE
                SetWindowLongPtr(h, -20, new IntPtr(ex | 0x80000 | 0x20 | 0x08000000)); // LAYERED|TRANSPARENT|NOACTIVATE → 点击穿透
                SetWindowPos(h, HWND_TOPMOST, mm.Left, mm.Top, mm.Width, mm.Height, 0x0010 | 0x0040); // NOACTIVATE|SHOWWINDOW
            };
            w.Show();
            wins.Add(w);
        }
        return wins;
    }

    // System.Drawing.Bitmap(32bppArgb) → 冻结的 WPF BitmapSource（复制像素，源可随后释放）。
    private static System.Windows.Media.Imaging.BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var src = System.Windows.Media.Imaging.BitmapSource.Create(
                bmp.Width, bmp.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            src.Freeze();
            return src;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>
    /// 矩形框选的共用交互模型（DIP 空间）：四角/四边调整 + 内部整体拖动 + 外部重画 + 方向键微调。
    /// 「截图框选」与「编辑限制区域」共用，保证两处手感完全一致。只管几何，绘制由调用方的 Layout 负责。
    /// </summary>
    private sealed class RectPicker
    {
        private const double Edge = 8;      // 边/角命中容差（DIP）
        private const double MinSize = 8;
        public double X, Y, W, H;
        public bool Has;
        private string? _grab;
        private System.Windows.Point _down;
        private double _ox, _oy, _ow, _oh;

        public bool Dragging => _grab != null;

        /// <summary>命中类型：nw/ne/sw/se 角、n/s/w/e 边、move 内部、new 外部（重画）。</summary>
        public string HitTest(System.Windows.Point p)
        {
            if (!Has || W < 1 || H < 1) return "new";
            bool nl = Math.Abs(p.X - X) <= Edge, nr = Math.Abs(p.X - (X + W)) <= Edge;
            bool nt = Math.Abs(p.Y - Y) <= Edge, nb = Math.Abs(p.Y - (Y + H)) <= Edge;
            bool inX = p.X >= X - Edge && p.X <= X + W + Edge;
            bool inY = p.Y >= Y - Edge && p.Y <= Y + H + Edge;
            if (inX && inY)
            {
                if (nl && nt) return "nw";
                if (nr && nt) return "ne";
                if (nl && nb) return "sw";
                if (nr && nb) return "se";
                if (nl) return "w";
                if (nr) return "e";
                if (nt) return "n";
                if (nb) return "s";
            }
            if (p.X > X && p.X < X + W && p.Y > Y && p.Y < Y + H) return "move";
            return "new";
        }

        public static Cursor CursorFor(string mode) => mode switch
        {
            "nw" or "se" => Cursors.SizeNWSE,
            "ne" or "sw" => Cursors.SizeNESW,
            "w" or "e" => Cursors.SizeWE,
            "n" or "s" => Cursors.SizeNS,
            "move" => Cursors.SizeAll,
            _ => Cursors.Cross,
        };

        /// <summary>用调用方已经算好的抓取方式开始（标注编辑用：轮廓上按下＝move，但 HitTest 会把轮廓判成边）。</summary>
        public void BeginWith(System.Windows.Point p, string grab)
        {
            _grab = grab; _down = p; _ox = X; _oy = Y; _ow = W; _oh = H;
        }

        public void Begin(System.Windows.Point p)
        {
            _grab = HitTest(p);
            _down = p; _ox = X; _oy = Y; _ow = W; _oh = H;
            if (_grab == "new")   // 外部按下＝从该点重画（等价于抓住右下角拖）
            {
                X = p.X; Y = p.Y; W = 0; H = 0; Has = true;
                _ox = X; _oy = Y; _ow = 0; _oh = 0; _grab = "se";
            }
        }

        public void Drag(System.Windows.Point p, double maxW, double maxH)
        {
            if (_grab == null) return;
            double dx = p.X - _down.X, dy = p.Y - _down.Y;
            if (_grab == "move")
            {
                X = Math.Clamp(_ox + dx, 0, Math.Max(0, maxW - W));
                Y = Math.Clamp(_oy + dy, 0, Math.Max(0, maxH - H));
                return;
            }
            double l = _ox, t = _oy, r = _ox + _ow, b = _oy + _oh;
            if (_grab.Contains('w')) l = _ox + dx;
            if (_grab.Contains('e')) r = _ox + _ow + dx;
            if (_grab.Contains('n')) t = _oy + dy;
            if (_grab.Contains('s')) b = _oy + _oh + dy;
            X = Math.Min(l, r); Y = Math.Min(t, b); W = Math.Abs(r - l); H = Math.Abs(b - t);
        }

        public void End() => _grab = null;

        /// <summary>方向键微调：resize=true 调大小（右/下边），否则整体移动。</summary>
        public void Nudge(double dx, double dy, bool resize)
        {
            if (!Has) return;
            if (resize) { W = Math.Max(MinSize, W + dx); H = Math.Max(MinSize, H + dy); }
            else { X += dx; Y += dy; }
        }

        /// <summary>限制在画布内（移动/缩放后调用）。</summary>
        public void Clamp(double maxW, double maxH)
        {
            W = Math.Clamp(W, 0, maxW); H = Math.Clamp(H, 0, maxH);
            X = Math.Clamp(X, 0, Math.Max(0, maxW - W));
            Y = Math.Clamp(Y, 0, Math.Max(0, maxH - H));
        }
    }

    // 帮助图标：细圆环 + 问号，矢量自绘。
    // 不用字体图标是因为 Segoe MDL2 / Fluent 各版本里那个问号字形又粗又方，与本项目其它细线图标不搭；
    // 自绘还能保证不同 Windows 版本（字体版本不同）渲染一致。颜色用 SetResourceReference 跟随主题。
    private static UIElement HelpGlyph()
    {
        var g = new Grid { Width = 16, Height = 16, SnapsToDevicePixels = true };
        var ring = new Ellipse { Width = 15, Height = 15, StrokeThickness = 1.2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Ink");
        var q = new TextBlock
        {
            Text = "?", FontSize = 10, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -0.5, 0, 0),   // 问号在字面里偏下，往上顶半像素才在圆心
        };
        q.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        g.Children.Add(ring); g.Children.Add(q);
        return g;
    }

    // 「正则随机生成」语法速查：分组罗列支持的规则，每条一句说明 + 一个可直接照抄的例子。
    private void ShowRegexHelpDialog()
    {
        var win = MakeDialog("随机文本 · 支持的正则语法");
        var grid = new Grid { Margin = new Thickness(20, 20, 6, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sp = new StackPanel();
        var scroller = MakeScrollHost(sp); Grid.SetRow(scroller, 0); grid.Children.Add(scroller);

        var mono = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI");
        StackPanel cur = null!;
        void Section(string title, string? note = null)
        {
            cur = new StackPanel();
            if (note != null)
                cur.Children.Add(new TextBlock { Text = note, Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            sp.Children.Add(GroupCard(title, cur));
        }
        // 一条规则：语法（等宽、强调色、浅底药丸）+ 说明；下一行是例子。
        void Rule(string syntax, string desc, string example)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Border
            {
                Background = (Brush)FindResource("Hover"), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = syntax, FontFamily = mono, FontSize = 13, Foreground = (Brush)FindResource("Accent"), FontWeight = FontWeights.SemiBold },
            });
            head.Children.Add(new TextBlock { Text = desc, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 13 });
            row.Children.Add(head);
            row.Children.Add(new TextBlock { Text = example, FontFamily = mono, FontSize = 12, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 0, 0) });
            cur.Children.Add(row);
        }

        Section("字符类", "从一堆候选字符里随机取【一个】；配合量词就能取多个。");
        Rule("[abc]", "括号内任选一个", "[abc]         →  a");
        Rule("[a-z]", "区间：a 到 z 任选一个", "[a-z]{5}      →  kqxwe");
        Rule("[0-9a-z]", "多个区间可并排写", "[0-9a-z]{10}  →  4udr6w0eqi");
        Rule("[^abc]", "取反：除这些之外的可打印字符", "[^0-9]{3}     →  _.x");

        Section("预定义字符类", "常用字符集的简写。");
        Rule("\\d", "数字 0-9", "\\d{6}         →  235991");
        Rule("\\w", "字母、数字、下划线", "\\w{4}         →  E52N");
        Rule("\\s", "空格", "a\\sb          →  a b");
        Rule("\\D \\W \\S", "上面三个的取反", "\\D{3}         →  +)]");
        Rule(".", "任意可打印字符（不含空格）", ".{5}          →  k#2Qw");

        Section("量词", "跟在字符、字符类或分组后面，指定重复次数。");
        Rule("{n}", "正好 n 次", "\\d{6}         →  235991");
        Rule("{n,m}", "n 到 m 次之间随机", "[A-Z]{2,4}    →  MPB");
        Rule("{n,}", "至少 n 次（最多再加 8 次）", "a{2,}         →  aaaaa");
        Rule("?", "0 或 1 次", "ab?c          →  ac 或 abc");
        Rule("*", "0 到 8 次", "ab*           →  abbb");
        Rule("+", "1 到 9 次", "ab+           →  abb");

        Section("分组与交替");
        Rule("(...)", "括起来当整体，可整体加量词", "(ab){3}       →  ababab");
        Rule("|", "多选一，随机挑一个分支", "(红|绿|蓝)     →  蓝");
        Rule("嵌套组合", "分组 / 交替 / 量词可任意组合", "(abc|xyz)-\\w{4}  →  xyz-Tckf");

        Section("转义与字面量");
        Rule("\\. \\[ \\{ \\( \\| \\\\", "特殊字符前加反斜杠 = 它本身", "a\\.b          →  a.b");
        Rule("\\n  \\t", "换行 / 制表符", "第一行\\n第二行");
        Rule("中文等字符", "非特殊字符原样输出，可混写", "颜色(红|绿|蓝) →  颜色绿");

        Section("常用示例", "可直接复制到内容框使用。");
        Rule("[0-9a-z]{10}", "10 位随机小写字母数字", "→  4udr6w0eqi");
        Rule("\\d{6}", "6 位数字验证码", "→  235991");
        Rule("1[3-9]\\d{9}", "随机手机号", "→  14951696329");
        Rule("[a-zA-Z]\\w{5,11}", "字母开头的用户名", "→  Kf3n8sz");
        Rule("user_\\d{1,3}@test\\.com", "随机邮箱", "→  user_69@test.com");

        Section("限制说明");
        cur.Children.Add(new TextBlock
        {
            Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = "· 这里的正则用来【生成】而不是匹配，所以断言 (?=...)、反向引用 \\1 这类只在匹配时有意义的语法不支持。\n" +
                   "· 锚点 ^ 与 $ 会被忽略。\n" +
                   "· * + {n,} 这类无上限量词最多再重复 8 次，避免生成超长内容；结果总长上限 4096 字符。\n" +
                   "· 每次执行都会重新生成：动作重复 N 次，就得到 N 个各不相同的结果。",
        });

        var closeBtn = new Button { Content = "关闭", Width = 88, Height = 36, IsDefault = true, IsCancel = true, Style = (Style)FindResource("PrimaryButton") };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(closeBtn);
        var footer = new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 14, 0), Child = bar };
        Grid.SetRow(footer, 1); grid.Children.Add(footer);
        closeBtn.Click += (_, _) => win.Close();
        win.Content = grid;
        win.ShowDialog();
    }


    // 在屏幕上用白色方框回显已截取的图片区域（虚拟像素），点任意处 / Esc 关闭。
    private void PreviewRegion(int vx, int vy, int w, int h, Window dialog)
    {
        var mainH = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dlgH = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
        SetWindowPos(dlgH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);
        SetWindowPos(mainH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);
        var (ox, oy, vw, vh) = VirtualBounds();
        var snapshot = Services.ScreenMatch.CaptureRegion(ox, oy, vw, vh);
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Cursor = Cursors.Arrow, Background = Brushes.Transparent,
        };
        var snapImg = new System.Windows.Controls.Image { Source = ToBitmapSource(snapshot), Stretch = System.Windows.Media.Stretch.Fill };
        var dim = new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)) };
        var canvas = new Canvas();
        var glow = new System.Windows.Shapes.Rectangle { Stroke = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)), StrokeThickness = 4 };
        var box = new System.Windows.Shapes.Rectangle { Stroke = Brushes.White, StrokeThickness = 2 };
        var label = new TextBlock { Foreground = Brushes.White, FontSize = 12, Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(6, 3, 6, 3), Text = $"截取区域：({vx}, {vy})  {w}×{h}" };
        canvas.Children.Add(glow); canvas.Children.Add(box); canvas.Children.Add(label);
        var hint = new TextBlock
        {
            Text = "这是截取的图片区域（点击任意处或按 Esc 关闭）", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 28, 0, 0),
            Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(12, 6, 12, 6),
        };
        var root = new Grid(); root.Children.Add(snapImg); root.Children.Add(dim); root.Children.Add(canvas); root.Children.Add(hint); overlay.Content = root;
        overlay.Closed += (_, _) => snapshot.Dispose();
        overlay.SourceInitialized += (_, _) =>
        {
            var hh = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            SetWindowPos(hh, HWND_TOPMOST, ox, oy, vw, vh, 0x0040);
        };
        // 尺寸未就绪时（SetWindowPos 撑大发生在布局之前）算出的位置是错的，交给 SizeChanged 重算。
        void PlaceBox()
        {
            if (snapImg.ActualWidth < 1) return;
            // DIP = 像素 / 比例；比例 = 快照像素宽 / 图片 DIP 宽（多屏/DPI 恒定）。
            double r = vw / snapImg.ActualWidth;
            double lx = (vx - ox) / r, ly = (vy - oy) / r, lw = w / r, lh = h / r;
            Canvas.SetLeft(box, lx); Canvas.SetTop(box, ly); box.Width = lw; box.Height = lh;
            Canvas.SetLeft(glow, lx - 1); Canvas.SetTop(glow, ly - 1); glow.Width = lw + 2; glow.Height = lh + 2;
            Canvas.SetLeft(label, lx); Canvas.SetTop(label, Math.Max(0, ly - 24));
        }
        snapImg.SizeChanged += (_, _) => PlaceBox();
        overlay.Loaded += (_, _) => { PlaceBox(); overlay.Activate(); overlay.Focus(); };
        overlay.MouseLeftButtonDown += (_, _) => overlay.Close();
        overlay.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; overlay.Close(); } };
        overlay.ShowDialog();
        Services.WindowActivator.ActivateHwnd(mainH);
        Services.WindowActivator.ActivateHwnd(dlgH);
    }

    // 编辑限制区域：冻屏后在快照上显示一个【可拖动·可缩放】的矩形（body 拖动整体移动、四角拖动缩放），
    // 「确定」返回新区域（虚拟像素），取消 / Esc 返回 null。用于「点击图片」限定搜索范围。
    private (int vx, int vy, int w, int h)? EditRegion(Window dialog, int? curVx, int? curVy, int? curW, int? curH)
    {
        var mainH = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dlgH = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
        SetWindowPos(dlgH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);
        SetWindowPos(mainH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);
        System.Threading.Thread.Sleep(120);
        // 自动框窗用的窗口矩形表：必须在【覆盖层显示之前】枚举（覆盖层置顶后只会命中它自己）。
        var winRects = EnumWindowRects();
        var (ox, oy, vw, vh) = VirtualBounds();
        var snapshot = Services.ScreenMatch.CaptureRegion(ox, oy, vw, vh);

        var accent = (Brush)FindResource("Accent");
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Background = Brushes.Transparent,
        };
        var snapImg = new System.Windows.Controls.Image { Source = ToBitmapSource(snapshot), Stretch = System.Windows.Media.Stretch.Fill };
        var dim = new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)) };
        var canvas = new Canvas();
        var box = new System.Windows.Shapes.Rectangle { Stroke = accent, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x8A, 0x78, 0x60)), IsHitTestVisible = false };
        var sizeLbl = new TextBlock { Foreground = Brushes.White, FontSize = 12, Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(6, 3, 6, 3), IsHitTestVisible = false };
        // 没有既有区域时的自动框窗高亮（与截图覆盖层同款虚线框）
        var autoBox = new System.Windows.Shapes.Rectangle
        {
            Stroke = accent, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x8A, 0x78, 0x60)), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        canvas.Children.Add(autoBox); canvas.Children.Add(box); canvas.Children.Add(sizeLbl);
        // 四角缩放手柄（纯视觉，命中判定由下方几何算，不依赖控件命中）
        var handles = new System.Windows.Shapes.Rectangle[4];
        for (int i = 0; i < 4; i++)
        {
            handles[i] = new System.Windows.Shapes.Rectangle { Width = 12, Height = 12, Fill = Brushes.White, Stroke = accent, StrokeThickness = 2, IsHitTestVisible = false };
            canvas.Children.Add(handles[i]);
        }
        var hint = new TextBlock
        {
            Text = "拖动区域内移动 · 拖边/角调整 · 区域外拖拽重画 · 方向键微调（Ctrl 调大小 / Shift 加速）· Ctrl+Z 回退 · 回车确定 · Esc 取消", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 28, 0, 0),
            Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(12, 6, 12, 6), IsHitTestVisible = false,
        };
        // 悬浮工具条：与截图覆盖层同一套（跟随区域摆放，区域占满屏时压进区域内部，不会被挤出屏幕）
        var toolbar = new OverlayToolbar(canvas);
        var undoBtn = toolbar.Add("回退上次修改（Ctrl+Z）", GlyphUndo());
        toolbar.Sep();
        var cancelBtn = toolbar.Add("取消（Esc）", GlyphCross());
        var okBtn = toolbar.Add("完成（回车）", GlyphCheck());
        var root = new Grid(); root.Children.Add(snapImg); root.Children.Add(dim); root.Children.Add(canvas); root.Children.Add(hint); overlay.Content = root;
        overlay.SourceInitialized += (_, _) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            SetWindowPos(h, HWND_TOPMOST, ox, oy, vw, vh, 0x0040);
        };

        // 区域在 DIP 空间维护；r = 虚拟像素/DIP（多屏/DPI 恒定）。交互与「截图框选」共用 RectPicker
        // （区域内拖动移动、边/角调整边界、区域外拖拽重画、方向键微调），两处手感一致。
        var pick = new RectPicker();
        double R() => vw / Math.Max(1.0, snapImg.ActualWidth);
        void Layout()
        {
            pick.Clamp(snapImg.ActualWidth, snapImg.ActualHeight);
            var vis = pick.Has ? Visibility.Visible : Visibility.Collapsed;   // 自动框窗阶段先不画 0 尺寸的空框
            box.Visibility = vis; sizeLbl.Visibility = vis;
            Canvas.SetLeft(box, pick.X); Canvas.SetTop(box, pick.Y); box.Width = pick.W; box.Height = pick.H;
            double[] hx = { pick.X, pick.X + pick.W, pick.X, pick.X + pick.W };
            double[] hy = { pick.Y, pick.Y, pick.Y + pick.H, pick.Y + pick.H };
            for (int i = 0; i < 4; i++) { handles[i].Visibility = vis; Canvas.SetLeft(handles[i], hx[i] - 6); Canvas.SetTop(handles[i], hy[i] - 6); }
            double r = R();
            int px = ox + (int)Math.Round(pick.X * r), py = oy + (int)Math.Round(pick.Y * r);
            sizeLbl.Text = $"({px}, {py})  {(int)Math.Round(pick.W * r)}×{(int)Math.Round(pick.H * r)}";
            Canvas.SetLeft(sizeLbl, pick.X); Canvas.SetTop(sizeLbl, Math.Max(0, pick.Y - 24));
            var area = WorkAreaOnCanvas(
                pick.Has ? new System.Windows.Point(pick.X + pick.W / 2, pick.Y + pick.H / 2) : CursorOnCanvas(ox, oy, r),
                ox, oy, r, snapImg.ActualWidth, snapImg.ActualHeight);
            if (pick.Has && pick.W >= 2 && pick.H >= 2) toolbar.LayoutFor(new System.Windows.Rect(pick.X, pick.Y, pick.W, pick.H), area);
            else toolbar.LayoutBottomCenter(area);   // 还没框选也要够得着取消/完成
        }
        // 回退：每次改动（拖拽一次 / 键盘微调一次）前压一份矩形快照，撤销即还原上一份。
        var hist = new List<(double x, double y, double w, double h, bool has)>();
        void PushHist() => hist.Add((pick.X, pick.Y, pick.W, pick.H, pick.Has));
        void Undo()
        {
            if (hist.Count == 0) return;
            var s = hist[^1]; hist.RemoveAt(hist.Count - 1);
            pick.X = s.x; pick.Y = s.y; pick.W = s.w; pick.H = s.h; pick.Has = s.has;
            Layout();
        }
        undoBtn.Click += (_, _) => Undo();
        // 既有区域的初始框：不能"只初始化一次"——窗口从默认尺寸被 SetWindowPos 撑到全屏会经历多轮布局，
        // 若在中间某轮（尺寸还不对）就锁死初始化，比例失真会把既有框算得又小又偏（被 clamp 后形同没框），
        // 之后正确尺寸到位也不再重算——表现成"有数据却要重新手动画框"。
        // 改为：用户第一次上手（按下鼠标/键盘微调）之前，每轮尺寸变化都从【原始虚拟像素】重新推导。
        bool touched = false;
        // 没有既有区域时，与截图覆盖层同款：悬停自动框住窗口，点一下＝采用它，拖拽＝自己画框（拖了就等于放弃自动识别）。
        bool autoPickable = !(curW is int w0 && w0 > 0 && curH is int h0 && h0 > 0);
        System.Windows.Rect? autoCandidate = null;
        System.Windows.Point downPt = default;
        bool dragMoved = false;
        const double DragSlop = 4;
        const string HintAdjust = "拖动区域内移动 · 拖边/角调整 · 区域外拖拽重画 · 方向键微调（Ctrl 调大小 / Shift 加速）· Ctrl+Z 回退 · 回车确定 · Esc 取消";
        if (autoPickable) hint.Text = "还没有区域：移动到目标窗口上会自动框选，点一下即采用 · 也可直接拖拽自己框 · Esc 取消";
        void SyncFromCur()
        {
            if (touched || snapImg.ActualWidth < 1 || snapImg.ActualHeight < 1) return;
            double r = R();
            if (curW is int cW && cW > 0 && curH is int cH && cH > 0)
            {
                pick.X = ((curVx ?? ox) - ox) / r; pick.Y = ((curVy ?? oy) - oy) / r;
                pick.W = cW / r; pick.H = cH / r; pick.Has = true;   // 有区域→画出既有框（可拖动/调边界）
            }
            else { pick.X = pick.Y = pick.W = pick.H = 0; pick.Has = false; }   // 无区域→自动框窗 / 自行拖拽画
            Layout();
        }
        snapImg.SizeChanged += (_, _) => SyncFromCur();
        overlay.Loaded += (_, _) => { SyncFromCur(); overlay.Activate(); overlay.Focus(); };

        // 直接鼠标交互（Thumb 在无边框透明置顶窗里命中不稳，早期"编辑区域不生效"就是拖动没被接住）。
        overlay.MouseLeftButtonDown += (_, e) =>
        {
            if (toolbar.HitTest(e.GetPosition(toolbar.Bar))) return;   // 点在工具条上交给按钮
            var p = e.GetPosition(snapImg);
            touched = true;   // 用户上手后初始框停止跟随布局重算
            PushHist();       // 本次拖拽前的状态，供回退
            downPt = p; dragMoved = false;
            pick.Begin(p);    // 两种意图都先保留：松手时按位移判定是"点选窗口"还是"自己画框"
            overlay.CaptureMouse(); e.Handled = true;
            if (!autoPickable) Layout();
        };
        overlay.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(snapImg);
            if (pick.Dragging)
            {
                if (!dragMoved && (Math.Abs(p.X - downPt.X) > DragSlop || Math.Abs(p.Y - downPt.Y) > DragSlop))
                {
                    dragMoved = true;                 // 开始拖拽＝放弃自动识别
                    autoCandidate = null;
                    autoBox.Visibility = Visibility.Collapsed;
                }
                if (dragMoved || !autoPickable) { pick.Drag(p, snapImg.ActualWidth, snapImg.ActualHeight); Layout(); }
                return;
            }
            if (pick.Has || !autoPickable) { overlay.Cursor = RectPicker.CursorFor(pick.HitTest(p)); return; }
            // 还没有区域：高亮光标下最上层的那个窗口，点一下即采用
            overlay.Cursor = Cursors.Cross;
            double r = R();
            var hitR = HitWindow(winRects, ox + (int)Math.Round(p.X * r), oy + (int)Math.Round(p.Y * r));
            if (hitR is { } wr)
            {
                double bx = (wr.X - ox) / r, by = (wr.Y - oy) / r, bw = wr.Width / r, bh = wr.Height / r;
                autoCandidate = new System.Windows.Rect(bx, by, bw, bh);
                Canvas.SetLeft(autoBox, bx); Canvas.SetTop(autoBox, by);
                autoBox.Width = bw; autoBox.Height = bh; autoBox.Visibility = Visibility.Visible;
                sizeLbl.Visibility = Visibility.Visible;
                sizeLbl.Text = $"({(int)wr.X}, {(int)wr.Y})  {(int)wr.Width}×{(int)wr.Height}";
                Canvas.SetLeft(sizeLbl, bx); Canvas.SetTop(sizeLbl, Math.Max(0, by - 24));
            }
            else { autoCandidate = null; autoBox.Visibility = Visibility.Collapsed; sizeLbl.Visibility = Visibility.Collapsed; }
        };
        overlay.MouseLeftButtonUp += (_, _) =>
        {
            if (!pick.Dragging) return;
            pick.End();
            overlay.ReleaseMouseCapture();
            if (autoPickable)
            {
                if (!dragMoved && autoCandidate is { } wr)
                { pick.X = wr.X; pick.Y = wr.Y; pick.W = wr.Width; pick.H = wr.Height; pick.Has = true; }   // 一次点击→采用高亮窗口
                if (pick.W >= 2 && pick.H >= 2)
                {
                    autoPickable = false;
                    autoCandidate = null;
                    autoBox.Visibility = Visibility.Collapsed;
                    hint.Text = HintAdjust;
                }
                else pick.Has = false;   // 空点一下：继续留在自动识别阶段
            }
            Layout();
        };

        // 结果在【窗口仍显示时】就地算好并存起来——关窗后 snapImg.ActualWidth 会变 0、R() 失真，
        // 那正是"编辑后回来区域没变/不对"的根因。
        (int, int, int, int)? outv = null;
        void Confirm()
        {
            double r = R();
            int wpx = (int)Math.Round(pick.W * r), hpx = (int)Math.Round(pick.H * r);
            if (wpx >= 8 && hpx >= 8) outv = (ox + (int)Math.Round(pick.X * r), oy + (int)Math.Round(pick.Y * r), wpx, hpx);
            overlay.Close();
        }
        okBtn.Click += (_, _) => Confirm();
        cancelBtn.Click += (_, _) => { outv = null; overlay.Close(); };
        // e.Handled=true 必须要：否则 Enter 会继续传到父「编辑动作」对话框触发它的 IsDefault「确定」按钮、
        // Esc 触发 IsCancel「取消」——把整个编辑动作窗口一起关掉（区域自然也没生效）。同文件其它覆盖层都这么做。
        overlay.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; outv = null; overlay.Close(); return; }
            if (e.Key == Key.Enter) { e.Handled = true; Confirm(); return; }
            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; Undo(); return; }
            int dx = 0, dy = 0;
            switch (e.Key)
            {
                case Key.Left: dx = -1; break;
                case Key.Right: dx = 1; break;
                case Key.Up: dy = -1; break;
                case Key.Down: dy = 1; break;
                default: return;
            }
            e.Handled = true;
            if (!pick.Has || pick.Dragging) return;
            touched = true;   // 键盘微调也算上手，停止跟随布局重算
            PushHist();
            // 步进按【物理像素】算（Shift 10px）再换 DIP；Ctrl+方向键 调大小，否则整体移动。
            double stepDip = ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1) / Math.Max(0.0001, R());
            pick.Nudge(dx * stepDip, dy * stepDip, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            Layout();
        };
        overlay.ShowDialog();

        snapshot.Dispose();
        Services.WindowActivator.ActivateHwnd(mainH);
        Services.WindowActivator.ActivateHwnd(dlgH);
        return outv;
    }

    private FrameworkElement BuildHookRow(Window owner, string label, Func<MacroStep?> get, Action<MacroStep?> set)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = true };
        var lbl = new TextBlock { Text = label, Width = 100, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(lbl, Dock.Left); row.Children.Add(lbl);
        var clearBtn = new Button { Content = "清除", Width = 56, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(clearBtn, Dock.Right); row.Children.Add(clearBtn);
        var setBtn = new Button { Content = "设置", Width = 56, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(setBtn, Dock.Right); row.Children.Add(setBtn);
        // 复制 / 粘贴：与动作列表共用同一个剪贴板 _clip，所以能把列表里的动作直接贴成监听动作，反之亦然。
        // 组合不在此列——监听里塞组合会让"一个挂点=一个动作"的心智模型失控，也难在一行摘要里看清。
        var pasteBtn = new Button { Style = (Style)FindResource("IconButton"), FontSize = 15, Content = "\uE77F", ToolTip = "粘贴为该监听动作（与动作列表共用剪贴板；组合除外）", Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(pasteBtn, Dock.Right); row.Children.Add(pasteBtn);
        var copyBtn = new Button { Style = (Style)FindResource("IconButton"), FontSize = 15, Content = "\uE8C8", ToolTip = "复制该监听动作（可粘到别的挂点或动作列表里）", Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(copyBtn, Dock.Right); row.Children.Add(copyBtn);
        // 摘要自动换行（不再 … 截断）：配置复杂的监听动作描述很长，让它多行显示看全，与外层文本一致。
        var summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted") };
        row.Children.Add(summary);

        void Refresh()
        {
            var s = get();
            summary.Text = s != null ? s.ToString() : "未设置";
            clearBtn.IsEnabled = s != null;
            copyBtn.IsEnabled = s != null;
            pasteBtn.IsEnabled = _clip != null && !_clip.IsGroup;
        }
        // 监听动作＝完整动作：用与外层同款的完整对话框（可配循环/运行条件/备注，且能继续配监听——递归下去）。
        setBtn.Click += (_, _) => { var s = ShowAddActionDialog(get()); if (s != null) { set(s); Refresh(); } };
        clearBtn.Click += (_, _) => { set(null); Refresh(); };
        copyBtn.Click += (_, _) =>
        {
            var s = get();
            if (s == null) return;
            SetClip(s.Clone());   // 走 SetClip 才会通知其它挂点行刷新"粘贴"按钮
            ShowToast("已复制监听动作");
        };
        pasteBtn.Click += (_, _) =>
        {
            if (_clip == null) return;
            if (_clip.IsGroup) { ThemedDialog.Show("组合动作不能作为监听动作粘贴。", "无法粘贴", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var copy = _clip.Clone();
            copy.RenewId();                                                   // 新身份，避免与原件抢同一个跳转目标
            copy.JumpTargetAlias = ""; copy.JumpTargetId = ""; copy.JumpTarget = 0; copy.JumpTimes = 0;   // 跳转对监听是 no-op，贴过来更没意义
            copy.ClearAliases();   // 别名标签同样清空，避免与列表里的原件重名
            set(copy); Refresh();
        };
        // 剪贴板是全局的：在别处（动作列表 / 另一个挂点）复制后，本行的"粘贴"要立刻可用。
        // 只在构造时判一次的话，先建好的行永远是灰的——这正是"复制了却粘不到别的挂点"的原因。
        ClipChanged += Refresh;
        owner.Closed += (_, _) => ClipChanged -= Refresh;
        Refresh();
        return row;
    }

    // 运行条件面板：启用勾选后才展开明细。typeCombo+img 非空时（仅动作级）提供“时间段/图片出现”两类，
    // 否则仅时间段（方案级/组合级复用）。
    /// <summary>
    /// 运行条件面板（v0.4 起支持多条件）：启用开关 → 满足方式（与/或）→ 条件列表 → 重复检查。
    /// 每条条件的具体内容在子对话框里编辑（<see cref="ShowConditionItemDialog"/>），
    /// 列表这里只展示摘要 + 编辑/删除，避免多条时把面板撑成一大坨。
    /// </summary>
    /// <summary>
    /// 条件列表编辑块：满足方式（与/或）+ 条件列表 + 添加按钮。
    /// 运行条件（BuildRunConditionPanel）与「重复直到条件满足」的停止条件（UntilBlock）共用同一份。
    /// </summary>
    private StackPanel BuildConditionListBlock(System.Collections.Generic.List<ConditionItem> items, ComboBox logicCombo, out Action refresh)
    {
        var block = new StackPanel();
        // ---- 满足方式：多条时才有意义，单条时藏起来 ----
        logicCombo.Items.Clear();
        logicCombo.Items.Add(new ComboBoxItem { Content = "全部满足（与）", Tag = "And" });
        logicCombo.Items.Add(new ComboBoxItem { Content = "任一满足（或）", Tag = "Or" });
        logicCombo.Height = 32; logicCombo.Width = 150;
        if (logicCombo.SelectedIndex < 0) logicCombo.SelectedIndex = 0;
        var logicRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        logicRow.Children.Add(new TextBlock { Text = "满足方式", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        logicRow.Children.Add(logicCombo);
        block.Children.Add(logicRow);

        // ---- 条件列表 ----
        var listPanel = new StackPanel();
        block.Children.Add(listPanel);
        var addBtn = new Button { Content = "添加条件", Height = 32, MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
        block.Children.Add(addBtn);
        var emptyNote = new TextBlock
        {
            Text = "还没有条件：点「添加条件」新增一条（时间段或图片出现）。",
            Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };

        void RefreshList()
        {
            listPanel.Children.Clear();
            logicRow.Visibility = items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;   // 只有一条时"与/或"没意义
            if (items.Count == 0) { listPanel.Children.Add(emptyNote); return; }
            for (int i = 0; i < items.Count; i++)
            {
                int idx = i;
                var it = items[i];
                var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
                var ops = new StackPanel { Orientation = Orientation.Horizontal };
                var edit = new Button { Style = (Style)FindResource("IconButton"), FontSize = 15, Content = "", ToolTip = "编辑该条件" };
                var del = new Button { Style = (Style)FindResource("IconButton"), FontSize = 15, Content = "", ToolTip = "删除该条件" };
                edit.Click += (_, _) =>
                {
                    var r = ShowConditionItemDialog(items[idx]);
                    if (r != null) { items[idx] = r; RefreshList(); }
                };
                del.Click += (_, _) => { items.RemoveAt(idx); RefreshList(); };
                ops.Children.Add(edit); ops.Children.Add(del);
                DockPanel.SetDock(ops, Dock.Right); row.Children.Add(ops);
                // 多条时前面标个序号，配合"与/或"看得清是第几条
                var label = new TextBlock
                {
                    Text = (items.Count > 1 ? $"{idx + 1}. " : "") + it.ToString(),
                    VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                    Foreground = it.IsValid ? (Brush)FindResource("Ink") : (Brush)FindResource("Danger"),
                };
                row.Children.Add(label);
                listPanel.Children.Add(new Border
                {
                    Background = (Brush)FindResource("Bg"), CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 4, 6, 4), Margin = new Thickness(0, 0, 0, 4), Child = row,
                });
            }
        }
        addBtn.Click += (_, _) =>
        {
            var r = ShowConditionItemDialog(null);
            if (r != null) { items.Add(r); RefreshList(); }
        };
        refresh = RefreshList;
        RefreshList();
        return block;
    }

    private StackPanel BuildRunConditionPanel(RunConditionEditor ed)
    {
        var enabled = ed.Enabled;
        var items = ed.Items;
        var logicCombo = ed.LogicCombo;
        var retry = ed.Retry;
        var retryInterval = ed.RetryInterval;
        var retryMax = ed.RetryMax;
        var retryTimeout = ed.RetryTimeout;
        var detail = new StackPanel();
        enabled.Content = "启用运行条件";
        detail.Children.Add(BuildConditionListBlock(items, logicCombo, out var refreshItems));
        ed.RefreshItems = refreshItems;   // 供回填（LoadRunCondition）在填完条件后刷新列表

        // ---- 重复检查（对整组条件生效）----
        retry.Content = "条件不满足时重复检查，直到满足";
        retry.Margin = new Thickness(0, 14, 0, 0);
        ComboBox UnitBox(ComboBox cb, int def)
        {
            cb.Items.Clear();
            foreach (var n in new[] { "毫秒", "秒", "分钟", "小时" }) cb.Items.Add(n);
            if (cb.SelectedIndex < 0) cb.SelectedIndex = def;
            cb.Width = 88; cb.Height = 32; cb.Margin = new Thickness(6, 0, 16, 0);
            return cb;
        }
        retryInterval.Width = 84; retryInterval.Height = 32; if (retryInterval.Text.Length == 0) retryInterval.Text = "1";
        retryMax.Width = 74; retryMax.Height = 32; if (retryMax.Text.Length == 0) retryMax.Text = "0";
        retryTimeout.Width = 84; retryTimeout.Height = 32; if (retryTimeout.Text.Length == 0) retryTimeout.Text = "0";
        // 两行：间隔 / 上限（次数 + 时长）。都能选单位，都用 0 表示"不限 / 不等待"。
        var rrow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 8, 0, 0) };
        rrow.Children.Add(new TextBlock { Text = "间隔", VerticalAlignment = VerticalAlignment.Center, Width = 60 });
        rrow.Children.Add(retryInterval);
        rrow.Children.Add(UnitBox(ed.RetryIntervalUnit, 1));
        rrow.Children.Add(new TextBlock { Text = "（0 = 立刻重判）", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Muted") });
        // 次数上限、时长上限各占一行：两个"最多 …"挤一行读起来像一句话，容易看成一个条件
        var rrow2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 8, 0, 0) };
        rrow2.Children.Add(new TextBlock { Text = "最多次数", VerticalAlignment = VerticalAlignment.Center, Width = 60 });
        rrow2.Children.Add(retryMax);
        rrow2.Children.Add(new TextBlock { Text = "次（0 = 不限）", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(6, 0, 0, 0) });
        var rrow3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 8, 0, 0) };
        rrow3.Children.Add(new TextBlock { Text = "最多时长", VerticalAlignment = VerticalAlignment.Center, Width = 60 });
        rrow3.Children.Add(retryTimeout);
        rrow3.Children.Add(UnitBox(ed.RetryTimeoutUnit, 1));
        rrow3.Children.Add(new TextBlock { Text = "（0 = 不限）", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Muted") });
        var rnote = new TextBlock
        {
            Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 6, 0, 0),
            Text = "不勾选时条件不满足就直接跳过该动作。勾选后按间隔反复判定，等到满足才继续（等待期间可暂停 / 停止）。\n次数上限与时间上限【同时生效，先到者结束】，到达后仍不满足则跳过该动作。间隔填 0 表示判完立刻再判——图片类条件会持续占用 CPU，酌情使用。\n方案级运行条件本来就会一直等到满足，因此该勾选对它无影响，但间隔与两个上限同样生效（超出即结束本次运行）。",
        };
        void RefreshRetry() { rrow.Visibility = rrow2.Visibility = rrow3.Visibility = rnote.Visibility = retry.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
        retry.Checked += (_, _) => RefreshRetry();
        retry.Unchecked += (_, _) => RefreshRetry();
        RefreshRetry();
        detail.Children.Add(retry); detail.Children.Add(rrow); detail.Children.Add(rrow2); detail.Children.Add(rrow3); detail.Children.Add(rnote);

        // 勾选开关后，明细收进一个缩进 + 弱底色 + 强调左条的面板里，一眼看出属于该开关的"势力范围"。
        var detailWrap = new Border
        {
            Background = (Brush)FindResource("Hover"),
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 6, 6, 0),
            Padding = new Thickness(12, 10, 12, 12),
            Margin = new Thickness(2, 10, 0, 0),
            Child = detail,
        };
        var panel = new StackPanel();
        panel.Children.Add(enabled);
        panel.Children.Add(detailWrap);
        void Refresh() => detailWrap.Visibility = enabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        enabled.Checked += (_, _) => Refresh();
        enabled.Unchecked += (_, _) => Refresh();
        Refresh();
        return panel;
    }

    /// <summary>编辑单条运行条件的子对话框。source 为 null 即新增。取消返回 null。</summary>
    private ConditionItem? ShowConditionItemDialog(ConditionItem? source)
    {
        var win = MakeDialog(source == null ? "添加运行条件" : "编辑运行条件");
        win.Closed += (_, _) => HideIdScreens(win);
        var grid = new Grid { Margin = new Thickness(20, 20, 6, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sp = new StackPanel();
        var scroller = MakeScrollHost(sp); Grid.SetRow(scroller, 0); grid.Children.Add(scroller);

        var typeCombo = new ComboBox { Height = 32, Margin = new Thickness(0, 0, 0, 12) };
        typeCombo.Items.Add("时间段"); typeCombo.Items.Add("图片出现");
        typeCombo.SelectedIndex = source?.Type == "ImageMatch" ? 1 : 0;

        // 时间段
        ComboBox sh = new(), sm = new(), eh = new(), em = new();
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal };
        timeRow.Children.Add(BuildTimeField("从", sh, sm));
        timeRow.Children.Add(new Border { Width = 22 });
        timeRow.Children.Add(BuildTimeField("到", eh, em));
        var timeSub = new StackPanel();
        timeSub.Children.Add(timeRow);
        timeSub.Children.Add(new TextBlock
        {
            Text = "某侧选“不限”表示开放边界（例：只设“到 18:00”即 18:00 前均满足）。",
            Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        });

        // 图片出现：与「点击图片」共用同一编辑器（无"匹配第几"）
        var img = new ClickImagePanel(this, win, withIndex: false, boxed: false, notchBgKey: "Bg");
        var imgSub = new StackPanel();
        imgSub.Children.Add(img.Panel);
        imgSub.Children.Add(new TextBlock
        {
            Text = "在限制区域内搜索目标图片，找到即视为本条满足（未设区域则搜整块锚定屏）。",
            Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });

        var invert = new CheckBox { Content = "取反（条件不成立时才算满足）", Margin = new Thickness(0, 14, 0, 0), IsChecked = source?.Invert == true };

        var body = new StackPanel();
        body.Children.Add(FieldLabel("条件类型"));
        body.Children.Add(typeCombo);
        body.Children.Add(timeSub);
        body.Children.Add(imgSub);
        body.Children.Add(invert);
        sp.Children.Add(GroupCard("条件", body));

        void RefreshType()
        {
            bool image = typeCombo.SelectedIndex == 1;
            timeSub.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
            imgSub.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
            // 图片条件要选屏：多屏时顺带把屏幕编号标出来。
            // 必须走后台优先级——ShowIdScreens 要为每块屏建一个置顶窗口，同步做会让切换下拉明显卡顿。
            if (image && ScreenInfo.All().Count > 1)
                win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => ShowIdScreens(win)));
            else HideIdScreens(win);
        }
        typeCombo.SelectionChanged += (_, _) => RefreshType();

        if (source != null)
        {
            SetTimeSelection(sh, sm, source.StartMinute);
            SetTimeSelection(eh, em, source.EndMinute);
            if (source.Type == "ImageMatch") img.LoadCond(source);
        }
        RefreshType();

        var okBtn = new Button { Content = "确定", Width = 88, Height = 36, IsDefault = true, Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 10, 0) };
        var cancelBtn = new Button { Content = "取消", Width = 88, Height = 36, IsCancel = true, Style = (Style)FindResource("GhostButton") };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(okBtn); bar.Children.Add(cancelBtn);
        var footer = new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 14, 0), Child = bar };
        Grid.SetRow(footer, 1); grid.Children.Add(footer);

        ConditionItem? result = null;
        okBtn.Click += (_, _) =>
        {
            try
            {
                var it = new ConditionItem { Invert = invert.IsChecked == true };
                if (typeCombo.SelectedIndex == 1)
                {
                    it.Type = "ImageMatch";
                    img.ApplyCond(it);   // 缺图会抛异常，下面统一提示
                }
                else
                {
                    var start = SelectedMinute(sh, sm);
                    var end = SelectedMinute(eh, em);
                    if (!start.HasValue && !end.HasValue)
                        throw new InvalidOperationException("请至少选择开始时间或结束时间。");
                    it.Type = "TimeRange";
                    it.StartMinute = start; it.EndMinute = end;
                }
                result = it;
                win.DialogResult = true;
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(ex.Message, "条件不完整", MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
        };
        win.Content = grid;
        return win.ShowDialog() == true ? result : null;
    }
    private StackPanel BuildTimeField(string label, ComboBox hour, ComboBox minute)
    {
        FillHourCombo(hour);
        FillMinuteCombo(minute);
        hour.Width = 66; hour.Height = 32;
        minute.Width = 58; minute.Height = 32;

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(hour);
        panel.Children.Add(new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(minute);

        void Refresh() => minute.IsEnabled = hour.SelectedIndex > 0; // 0 = 不限
        hour.SelectionChanged += (_, _) => Refresh();
        Refresh();
        return panel;
    }

    private static void FillHourCombo(ComboBox combo)
    {
        combo.Items.Clear();
        combo.Items.Add("不限");
        for (int i = 0; i < 24; i++) combo.Items.Add($"{i:00}");
        combo.SelectedIndex = 0;
    }

    private static void FillMinuteCombo(ComboBox combo)
    {
        combo.Items.Clear();
        for (int i = 0; i < 60; i++) combo.Items.Add($"{i:00}");
        combo.SelectedIndex = 0;
    }

    // 小时为“不限”(index 0) → 该侧开放，返回 null；否则 (时)*60 + 分。
    private static int? SelectedMinute(ComboBox hour, ComboBox minute)
    {
        if (hour.SelectedIndex <= 0) return null;
        int h = hour.SelectedIndex - 1;
        int m = Math.Max(0, minute.SelectedIndex);
        return h * 60 + m;
    }

    private static void SetTimeSelection(ComboBox hour, ComboBox minute, int? value)
    {
        if (value is not int v)
        {
            hour.SelectedIndex = 0;   // 不限
            minute.SelectedIndex = 0;
            return;
        }
        v = ((v % 1440) + 1440) % 1440;
        hour.SelectedIndex = v / 60 + 1;
        minute.SelectedIndex = v % 60;
    }

    // ---------- 动作编辑对话框 ----------
    private MacroStep? ShowAddActionDialog(MacroStep? source = null)
    {
        string capturedKey = "";
        byte capturedModifier = 0;
        bool capturingKey = false;

        var win = MakeDialog(source == null ? "添加动作" : "编辑动作");
        // 屏幕编号标记：在"目标显示器/选择窗口"视图常显，退出编辑窗口时关闭（见 UpdatePanels / win.Closed）。
        win.Closed += (_, _) => HideIdScreens(win);
        var grid = new Grid { Margin = new Thickness(20, 20, 6, 20) }; // 右侧小边距，让滚动条贴近窗口右缘
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sp = new StackPanel { Margin = new Thickness(0) };
        var scroller = MakeScrollHost(sp); Grid.SetRow(scroller, 0); grid.Children.Add(scroller);

        // 基础设置组：动作类型 + 其对应的类型面板（鼠标/键盘/等待/激活窗口），整组包进一张卡片。
        var baseContent = new StackPanel();
        baseContent.Children.Add(FieldLabel("动作类型"));
        // 三级级联：类别 → 输入设备 → 具体动作（输入 > 鼠标 > 点击坐标）。
        // 后两级按上一级收放，非"输入"类别时只剩第一个下拉。
        var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var typeCombo = new ComboBox { Width = 116, Height = 32 };
        typeCombo.Items.Add("输入"); typeCombo.Items.Add("运行"); typeCombo.SelectedIndex = 0;
        var deviceCombo = new ComboBox { Width = 100, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        deviceCombo.Items.Add("鼠标"); deviceCombo.Items.Add("键盘"); deviceCombo.SelectedIndex = 0;
        var mouseActionCombo = new ComboBox { Width = 124, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        mouseActionCombo.Items.Add("点击"); mouseActionCombo.Items.Add("移动"); mouseActionCombo.Items.Add("拖动"); mouseActionCombo.Items.Add("滚轮");
        mouseActionCombo.SelectedIndex = 0;
        // 「点击 / 移动」的目标（仅点击 / 点击坐标 / 点击图片）不再挤进上面的类型行——类型行最多三级，
        // 再加一级又长又难扫。它作为一张卡片放在【鼠标按钮下面】，属于"这个动作怎么做"的参数。
        // 存储类型不变（MouseClick / MouseClickAt / MouseClickImage / MouseMove / MouseMoveImage），旧方案照常读。
        var mouseTargetCombo = new ComboBox { Height = 32 };
        // 键盘同理：按键 / 文本 也从类型行挪进键盘面板里的一张卡片
        var keyActionCombo = new ComboBox { Height = 32 };
        keyActionCombo.Items.Add("按键"); keyActionCombo.Items.Add("文本"); keyActionCombo.SelectedIndex = 0;
        var runActionCombo = new ComboBox { Width = 108, Height = 32, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
        runActionCombo.Items.Add("等待"); runActionCombo.Items.Add("激活窗口"); runActionCombo.Items.Add("跳转");
        runActionCombo.SelectedIndex = 0;
        typeRow.Children.Add(typeCombo); typeRow.Children.Add(deviceCombo); typeRow.Children.Add(mouseActionCombo); typeRow.Children.Add(runActionCombo);
        baseContent.Children.Add(typeRow);

        // 鼠标面板
        // 鼠标面板拆两段：上段（按钮/类型/拖动方式）→ 【窗口选择框】→ 下段（坐标/终点/图片/次数…）。
        // 「拖动窗口」要先选窗口再设终点坐标，而窗口选择框是与「激活窗口」共用的同一个控件，
        // 只能放在两段之间，才能同时满足两种动作的先后顺序。
        var mousePanel = new StackPanel(); baseContent.Children.Add(mousePanel);
        var mousePanel2 = new StackPanel();

        // 鼠标按钮：多一个「仅移动」——选它即只移动不点击（存为 MouseMove）。
        var buttonCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 0), Height = 32 };
        buttonCombo.Items.Add("左键"); buttonCombo.Items.Add("右键"); buttonCombo.Items.Add("中键");
        buttonCombo.SelectedIndex = 0;
        var mouseButtonPanel = SubGroup("鼠标按钮", buttonCombo);

        var mouseTargetTitle = new TextBlock { Text = "点击类型", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        var mouseTargetPanel = SubGroup(null, mouseTargetTitle, mouseTargetCombo);

        var holdRow = new TimeInputRow(this, _doc.DefaultHoldMs);
        holdRow.Panel.Margin = new Thickness(0, 0, 0, 0);
        var mouseHoldPanel = SubGroup("按住时间", holdRow.Panel);

        // 坐标块（可复用）：点击/移动用一个；拖动用两个（起点、终点）。
        var coord = new CoordBlock(this, win, "设置坐标（先移动到该位置再执行）", showCheck: true, withOffset: true);
        var mouseMovePanel = coord.Panel;
        var coordCheck = coord.Enabled;
        // 拖动方式：坐标拖动（起点→终点）/ 拖动窗口（激活目标窗口后把它的左上角拖到终点）
        var dragModeCombo = new ComboBox { Height = 32 };
        dragModeCombo.Items.Add("坐标拖动（起点 → 终点）"); dragModeCombo.Items.Add("拖动窗口（把窗口拖到终点）");
        dragModeCombo.SelectedIndex = 0;
        var dragModePanel = SubGroup("拖动类型", dragModeCombo,
            new TextBlock
            {
                Text = "拖动窗口：先激活选定的窗口，再按住它的标题栏拖动，使窗口【左上角】落在终点坐标。",
                Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });

        // 终点坐标对应窗口的哪个点（3×3）：默认左上角。它是终点坐标的一部分，因此就放在那张卡里（见下方 InsertTopRow）。
        var dragAnchorCombo = new ComboBox { Height = 32, Width = 132 };
        for (int i = 0; i < 9; i++) dragAnchorCombo.Items.Add(new ComboBoxItem { Content = MacroStep.AnchorCn(i), Tag = i.ToString() });
        dragAnchorCombo.SelectedIndex = 0;
        var dragAnchorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 10) };
        dragAnchorRow.Children.Add(new TextBlock { Text = "对齐点", Width = 52, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        dragAnchorRow.Children.Add(dragAnchorCombo);
        dragAnchorRow.Children.Add(new TextBlock
        {
            Text = "窗口的这个点会落在下面的坐标上", VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(10, 0, 0, 0),
        });

        // 拖动终点：始终展开、无勾选框
        var dragEnd = new CoordBlock(this, win, "终点坐标", showCheck: false);
        var dragEndPanel = dragEnd.Panel;

        // 点击图片编辑块（目标图 + 限制区域 + 相似度 + 匹配第几）。
        var clickImage = new ClickImagePanel(this, win);
        var clickImagePanel = clickImage.Panel;

        // 拟人化移动（动作级）：独立成块，排在最后；只在启用坐标时有意义。
        // 默认勾选：这是本工具的主要卖点之一，绝大多数场景都该开着；不想要的再手动关。
        // 编辑既有动作时会被 LoadMoveFields/各 case 按存档值覆盖，不影响老方案。
        var humanizeMoveCheck = new CheckBox { Content = "拟人化移动（走缓入缓出的弧线轨迹，更像真人）", IsChecked = true };
        var humanizeNote = new TextBlock
        {
            Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = "沿带随机弧度、缓入缓出的路径分多步移动，比瞬移多花约 0.2–0.6 秒。CH9329 下每步仍是真实硬件相对闭环。",
        };
        var humanizePanel = SubGroup(null, humanizeMoveCheck, humanizeNote);

        var mouseWheelPanel = new StackPanel { Visibility = Visibility.Collapsed };
        mouseWheelPanel.Children.Add(FieldLabel("滚轮格数（正 = 向上，负 = 向下）"));
        var wheelText = new TextBox { Text = "0", Margin = new Thickness(0, 0, 0, 6), Height = 32 };
        mouseWheelPanel.Children.Add(wheelText);
        mouseWheelPanel.Children.Add(new TextBlock { Text = "以“格”为单位（一格＝常规滚一下）。两种输出方式一致；CH9329 单次上限 ±127 格。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });

        // 键盘方式卡（按键 / 文本）：与鼠标的「目标位置」同理，从类型行挪下来变成面板里的一个选项
        var keyKindPanel = SubGroup("输入方式", keyActionCombo);
        keyKindPanel.Visibility = Visibility.Collapsed;
        baseContent.Children.Add(keyKindPanel);

        // 键盘面板（布局与鼠标对齐：按键 / 按住时间 / 按键次数+重复间隔 各自成独立小卡）
        var keyboardPanel = new StackPanel { Visibility = Visibility.Collapsed }; baseContent.Children.Add(keyboardPanel);
        var capturedText = new TextBlock { Text = "请直接按键，自动捕获（以最新一次为准）", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 6) };
        keyboardPanel.Children.Add(SubGroup("按键",
            capturedText,
            new TextBlock { Text = "支持左/右 Ctrl、Shift、Alt、Win 等修饰键组合（如 Ctrl+Alt+A），按其他键可随时覆盖。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap }));
        var keyboardHoldRow = new TimeInputRow(this, _doc.DefaultHoldMs);
        keyboardHoldRow.Panel.Margin = new Thickness(0, 0, 0, 0);
        keyboardPanel.Children.Add(SubGroup("按住时间", keyboardHoldRow.Panel));
        var kbRepeat = new RepeatBlock(this, "按键次数（0 为无限）");
        keyboardPanel.Children.Add(kbRepeat.Panel);

        // ---- 文本面板（输入 → 键盘 → 文本）----
        // 键盘协议传的是按键位置而非字符，汉字没有对应键位：软件后端可用 Unicode 注入绕过布局直接投递字符；
        // CH9329 是真实 HID 键盘，物理上发不出汉字，只能剪贴板粘贴。
        bool hwBackend = string.Equals(_doc.Backend, "Serial", StringComparison.OrdinalIgnoreCase);
        var textPanel = new StackPanel { Visibility = Visibility.Collapsed }; baseContent.Children.Add(textPanel);
        var textBox = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(6, 4, 6, 4),
        };
        var textHintNormal = new TextBlock { Text = "支持中文、换行等任意字符。执行时会输入到【当前焦点窗口】，通常需要先用「运行 → 激活窗口」把目标窗口切到前台。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        // 正则随机：把上面的内容当【生成模式】，每次执行随机生成一串（不是用来匹配的）。
        var randomCheck = new CheckBox { Content = "正则随机生成", VerticalAlignment = VerticalAlignment.Center };
        var previewBtn = new Button { Style = (Style)FindResource("IconButton"), FontSize = 16, Content = "\uE890", ToolTip = "预览：按当前模式随机生成一个示例" };
        var regexHelpBtn = new Button { Style = (Style)FindResource("IconButton"), Content = HelpGlyph(), ToolTip = "支持的正则语法与示例" };
        regexHelpBtn.Click += (_, _) => ShowRegexHelpDialog();
        var randomRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(randomCheck, Dock.Left); randomRow.Children.Add(randomCheck);
        DockPanel.SetDock(regexHelpBtn, Dock.Right); randomRow.Children.Add(regexHelpBtn);   // 先 dock 的更靠右
        DockPanel.SetDock(previewBtn, Dock.Right); randomRow.Children.Add(previewBtn);
        var randomHint = new TextBlock
        {
            Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = "把上面的内容当作正则【生成模式】，每次执行随机生成一串（重复多次即每次不同）。\n" +
                   "例：[0-9a-z]{10} → 10 位随机小写字母数字；\\d{6} → 6 位数字验证码。完整语法与更多示例点右上角 ? 查看。",
        };
        var previewText = new TextBlock { FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        previewBtn.Click += (_, _) =>
        {
            previewText.Visibility = Visibility.Visible;
            try
            {
                var sample = Services.RandomText.Generate(textBox.Text ?? "");
                previewText.Foreground = (Brush)FindResource("Ink");
                previewText.Text = "示例：" + (sample.Length == 0 ? "（生成为空）" : sample);
            }
            catch (Exception ex)
            {
                previewText.Foreground = (Brush)FindResource("Danger");
                previewText.Text = "模式不合法：" + ex.Message;
            }
        };
        void RefreshRandom()
        {
            bool on = randomCheck.IsChecked == true;
            randomHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            previewBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) previewText.Visibility = Visibility.Collapsed;
            textHintNormal.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        }
        randomCheck.Checked += (_, _) => RefreshRandom();
        randomCheck.Unchecked += (_, _) => RefreshRandom();
        RefreshRandom();
        textPanel.Children.Add(SubGroup("文本内容", textBox, textHintNormal, randomRow, randomHint, previewText));

        var textModeCombo = new ComboBox { Height = 32 };
        var itAuto = new ComboBoxItem { Content = "自动（推荐）", Tag = "" };
        var itUni = new ComboBoxItem { Content = "字符注入", Tag = "Unicode" };
        var itClip = new ComboBoxItem { Content = "剪贴板粘贴", Tag = "Clipboard" };
        textModeCombo.Items.Add(itAuto); textModeCombo.Items.Add(itUni); textModeCombo.Items.Add(itClip);
        textModeCombo.SelectedIndex = 0;
        var charDelayText = new TextBox { Text = "0", Width = 90, Height = 32 };
        var charDelayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        charDelayRow.Children.Add(new TextBlock { Text = "逐字间隔", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        charDelayRow.Children.Add(charDelayText);
        charDelayRow.Children.Add(new TextBlock { Text = "毫秒（0 = 不等待；仅字符注入方式有效）", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(8, 0, 0, 0) });
        var textModeNote = new TextBlock { Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        textPanel.Children.Add(SubGroup("输入方式", textModeCombo, textModeNote, charDelayRow));

        void RefreshTextMode()
        {
            string m = (textModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            bool clip = hwBackend || m == "Clipboard";
            charDelayRow.Visibility = clip ? Visibility.Collapsed : Visibility.Visible;
            textModeNote.Text = hwBackend
                ? "当前输出方式为 CH9329 硬件键盘：它模拟的是真实键盘按键，无法直接发出汉字等字符，因此文本一律走剪贴板粘贴（会自动备份并还原你原有的剪贴板内容）。\n⚠ 部分软件禁用粘贴、或用自绘输入框不响应 Ctrl+V，此时不会生效；游戏内多数也不支持。"
                : m == "Clipboard"
                    ? "把文本写入剪贴板后发送 Ctrl+V（会自动备份并还原你原有的剪贴板内容）。\n⚠ 部分软件禁用粘贴、或用自绘输入框不响应 Ctrl+V，此时不会生效；游戏内多数也不支持。"
                    : m == "Unicode"
                        ? "逐字符直接注入（绕过键盘布局与输入法，中文可直接输出）。\n⚠ 游戏等直接读取键盘扫描码的程序收不到这类字符，此时请改用剪贴板粘贴。"
                        : "自动：本机模拟 → 逐字符注入（中文可直接输出）；CH9329 硬件 → 剪贴板粘贴。\n⚠ 注入对读取扫描码的游戏无效；粘贴对禁用 Ctrl+V 的软件无效。";
        }
        if (hwBackend)   // 硬件后端：注入项不可选，直接锁到剪贴板
        {
            itAuto.IsEnabled = false; itUni.IsEnabled = false;
            textModeCombo.SelectedItem = itClip;
        }
        textModeCombo.SelectionChanged += (_, _) => RefreshTextMode();
        RefreshTextMode();
        var textRepeat = new RepeatBlock(this, "输入次数（0 为无限）");
        textPanel.Children.Add(textRepeat.Panel);

        // 等待面板
        var waitRow = new TimeInputRow(this, _doc.DefaultWaitMs);
        waitRow.Panel.Margin = new Thickness(0, 0, 0, 0);
        var waitPanel = SubGroup("等待时间", waitRow.Panel);
        waitPanel.Visibility = Visibility.Collapsed; baseContent.Children.Add(waitPanel);

        // 激活窗口面板：从当前窗口列表选目标，选中即锁定该进程（含 PID）。
        var windowInner = new StackPanel();
        var windowPanel = SubGroup(null, windowInner);
        windowPanel.Visibility = Visibility.Collapsed; baseContent.Children.Add(windowPanel);
        baseContent.Children.Add(mousePanel2);   // 窗口选择框之后才是终点坐标等（顺序即"先选窗口、再设终点"）
        var winHeader = new DockPanel { LastChildFill = false };
        winHeader.Children.Add(new TextBlock { Text = "选择目标窗口", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var idBtnWin = new Button { Style = (Style)FindResource("IconButton"), FontSize = 16, Content = "", ToolTip = "标识屏幕（在各屏显示编号，帮你分清桌面对应哪块屏）" };
        DockPanel.SetDock(idBtnWin, Dock.Right); winHeader.Children.Add(idBtnWin);
        idBtnWin.Click += (_, _) => ShowIdScreens(win);
        windowInner.Children.Add(winHeader);
        int selPid = 0; string selProc = ""; string selTitle = "";
        const string Desk = Services.WindowActivator.DesktopSentinel;
        static string TitleOr(string t) => string.IsNullOrEmpty(t) ? "(无标题)" : t;

        // 当前选择显示框（点击弹出搜索列表）——自绘，避开可编辑 ComboBox 的焦点/过滤/清空坑。
        var selText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)FindResource("Ink"), FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI") };
        var arrow = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L8,0 L4,5 Z"), Fill = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) };
        var fieldDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(arrow, Dock.Right); fieldDock.Children.Add(arrow); fieldDock.Children.Add(selText);
        var fieldBorder = new Border { Background = (Brush)FindResource("Field"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Height = 34, Padding = new Thickness(10, 0, 8, 0), Cursor = Cursors.Hand, Child = fieldDock };
        var winGrid = new Grid { Margin = new Thickness(0, 6, 0, 8) };
        winGrid.ColumnDefinitions.Add(new ColumnDefinition());
        winGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(fieldBorder, 0); winGrid.Children.Add(fieldBorder);
        var refreshBtn = new Button { Style = (Style)FindResource("IconButton"), FontSize = 16, Content = "", ToolTip = "刷新窗口列表", Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(refreshBtn, 1); winGrid.Children.Add(refreshBtn);
        windowInner.Children.Add(winGrid);
        windowInner.Children.Add(new TextBlock { Text = "点上方选择目标窗口，可输入关键词搜索（标题 / 进程 / PID）。选中即锁定该进程（同名多开用 PID 区分）；下次运行优先按 PID 命中，PID 变了按进程名回退。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap });

        // 弹出层：搜索框 + 窗口列表（我方完全掌控开合，不依赖 ComboBox 焦点行为）。
        var winItems = new ObservableCollection<WinPick>();
        var winView = System.Windows.Data.CollectionViewSource.GetDefaultView(winItems);
        var searchBox = new TextBox { Height = 34, Margin = new Thickness(0, 0, 0, 6), VerticalContentAlignment = VerticalAlignment.Center };
        var listBox = new ListBox { MaxHeight = 320, ItemsSource = winItems, FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI") };
        var popContent = new StackPanel { Margin = new Thickness(8) };
        popContent.Children.Add(searchBox); popContent.Children.Add(listBox);
        var popBorder = new Border { Background = (Brush)FindResource("Panel"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = popContent };
        // StaysOpen=true：开合完全我方掌控，避免"点开即被同一次点击当外部点击关掉"的闪烁。
        var popup = new System.Windows.Controls.Primitives.Popup { PlacementTarget = fieldBorder, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = true, AllowsTransparency = true, Child = popBorder };
        static bool InTree(object o, DependencyObject root)
        {
            var d = o as DependencyObject;
            while (d != null)
            {
                if (d == root) return true;
                d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        void UpdateSelLabel()
        {
            if (selProc == Desk) { selText.Text = string.IsNullOrEmpty(selTitle) ? "桌面（所有应用失活）" : $"桌面 · {ScreenInfo.ByDevice(selTitle).Label}"; return; }
            selText.Text = (selPid > 0 || selProc.Length > 0 || selTitle.Length > 0)
                ? $"[{selPid,-6}] {selProc}.exe  —  {TitleOr(selTitle)}" : "点此选择窗口…";
        }
        bool loadingList = false;
        void RefreshWindows()
        {
            loadingList = true;
            winItems.Clear();
            winItems.Add(new WinPick { Info = new Services.WindowActivator.WinInfo(IntPtr.Zero, "", Desk, -1), Display = "🖥  桌面（所有应用失活）" });
            foreach (var m in ScreenInfo.All())
                winItems.Add(new WinPick { Info = new Services.WindowActivator.WinInfo(IntPtr.Zero, m.Device, Desk, -1), Display = "🖥  桌面 · " + m.Label });
            foreach (var w in Services.WindowActivator.ListTopWindows())
                winItems.Add(new WinPick { Info = w, Display = $"[{w.Pid,-6}] {w.Process}.exe  —  {TitleOr(w.Title)}" });
            winView.Filter = null;
            listBox.SelectedItem = null;
            loadingList = false;
        }
        void FlashPick(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            Services.WindowActivator.ActivateHwnd(hwnd);
            var back = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            t.Tick += (_, _) => { t.Stop(); Services.WindowActivator.ActivateHwnd(back); };
            t.Start();
        }
        bool picking = false;
        void Pick(WinPick wp)
        {
            selPid = wp.Info.Pid; selProc = wp.Info.Process; selTitle = wp.Info.Title;
            UpdateSelLabel();
            popup.IsOpen = false;
            // 延后"闪一下目标窗口"：FlashPick → ActivateHwnd 内部会 SetForegroundWindow，
            // 若在 SelectionChanged 同步调用会泵消息循环、被排队的输入事件重入，
            // 造成 SelectedItem 在处理中途变化 → 间歇性选中/激活到另一个窗口。放到输入处理完成后再跑。
            var hwnd = wp.Info.Hwnd;
            win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => FlashPick(hwnd)));
        }
        void OpenPicker()
        {
            RefreshWindows();
            searchBox.Text = "";
            popup.MinWidth = fieldBorder.ActualWidth;
            popup.IsOpen = true;
            searchBox.Focus();
        }
        fieldBorder.MouseLeftButtonDown += (_, _) => { if (popup.IsOpen) popup.IsOpen = false; else OpenPicker(); };
        // 弹层内的点击在独立 hwnd，不会触发本窗口的 PreviewMouseDown；点字段之外的地方（且非字段本身）才关闭。
        win.PreviewMouseDown += (_, e) => { if (popup.IsOpen && !InTree(e.OriginalSource, fieldBorder)) popup.IsOpen = false; };
        searchBox.TextChanged += (_, _) =>
        {
            string q = searchBox.Text ?? "";
            winView.Filter = q.Length == 0 ? null : o => ((WinPick)o).Display.Contains(q, StringComparison.OrdinalIgnoreCase);
        };
        listBox.SelectionChanged += (_, e) =>
        {
            if (loadingList || picking) return;
            // 用 e.AddedItems（本次确实新选中的项）而非 SelectedItem——后者在重入/刷新时可能已被改写。
            var wp = (e.AddedItems.Count > 0 ? e.AddedItems[0] : listBox.SelectedItem) as WinPick;
            if (wp == null) return;
            picking = true;
            try { Pick(wp); } finally { picking = false; }
        };
        refreshBtn.Click += (_, _) => OpenPicker();
        UpdateSelLabel();

        // 标记区：循环 / 跳转 / 监听 / 备注
        // 鼠标的「点击次数 / 滚动次数 + 重复间隔」（键盘那份在键盘面板里，同一套 RepeatBlock 逻辑）。
        var mouseRepeat = new RepeatBlock(this, "点击次数（0 为无限）");

        // 界面顺序（每块都是独立小卡，关联字段在同一卡内）：
        // 鼠标按钮 → 坐标 → 按住时间 → 点击次数(+重复间隔) → 滚轮格数 → 拟人化移动（最后）
        mousePanel.Children.Add(mouseButtonPanel);
        mousePanel.Children.Add(mouseTargetPanel);   // 紧跟在「鼠标按钮」下面
        mousePanel.Children.Add(dragModePanel);
        // —— 中间是 windowPanel（拖动窗口 / 激活窗口共用）——
        dragEnd.InsertTopRow(dragAnchorRow);   // 对齐点与终点坐标同卡：它说的就是"这个坐标指窗口的哪儿"
        mousePanel2.Children.Add(mouseMovePanel);
        mousePanel2.Children.Add(dragEndPanel);
        mousePanel2.Children.Add(clickImagePanel);
        mousePanel2.Children.Add(mouseHoldPanel);
        mousePanel2.Children.Add(mouseWheelPanel);      // 滚轮格数排在滚动次数前面：先定"一次滚多少"，再定"滚几次"
        mousePanel2.Children.Add(mouseRepeat.Panel);
        mousePanel2.Children.Add(humanizePanel);

        // 跳转面板（运行 → 跳转）：原先挂在每个动作上的「执行后跳转到」已剥离成这个独立动作。
        var jumpInner = new StackPanel();
        var jumpPanel = SubGroup(null, jumpInner);
        jumpPanel.Visibility = Visibility.Collapsed; baseContent.Children.Add(jumpPanel);
        var jumpTargetCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), Height = 32 };
        // 目标按【别名】选择（像 goto 的标签）：只有设了别名的动作才能被跳转。
        // 与序号/身份彻底解耦——插入、删除、排序都不需要任何同步逻辑。
        jumpTargetCombo.Items.Add(new ComboBoxItem { Content = "（选择目标别名）", Tag = "" });
        if (_plan != null)
            foreach (var tgt in _plan.Steps)
            {
                if (tgt.Alias.Length == 0) continue;
                var brief = tgt.Brief;
                if (brief.Length > 36) brief = brief[..36] + "…";
                jumpTargetCombo.Items.Add(new ComboBoxItem { Content = $"「{tgt.Alias}」 {brief}", Tag = tgt.Alias });
            }
        jumpTargetCombo.SelectedIndex = 0;
        jumpInner.Children.Add(FieldLabel("跳转到（按别名）"));
        jumpInner.Children.Add(jumpTargetCombo);
        jumpInner.Children.Add(new TextBlock
        {
            Text = "只有设置了「别名」的动作才会出现在这里；给目标动作填上别名（下方「别名与备注」卡片）即可被跳转。",
            Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        jumpInner.Children.Add(FieldLabel("最大重复次数（0 为不限）"));
        var jumpMaxText = new TextBox { Text = "0", Margin = new Thickness(0, 0, 0, 8), Height = 32 };
        jumpInner.Children.Add(jumpMaxText);
        jumpInner.Children.Add(new TextBlock { Text = "每次执行到本动作就跳到指定别名的动作继续（仅方案顶层生效）。最大重复次数是防死循环的上限：本轮内已跳次数达到上限后，该跳转不再生效、按顺序往下走；0 表示不设上限。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap });

        // 运行类（等待/激活窗口）的 执行次数+重复间隔：与鼠标/键盘同一套 RepeatBlock，后续新类型照此办理。
        var runRepeat = new RepeatBlock(this, "执行次数（0 为无限）");
        Border? stepRepeatCard = null;
        var stepRepeat = new RepeatBlock(this, "重复次数（0 为无限）", forRepeat: true, note: "每一趟都会【重新判定上面的运行条件、重新触发监听动作】，并各记一条日志。与上面各类动作里的「点击次数 / 按键次数 / 滚动次数 / 执行次数」不同——那个只是把动作本体多做几遍，条件只判一次、监听只走一遍。");
        var stepUntil = new UntilBlock(this, stepRepeat);
        baseContent.Children.Add(runRepeat.Panel);
        var noteText = new TextBox { Text = "", Margin = new Thickness(0, 0, 0, 14), Height = 32 };
        var aliasText = new TextBox { Text = "", Margin = new Thickness(0, 0, 0, 14), Height = 32 };
        var cond = BuildRunConditionEditor(null);    // 与方案级共用同一套控件与逻辑

        MacroStep? hookPreCond = source?.PreCondAction, hookCondOk = source?.CondSuccessAction, hookCondFail = source?.CondFailAction,
                   hookPreRun = source?.PreRunAction,
                   hookSuccess = source?.SuccessAction, hookComplete = source?.CompleteAction, hookFail = source?.FailAction;
        sp.Children.Add(GroupCard("基础设置", baseContent));
        {
            var condPanel = cond.Panel;
            condPanel.Margin = new Thickness(0, 0, 0, 4);
            sp.Children.Add(GroupCard("运行条件", condPanel));   // 独立成卡
            // 「重复次数」紧跟运行条件：它的意义就是"整趟重复，每趟重新判条件"，放一起才看得懂
            stepRepeatCard = GroupCard("重复", stepUntil.Panel);
            sp.Children.Add(stepRepeatCard);

            var hookNote = new TextBlock { Text = "在动作生命周期的各节点追加执行一个完整动作（可含循环、运行条件、组合，并能继续挂自己的监听）。「条件」类监听仅在本动作设置了运行条件时触发。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(GroupCard("事件监听",
                hookNote,
                BuildHookRow(win, "运行条件判断前", () => hookPreCond, v => hookPreCond = v),
                BuildHookRow(win, "判断成功后", () => hookCondOk, v => hookCondOk = v),
                BuildHookRow(win, "判断失败后", () => hookCondFail, v => hookCondFail = v),
                BuildHookRow(win, "运行前", () => hookPreRun, v => hookPreRun = v),
                BuildHookRow(win, "运行成功后", () => hookSuccess, v => hookSuccess = v),
                BuildHookRow(win, "运行失败后", () => hookFail, v => hookFail = v),
                BuildHookRow(win, "运行结束后", () => hookComplete, v => hookComplete = v)));

            sp.Children.Add(GroupCard("别名与备注（可选）",
                FieldLabel("别名（跳转用标签，方案内应唯一）"), aliasText,
                FieldLabel("备注"), noteText));
        }

        // 主/次动作分明：确定=强调色实心（主动作），取消=描边空心（次动作）。底部固定不随内容滚动，上方加分割线。
        var okBtn = new Button { Content = source == null ? "添加" : "确定", Width = 88, Height = 36, IsDefault = true, Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 10, 0) };
        var cancelBtn = new Button { Content = "取消", Width = 88, Height = 36, IsCancel = true, Style = (Style)FindResource("GhostButton"), Margin = new Thickness(0) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(okBtn); bar.Children.Add(cancelBtn);
        var footer = new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 14, 0), Child = bar };
        Grid.SetRow(footer, 1); grid.Children.Add(footer);

        // 屏幕序号标记同步：进入"目标显示器/选择窗口"视图显示、离开收起。放后台优先级异步做，别拖慢窗口打开。
        // 当前选中的三级路径。非"输入"类别时后两级无意义，统一返回空串。
        string Cat() => typeCombo.SelectedItem?.ToString() ?? "输入";
        string Dev() => Cat() == "输入" ? (deviceCombo.SelectedItem?.ToString() ?? "鼠标") : "";
        string Act() => Dev() == "鼠标" ? (mouseActionCombo.SelectedItem?.ToString() ?? "点击") : "";
        string Target() => mouseTargetCombo.SelectedItem?.ToString() ?? "";
        // 目标种类：self=当前位置 / coord=坐标 / image=图片。按语义判断，别拿显示文案去比。
        string TargetKind()
        {
            var t = Target();
            if (t.Contains("图片")) return "image";
            if (t.Contains("坐标")) return "coord";
            return "self";
        }
        // 「点击」可以打当前位置/坐标/图片；「移动」只有坐标/图片（移到"当前位置"没有意义）。
        // 换动作时重建选项，并尽量保留原来选的那个。
        bool _tgtLoading = false;
        void SyncTargets()
        {
            string a2 = Act();
            if (a2 is not ("点击" or "移动")) return;
            var want = a2 == "点击"
                ? new[] { "仅点击（当前位置）", "点击坐标", "点击图片" }
                : new[] { "移动到坐标", "移动到图片" };
            var keep = TargetKind();
            if (mouseTargetCombo.Items.Count == want.Length)
            {
                bool same = true;
                for (int i = 0; i < want.Length; i++) if (!Equals(mouseTargetCombo.Items[i], want[i])) { same = false; break; }
                if (same) return;
            }
            _tgtLoading = true;
            mouseTargetCombo.Items.Clear();
            foreach (var t in want) mouseTargetCombo.Items.Add(t);
            int idx = 0;   // 换动作时保留同一种目标（坐标↔坐标、图片↔图片），没有对应项就落回第一个
            for (int i = 0; i < want.Length; i++)
            {
                string k = want[i].Contains("图片") ? "image" : want[i].Contains("坐标") ? "coord" : "self";
                if (k == keep) { idx = i; break; }
            }
            mouseTargetCombo.SelectedIndex = idx;
            _tgtLoading = false;
        }
        string KeyAct() => Dev() == "键盘" ? (keyActionCombo.SelectedItem?.ToString() ?? "按键") : "";
        string RunAct() => Cat() == "运行" ? (runActionCombo.SelectedItem?.ToString() ?? "等待") : "";

        void SyncIdScreens()
        {
            // 需要选屏的两种：激活窗口、带坐标的鼠标动作。只有一块屏时不自动标（没意义），手动「标识屏幕」按钮不受影响。
            bool coordView = Act() is "拖动" || (Act() is "点击" or "移动" && TargetKind() != "self");
            bool needScreens = (RunAct() == "激活窗口" || coordView) && ScreenInfo.All().Count > 1;
            if (needScreens) ShowIdScreens(win); else HideIdScreens(win);
        }
        void UpdatePanels()
        {
            string t = Cat(), d = Dev(), a = Act(), ra = RunAct();
            bool dragWindow = a == "拖动" && dragModeCombo.SelectedIndex == 1;   // 拖动窗口模式（起点是窗口自己，不用坐标）
            deviceCombo.Visibility = t == "输入" ? Visibility.Visible : Visibility.Collapsed;
            mouseActionCombo.Visibility = d == "鼠标" ? Visibility.Visible : Visibility.Collapsed;
            runActionCombo.Visibility = t == "运行" ? Visibility.Visible : Visibility.Collapsed;
            keyKindPanel.Visibility = d == "键盘" ? Visibility.Visible : Visibility.Collapsed;

            mousePanel.Visibility = mousePanel2.Visibility = d == "鼠标" ? Visibility.Visible : Visibility.Collapsed;
            keyboardPanel.Visibility = d == "键盘" && KeyAct() == "按键" ? Visibility.Visible : Visibility.Collapsed;
            textPanel.Visibility = d == "键盘" && KeyAct() == "文本" ? Visibility.Visible : Visibility.Collapsed;
            waitPanel.Visibility = ra == "等待" ? Visibility.Visible : Visibility.Collapsed;
            // 窗口选择框：激活窗口动作用它，「拖动窗口」也复用同一个（一个对话框只编辑一个动作，不会打架）
            windowPanel.Visibility = ra == "激活窗口" || (a == "拖动" && dragWindow) ? Visibility.Visible : Visibility.Collapsed;
            jumpPanel.Visibility = ra == "跳转" ? Visibility.Visible : Visibility.Collapsed;
            // 点击/移动 的目标由第二个下拉决定：当前位置(仅点击) / 坐标 / 图片；拖动固定两个坐标；滚轮无目标。
            SyncTargets();
            bool isClick = a == "点击", isMove = a == "移动", isDrag = a == "拖动", isWheel = a == "滚轮";
            mouseTargetPanel.Visibility = (isClick || isMove) && d == "鼠标" ? Visibility.Visible : Visibility.Collapsed;
            mouseTargetTitle.Text = isMove ? "移动类型" : "点击类型";
            dragModePanel.Visibility = isDrag ? Visibility.Visible : Visibility.Collapsed;
            dragAnchorRow.Visibility = dragWindow ? Visibility.Visible : Visibility.Collapsed;   // 坐标拖动没有"窗口的哪个点"一说
            string tgt = TargetKind();
            bool isClickAt = isClick && tgt == "coord", isClickImage = isClick && tgt == "image";
            bool isMoveAt = isMove && tgt != "image", isMoveImage = isMove && tgt == "image";
            bool anyImage = isClickImage || isMoveImage;
            bool coordForced = isMoveAt || (isDrag && !dragWindow) || isClickAt;   // 这三种必须有起点坐标（拖动窗口的"起点"是窗口自己）
            if (coordForced && coordCheck.IsChecked != true) coordCheck.IsChecked = true;
            coordCheck.IsEnabled = false;   // 坐标显隐完全由动作类型决定，勾选框不再交互
            coordCheck.Content = isMoveAt ? "坐标（移动到该位置，必须设置）"
                               : isDrag ? "起点坐标（在此按下鼠标键）"
                               : "坐标（移动到该位置再点击）";
            dragEndPanel.Visibility = isDrag ? Visibility.Visible : Visibility.Collapsed;   // 拖动才有终点
            dragEnd.SetTitle(dragWindow ? "终点坐标（窗口拖到这里）" : "终点坐标");

            // 点击（无论打哪儿）都要按钮 + 按住时间；滚轮/移动不要。
            bool anyClick = isClick;
            mouseButtonPanel.Visibility = anyClick || isDrag ? Visibility.Visible : Visibility.Collapsed;   // 拖动窗口也要选用哪个键按住标题栏
            mouseMovePanel.Visibility = coordForced ? Visibility.Visible : Visibility.Collapsed;   // 纯点击/点击图片无坐标块
            clickImagePanel.Visibility = anyImage ? Visibility.Visible : Visibility.Collapsed;
            mouseHoldPanel.Visibility = anyClick ? Visibility.Visible : Visibility.Collapsed;
            mouseWheelPanel.Visibility = isWheel ? Visibility.Visible : Visibility.Collapsed;
            // 拟人化在会发生"移动到目标"时都有意义：点击坐标 / 移动 / 图片 / 两种拖动都算
            //（拖动窗口同样是按住标题栏一路拖过去的，理应也能走拟人化轨迹）。
            humanizePanel.Visibility = coordForced || anyImage || isDrag ? Visibility.Visible : Visibility.Collapsed;

            bool hasRepeat = anyClick || isWheel || isDrag;   // 拖动也要次数（拖几次），只是它没有"重复次数"
            mouseRepeat.Panel.Visibility = hasRepeat ? Visibility.Visible : Visibility.Collapsed;
            mouseRepeat.CountLabel.Text = isWheel ? "滚动次数（0 为无限）"
                                        : isDrag ? "拖动次数（0 为无限）"
                                        : "点击次数（0 为无限）";
            // 移动与拖动不给「重复次数」：移动重复等于原地不动；拖动要重复用上面的"拖动次数"就够了。
            bool noStepRepeat = d == "鼠标" && (isMove || isDrag);
            if (stepRepeatCard != null) stepRepeatCard.Visibility = noStepRepeat ? Visibility.Collapsed : Visibility.Visible;
            // 运行类的 执行次数+重复间隔：等待/激活窗口显示；跳转有自己的跳转次数，不显示。
            runRepeat.Panel.Visibility = ra is "等待" or "激活窗口" ? Visibility.Visible : Visibility.Collapsed;

            capturingKey = d == "键盘" && KeyAct() == "按键";
            if (capturingKey) win.Focus();
            // 窗口列表改按需枚举（点选择器时才 RefreshWindows，见 OpenPicker），不在打开时同步枚举；
            // 屏幕序号标记（每屏一个置顶窗口）也延后到后台优先级异步显示 —— 消除激活窗口/鼠标移动动作双击打开时的卡顿厚重感。
            win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(SyncIdScreens));
        }
        typeCombo.SelectionChanged += (_, _) => UpdatePanels();
        deviceCombo.SelectionChanged += (_, _) => UpdatePanels();
        mouseActionCombo.SelectionChanged += (_, _) => UpdatePanels();
        mouseTargetCombo.SelectionChanged += (_, _) => { if (!_tgtLoading) UpdatePanels(); };
        dragModeCombo.SelectionChanged += (_, _) => UpdatePanels();
        runActionCombo.SelectionChanged += (_, _) => UpdatePanels();
        keyActionCombo.SelectionChanged += (_, _) => UpdatePanels();
        buttonCombo.SelectionChanged += (_, _) => UpdatePanels();   // 切「仅移动」要收起按住时间/次数
        coordCheck.Checked += (_, _) => UpdatePanels();             // 勾选坐标才显示拟人化、才需要标屏
        coordCheck.Unchecked += (_, _) => UpdatePanels();

        win.PreviewKeyDown += (_, e) =>
        {
            if (!capturingKey || Keyboard.FocusedElement is TextBox) return;
            e.Handled = true;
            try
            {
                var c = ConvertWpfKey(e);
                capturedKey = c.Key; capturedModifier = c.Modifier; capturedText.Text = c.Display;
            }
            catch (Exception ex)
            {
                capturedKey = ""; capturedModifier = 0; capturedText.Text = "该按键不支持，请按其他键";
                ThemedDialog.Show(ex.Message, "按键暂不支持", MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
        };

        MacroStep? result = null;
        bool settingsChanged = false;
        okBtn.Click += (_, _) =>
        {
            try
            {
                string t = Cat(), dev = Dev(), ra = RunAct();
                if (dev == "鼠标")
                {
                    string a = Act();
                    // 点击坐标同时用到坐标与按钮两组字段，故与移动/点击共用同一套读取。
                    void FillMove(MacroStep m)
                    {
                        var (dev0, nx0, ny0) = coord.Read();
                        m.MoveMonitor = dev0; m.MoveNormX = nx0; m.MoveNormY = ny0;
                        m.Humanize = humanizeMoveCheck.IsChecked == true;
                        m.ClickOffset = coord.ReadOffset();
                    }
                    void FillButton(MacroStep m)
                    {
                        m.Button = ButtonToInternal(buttonCombo.SelectedItem?.ToString() ?? "左键");
                        m.HoldMs = holdRow.GetMs();
                        m.HoldUnit = holdRow.UnitIndex;
                    }
                    // 「动作 + 目标」→ 存储类型（存储沿用原来的类型名，旧方案零迁移）：
                    //   点击 + 当前位置/坐标/图片 → MouseClick / MouseClickAt / MouseClickImage
                    //   移动 + 坐标/图片          → MouseMove / MouseMoveImage
                    //   拖动 → MouseDrag、滚轮 → MouseWheel
                    void ApplyHoldDefault()
                    {
                        if (holdRow.SetAsDefault)
                        {
                            var ms = holdRow.GetMs();
                            if (_doc.DefaultHoldMs != ms) { _doc.DefaultHoldMs = ms; settingsChanged = true; }
                        }
                    }
                    string tgt = TargetKind();
                    if (a == "滚轮")
                    {
                        result = new MacroStep { Type = "MouseWheel", Wheel = ParseInt(wheelText.Text, 0) };
                        mouseRepeat.Apply(result);
                    }
                    else if (a == "拖动")
                    {
                        // 坐标拖动 = 移到起点 → 按下 → 移到终点 → 松开（起点存 MoveXxx，终点存 DragEndXxx）。
                        // 拖动窗口 = 激活 Target* 指定的窗口 → 按住标题栏 → 拖到"左上角落在终点"的位置。
                        result = new MacroStep { Type = "MouseDrag", Button = ButtonToInternal(buttonCombo.SelectedItem?.ToString() ?? "左键") };
                        if (dragModeCombo.SelectedIndex == 1)
                        {
                            if (selPid <= 0 && selProc.Length == 0 && selTitle.Length == 0)
                                throw new InvalidOperationException("请从列表选择要拖动的窗口。");
                            if (selProc == WindowActivator.DesktopSentinel)
                                throw new InvalidOperationException("桌面不能拖动，请选择一个普通窗口。");
                            result.DragMode = "Window";
                            result.DragAnchor = ParseInt(TagOf(dragAnchorCombo), 0);
                            result.TargetProcess = selProc; result.TargetTitle = selTitle; result.TargetPid = selPid;
                        }
                        else FillMove(result);
                        var (devE, nxE, nyE) = dragEnd.Read();
                        result.DragEndMonitor = devE; result.DragEndNormX = nxE; result.DragEndNormY = nyE;
                        result.Humanize = humanizeMoveCheck.IsChecked == true;
                        mouseRepeat.Apply(result);   // 拖动次数（连拖几次，中间按重复间隔停顿）
                    }
                    else if (a == "移动")
                    {
                        if (tgt == "image")   // 区域内搜图 → 只把光标移到第 N 个命中处
                        {
                            result = new MacroStep { Type = "MouseMoveImage" };
                            clickImage.Apply(result);   // 校验缺图会抛异常，下面统一提示
                            result.Humanize = humanizeMoveCheck.IsChecked == true;
                        }
                        else
                        {
                            result = new MacroStep { Type = "MouseMove" };
                            FillMove(result);
                        }
                        result.LoopCount = 1;   // 移动没有次数概念
                    }
                    else if (a == "点击")
                    {
                        if (tgt == "image")        // 区域内搜图 → 点第 N 个
                        {
                            result = new MacroStep { Type = "MouseClickImage" };
                            clickImage.Apply(result);
                            result.Humanize = humanizeMoveCheck.IsChecked == true;
                        }
                        else if (tgt == "coord")   // 先移动到坐标再点
                        {
                            result = new MacroStep { Type = "MouseClickAt" };
                            FillMove(result);
                        }
                        else                      // 当前位置：纯点击，不涉及坐标
                        {
                            result = new MacroStep { Type = "MouseClick" };
                        }
                        FillButton(result);
                        mouseRepeat.Apply(result);
                        ApplyHoldDefault();
                    }
                    else throw new InvalidOperationException("请选择鼠标动作。");
                }
                else if (dev == "键盘" && KeyAct() == "文本")
                {
                    var txt = textBox.Text ?? "";
                    if (txt.Length == 0) throw new InvalidOperationException("请先填写要输入的文本内容。");
                    result = new MacroStep
                    {
                        Type = "TextInput",
                        Text = txt,
                        TextMode = (textModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
                        TextCharDelayMs = Math.Max(0, ParseInt(charDelayText.Text, 0)),
                        TextRandom = randomCheck.IsChecked == true,
                    };
                    if (result.TextRandom)   // 存之前先校验模式，别让非法模式留到运行时才炸
                    {
                        try { Services.RandomText.Generate(txt); }
                        catch (Exception ex) { throw new InvalidOperationException("随机模式不合法：" + ex.Message); }
                    }
                    textRepeat.Apply(result);
                }
                else if (dev == "键盘")
                {
                    if (string.IsNullOrWhiteSpace(capturedKey) && capturedModifier == 0)
                        throw new InvalidOperationException("请先按下需要模拟的键。");
                    result = new MacroStep { Type = "KeyTap", Key = capturedKey, Modifier = capturedModifier, HoldMs = keyboardHoldRow.GetMs(), HoldUnit = keyboardHoldRow.UnitIndex };
                    kbRepeat.Apply(result);   // 按键次数 + 重复间隔（与鼠标同一套逻辑）
                    if (keyboardHoldRow.SetAsDefault)
                    {
                        var ms = keyboardHoldRow.GetMs();
                        if (_doc.DefaultHoldMs != ms) { _doc.DefaultHoldMs = ms; settingsChanged = true; }
                    }
                }
                else if (ra == "等待")
                {
                    result = new MacroStep { Type = "Wait", DurationMs = waitRow.GetMs(), DurationUnit = waitRow.UnitIndex };
                    if (waitRow.SetAsDefault)
                    {
                        var ms = waitRow.GetMs();
                        if (_doc.DefaultWaitMs != ms) { _doc.DefaultWaitMs = ms; settingsChanged = true; }
                    }
                }
                else if (ra == "跳转")
                {
                    if (jumpTargetCombo.SelectedIndex < 1)
                        throw new InvalidOperationException("请选择跳转的目标别名（目标动作要先设置别名）。");
                    result = new MacroStep
                    {
                        Type = "Jump",
                        JumpTargetAlias = (jumpTargetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
                        JumpTimes = Math.Max(0, ParseInt(jumpMaxText.Text, 0)),
                    };
                }
                else // 激活窗口
                {
                    if (selPid <= 0 && selProc.Length == 0 && selTitle.Length == 0)
                        throw new InvalidOperationException("请从列表选择目标窗口。");
                    result = new MacroStep { Type = "ActivateWindow", TargetProcess = selProc, TargetTitle = selTitle, TargetPid = selPid };
                }
                {
                    if (ra is "等待" or "激活窗口") runRepeat.Apply(result);   // 鼠标/键盘的次数已由各自 RepeatBlock 写过
                    // 整趟重复：卡片藏起来的动作（移动/拖动）强制回到 1，避免把隐藏控件里的残值写进去
                    if (stepRepeatCard is { Visibility: Visibility.Visible }) stepUntil.Apply(result);
                    else { result.RepeatCount = 1; result.RepeatUntil = false; result.UntilConditions = new(); }
                    ApplyRunCondition(cond, result);   // 与方案级同一份写回逻辑（校验失败抛异常，下面统一提示）
                    result.PreCondAction = hookPreCond; result.CondSuccessAction = hookCondOk; result.CondFailAction = hookCondFail;
                    result.PreRunAction = hookPreRun;
                    result.SuccessAction = hookSuccess; result.CompleteAction = hookComplete; result.FailAction = hookFail;
                    result.Note = noteText.Text.Trim();
                    result.Alias = aliasText.Text.Trim();
                }
                if (settingsChanged) PersistSettings(); // 持久化默认时长等设置，不提交未保存的方案修改
                win.DialogResult = true;
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(ex.Message, "添加失败", MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
        };

        // 回填已有动作
        if (source != null)
        {
            // 回填坐标（移动 / 点击坐标共用）
            void LoadMoveFields()
            {
                if (!string.IsNullOrEmpty(source.MoveMonitor)) coord.Write(source.MoveMonitor, source.MoveNormX, source.MoveNormY);
                else // 旧数据：主屏像素 → 主屏归一化
                {
                    var pm = ScreenInfo.Primary();
                    coord.Write(pm.Device, source.X / (double)pm.Width, source.Y / (double)pm.Height);
                }
                humanizeMoveCheck.IsChecked = source.Humanize;
                coord.WriteOffset(source.ClickOffset);
            }
            // 回填按钮/按住（点击 / 点击坐标共用）
            void LoadButtonFields()
            {
                buttonCombo.SelectedItem = TranslateButtonToDisplay(source.Button);
                holdRow.SetMs(source.HoldMs, source.HoldUnit);
            }
            switch (source.Type)
            {
                // 存储类型 → 「动作 + 目标」：目标按下标选（点击 0 仅点击 / 1 坐标 / 2 图片；移动 0 坐标 / 1 图片），
                // 不比对显示文案，改措辞不会连带出错。
                case "MouseMove":
                    typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "鼠标"; mouseActionCombo.SelectedItem = "移动";
                    SyncTargets(); mouseTargetCombo.SelectedIndex = 0; LoadMoveFields(); break;
                case "MouseClick":
                    typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "鼠标"; mouseActionCombo.SelectedItem = "点击";
                    SyncTargets(); mouseTargetCombo.SelectedIndex = 0; LoadButtonFields(); break;
                case "MouseClickAt":
                    typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "鼠标"; mouseActionCombo.SelectedItem = "点击";
                    SyncTargets(); mouseTargetCombo.SelectedIndex = 1; LoadMoveFields(); LoadButtonFields(); break;
                case "MouseClickImage":
                    typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "鼠标"; mouseActionCombo.SelectedItem = "点击";
                    SyncTargets(); mouseTargetCombo.SelectedIndex = 2;
                    clickImage.Load(source); humanizeMoveCheck.IsChecked = source.Humanize; LoadButtonFields(); break;
                case "MouseMoveImage":
                    typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "鼠标"; mouseActionCombo.SelectedItem = "移动";
                    SyncTargets(); mouseTargetCombo.SelectedIndex = 1;
                    clickImage.Load(source); humanizeMoveCheck.IsChecked = source.Humanize; break;
                case "MouseDrag":
                    typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "鼠标"; mouseActionCombo.SelectedItem = "拖动";
                    dragModeCombo.SelectedIndex = source.DragMode == "Window" ? 1 : 0;
                    if (source.DragMode == "Window")
                    {
                        selPid = source.TargetPid; selProc = source.TargetProcess; selTitle = source.TargetTitle; UpdateSelLabel();
                        dragAnchorCombo.SelectedIndex = Math.Clamp(source.DragAnchor, 0, 8);
                    }
                    coordCheck.IsChecked = true; LoadMoveFields(); LoadButtonFields();
                    dragEnd.Write(source.DragEndMonitor, source.DragEndNormX, source.DragEndNormY);
                    break;
                case "MouseWheel":    typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "鼠标"; mouseActionCombo.SelectedItem = "滚轮"; wheelText.Text = source.Wheel.ToString(); break;
                case "TextInput":
                    typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "键盘"; keyActionCombo.SelectedItem = "文本";
                    textBox.Text = source.Text;
                    charDelayText.Text = Math.Max(0, source.TextCharDelayMs).ToString();
                    randomCheck.IsChecked = source.TextRandom;
                    if (!hwBackend)   // 硬件后端已锁定剪贴板，不用回填模式
                        foreach (var it in textModeCombo.Items)
                            if (it is ComboBoxItem c && (c.Tag as string ?? "") == (source.TextMode ?? "")) { textModeCombo.SelectedItem = it; break; }
                    break;
                case "KeyTap":        typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "键盘"; keyActionCombo.SelectedItem = "按键"; capturedKey = source.Key; capturedModifier = source.Modifier; capturedText.Text = FormatCapturedKey(source.Key, source.Modifier); keyboardHoldRow.SetMs(source.HoldMs, source.HoldUnit); break;
                case "Wait":          typeCombo.SelectedItem = "运行"; runActionCombo.SelectedItem = "等待"; waitRow.SetMs(source.DurationMs, source.DurationUnit); break;
                case "ActivateWindow":typeCombo.SelectedItem = "运行"; runActionCombo.SelectedItem = "激活窗口"; selPid = source.TargetPid; selProc = source.TargetProcess; selTitle = source.TargetTitle; UpdateSelLabel(); break;
                case "Jump":          typeCombo.SelectedItem = "运行"; runActionCombo.SelectedItem = "跳转"; break;   // 目标/次数由下方通用回填写入
            }
            mouseRepeat.Load(source); kbRepeat.Load(source); runRepeat.Load(source); textRepeat.Load(source);
            LoadRunCondition(cond, source);   // 与方案级同一份回填逻辑
            // 回填按别名找；旧格式（Id/序号）没有别名，停在提示项由用户重选（运行期仍按旧字段回退可跑）
            if (source.JumpTargetAlias.Length > 0)
            {
                for (int n = 1; n < jumpTargetCombo.Items.Count; n++)
                    if ((jumpTargetCombo.Items[n] as ComboBoxItem)?.Tag as string == source.JumpTargetAlias) { jumpTargetCombo.SelectedIndex = n; break; }
            }
            jumpMaxText.Text = Math.Max(0, source.JumpTimes).ToString();
            noteText.Text = source.Note;
            aliasText.Text = source.Alias;
            stepUntil.Load(source);
        }

        win.Content = grid;
        UpdatePanels();
        return win.ShowDialog() == true ? result : null;
    }

    // ---------- 组合编辑对话框 ----------
    private MacroStep? ShowEditGroupDialog(MacroStep source)
    {
        var win = MakeDialog("编辑组合");
        var grid = new Grid { Margin = new Thickness(20, 20, 6, 20) }; // 右侧小边距，让滚动条贴近窗口右缘
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sp = new StackPanel { Margin = new Thickness(0) };
        var scroller = MakeScrollHost(sp); Grid.SetRow(scroller, 0); grid.Children.Add(scroller);

        var working = new ObservableCollection<MacroStep>(source.Children.Select(c => c.Clone()));
        var listContent = new StackPanel();
        var header = FieldLabel("");
        listContent.Children.Add(header);
        var childList = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
        listContent.Children.Add(new Border
        {
            BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 6, 0, 8),
            Child = new ScrollViewer { MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = childList },
        });
        var addBtn = new Button { Content = "＋ 添加动作", Height = 32, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 0, 6) };
        listContent.Children.Add(addBtn);
        sp.Children.Add(GroupCard("组合内容", listContent));

        void Rebuild()
        {
            header.Text = $"组合包含 {working.Count} 个动作";
            childList.Children.Clear();
            if (working.Count == 0)
            {
                childList.Children.Add(new TextBlock { Text = "（暂无动作，点击下方“＋ 添加动作”）", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 2) });
                return;
            }
            for (int i = 0; i < working.Count; i++)
            {
                int idx = i; var item = working[idx];
                var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var tb = new TextBlock { Text = $"{idx + 1}. {item}", Foreground = (Brush)FindResource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(tb, 0); g.Children.Add(tb);
                var ops = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(ops, 1);
                var edit = new Button { Content = "编辑", Width = 48, Height = 26, Margin = new Thickness(4, 0, 0, 0), FontSize = 12 };
                edit.Click += (_, _) =>
                {
                    var s = item.IsGroup ? ShowEditGroupDialog(item) : ShowAddActionDialog(item);   // 子项可为嵌套组合
                    if (s != null && SerializeStep(s) != SerializeStep(item)) { working[idx] = s; Rebuild(); }
                };
                var up = new Button { Content = "↑", Width = 30, Height = 26, Margin = new Thickness(4, 0, 0, 0), FontSize = 12, IsEnabled = idx > 0 };
                up.Click += (_, _) => { if (idx > 0) { working.Move(idx, idx - 1); Rebuild(); } };
                var down = new Button { Content = "↓", Width = 30, Height = 26, Margin = new Thickness(4, 0, 0, 0), FontSize = 12, IsEnabled = idx < working.Count - 1 };
                down.Click += (_, _) => { if (idx < working.Count - 1) { working.Move(idx, idx + 1); Rebuild(); } };
                var del = new Button { Content = "删除", Width = 48, Height = 26, Margin = new Thickness(4, 0, 0, 0), FontSize = 12 };
                del.Click += (_, _) => { working.RemoveAt(idx); Rebuild(); };
                ops.Children.Add(edit); ops.Children.Add(up); ops.Children.Add(down); ops.Children.Add(del);
                g.Children.Add(ops);
                childList.Children.Add(g);
            }
        }
        addBtn.Click += (_, _) => { var s = ShowAddActionDialog(); if (s != null) { working.Add(s); Rebuild(); } };
        Rebuild();

        // 执行次数+重复间隔：与动作对话框同一套 RepeatBlock，放进「组合内容」卡（即组合的基础设置）。
        var groupRepeat = new RepeatBlock(this, "执行次数（0 为无限）");
        groupRepeat.Load(source);
        listContent.Children.Add(groupRepeat.Panel);
        // 组合级运行条件与方案级/动作级完全一致：同一套控件、同一份回填与写回逻辑。
        var cond = BuildRunConditionEditor(source);
        var condPanel = cond.Panel;
        condPanel.Margin = new Thickness(0, 0, 0, 4);
        sp.Children.Add(GroupCard("运行条件", condPanel));   // 与动作对话框一致：运行条件独立成卡
        var groupStepRepeat = new RepeatBlock(this, "重复次数（0 为无限）", forRepeat: true, note: "每一趟都会【重新判定上面的运行条件、重新触发监听动作】，并各记一条日志。与上面各类动作里的「点击次数 / 按键次数 / 滚动次数 / 执行次数」不同——那个只是把动作本体多做几遍，条件只判一次、监听只走一遍。");
        var groupUntil = new UntilBlock(this, groupStepRepeat);
        groupUntil.Load(source);
        sp.Children.Add(GroupCard("重复", groupUntil.Panel));

        MacroStep? hookPreCond = source.PreCondAction, hookCondOk = source.CondSuccessAction, hookCondFail = source.CondFailAction,
                   hookPreRun = source.PreRunAction,
                   hookSuccess = source.SuccessAction, hookComplete = source.CompleteAction, hookFail = source.FailAction;
        var hookNote = new TextBlock { Text = "在动作生命周期的各节点追加执行一个完整动作（可含循环、运行条件、组合，并能继续挂自己的监听）。「条件」类监听仅在本动作设置了运行条件时触发。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        sp.Children.Add(GroupCard("事件监听",
            hookNote,
            BuildHookRow(win, "运行条件判断前", () => hookPreCond, v => hookPreCond = v),
            BuildHookRow(win, "判断成功后", () => hookCondOk, v => hookCondOk = v),
            BuildHookRow(win, "判断失败后", () => hookCondFail, v => hookCondFail = v),
            BuildHookRow(win, "运行前", () => hookPreRun, v => hookPreRun = v),
            BuildHookRow(win, "运行成功后", () => hookSuccess, v => hookSuccess = v),
            BuildHookRow(win, "运行失败后", () => hookFail, v => hookFail = v),
            BuildHookRow(win, "运行结束后", () => hookComplete, v => hookComplete = v)));

        var noteText = new TextBox { Text = source.Note, Margin = new Thickness(0, 0, 0, 14), Height = 32 };
        var aliasText = new TextBox { Text = source.Alias, Margin = new Thickness(0, 0, 0, 14), Height = 32 };
        sp.Children.Add(GroupCard("别名与备注（可选）",
            FieldLabel("别名（跳转用标签，方案内应唯一）"), aliasText,
            FieldLabel("备注"), noteText));

        var okBtn = new Button { Content = "确定", Width = 88, Height = 36, IsDefault = true, Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 10, 0) };
        var cancelBtn = new Button { Content = "取消", Width = 88, Height = 36, IsCancel = true, Style = (Style)FindResource("GhostButton"), Margin = new Thickness(0) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(okBtn); bar.Children.Add(cancelBtn);
        var footer = new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 14, 0), Child = bar };
        Grid.SetRow(footer, 1); grid.Children.Add(footer);

        MacroStep? result = null;
        okBtn.Click += (_, _) =>
        {
            result = new MacroStep
            {
                Type = "Group", Children = new ObservableCollection<MacroStep>(working),
                PreCondAction = hookPreCond, CondSuccessAction = hookCondOk, CondFailAction = hookCondFail,
                PreRunAction = hookPreRun,
                SuccessAction = hookSuccess, CompleteAction = hookComplete, FailAction = hookFail,
                Note = noteText.Text.Trim(),
                Alias = aliasText.Text.Trim(),
            };
            try { groupRepeat.Apply(result); groupUntil.Apply(result); }
            catch (Exception ex) { ThemedDialog.Show(ex.Message, "编辑失败", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
            try { ApplyRunCondition(cond, result); }   // 与方案级/动作级同一份写回逻辑
            catch (Exception ex) { ThemedDialog.Show(ex.Message, "编辑失败", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
            win.DialogResult = true;
        };

        win.Content = grid;
        return win.ShowDialog() == true ? result : null;
    }

    // ---------- 按键 / 按钮 / 时间 辅助 ----------
    private readonly record struct CapturedKey(string Key, byte Modifier, string Display);

    private static CapturedKey ConvertWpfKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
        byte mod = GetModifierFromKey(key);
        if (mod != 0)
        {
            // 按下的本身是修饰键：合并当前按住的全部修饰键一起显示——
            // 按住 Ctrl 再按 Alt 应显示 Ctrl+Alt（而不是只显示 Alt），继续按 A 才落成 Ctrl+Alt+A。
            mod |= GetCurrentModifierState();
            return new CapturedKey("", mod, FormatCapturedKey("", mod));
        }
        mod = GetCurrentModifierState();
        string k = KeyToHidName(key);
        // 收敛到 KeyMap：两张键码表都没有的键，捕获阶段就拒绝，避免录进去、运行时才静默失效。
        if (k.Length > 0 && !KeyMap.Hid.ContainsKey(k) && !KeyMap.Vk.ContainsKey(k))
            throw new InvalidOperationException($"暂不支持该按键：{key}");
        return new CapturedKey(k, mod, FormatCapturedKey(k, mod));
    }

    private static byte GetModifierFromKey(Key key) => key switch
    {
        Key.LeftCtrl => 1, Key.LeftShift => 2, Key.LeftAlt => 4, Key.LWin => 8,
        Key.RightCtrl => 16, Key.RightShift => 32, Key.RightAlt => 64, Key.RWin => 128, _ => 0,
    };

    private static byte GetCurrentModifierState()
    {
        byte b = 0;
        if (Keyboard.IsKeyDown(Key.LeftCtrl)) b |= 1;
        if (Keyboard.IsKeyDown(Key.LeftShift)) b |= 2;
        if (Keyboard.IsKeyDown(Key.LeftAlt)) b |= 4;
        if (Keyboard.IsKeyDown(Key.LWin)) b |= 8;
        if (Keyboard.IsKeyDown(Key.RightCtrl)) b |= 0x10;
        if (Keyboard.IsKeyDown(Key.RightShift)) b |= 0x20;
        if (Keyboard.IsKeyDown(Key.RightAlt)) b |= 0x40;
        if (Keyboard.IsKeyDown(Key.RWin)) b |= 0x80;
        return b;
    }

    private static string KeyToHidName(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return key.ToString().ToUpperInvariant();
        if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return $"NUM{(int)(key - Key.NumPad0)}";
        if (key >= Key.F1 && key <= Key.F12) return key.ToString().ToUpperInvariant();
        return key switch
        {
            Key.Return => "ENTER", Key.Escape => "ESC", Key.Space => "SPACE", Key.Tab => "TAB",
            Key.Back => "BACKSPACE", Key.Delete => "DELETE", Key.Insert => "INSERT", Key.Home => "HOME", Key.End => "END",
            Key.Prior => "PAGEUP", Key.Next => "PAGEDOWN", Key.Left => "LEFT", Key.Right => "RIGHT", Key.Up => "UP", Key.Down => "DOWN",
            Key.Snapshot => "PRINTSCREEN", Key.Scroll => "SCROLLLOCK", Key.Pause => "PAUSE", Key.Capital => "CAPSLOCK", Key.NumLock => "NUMLOCK",
            Key.Add => "NUM+", Key.Subtract => "NUM-", Key.Multiply => "NUM*", Key.Divide => "NUM/", Key.Decimal => "NUM.",
            Key.OemMinus => "-", Key.OemPlus => "=", Key.Oem4 => "[", Key.Oem6 => "]", Key.Oem5 => "\\",
            Key.Oem1 => ";", Key.Oem7 => "'", Key.Oem3 => "`", Key.OemComma => ",", Key.OemPeriod => ".", Key.Oem2 => "/",
            _ => throw new InvalidOperationException($"暂不支持该按键：{key}"),
        };
    }

    private static string FormatCapturedKey(string key, byte modifier)
    {
        var parts = new List<string>();
        if ((modifier & 0x01) != 0) parts.Add("左Ctrl");
        if ((modifier & 0x02) != 0) parts.Add("左Shift");
        if ((modifier & 0x04) != 0) parts.Add("左Alt");
        if ((modifier & 0x08) != 0) parts.Add("左Win");
        if ((modifier & 0x10) != 0) parts.Add("右Ctrl");
        if ((modifier & 0x20) != 0) parts.Add("右Shift");
        if ((modifier & 0x40) != 0) parts.Add("右Alt");
        if ((modifier & 0x80) != 0) parts.Add("右Win");
        if (!string.IsNullOrEmpty(key)) parts.Add(key);
        return parts.Count == 0 ? "（未捕获）" : string.Join(" + ", parts);
    }

    private static string ButtonToInternal(string text) => text switch { "左键" => "Left", "右键" => "Right", "中键" => "Middle", _ => text };
    private static string TranslateButtonToDisplay(string button) => button switch { "Left" => "左键", "Right" => "右键", "Middle" => "中键", _ => "左键" };

    // 时间输入行：数值 + 单位(毫秒/秒/分钟/小时) + "设为默认"。
    /// <summary>
    /// 坐标块：显示器 + 屏内百分比 + 点选/预览。可复用——点击/移动各一个，拖动用两个（起点、终点）。
    /// showCheck=true 时带勾选框（勾选后才展开明细），false 则常驻展开。
    /// </summary>
    // 限制区域的一个「尖角」边输入格（对齐自动精灵）：无圆角、与相邻格贴合（-1px 让边框共享）；
    // 标签(左/右/上/下)在空且未聚焦时作占位居中，聚焦或有值时缩小浮到上边框（Material 描边式，在边框上开缺口）。
    // 右侧单位可点击在 % / DP(屏内像素) 间切换并自动换算。值对外统一以「屏内像素」读写。
    //
    // 【浮起标签是画在格子外面的】它靠负 margin 顶到上边框之上，所以【上方必须有净空】：
    // 本格上面那一行（显示器行）留了 22px 下边距，够浮起的 8px + 标签高度；同时对话框最小宽度必须
    // 保证这一行四个格子排得下（见 MakeDialog 的 MinWidth），否则挤压变形时标签会和上一行糊在一起。
    private sealed class EdgeCell
    {
        private const double Float = 8;      // 浮起高度（负 margin）
        private readonly Brush _notchBg;
        private readonly TextBox _box;
        private readonly TextBlock _lbl, _unit;
        private readonly Border _lblBg;
        private readonly Func<int> _dim;     // 该边换算用的屏尺寸（左右=宽，上下=高）
        private bool _isDp;
        public readonly Border Root;
        public event Action? Committed;      // 失焦提交（→ 面板 EdgesToRegion）

        public EdgeCell(MainWindow o, string label, Func<int> dim, bool first, string notchBgKey = "Bg")
        {
            _dim = dim; _notchBg = (Brush)o.FindResource(notchBgKey);
            Brush B(string k) => (Brush)o.FindResource(k);
            _box = new TextBox
            {
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
                MinHeight = 0, Width = 40, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Left, Foreground = B("Ink"),
            };
            _unit = new TextBlock { Text = "%", Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0), ToolTip = "点击切换 % / DP（屏内像素）" };
            _lbl = new TextBlock { Text = label, Foreground = B("Muted") };
            _lblBg = new Border { Child = _lbl, HorizontalAlignment = HorizontalAlignment.Left };

            var grid = new Grid { Height = 42 };
            grid.Children.Add(_box); grid.Children.Add(_unit); grid.Children.Add(_lblBg);
            Root = new Border { BorderBrush = B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(0), Margin = new Thickness(first ? 0 : -1, 0, 0, 0), MinWidth = 86, Child = grid };

            _box.GotFocus += (_, _) => Render();
            _box.LostFocus += (_, _) => { Render(); Committed?.Invoke(); };
            _box.TextChanged += (_, _) => Render();
            _unit.MouseLeftButtonDown += (_, _) => { var px = GetPx(); _isDp = !_isDp; _unit.Text = _isDp ? "DP" : "%"; if (px is int p) SetPx(p); };
            Render();
        }

        private void Render()
        {
            bool active = _box.IsFocused || !string.IsNullOrEmpty(_box.Text);
            _lbl.FontSize = active ? 11 : 14;
            _lblBg.VerticalAlignment = active ? VerticalAlignment.Top : VerticalAlignment.Center;
            _lblBg.Margin = active ? new Thickness(6, -Float, 0, 0) : new Thickness(10, 0, 0, 0);
            _lblBg.Padding = active ? new Thickness(3, 0, 3, 0) : new Thickness(0);
            _lblBg.Background = active ? _notchBg : Brushes.Transparent;   // 浮起时遮住上边框做缺口（底色跟宿主容器一致）
        }

        public int? GetPx()
        {
            var t = _box.Text?.Trim();
            if (string.IsNullOrEmpty(t) || !double.TryParse(t, out var v)) return null;
            return _isDp ? (int)Math.Round(v) : (int)Math.Round(Math.Clamp(v, 0, 100) / 100.0 * Math.Max(1, _dim()));
        }
        public void SetPx(int px)
        {
            _box.Text = _isDp ? px.ToString() : (Math.Clamp(px / (double)Math.Max(1, _dim()), 0, 1) * 100).ToString("0.#");
            Render();
        }
        public void Clear() { _box.Text = ""; Render(); }
    }

    // 「点击图片」编辑块：目标图（截图 / 导入 / 复制 / 粘贴）+ 限制区域 + 相似度 + 匹配第几。
    private sealed class ClickImagePanel
    {
        public byte[]? Png;
        public string Monitor = "";
        public int RelX, RelY, W, H;                 // 限制区域屏内相对像素（W/H=0 表示全屏）
        // 截图那一刻的区域，供「还原到截图区域」；W/H=0 表示这张图不是截来的（导入/粘贴），没有可还原的原始区域
        public string OrigMonitor = "";
        public int OrigX, OrigY, OrigW, OrigH;
        private readonly System.Windows.Controls.Image _thumb = new() { MaxWidth = 220, MaxHeight = 150, Stretch = System.Windows.Media.Stretch.Uniform };
        private readonly Border _thumbBorder;
        private readonly TextBlock _imgStatus, _regionHint;
        // 锚定屏下拉：手动调参（填四边）时指定基准屏；截图/编辑区域自动识别后自动跟随。
        private readonly ComboBox _monCombo = new() { Height = 32, VerticalAlignment = VerticalAlignment.Center };
        // 限制区域四边（尖角浮标输入格，值内部统一为屏内像素）：左/右/上/下。构造时 new（需 dim 委托）。
        private EdgeCell _left = null!, _right = null!, _top = null!, _bottom = null!;
        // 相似度阈值：复用运行条件那套百分比文本框（越高越严格），默认 90。
        private readonly TextBox _thrText = new() { Width = 68, Height = 32, Text = "90" };
        private readonly TextBox _index = new() { Text = "1", Width = 68, Height = 32 };
        private readonly Button _previewBtn;
        private readonly Button _restoreBtn;
        private bool _syncing;   // 防"区域→四边框→区域"回填递归
        public readonly Border Panel;

        public double Threshold => Math.Clamp(ParseInt(_thrText.Text, 90), 10, 100) / 100.0;
        public int Index => Math.Max(1, ParseInt(_index.Text, 1));
        public bool HasImage => Png != null && Png.Length > 0;

        public ClickImagePanel(MainWindow o, Window? win = null, bool withIndex = true, bool boxed = true, string notchBgKey = "Bg")
        {
            // 宿主窗口懒解析：运行条件编辑器在对话框组装前就要构建本面板，点击时再从可视树取。
            Window Win() => win ?? Window.GetWindow(Panel)!;
            var inner = new StackPanel();

            // 动作按钮统一：图标 + 悬停中文 tooltip（IconButton 样式），不用文字按钮。
            Button MkIcon(string glyph, string tip) => new() { Style = (Style)o.FindResource("IconButton"), FontSize = 16, Content = glyph, ToolTip = tip, Margin = new Thickness(0, 0, 4, 0) };

            // —— 图片（截图 / 导入 / 复制 / 粘贴，对齐自动精灵图标行）——
            var imgHeader = new DockPanel { LastChildFill = false };
            imgHeader.Children.Add(new TextBlock { Text = "图片", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Width = 64 });
            var shotBtn = MkIcon("", "截图：框选屏幕截取目标图（并自动把限制区域设为截图位置）");   // 剪刀，与系统截图工具同款
            var importBtn = MkIcon("", "导入：从本地图片文件导入");
            var copyBtn = MkIcon("", "复制：把当前目标图复制到剪贴板");
            var pasteBtn = MkIcon("", "粘贴：从剪贴板粘贴图片");
            var imgBtns = new StackPanel { Orientation = Orientation.Horizontal };
            imgBtns.Children.Add(shotBtn); imgBtns.Children.Add(importBtn); imgBtns.Children.Add(copyBtn); imgBtns.Children.Add(pasteBtn);
            imgHeader.Children.Add(imgBtns);
            inner.Children.Add(imgHeader);
            _imgStatus = new TextBlock { Foreground = (Brush)o.FindResource("Muted"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Text = "未设置图片" };
            inner.Children.Add(_imgStatus);
            _thumbBorder = new Border { BorderBrush = (Brush)o.FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Child = _thumb, Visibility = Visibility.Collapsed };
            inner.Children.Add(_thumbBorder);

            // —— 限制区域（风格与坐标块一致：标题+操作图标行 → 强调左条缩进块内：显示器行 / 区域值行 / 提示）——
            var editBtn = MkIcon("", "编辑区域：在屏幕上拖动·缩放调整搜索范围");
            _restoreBtn = MkIcon("", "还原到截图时的区域（手动调过限制区域后可一键退回）");   // 与截图区域重新对齐
            var restoreBtn = _restoreBtn;
            _previewBtn = MkIcon("", "预览：在屏幕上白框回显当前区域");
            var clearBtn = MkIcon("", "清除限制区域（改为搜索整块主屏）");
            var idBtn = MkIcon("", "标识屏幕（在各屏显示编号，帮你分清下拉对应哪块屏）");
            idBtn.Click += (_, _) => o.ShowIdScreens(Win());
            var regionHeader = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 16, 0, 0) };
            var regionTitle = new TextBlock { Text = "限制区域（只在此范围内搜索）", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(regionTitle, Dock.Left); regionHeader.Children.Add(regionTitle);
            var regionBtns = new StackPanel { Orientation = Orientation.Horizontal };
            regionBtns.Children.Add(editBtn); regionBtns.Children.Add(restoreBtn); regionBtns.Children.Add(_previewBtn); regionBtns.Children.Add(clearBtn); regionBtns.Children.Add(idBtn);
            DockPanel.SetDock(regionBtns, Dock.Right); regionHeader.Children.Add(regionBtns);
            inner.Children.Add(regionHeader);

            var regionDetail = new StackPanel();
            TextBlock RLabel(string t2) => new() { Text = t2, Width = 52, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            // 显示器行（与坐标块同款）：锚定屏——手动填四边时的基准；截图/编辑区域自动识别后自动跟随。
            var monRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 22) };   // 22 = 给下一行浮起的标签留净空
            monRow.Children.Add(RLabel("显示器"));
            foreach (var m in ScreenInfo.All()) _monCombo.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Device });
            monRow.Children.Add(_monCombo);
            regionDetail.Children.Add(monRow);
            _monCombo.SelectionChanged += (_, _) =>
            {
                if (_syncing) return;
                // 手动换屏：四边百分比不变，按新屏尺寸重算区域像素（DP 模式则保持像素值）。
                Monitor = (_monCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                EdgesToRegion();
                SyncEdges();
            };
            // 区域值行：四个尖角浮标格（贴合一体）。
            _left = new EdgeCell(o, "左", () => RegionMon().Width, true, notchBgKey);
            _right = new EdgeCell(o, "右", () => RegionMon().Width, false, notchBgKey);
            _top = new EdgeCell(o, "上", () => RegionMon().Height, false, notchBgKey);
            _bottom = new EdgeCell(o, "下", () => RegionMon().Height, false, notchBgKey);
            foreach (var c in new[] { _left, _right, _top, _bottom }) c.Committed += EdgesToRegion;
            var cellsRow = new DockPanel { LastChildFill = true };
            var rvLabel = RLabel("区域值"); DockPanel.SetDock(rvLabel, Dock.Left); cellsRow.Children.Add(rvLabel);
            // WrapPanel：宽度不够就换行，而不是把最后一格挤出可视区
            var cells = new WrapPanel { Orientation = Orientation.Horizontal };
            cells.Children.Add(_left.Root); cells.Children.Add(_right.Root); cells.Children.Add(_top.Root); cells.Children.Add(_bottom.Root);
            cellsRow.Children.Add(cells);
            regionDetail.Children.Add(cellsRow);
            _regionHint = new TextBlock { Foreground = (Brush)o.FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            regionDetail.Children.Add(_regionHint);
            // 强调左条缩进（与坐标块 _wrap 同款视觉）。
            inner.Children.Add(new Border
            {
                BorderBrush = (Brush)o.FindResource("Accent"), BorderThickness = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(0, 6, 6, 0), Padding = new Thickness(12, 10, 0, 2),
                Margin = new Thickness(2, 10, 0, 0), Child = regionDetail,
            });
            clearBtn.Click += (_, _) => { Monitor = ""; RelX = RelY = W = H = 0; Refresh(); SyncEdges(); };
            restoreBtn.Click += (_, _) =>
            {
                if (OrigW <= 0 || OrigH <= 0) return;
                Monitor = OrigMonitor; RelX = OrigX; RelY = OrigY; W = OrigW; H = OrigH;
                Refresh(); SyncEdges();
            };
            copyBtn.Click += (_, _) =>
            {
                if (!HasImage) return;
                try
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    using var ms = new System.IO.MemoryStream(Png!);
                    bi.BeginInit(); bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.StreamSource = ms; bi.EndInit();
                    System.Windows.Clipboard.SetImage(bi);
                }
                catch { }
            };

            // —— 相似度阈值（复用运行条件风格：百分比文本框）——
            var thrRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            thrRow.Children.Add(new TextBlock { Text = "相似度阈值(%)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            NumericBox(_thrText, 10, 100, 90);   // 相似度是百分比，填不进 100 以上
            thrRow.Children.Add(_thrText);
            thrRow.Children.Add(new TextBlock { Text = "（越高越严格）", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)o.FindResource("Muted"), FontSize = 12, Margin = new Thickness(8, 0, 0, 0) });
            inner.Children.Add(thrRow);

            // —— 匹配第几（标签 + 输入框同一行）——
            var idxRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            idxRow.Children.Add(new TextBlock { Text = "匹配第几个", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            NumericBox(_index, 1, 999, 1);
            idxRow.Children.Add(_index);
            if (withIndex)
            {
                inner.Children.Add(idxRow);
                inner.Children.Add(new TextBlock { Text = "区域内命中多个时点击第几个（按从上到下、从左到右排序；1 起）。", Foreground = (Brush)o.FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            }

            Panel = boxed ? o.SubGroup(null, inner) : new Border { Child = inner };

            // 四边格提交（失焦）→ 回写区域，已在上面 c.Committed += EdgesToRegion 里挂好。

            shotBtn.Click += (_, _) =>
            {
                var r = o.CaptureTargetImage(Win());
                if (r is { } c)
                {
                    var (dev, _, _) = ScreenInfo.FromPoint(c.vx, c.vy);
                    var mon = ScreenInfo.ByDevice(dev);
                    Png = c.png; Monitor = dev; RelX = c.vx - mon.Left; RelY = c.vy - mon.Top; W = c.w; H = c.h;   // 截图自动填充限制区域
                    OrigMonitor = Monitor; OrigX = RelX; OrigY = RelY; OrigW = W; OrigH = H;                        // 记下原始区域供一键还原
                    Refresh(); SyncEdges();
                }
            };
            importBtn.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*", Title = "导入目标图片" };
                if (dlg.ShowDialog(Win()) == true)
                {
                    try { using var bmp = new System.Drawing.Bitmap(dlg.FileName); Png = Services.ScreenMatch.ToPng(bmp); Refresh(); }
                    catch (Exception ex) { ThemedDialog.Show("无法读取该图片：" + ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Exclamation); }
                }
            };
            pasteBtn.Click += (_, _) =>
            {
                var png = ClipboardPng();
                if (png == null) { ThemedDialog.Show("剪贴板里没有图片。", "粘贴", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                Png = png; Refresh();
            };
            editBtn.Click += (_, _) =>
            {
                int? cvx = null, cvy = null, cw = null, ch = null;
                if (W > 0 && H > 0) { var m = ScreenInfo.ByDevice(Monitor); cvx = m.Left + RelX; cvy = m.Top + RelY; cw = W; ch = H; }
                var r = o.EditRegion(Win(), cvx, cvy, cw, ch);
                if (r is { } g)
                {
                    var (dev, _, _) = ScreenInfo.FromPoint(g.vx, g.vy);
                    var mon = ScreenInfo.ByDevice(dev);
                    Monitor = dev; RelX = g.vx - mon.Left; RelY = g.vy - mon.Top; W = g.w; H = g.h;
                    Refresh(); SyncEdges();
                }
            };
            _previewBtn.Click += (_, _) =>
            {
                if (W > 0 && H > 0) { var m = ScreenInfo.ByDevice(Monitor); o.PreviewRegion(m.Left + RelX, m.Top + RelY, W, H, Win()); }
            };
            Refresh(); SyncEdges();   // SyncEdges 初始化锚定屏下拉（默认主屏）
        }

        private static byte[]? ClipboardPng()
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsImage()) return null;
                var src = System.Windows.Clipboard.GetImage();
                if (src == null) return null;
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
                using var ms = new System.IO.MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }

        private void Refresh()
        {
            if (HasImage)
            {
                _imgStatus.Text = "已设置图片";
                try
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    using var ms = new System.IO.MemoryStream(Png!);
                    bi.BeginInit(); bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.StreamSource = ms; bi.EndInit();
                    _thumb.Source = bi; _thumbBorder.Visibility = Visibility.Visible;
                }
                catch { _thumbBorder.Visibility = Visibility.Collapsed; }
            }
            else { _imgStatus.Text = "未设置图片"; _thumbBorder.Visibility = Visibility.Collapsed; }

            bool hasRegion = W > 0 && H > 0;
            // 注意：不在 Refresh 里回写四边格——否则用户填一个格失焦(区域仍不完整)时会把刚输入的清掉。
            // 只有区域被【程序化】改动(截图/编辑/清除/回填)时才 SyncEdges。
            _regionHint.Text = hasRegion
                ? $"屏幕 {ScreenInfo.ByDevice(Monitor).Label}：{W}×{H}px（四边可切 % / DP，直接改）"
                : "未限制（搜索整块主屏）。可手动填四边，或用截图 / 编辑区域设定。";
            _previewBtn.IsEnabled = hasRegion;
            // 没有截图过（导入/粘贴的图）就没有"原始区域"可还原；当前就等于原始区域时按钮变灰
            bool hasOrig = OrigW > 0 && OrigH > 0;
            _restoreBtn.Visibility = hasOrig ? Visibility.Visible : Visibility.Collapsed;
            _restoreBtn.IsEnabled = hasOrig && !(Monitor == OrigMonitor && RelX == OrigX && RelY == OrigY && W == OrigW && H == OrigH);
        }

        private ScreenInfo.Monitor RegionMon() => ScreenInfo.ByDevice(string.IsNullOrEmpty(Monitor) ? ScreenInfo.Primary().Device : Monitor);

        // 区域 → 四边格（各格按自身 %/DP 模式渲染屏内像素）+ 锚定屏下拉跟随。空区域→清空（露出占位标签）。
        // 仅在区域被程序化改动时调用（截图/编辑/清除/回填/换屏），别在 Refresh 里无脑调（会清掉用户半途输入）。
        private void SyncEdges()
        {
            _syncing = true;
            var dev = RegionMon().Device;
            foreach (var it in _monCombo.Items)
                if (it is ComboBoxItem c && c.Tag is string d && string.Equals(d, dev, StringComparison.OrdinalIgnoreCase)) { _monCombo.SelectedItem = it; break; }
            if (W > 0 && H > 0)
            {
                _left.SetPx(RelX); _right.SetPx(RelX + W); _top.SetPx(RelY); _bottom.SetPx(RelY + H);
            }
            else { _left.Clear(); _right.Clear(); _top.Clear(); _bottom.Clear(); }
            _syncing = false;
        }

        // 四边格 → 区域（失焦提交）：各格取屏内像素；四个都有值且右>左、下>上 才更新。手填且未绑屏时默认绑主屏。
        private void EdgesToRegion()
        {
            if (_syncing) return;
            if (_left.GetPx() is int lx && _right.GetPx() is int rx && _top.GetPx() is int tx && _bottom.GetPx() is int bx
                && rx - lx >= 4 && bx - tx >= 4)
            {
                Monitor = RegionMon().Device; RelX = lx; RelY = tx; W = rx - lx; H = bx - tx;
            }
            Refresh();
        }

        public void Load(MacroStep s)
        {
            Png = ImageStore.Bytes(s.ClickImage);
            Monitor = s.ClickImageMonitor; RelX = s.ClickImageRectX; RelY = s.ClickImageRectY; W = s.ClickImageRectW; H = s.ClickImageRectH;
            OrigMonitor = s.ClickImageOrigMonitor; OrigX = s.ClickImageOrigRectX; OrigY = s.ClickImageOrigRectY; OrigW = s.ClickImageOrigRectW; OrigH = s.ClickImageOrigRectH;
            _thrText.Text = ((int)Math.Round(Math.Clamp(s.ClickImageThreshold, 0.1, 1.0) * 100)).ToString();
            _index.Text = Math.Max(1, s.ClickImageIndex).ToString();
            Refresh(); SyncEdges();
        }

        // 运行条件「图片出现」共用本编辑器：读写 IRunCondition 的 RunConditionXxx 字段（与 MacroStep 的 ClickImage* 平行）。
        public void LoadCond(IConditionData src)
        {
            Png = ImageStore.Bytes(src.Image);
            Monitor = src.Monitor; RelX = src.RectX; RelY = src.RectY; W = src.RectW; H = src.RectH;
            OrigMonitor = src.OrigMonitor; OrigX = src.OrigRectX; OrigY = src.OrigRectY; OrigW = src.OrigRectW; OrigH = src.OrigRectH;
            _thrText.Text = ((int)Math.Round(Math.Clamp(src.Threshold > 0 ? src.Threshold : 0.9, 0.1, 1.0) * 100)).ToString();
            _index.Text = "1";
            Refresh(); SyncEdges();
        }

        public void ApplyCond(IConditionData dst)
        {
            if (!HasImage) throw new InvalidOperationException("请先设置目标图片（截图 / 导入 / 粘贴）。");
            dst.Image = ImageStore.Ref(Png!);   // 立即外置成 file:hash 引用
            dst.Monitor = Monitor; dst.RectX = RelX; dst.RectY = RelY;
            dst.RectW = W; dst.RectH = H;
            dst.Threshold = Threshold;
            dst.OrigMonitor = OrigMonitor; dst.OrigRectX = OrigX; dst.OrigRectY = OrigY; dst.OrigRectW = OrigW; dst.OrigRectH = OrigH;
        }

        public void Apply(MacroStep s)
        {
            if (!HasImage) throw new InvalidOperationException("请先设置目标图片（截图 / 导入 / 粘贴）。");
            s.ClickImage = ImageStore.Ref(Png!);
            s.ClickImageMonitor = Monitor; s.ClickImageRectX = RelX; s.ClickImageRectY = RelY; s.ClickImageRectW = W; s.ClickImageRectH = H;
            s.ClickImageThreshold = Threshold; s.ClickImageIndex = Index;
            s.ClickImageOrigMonitor = OrigMonitor; s.ClickImageOrigRectX = OrigX; s.ClickImageOrigRectY = OrigY;
            s.ClickImageOrigRectW = OrigW; s.ClickImageOrigRectH = OrigH;
        }
    }

    private sealed class CoordBlock
    {
        private readonly MainWindow _o;
        private readonly Window _win;
        private readonly ComboBox _monitor = new() { Height = 32 };
        private readonly TextBox _x = new() { Text = "50", Width = 86, Height = 32 };
        private readonly TextBox _y = new() { Text = "50", Width = 86, Height = 32 };
        private readonly TextBox _offset = new() { Text = "0", Width = 90, Height = 32 };
        private readonly TextBlock _status;
        private readonly Border _wrap;
        public readonly CheckBox Enabled = new() { VerticalAlignment = VerticalAlignment.Center };
        private TextBlock? _titleText;   // showCheck=false 时标题是普通文本（此时 Enabled 根本不在可视树里）
        private StackPanel? _detail;     // 卡片内容区，供调用方插入附加行

        /// <summary>往卡片内容区顶部插一行（紧跟标题行）。拖动窗口的"对齐点"就是这样与终点坐标合成一张卡的。</summary>
        public void InsertTopRow(UIElement row) => _detail?.Children.Insert(1, row);

        /// <summary>改标题：勾选框式与纯文本式都要覆盖到，否则 showCheck=false 的块改了没反应。</summary>
        public void SetTitle(string t) { Enabled.Content = t; if (_titleText != null) _titleText.Text = t; }
        public readonly Border Panel;

        public CoordBlock(MainWindow o, Window win, string title, bool showCheck, bool withOffset = false)
        {
            _o = o; _win = win;
            var detail = new StackPanel();
            _detail = detail;

            // 标题行：勾选框（或纯标题）+ 标识屏幕按钮
            var header = new DockPanel { LastChildFill = false };
            var idBtn = new Button { Style = (Style)o.FindResource("IconButton"), FontSize = 16, Content = "\uE7F4", ToolTip = "标识屏幕（在各屏显示编号）" };
            DockPanel.SetDock(idBtn, Dock.Right); header.Children.Add(idBtn);
            idBtn.Click += (_, _) => o.ShowIdScreens(win);
            if (showCheck)
            {
                Enabled.Content = title;
                DockPanel.SetDock(Enabled, Dock.Left); header.Children.Add(Enabled);
            }
            else
            {
                Enabled.IsChecked = true;
                var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                _titleText = t;
                DockPanel.SetDock(t, Dock.Left); header.Children.Add(t);
            }

            // 显示器行
            TextBlock Label(string t2) => new() { Text = t2, Width = 52, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var monRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 12) };
            monRow.Children.Add(Label("显示器"));
            monRow.Children.Add(_monitor);
            detail.Children.Add(monRow);

            // 坐标值行：横/纵 + 点选/预览
            StackPanel PctField(string cap, TextBox box)
            {
                var f = new StackPanel();
                f.Children.Add(new TextBlock { Text = cap, FontSize = 10, Foreground = (Brush)o.FindResource("Muted"), Margin = new Thickness(2, 0, 0, 2) });
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(box);
                row.Children.Add(new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)o.FindResource("Muted"), Margin = new Thickness(5, 0, 0, 0) });
                f.Children.Add(row);
                return f;
            }
            var pickBtn = new Button { Style = (Style)o.FindResource("IconButton"), FontSize = 16, Content = "\uE81D", ToolTip = "点选坐标（可跨屏，自动识别屏幕）" };
            var previewBtn = new Button { Style = (Style)o.FindResource("IconButton"), FontSize = 16, Content = "\uE7B3", ToolTip = "预览已选位置", Margin = new Thickness(2, 0, 0, 0) };
            var valRow = new DockPanel { LastChildFill = false };
            var valLabel = Label("坐标值");
            valLabel.VerticalAlignment = VerticalAlignment.Bottom; valLabel.Margin = new Thickness(0, 0, 0, 7);
            valRow.Children.Add(valLabel);
            valRow.Children.Add(PctField("横坐标", _x));
            var yField = PctField("纵坐标", _y); yField.Margin = new Thickness(12, 0, 0, 0);
            valRow.Children.Add(yField);
            var pickBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 0) };
            pickBtns.Children.Add(pickBtn); pickBtns.Children.Add(previewBtn);
            valRow.Children.Add(pickBtns);
            detail.Children.Add(valRow);

            // 落点偏移（可选）：随坐标一起收纳在本块内，而不是另起一张卡。
            if (withOffset)
            {
                var offRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
                var offLabel = Label("落点偏移");
                offLabel.VerticalAlignment = VerticalAlignment.Center;
                offRow.Children.Add(offLabel);
                var offInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                offInner.Children.Add(_offset);
                offInner.Children.Add(new TextBlock { Text = "像素", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)o.FindResource("Muted"), Margin = new Thickness(6, 0, 0, 0) });
                offRow.Children.Add(offInner);
                detail.Children.Add(offRow);
                detail.Children.Add(new TextBlock
                {
                    Foreground = (Brush)o.FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                    Text = "在目标点周围该半径的圆内随机落点，配合拟人化移动更像真人；0 = 每次精确命中同一像素。多次点击时每次各自随机。",
                });
            }

            _status = new TextBlock { Foreground = (Brush)o.FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            detail.Children.Add(_status);
            if (string.Equals(o._doc.Backend, "Serial", StringComparison.OrdinalIgnoreCase))
                detail.Children.Add(new TextBlock
                {
                    Foreground = (Brush)o.FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
                    Text = "CH9329 采用相对闭环移动到位：全程真实硬件、可跨屏（含副屏），远距离会多用几帧逼近目标。",
                });

            _wrap = new Border
            {
                BorderBrush = (Brush)o.FindResource("Accent"), BorderThickness = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(0, 6, 6, 0), Padding = new Thickness(12, 10, 0, 2),
                Margin = new Thickness(2, 10, 0, 0), Child = detail,
            };
            var inner = new StackPanel();
            inner.Children.Add(header);
            inner.Children.Add(_wrap);
            Panel = o.SubGroup(null, inner);

            if (showCheck)
            {
                void Refresh() => _wrap.Visibility = Enabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                Enabled.Checked += (_, _) => Refresh();
                Enabled.Unchecked += (_, _) => Refresh();
                Refresh();
            }

            FillMonitors();
            pickBtn.Click += (_, _) =>
            {
                var r = o.PickAnywhere(win);   // 跨屏点选：自动识别屏幕，回填时下拉自动切到落点所在屏
                if (r is { } picked)
                {
                    Write(picked.dev, picked.nx, picked.ny);
                    _status.Text = $"已选取：{ScreenInfo.ByDevice(picked.dev).Label}（{picked.nx * 100:0.#}%, {picked.ny * 100:0.#}%）";
                }
            };
            previewBtn.Click += (_, _) =>
            {
                var (dev, nx, ny) = Read();
                o.PreviewPositionOnMonitor(ScreenInfo.ByDevice(dev), nx, ny, win);
            };
        }

        private string Device => (_monitor.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        private void FillMonitors()
        {
            _monitor.Items.Clear();
            int primaryIdx = 0, i = 0;
            foreach (var m in ScreenInfo.All())
            {
                _monitor.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Device });
                if (m.Primary) primaryIdx = i;
                i++;
            }
            if (_monitor.Items.Count > 0) _monitor.SelectedIndex = primaryIdx;   // 默认选主屏
        }

        /// <summary>落点偏移半径（像素，≥0）。仅 withOffset 的块有意义，其余恒为 0。</summary>
        public int ReadOffset() => Math.Max(0, ParseInt(_offset.Text, 0));
        public void WriteOffset(int v) => _offset.Text = Math.Max(0, v).ToString();

        /// <summary>读取当前设置：(显示器设备名, 屏内归一化 X, Y)。</summary>
        public (string dev, double nx, double ny) Read() =>
            (Device,
             Math.Clamp(ParseDouble(_x.Text, 50) / 100.0, 0, 1),
             Math.Clamp(ParseDouble(_y.Text, 50) / 100.0, 0, 1));

        /// <summary>回填（找不到该显示器则落到第一块）。</summary>
        public void Write(string dev, double nx, double ny)
        {
            bool hit = false;
            foreach (var it in _monitor.Items)
                if (it is ComboBoxItem c && c.Tag is string d && string.Equals(d, dev, StringComparison.OrdinalIgnoreCase))
                { _monitor.SelectedItem = it; hit = true; break; }
            if (!hit && _monitor.Items.Count > 0) _monitor.SelectedIndex = 0;
            _x.Text = (nx * 100).ToString("0.#");
            _y.Text = (ny * 100).ToString("0.#");
        }
    }

    // 「次数 + 重复间隔」块：点击/滚动/按键共用同一套逻辑（间隔仅在次数 != 1 时显示；重复时必填校验）。
    /// <summary>
    /// 次数 + 间隔的可复用块。两种口径由 <c>forRepeat</c> 决定：
    ///   false = 【执行次数】写 LoopCount/LoopDelayMs —— 重复动作本体（条件判一次、监听走一遍）；
    ///   true  = 【重复次数】写 RepeatCount/RepeatDelayMs —— 整趟重复，每趟都重新判条件、重新走监听。
    /// </summary>
    private sealed class RepeatBlock
    {
        private readonly bool _forRepeat;
        public readonly Border Panel;
        public readonly TextBlock CountLabel;
        public readonly TextBox Count = new() { Text = "1", Height = 32 };
        private readonly TextBox _delayVal = new() { Width = 96, Height = 32, Text = "1", VerticalAlignment = VerticalAlignment.Center };
        private readonly ComboBox _delayUnit = new() { Width = 84, Height = 32, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _delayBlock;

        public RepeatBlock(MainWindow owner, string label, bool forRepeat = false, string? note = null)
        {
            _forRepeat = forRepeat;
            CountLabel = FieldLabel(label);
            foreach (var u in new[] { "毫秒", "秒", "分钟", "小时" }) _delayUnit.Items.Add(u);
            _delayUnit.SelectedIndex = 1;   // 默认 1 秒
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_delayVal); row.Children.Add(_delayUnit);
            _delayBlock = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
            _delayBlock.Children.Add(FieldLabel("重复间隔"));
            _delayBlock.Children.Add(row);
            var inner = new StackPanel();
            inner.Children.Add(CountLabel);
            inner.Children.Add(Count);
            if (note != null)
                inner.Children.Add(new TextBlock
                {
                    Text = note, Foreground = (Brush)owner.FindResource("Muted"), FontSize = 12,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                });
            inner.Children.Add(_delayBlock);
            Panel = owner.SubGroup(null, inner);
            Count.TextChanged += (_, _) => Refresh();
            Refresh();
        }

        private void Refresh()
        {
            _delayBlock.Visibility = ParseInt(Count.Text, 1) == 1 ? Visibility.Collapsed : Visibility.Visible;
            if (_forRepeat && _delayBlock.Children.Count > 0 && _delayBlock.Children[0] is TextBlock lb) lb.Text = "每趟之间的间隔";
        }
        private double UnitFactor => _delayUnit.SelectedIndex switch { 0 => 1, 2 => 60000, 3 => 3600000, _ => 1000 };

        private int DelayMs()
        {
            if (string.IsNullOrWhiteSpace(_delayVal.Text)) throw new InvalidOperationException("请填写重复间隔。");
            if (!double.TryParse(_delayVal.Text.Trim(), out var v) || v < 0) throw new InvalidOperationException("重复间隔需为不小于 0 的数字。");
            return (int)Math.Round(v * UnitFactor);
        }

        public void Apply(MacroStep r)
        {
            int n = Math.Max(0, ParseInt(Count.Text, 1));
            if (_forRepeat)
            {
                r.RepeatCount = n;
                if (n != 1) { r.RepeatDelayMs = DelayMs(); r.RepeatDelayUnit = Math.Max(0, _delayUnit.SelectedIndex); }
            }
            else
            {
                r.LoopCount = n;
                if (n != 1) { r.LoopDelayMs = DelayMs(); r.LoopDelayUnit = Math.Max(0, _delayUnit.SelectedIndex); }
            }
        }

        public void Load(MacroStep sSrc)
        {
            Count.Text = (_forRepeat ? sSrc.RepeatCount : sSrc.LoopCount).ToString();
            int u = Math.Clamp(_forRepeat ? sSrc.RepeatDelayUnit : sSrc.LoopDelayUnit, 0, 3);
            _delayUnit.SelectedIndex = u;
            double f = u switch { 0 => 1, 2 => 60000, 3 => 3600000, _ => 1000 };
            double v = (_forRepeat ? sSrc.RepeatDelayMs : sSrc.LoopDelayMs) / f;
            _delayVal.Text = v % 1.0 == 0 ? ((long)v).ToString() : v.ToString("0.###");
        }
    }

    /// <summary>
    /// 「重复」卡的整体块：重复方式（固定次数 / 直到条件满足）二选一 + 对应面板。
    /// 直到模式＝do-while：每趟照常判运行条件、走监听、执行本体，趟末判定停止条件，满足即结束；
    /// 每趟间隔复用 RepeatDelayMs，趟数/时长上限 0=不限（同时生效、先到者停）。
    /// </summary>
    private sealed class UntilBlock
    {
        private readonly RepeatBlock _fixed;
        public readonly StackPanel Panel = new();
        public readonly ComboBox Mode = new() { Height = 32, Width = 170 };
        private readonly Border _untilBorder;
        private readonly System.Collections.Generic.List<ConditionItem> _items = new();
        private readonly ComboBox _logic = new();
        private readonly Action _refreshItems;
        private readonly TextBox _intervalVal = new() { Width = 84, Height = 32, Text = "1" };
        private readonly ComboBox _intervalUnit = new();
        private readonly TextBox _maxCount = new() { Width = 74, Height = 32, Text = "0" };
        private readonly TextBox _timeoutVal = new() { Width = 84, Height = 32, Text = "0" };
        private readonly ComboBox _timeoutUnit = new();

        public UntilBlock(MainWindow owner, RepeatBlock fixedBlock)
        {
            _fixed = fixedBlock;
            Mode.Items.Add("固定次数"); Mode.Items.Add("直到条件满足");
            Mode.SelectedIndex = 0;
            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            modeRow.Children.Add(new TextBlock { Text = "重复方式", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            modeRow.Children.Add(Mode);

            ComboBox UnitBox(ComboBox cb, int def)
            {
                foreach (var n in new[] { "毫秒", "秒", "分钟", "小时" }) cb.Items.Add(n);
                cb.SelectedIndex = def; cb.Width = 88; cb.Height = 32; cb.Margin = new Thickness(6, 0, 0, 0);
                return cb;
            }
            StackPanel Row(string label, params UIElement[] cells)
            {
                var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                r.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 60 });
                foreach (var c in cells) r.Children.Add(c);
                return r;
            }
            TextBlock Hint(string t) => new()
            {
                Text = t, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)owner.FindResource("Muted"), Margin = new Thickness(6, 0, 0, 0),
            };
            foreach (var tb in new[] { _intervalVal, _maxCount, _timeoutVal }) tb.VerticalAlignment = VerticalAlignment.Center;

            var inner = new StackPanel();
            inner.Children.Add(FieldLabel("停止条件（每趟执行完检查一次，满足即结束）"));
            inner.Children.Add(owner.BuildConditionListBlock(_items, _logic, out _refreshItems));
            inner.Children.Add(Row("每趟间隔", _intervalVal, UnitBox(_intervalUnit, 1), Hint("（0 = 立刻再来一趟）")));
            inner.Children.Add(Row("最多趟数", _maxCount, Hint("趟（0 = 不限）")));
            inner.Children.Add(Row("最多时长", _timeoutVal, UnitBox(_timeoutUnit, 1), Hint("（0 = 不限）")));
            inner.Children.Add(new TextBlock
            {
                Text = "每一趟都会照常判定运行条件、触发监听并执行动作，趟末检查上面的停止条件，满足即结束该动作。\n两个上限同时生效、先到者停；到上限仍未满足则结束重复并继续后续动作（不算执行失败）。",
                Foreground = (Brush)owner.FindResource("Muted"), FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            });
            _untilBorder = owner.SubGroup(null, inner);
            _untilBorder.Visibility = Visibility.Collapsed;

            Panel.Children.Add(modeRow);
            Panel.Children.Add(_fixed.Panel);
            Panel.Children.Add(_untilBorder);
            Mode.SelectionChanged += (_, _) => RefreshMode();
        }

        private void RefreshMode()
        {
            bool until = Mode.SelectedIndex == 1;
            _fixed.Panel.Visibility = until ? Visibility.Collapsed : Visibility.Visible;
            _untilBorder.Visibility = until ? Visibility.Visible : Visibility.Collapsed;
        }

        public void Load(MacroStep sSrc)
        {
            _fixed.Load(sSrc);
            Mode.SelectedIndex = sSrc.RepeatUntil ? 1 : 0;
            _items.Clear();
            foreach (var it in sSrc.UntilConditions) _items.Add(it.Clone());   // 副本：取消编辑不影响原对象
            _refreshItems();
            _logic.SelectedIndex = string.Equals(sSrc.UntilLogic, "Or", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            int u = Math.Clamp(sSrc.RepeatDelayUnit, 0, 3);
            _intervalUnit.SelectedIndex = u;
            _intervalVal.Text = FormatDelayValue(Math.Max(0, sSrc.RepeatDelayMs), u);
            _maxCount.Text = Math.Max(0, sSrc.UntilMaxCount).ToString();
            int tu = Math.Clamp(sSrc.UntilTimeoutUnit, 0, 3);
            _timeoutUnit.SelectedIndex = tu;
            _timeoutVal.Text = FormatDelayValue(Math.Max(0, sSrc.UntilTimeoutMs), tu);
            RefreshMode();
        }

        public void Apply(MacroStep r)
        {
            if (Mode.SelectedIndex == 1)
            {
                var valid = _items.FindAll(i => i.IsValid);
                if (valid.Count == 0) throw new InvalidOperationException("重复方式已选「直到条件满足」，请至少添加一条有效的停止条件。");
                r.RepeatUntil = true;
                r.UntilConditions = new();
                foreach (var it in valid) r.UntilConditions.Add(it.Clone());
                r.UntilLogic = (_logic.SelectedItem as ComboBoxItem)?.Tag as string ?? "And";
                int u = Math.Clamp(_intervalUnit.SelectedIndex, 0, 3);
                r.RepeatDelayMs = (int)Math.Round(Math.Max(0, ParseDouble(_intervalVal.Text, 1)) * LoopUnitFactor(u));
                r.RepeatDelayUnit = u;
                r.UntilMaxCount = Math.Max(0, ParseInt(_maxCount.Text, 0));
                int tu = Math.Clamp(_timeoutUnit.SelectedIndex, 0, 3);
                r.UntilTimeoutMs = (int)Math.Round(Math.Max(0, ParseDouble(_timeoutVal.Text, 0)) * LoopUnitFactor(tu));
                r.UntilTimeoutUnit = tu;
                r.RepeatCount = 1;   // 直到模式下固定趟数不参与，回写默认防旧残值
            }
            else
            {
                r.RepeatUntil = false;
                r.UntilConditions = new();
                _fixed.Apply(r);
            }
        }
    }

    private sealed class TimeInputRow
    {
        private static readonly (string Name, double Factor)[] Units = { ("毫秒", 1), ("秒", 1000), ("分钟", 60000), ("小时", 3600000) };
        private readonly TextBox _value;
        private readonly ComboBox _unit;
        private readonly CheckBox _setDefault;
        public Panel Panel { get; }
        public bool SetAsDefault => _setDefault.IsChecked == true;
        public int UnitIndex => Math.Max(0, _unit.SelectedIndex); // 用户当前所选单位下标

        public TimeInputRow(Window _, int initialMs)
        {
            _value = new TextBox { Width = 96, Height = 32, VerticalAlignment = VerticalAlignment.Center };
            _unit = new ComboBox { Width = 84, Height = 32, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (var u in Units) _unit.Items.Add(u.Name);
            // “设为默认”改为右缘图钉图标按钮，不再夹在单位旁抢占核心参数的阅读。
            _setDefault = new CheckBox
            {
                Style = (Style)System.Windows.Application.Current.FindResource("PinToggle"),
                ToolTip = "设为该类动作的默认时长（新建同类动作时自动带出）",
            };
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(_value); left.Children.Add(_unit);
            var dock = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 6, 0, 14) };
            DockPanel.SetDock(_setDefault, Dock.Right); dock.Children.Add(_setDefault);
            dock.Children.Add(left);
            Panel = dock;
            SetMs(initialMs);
        }

        public int GetMs()
        {
            double factor = Units[Math.Max(0, _unit.SelectedIndex)].Factor;
            double v = double.TryParse(_value.Text, out var r) ? r : 0;
            return (int)Math.Round(Math.Max(0, v) * factor);
        }

        // 自动进位选单位（用于新建/未记录单位时）
        public void SetMs(int ms)
        {
            int idx = 0;
            if (ms != 0)
            {
                if (ms % 3600000 == 0) idx = 3;
                else if (ms % 60000 == 0) idx = 2;
                else if (ms % 1000 == 0) idx = 1;
            }
            SetMs(ms, idx);
        }

        // 按指定单位还原（编辑时用用户当初选的单位）；unitIndex 越界则回退自动进位。
        public void SetMs(int ms, int unitIndex)
        {
            if (unitIndex < 0 || unitIndex >= Units.Length) { SetMs(ms); return; }
            _unit.SelectedIndex = unitIndex;
            double val = ms / Units[unitIndex].Factor;
            _value.Text = val % 1.0 == 0 ? ((long)val).ToString() : val.ToString("0.###");
        }
    }

    // 把对话框定位到鼠标光标附近（限制在主窗口范围内）。
    /// <summary>
    /// 嵌套对话框错开摆放：相对上一层右下偏移一个标题栏的量，露出上一层的边框与标题，
    /// 一眼看得出层级关系。越界则贴回所在屏工作区内（不会跑到屏幕外或任务栏下面）。
    /// </summary>
    private static void CascadeFromOwner(Window window, Window owner)
    {
        const double Step = 36;
        if (double.IsNaN(owner.Left) || double.IsNaN(owner.Top)) return;
        var dpi = VisualTreeHelper.GetDpi(owner);
        double w = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        double h = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        double left = owner.Left + Step, top = owner.Top + Step;

        // owner 所在屏的工作区（物理像素 → DIP）
        int px = (int)Math.Round((owner.Left + Math.Max(0, owner.ActualWidth) / 2) * dpi.DpiScaleX);
        int py = (int)Math.Round((owner.Top + 20) * dpi.DpiScaleY);
        var mon = ScreenInfo.Primary();
        foreach (var m in ScreenInfo.All()) if (m.Contains(px, py)) { mon = m; break; }
        double wl = mon.WorkLeft / dpi.DpiScaleX, wt = mon.WorkTop / dpi.DpiScaleY;
        double wr = mon.WorkRight / dpi.DpiScaleX, wb = mon.WorkBottom / dpi.DpiScaleY;
        if (!double.IsNaN(w) && w > 0 && left + w > wr) left = Math.Max(wl, wr - w);
        if (!double.IsNaN(h) && h > 0 && top + h > wb) top = Math.Max(wt, wb - h);
        window.Left = left; window.Top = top;
    }

    private static void PositionWindowAtCursor(Window window, Window? owner)
    {
        if (owner == null || !GetCursorPos(out var pt)) return;
        var dpi = VisualTreeHelper.GetDpi(owner);
        double cx = pt.X / dpi.DpiScaleX, cy = pt.Y / dpi.DpiScaleY;
        double w = window.ActualWidth, h = window.ActualHeight;
        double left = cx - w / 2, top = cy - 24;
        double minL = owner.Left + 8, maxL = owner.Left + owner.ActualWidth - w - 8;
        double minT = owner.Top + 8, maxT = owner.Top + owner.ActualHeight - h - 8;
        if (maxL > minL) left = Math.Max(minL, Math.Min(left, maxL));
        if (maxT > minT) top = Math.Max(minT, Math.Min(top, maxT));
        window.Left = left; window.Top = top;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr val);
}
