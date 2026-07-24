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


    private Window MakeDialog(string title)
    {
        var win = new Window
        {
            Title = title, Owner = this, Width = 460, SizeToContent = SizeToContent.Height,
            MaxHeight = 640,   // 初始按内容自适应，超出则内容区滚动
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize,
            Background = Background,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI"),
        };
        win.SetResourceReference(ForegroundProperty, "Ink");
        win.SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBar(win, ThemeManager.EffectiveDark);
        win.MinWidth = 460;
        win.MinHeight = 320;
        // 记住每个对话框各自调整后的位置与大小（key = 标题），与主窗口同一套机制。
        WindowMemory.Attach(win, "Dlg:" + title);
        bool restored = WindowMemory.WasRestored(win);
        // 记住的高度可能超过初始 MaxHeight(640)，要立刻解除上限，否则窗口会被夹到 640。
        if (restored) { win.MaxWidth = double.PositiveInfinity; win.MaxHeight = double.PositiveInfinity; }
        win.Loaded += (_, _) =>
        {
            // 用户上次调整过：尊重记住的尺寸/位置，别再按内容重算高度、也别挪到鼠标处。
            if (restored) return;
            PositionWindowAtCursor(win, this);
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
    private (string dev, double nx, double ny)? PickOnMonitor(ScreenInfo.Monitor mon, Window dialog)
    {
        (string dev, double nx, double ny)? result = null;
        var mainH = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dlgH = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
        // 拾取期间把编辑窗口与本体下沉到底层，让目标屏上的应用透过透明覆盖层清晰可见。
        SetWindowPos(dlgH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);  // SWP_NOSIZE|NOMOVE|NOACTIVATE
        SetWindowPos(mainH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Cursor = Cursors.Cross,
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)),
        };
        var accent = (Brush)FindResource("Accent");
        var root = new Grid();
        var canvas = new Canvas();
        var vLine = new Line { Stroke = accent, StrokeThickness = 1 };
        var hLine = new Line { Stroke = accent, StrokeThickness = 1 };
        var coord = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 12,
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(6, 3, 6, 3),
        };
        canvas.Children.Add(vLine); canvas.Children.Add(hLine); canvas.Children.Add(coord);
        var hint = new TextBlock
        {
            Text = $"在「{mon.Label}」上点击选择位置（Esc 取消）",
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 28, 0, 0),
            Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(12, 6, 12, 6),
        };
        root.Children.Add(canvas); root.Children.Add(hint);
        overlay.Content = root;

        overlay.SourceInitialized += (_, _) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            SetWindowPos(h, HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, 0x0040); // SWP_SHOWWINDOW
        };
        overlay.Loaded += (_, _) => { overlay.Activate(); overlay.Focus(); };
        overlay.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(canvas);
            vLine.X1 = vLine.X2 = p.X; vLine.Y1 = 0; vLine.Y2 = canvas.ActualHeight;
            hLine.Y1 = hLine.Y2 = p.Y; hLine.X1 = 0; hLine.X2 = canvas.ActualWidth;
            var (cx, cy) = ScreenInfo.CursorPos();
            var f = ScreenInfo.FromPoint(cx, cy);
            coord.Text = $"{f.nx * 100:0.#}% , {f.ny * 100:0.#}%";
            Canvas.SetLeft(coord, Math.Min(p.X + 14, Math.Max(0, canvas.ActualWidth - 96)));
            Canvas.SetTop(coord, Math.Min(p.Y + 14, Math.Max(0, canvas.ActualHeight - 26)));
        };
        overlay.MouseLeftButtonDown += (_, _) =>
        {
            var (cx, cy) = ScreenInfo.CursorPos();
            result = ScreenInfo.FromPoint(cx, cy);
            overlay.Close();
        };
        overlay.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; overlay.Close(); } };
        overlay.ShowDialog();
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

        void PlaceMarker()
        {
            double px = nx * mon.Width, py = ny * mon.Height;
            vLine.X1 = vLine.X2 = px; vLine.Y1 = 0; vLine.Y2 = mon.Height;
            hLine.Y1 = hLine.Y2 = py; hLine.X1 = 0; hLine.X2 = mon.Width;
            Canvas.SetLeft(ring, px - ring.Width / 2); Canvas.SetTop(ring, py - ring.Height / 2);
            Canvas.SetLeft(dot, px - dot.Width / 2); Canvas.SetTop(dot, py - dot.Height / 2);
            Canvas.SetLeft(coord, Math.Min(px + 26, Math.Max(0, mon.Width - 160)));
            Canvas.SetTop(coord, Math.Min(py + 14, Math.Max(0, mon.Height - 26)));
        }

        overlay.SourceInitialized += (_, _) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            SetWindowPos(h, HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, 0x0040);
        };
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

    // 手动截取目标图片：先下沉本体+编辑窗口，浮动工具条让用户自由整理桌面（把目标窗口拖到前台）；
    // 点“开始框选”后冻结整屏快照，在其上橡皮筋选区；结果从快照裁剪（不含覆盖层）。返回 PNG + 绑定的虚拟像素区域。
    private (byte[] png, int vx, int vy, int w, int h)? CaptureTargetImage(Window dialog)
    {
        // 屏幕序号标签是常显置顶窗口，会被拍进冻结快照/目标图——截图期间先藏起来，截完若原本在显示则恢复。
        bool hadIds = _idScreensByDialog.TryGetValue(dialog, out var ids) && ids.Count > 0;
        HideIdScreens(dialog);

        var mainH = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dlgH = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
        SetWindowPos(dlgH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);
        SetWindowPos(mainH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);

        // --- 浮动工具条（非模态于其它程序）：用户整理好桌面再点“开始框选” ---
        bool proceed = false;
        var toolbar = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Background = Brushes.Transparent, SizeToContent = SizeToContent.WidthAndHeight,
        };
        var pClr = ((SolidColorBrush)FindResource("Panel")).Color;
        var tbCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC0, pClr.R, pClr.G, pClr.B)),  // 半透明，露出底下桌面
            BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Cursor = Cursors.SizeAll,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.2, Color = Colors.Black },
        };
        var tbStack = new StackPanel();
        tbStack.Children.Add(new TextBlock { Text = "整理好桌面后点下方按钮框选（可拖动本条移动位置）", Foreground = (Brush)FindResource("Ink"), FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Margin = new Thickness(0, 0, 0, 10) });
        var tbBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var startBtn = new Button { Style = (Style)FindResource("IconButton"), FontSize = 16, Content = "", ToolTip = "开始框选目标区域" };
        var cancelBtn = new Button { Style = (Style)FindResource("IconButton"), FontSize = 16, Content = "", ToolTip = "取消", Margin = new Thickness(4, 0, 0, 0) };
        tbBtns.Children.Add(startBtn); tbBtns.Children.Add(cancelBtn);
        tbStack.Children.Add(tbBtns); tbCard.Child = tbStack; toolbar.Content = tbCard;
        startBtn.Click += (_, _) => { proceed = true; toolbar.Close(); };
        cancelBtn.Click += (_, _) => { proceed = false; toolbar.Close(); };
        tbCard.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) toolbar.DragMove(); };  // 拖动移动
        toolbar.Loaded += (_, _) =>
        {
            var pm = ScreenInfo.Primary();
            toolbar.Left = pm.Left + (pm.Width - toolbar.ActualWidth) / 2;
            toolbar.Top = pm.Top + 40;
        };
        toolbar.ShowDialog();

        if (!proceed) { Services.WindowActivator.ActivateHwnd(mainH); Services.WindowActivator.ActivateHwnd(dlgH); return null; }

        // 关闭模态工具条会让 WPF 重新激活本体（owner）弹到前台——重新下沉，并留时间让被盖住的目标窗口重绘，
        // 保证冻屏抓到的就是“开始框选”那一刻的桌面（与整理阶段一致，不含本体）。
        SetWindowPos(dlgH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);
        SetWindowPos(mainH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);
        System.Threading.Thread.Sleep(180);

        // --- 冻结整屏快照 ---
        var (ox, oy, vw, vh) = VirtualBounds();
        var snapshot = Services.ScreenMatch.CaptureRegion(ox, oy, vw, vh);

        // --- 选区覆盖层：把冻结快照当背景图显示（跨全部显示器 1:1），选区在“快照像素空间”里算，
        // 视觉与裁剪同源，多屏/混合 DPI 都不会错位。 ---
        (int x, int y, int w, int h)? sel = null;
        var accent = (Brush)FindResource("Accent");
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Cursor = Cursors.Cross, Background = Brushes.Transparent,
        };
        var snapImg = new System.Windows.Controls.Image { Source = ToBitmapSource(snapshot), Stretch = System.Windows.Media.Stretch.Fill };
        var dim = new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)) };  // 轻微压暗，突出选框
        var canvas = new Canvas();
        var rubber = new System.Windows.Shapes.Rectangle { Stroke = accent, StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x8A, 0x78, 0x60)), Visibility = Visibility.Collapsed };
        var sizeLbl = new TextBlock { Foreground = Brushes.White, FontSize = 12, Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(6, 3, 6, 3), Visibility = Visibility.Collapsed };
        canvas.Children.Add(rubber); canvas.Children.Add(sizeLbl);
        var hint = new TextBlock
        {
            Text = "拖拽框选目标图片（Esc 取消）", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 28, 0, 0),
            Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(12, 6, 12, 6),
        };
        var root = new Grid(); root.Children.Add(snapImg); root.Children.Add(dim); root.Children.Add(canvas); root.Children.Add(hint); overlay.Content = root;
        overlay.SourceInitialized += (_, _) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            SetWindowPos(h, HWND_TOPMOST, ox, oy, vw, vh, 0x0040);
        };
        overlay.Loaded += (_, _) => { overlay.Activate(); overlay.Focus(); };
        // 快照像素/DIP 比例：图片铺满窗口，其像素宽=vw；无论多屏/DPI，映射恒定。
        double PixPerDip() => vw / Math.Max(1.0, snapImg.ActualWidth);
        System.Windows.Point? downDip = null;
        void UpdateSel(System.Windows.Point cur)
        {
            var d = downDip!.Value;
            double x = Math.Min(cur.X, d.X), y = Math.Min(cur.Y, d.Y), w = Math.Abs(cur.X - d.X), h = Math.Abs(cur.Y - d.Y);
            Canvas.SetLeft(rubber, x); Canvas.SetTop(rubber, y); rubber.Width = w; rubber.Height = h;
            double r = PixPerDip();
            int px = ox + (int)Math.Round(x * r), py = oy + (int)Math.Round(y * r);
            sizeLbl.Text = $"({px}, {py})  {(int)Math.Round(w * r)}×{(int)Math.Round(h * r)}";
            Canvas.SetLeft(sizeLbl, x); Canvas.SetTop(sizeLbl, Math.Max(0, y - 24));
        }
        overlay.MouseLeftButtonDown += (_, e) =>
        {
            downDip = e.GetPosition(snapImg);
            rubber.Visibility = Visibility.Visible; sizeLbl.Visibility = Visibility.Visible;
            UpdateSel(downDip.Value);
        };
        overlay.MouseMove += (_, e) => { if (downDip != null) UpdateSel(e.GetPosition(snapImg)); };
        overlay.MouseLeftButtonUp += (_, e) =>
        {
            if (downDip is { } d)
            {
                var up = e.GetPosition(snapImg);
                double r = PixPerDip();
                int px = (int)Math.Round(Math.Min(d.X, up.X) * r), py = (int)Math.Round(Math.Min(d.Y, up.Y) * r);
                int pw = (int)Math.Round(Math.Abs(up.X - d.X) * r), ph = (int)Math.Round(Math.Abs(up.Y - d.Y) * r);
                sel = (ox + px, oy + py, pw, ph);
            }
            overlay.Close();
        };
        overlay.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; sel = null; overlay.Close(); } };
        overlay.ShowDialog();

        byte[]? png = null; int rx = 0, ry = 0, rw = 0, rh = 0;
        if (sel is { } s && s.w >= 4 && s.h >= 4)
        {
            rx = s.x; ry = s.y; rw = s.w; rh = s.h;
            try
            {
                using var crop = new System.Drawing.Bitmap(rw, rh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(crop))
                    g.DrawImage(snapshot, new System.Drawing.Rectangle(0, 0, rw, rh), new System.Drawing.Rectangle(rx - ox, ry - oy, rw, rh), System.Drawing.GraphicsUnit.Pixel);
                png = Services.ScreenMatch.ToPng(crop);
            }
            catch { png = null; }
        }
        snapshot.Dispose();
        Services.WindowActivator.ActivateHwnd(mainH);
        Services.WindowActivator.ActivateHwnd(dlgH);
        if (hadIds) ShowIdScreens(dialog);   // 恢复原先显示的屏幕序号标签
        return png == null ? null : (png, rx, ry, rw, rh);
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
        overlay.Loaded += (_, _) =>
        {
            // DIP = 像素 / 比例；比例 = 快照像素宽 / 图片 DIP 宽（多屏/DPI 恒定）。
            double r = vw / Math.Max(1.0, snapImg.ActualWidth);
            double lx = (vx - ox) / r, ly = (vy - oy) / r, lw = w / r, lh = h / r;
            Canvas.SetLeft(box, lx); Canvas.SetTop(box, ly); box.Width = lw; box.Height = lh;
            Canvas.SetLeft(glow, lx - 1); Canvas.SetTop(glow, ly - 1); glow.Width = lw + 2; glow.Height = lh + 2;
            Canvas.SetLeft(label, lx); Canvas.SetTop(label, Math.Max(0, ly - 24));
            overlay.Activate(); overlay.Focus();
        };
        overlay.MouseLeftButtonDown += (_, _) => overlay.Close();
        overlay.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; overlay.Close(); } };
        overlay.ShowDialog();
        Services.WindowActivator.ActivateHwnd(mainH);
        Services.WindowActivator.ActivateHwnd(dlgH);
    }

    private FrameworkElement BuildHookRow(string label, Func<MacroStep?> get, Action<MacroStep?> set)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = true };
        var lbl = new TextBlock { Text = label, Width = 52, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(lbl, Dock.Left); row.Children.Add(lbl);
        var clearBtn = new Button { Content = "清除", Width = 56, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(clearBtn, Dock.Right); row.Children.Add(clearBtn);
        var setBtn = new Button { Content = "设置", Width = 56, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(setBtn, Dock.Right); row.Children.Add(setBtn);
        // 摘要自动换行（不再 … 截断）：配置复杂的监听动作描述很长，让它多行显示看全，与外层文本一致。
        var summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Muted") };
        row.Children.Add(summary);

        void Refresh()
        {
            var s = get();
            summary.Text = s != null ? s.ToString() : "未设置";
            clearBtn.IsEnabled = s != null;
        }
        // 监听动作＝完整动作：用与外层同款的完整对话框（可配循环/运行条件/备注，且能继续配监听——递归下去）。
        setBtn.Click += (_, _) => { var s = ShowAddActionDialog(get()); if (s != null) { set(s); Refresh(); } };
        clearBtn.Click += (_, _) => { set(null); Refresh(); };
        Refresh();
        return row;
    }

    // ImageMatch 编辑态：目标图 PNG + 绑定的虚拟像素区域 + 相似度阈值。
    private sealed class ImageCond
    {
        public byte[]? Png;
        public string Monitor = "";     // 绑定的屏幕设备名
        public int RelX, RelY, W, H;    // 屏内相对像素矩形
        public double Threshold = 0.9;
        public bool Has => Png != null && Png.Length > 0 && W > 0 && H > 0;
    }

    // 运行条件面板：启用勾选后才展开明细。typeCombo+img 非空时（仅动作级）提供“时间段/图片出现”两类，
    // 否则仅时间段（方案级/组合级复用）。
    private StackPanel BuildRunConditionPanel(
        CheckBox enabled,
        CheckBox invert,
        ComboBox startHour,
        ComboBox startMinute,
        ComboBox endHour,
        ComboBox endMinute,
        ComboBox? typeCombo = null,
        ImageCond? img = null)
    {
        bool withImage = typeCombo != null && img != null;

        var timeRow = new StackPanel { Orientation = Orientation.Horizontal };
        timeRow.Children.Add(BuildTimeField("从", startHour, startMinute));
        timeRow.Children.Add(new Border { Width = 22 });
        timeRow.Children.Add(BuildTimeField("到", endHour, endMinute));
        var timeSub = new StackPanel();
        timeSub.Children.Add(timeRow);
        timeSub.Children.Add(new TextBlock
        {
            Text = "某侧选“不限”表示开放边界（例：只设“到 18:00”即 18:00 前均执行）。",
            Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        });

        var detail = new StackPanel();

        if (!withImage)
        {
            enabled.Content = "仅在时间段内执行";
            invert.Content = "取反（不在该时间段内执行）";
            invert.Margin = new Thickness(0, 12, 0, 0);
            detail.Children.Add(timeSub);
            detail.Children.Add(invert);
        }
        else
        {
            enabled.Content = "启用运行条件";
            invert.Content = "取反（条件不满足时才执行）";
            invert.Margin = new Thickness(0, 12, 0, 0);

            typeCombo!.Items.Clear();
            typeCombo.Items.Add("时间段"); typeCombo.Items.Add("图片出现");
            typeCombo.Height = 32; typeCombo.Margin = new Thickness(0, 0, 0, 10);
            if (typeCombo.SelectedIndex < 0) typeCombo.SelectedIndex = 0;

            var imageSub = new StackPanel();
            var capBtn = new Button { Style = (Style)FindResource("IconButton"), FontSize = 16, Content = "", ToolTip = "截取目标图片" };
            var previewImgBtn = new Button { Style = (Style)FindResource("IconButton"), FontSize = 16, Content = "", ToolTip = "预览截取区域（白框标示）", IsEnabled = false };
            var capStatus = new TextBlock { Foreground = (Brush)FindResource("Muted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            var capRow = new StackPanel { Orientation = Orientation.Horizontal };
            capRow.Children.Add(capBtn); capRow.Children.Add(previewImgBtn); capRow.Children.Add(capStatus);
            imageSub.Children.Add(capRow);
            var posText = new TextBlock { Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            imageSub.Children.Add(posText);
            var thumb = new System.Windows.Controls.Image { MaxWidth = 220, MaxHeight = 150, Stretch = System.Windows.Media.Stretch.Uniform };
            var thumbBorder = new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Child = thumb, Visibility = Visibility.Collapsed };
            imageSub.Children.Add(thumbBorder);
            var thrRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            thrRow.Children.Add(new TextBlock { Text = "相似度阈值(%)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var thrText = new TextBox { Width = 64, Height = 30, Text = ((int)Math.Round(img!.Threshold * 100)).ToString() };
            thrRow.Children.Add(thrText);
            thrRow.Children.Add(new TextBlock { Text = "（越高越严格，默认 90）", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(8, 0, 0, 0) });
            imageSub.Children.Add(thrRow);
            imageSub.Children.Add(new TextBlock { Text = "图片需出现在截取时的同一屏幕位置。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

            void RefreshThumb()
            {
                if (img.Has)
                {
                    capStatus.Text = $"已截取（{img.W}×{img.H}）";
                    capBtn.ToolTip = "重新截取目标图片";
                    posText.Text = $"屏幕 {ScreenInfo.ByDevice(img.Monitor).Label}：屏内 ({img.RelX}, {img.RelY})  尺寸 {img.W}×{img.H}";
                    posText.Visibility = Visibility.Visible;
                    previewImgBtn.IsEnabled = true;
                    try
                    {
                        var bi = new System.Windows.Media.Imaging.BitmapImage();
                        using var ms = new System.IO.MemoryStream(img.Png!);
                        bi.BeginInit(); bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.StreamSource = ms; bi.EndInit();
                        thumb.Source = bi; thumbBorder.Visibility = Visibility.Visible;
                    }
                    catch { thumbBorder.Visibility = Visibility.Collapsed; }
                }
                else { capStatus.Text = "未截取"; capBtn.ToolTip = "截取目标图片"; posText.Visibility = Visibility.Collapsed; previewImgBtn.IsEnabled = false; thumbBorder.Visibility = Visibility.Collapsed; }
            }
            capBtn.Click += (_, _) =>
            {
                var w = Window.GetWindow(capBtn);
                if (w == null) return;
                var r = CaptureTargetImage(w);
                if (r is { } c)
                {
                    var (dev, _, _) = ScreenInfo.FromPoint(c.vx, c.vy);
                    var mon = ScreenInfo.ByDevice(dev);
                    img.Png = c.png; img.Monitor = dev; img.RelX = c.vx - mon.Left; img.RelY = c.vy - mon.Top; img.W = c.w; img.H = c.h;
                    RefreshThumb();
                }
            };
            previewImgBtn.Click += (_, _) =>
            {
                var w = Window.GetWindow(previewImgBtn);
                if (w != null && img.Has) { var mon = ScreenInfo.ByDevice(img.Monitor); PreviewRegion(mon.Left + img.RelX, mon.Top + img.RelY, img.W, img.H, w); }
            };
            thrText.TextChanged += (_, _) => { if (double.TryParse(thrText.Text, out var v)) img.Threshold = Math.Clamp(v / 100.0, 0.1, 1.0); };
            RefreshThumb();

            detail.Children.Add(typeCombo);
            detail.Children.Add(timeSub);
            detail.Children.Add(imageSub);
            detail.Children.Add(invert);

            void RefreshType()
            {
                bool image = typeCombo.SelectedIndex == 1;
                timeSub.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
                imageSub.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
                if (image)
                {
                    // 阈值框在建面板时按默认值(90)初始化，回填既有条件是在那之后才还原 img.Threshold 的——
                    // 切到图片视图时把阈值框同步到实际值，否则一直显示 90（改了保存后再打开还显示 90）。
                    thrText.Text = ((int)Math.Round(img.Threshold * 100)).ToString();
                    RefreshThumb();   // 编辑既有图片条件时，切到图片视图即回显缩略图
                }
            }
            typeCombo.SelectionChanged += (_, _) => RefreshType();
            RefreshType();
        }

        // 勾选开关后，明细收进一个缩进 + 弱底色 + 强调左条的面板里，一眼看出属于该开关的“势力范围”。
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

    // 单个时间字段：标签 + [时]:[分]；小时选“不限”时禁用分钟。
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
        var runActionCombo = new ComboBox { Width = 108, Height = 32, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
        runActionCombo.Items.Add("等待"); runActionCombo.Items.Add("激活窗口"); runActionCombo.Items.Add("跳转");
        runActionCombo.SelectedIndex = 0;
        typeRow.Children.Add(typeCombo); typeRow.Children.Add(deviceCombo); typeRow.Children.Add(mouseActionCombo); typeRow.Children.Add(runActionCombo);
        baseContent.Children.Add(typeRow);

        // 鼠标面板
        var mousePanel = new StackPanel(); baseContent.Children.Add(mousePanel);

        // 鼠标按钮：多一个「仅移动」——选它即只移动不点击（存为 MouseMove）。
        var buttonCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 0), Height = 32 };
        buttonCombo.Items.Add("左键"); buttonCombo.Items.Add("右键"); buttonCombo.Items.Add("中键");
        buttonCombo.SelectedIndex = 0;
        var mouseButtonPanel = SubGroup("鼠标按钮", buttonCombo);

        var holdRow = new TimeInputRow(this, _doc.DefaultHoldMs);
        holdRow.Panel.Margin = new Thickness(0, 0, 0, 0);
        var mouseHoldPanel = SubGroup("按住时间", holdRow.Panel);

        // 坐标块（可复用）：点击/移动用一个；拖动用两个（起点、终点）。
        var coord = new CoordBlock(this, win, "设置坐标（先移动到该位置再执行）", showCheck: true);
        var mouseMovePanel = coord.Panel;
        var coordCheck = coord.Enabled;
        // 拖动终点：始终展开、无勾选框
        var dragEnd = new CoordBlock(this, win, "终点坐标", showCheck: false);
        var dragEndPanel = dragEnd.Panel;

        // 拟人化移动（动作级）：独立成块，排在最后；只在启用坐标时有意义。
        var humanizeMoveCheck = new CheckBox { Content = "拟人化移动（走缓入缓出的弧线轨迹，更像真人）" };
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

        // 等待面板
        var waitRow = new TimeInputRow(this, _doc.DefaultWaitMs);
        waitRow.Panel.Margin = new Thickness(0, 0, 0, 0);
        var waitPanel = SubGroup("等待时间", waitRow.Panel);
        waitPanel.Visibility = Visibility.Collapsed; baseContent.Children.Add(waitPanel);

        // 激活窗口面板：从当前窗口列表选目标，选中即锁定该进程（含 PID）。
        var windowInner = new StackPanel();
        var windowPanel = SubGroup(null, windowInner);
        windowPanel.Visibility = Visibility.Collapsed; baseContent.Children.Add(windowPanel);
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
        mousePanel.Children.Add(mouseMovePanel);
        mousePanel.Children.Add(dragEndPanel);
        mousePanel.Children.Add(mouseHoldPanel);
        mousePanel.Children.Add(mouseRepeat.Panel);
        mousePanel.Children.Add(mouseWheelPanel);
        mousePanel.Children.Add(humanizePanel);

        // 跳转面板（运行 → 跳转）：原先挂在每个动作上的「执行后跳转到」已剥离成这个独立动作。
        var jumpInner = new StackPanel();
        var jumpPanel = SubGroup(null, jumpInner);
        jumpPanel.Visibility = Visibility.Collapsed; baseContent.Children.Add(jumpPanel);
        var jumpTargetCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), Height = 32 };
        jumpTargetCombo.Items.Add("（选择目标动作）");
        int count = _plan?.Steps.Count ?? 0;
        for (int n = 1; n <= count; n++)
        {
            // 有备注显示备注，否则显示动作简述（截断防超宽）
            var brief = _plan!.Steps[n - 1].Brief;
            if (brief.Length > 42) brief = brief[..42] + "…";
            jumpTargetCombo.Items.Add($"{n}. {brief}");
        }
        jumpTargetCombo.SelectedIndex = 0;
        jumpInner.Children.Add(FieldLabel("跳转到"));
        jumpInner.Children.Add(jumpTargetCombo);
        jumpInner.Children.Add(FieldLabel("最大重复次数（0 为不限）"));
        var jumpMaxText = new TextBox { Text = "0", Margin = new Thickness(0, 0, 0, 8), Height = 32 };
        jumpInner.Children.Add(jumpMaxText);
        jumpInner.Children.Add(new TextBlock { Text = "每次执行到本动作就跳到指定序号继续（仅方案顶层生效）。最大重复次数是防死循环的上限：本轮内已跳次数达到上限后，该跳转不再生效、按顺序往下走；0 表示不设上限。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap });

        // 运行类（等待/激活窗口）的 执行次数+重复间隔：与鼠标/键盘同一套 RepeatBlock，后续新类型照此办理。
        var runRepeat = new RepeatBlock(this, "执行次数（0 为无限）");
        baseContent.Children.Add(runRepeat.Panel);
        var noteText = new TextBox { Text = "", Margin = new Thickness(0, 0, 0, 14), Height = 32 };
        var cond = BuildRunConditionEditor(null);    // 与方案级共用同一套控件与逻辑

        MacroStep? hookSuccess = source?.SuccessAction, hookComplete = source?.CompleteAction, hookFail = source?.FailAction;
        sp.Children.Add(GroupCard("基础设置", baseContent));
        {
            var condPanel = cond.Panel;
            condPanel.Margin = new Thickness(0, 0, 0, 4);
            sp.Children.Add(GroupCard("运行条件", condPanel));   // 独立成卡

            var hookNote = new TextBlock { Text = "执行成功 / 结束 / 失败后追加执行一个完整动作（可含循环、运行条件、组合，并能继续挂自己的监听）。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(GroupCard("事件监听",
                hookNote,
                BuildHookRow("成功后", () => hookSuccess, v => hookSuccess = v),
                BuildHookRow("结束后", () => hookComplete, v => hookComplete = v),
                BuildHookRow("失败后", () => hookFail, v => hookFail = v)));

            sp.Children.Add(GroupCard("备注（可选）", noteText));
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
        string RunAct() => Cat() == "运行" ? (runActionCombo.SelectedItem?.ToString() ?? "等待") : "";

        void SyncIdScreens()
        {
            // 需要选屏的两种：激活窗口、带坐标的鼠标动作。只有一块屏时不自动标（没意义），手动「标识屏幕」按钮不受影响。
            bool coordView = Act() is "移动" or "拖动" || (Act() == "点击" && coordCheck.IsChecked == true);
            bool needScreens = (RunAct() == "激活窗口" || coordView) && ScreenInfo.All().Count > 1;
            if (needScreens) ShowIdScreens(win); else HideIdScreens(win);
        }
        void UpdatePanels()
        {
            string t = Cat(), d = Dev(), a = Act(), ra = RunAct();
            deviceCombo.Visibility = t == "输入" ? Visibility.Visible : Visibility.Collapsed;
            mouseActionCombo.Visibility = d == "鼠标" ? Visibility.Visible : Visibility.Collapsed;
            runActionCombo.Visibility = t == "运行" ? Visibility.Visible : Visibility.Collapsed;

            mousePanel.Visibility = d == "鼠标" ? Visibility.Visible : Visibility.Collapsed;
            keyboardPanel.Visibility = d == "键盘" ? Visibility.Visible : Visibility.Collapsed;
            waitPanel.Visibility = ra == "等待" ? Visibility.Visible : Visibility.Collapsed;
            windowPanel.Visibility = ra == "激活窗口" ? Visibility.Visible : Visibility.Collapsed;
            jumpPanel.Visibility = ra == "跳转" ? Visibility.Visible : Visibility.Collapsed;
            bool isMove = a == "移动", isClick = a == "点击", isDrag = a == "拖动";
            // 移动/拖动必须有坐标：强制勾上且不给取消；点击的坐标是可选项（勾了=先移动再点击）。
            bool coordForced = isMove || isDrag;
            if (coordForced && coordCheck.IsChecked != true) coordCheck.IsChecked = true;
            coordCheck.IsEnabled = !coordForced;
            coordCheck.Content = isMove ? "坐标（移动到该位置，必须设置）"
                               : isDrag ? "起点坐标（在此按下鼠标键）"
                               : "设置坐标（先移动到该位置再点击）";
            dragEndPanel.Visibility = isDrag ? Visibility.Visible : Visibility.Collapsed;   // 拖动才有终点

            mouseButtonPanel.Visibility = isClick || isDrag ? Visibility.Visible : Visibility.Collapsed;
            mouseMovePanel.Visibility = isMove || isClick || isDrag ? Visibility.Visible : Visibility.Collapsed;
            // 按住时间只对点击有意义；拖动的"按住"贯穿整个移动过程，无需时长。
            mouseHoldPanel.Visibility = isClick ? Visibility.Visible : Visibility.Collapsed;
            mouseWheelPanel.Visibility = a == "滚轮" ? Visibility.Visible : Visibility.Collapsed;
            // 拟人化只在会发生移动时才有意义。
            humanizePanel.Visibility = coordForced || (isClick && coordCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;

            bool hasRepeat = isClick || a == "滚轮";
            mouseRepeat.Panel.Visibility = hasRepeat ? Visibility.Visible : Visibility.Collapsed;
            mouseRepeat.CountLabel.Text = a == "滚轮" ? "滚动次数（0 为无限）" : "点击次数（0 为无限）";
            // 运行类的 执行次数+重复间隔：等待/激活窗口显示；跳转有自己的跳转次数，不显示。
            runRepeat.Panel.Visibility = ra is "等待" or "激活窗口" ? Visibility.Visible : Visibility.Collapsed;

            capturingKey = d == "键盘";
            if (capturingKey) win.Focus();
            // 窗口列表改按需枚举（点选择器时才 RefreshWindows，见 OpenPicker），不在打开时同步枚举；
            // 屏幕序号标记（每屏一个置顶窗口）也延后到后台优先级异步显示 —— 消除激活窗口/鼠标移动动作双击打开时的卡顿厚重感。
            win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(SyncIdScreens));
        }
        typeCombo.SelectionChanged += (_, _) => UpdatePanels();
        deviceCombo.SelectionChanged += (_, _) => UpdatePanels();
        mouseActionCombo.SelectionChanged += (_, _) => UpdatePanels();
        runActionCombo.SelectionChanged += (_, _) => UpdatePanels();
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
                    }
                    void FillButton(MacroStep m)
                    {
                        m.Button = ButtonToInternal(buttonCombo.SelectedItem?.ToString() ?? "左键");
                        m.HoldMs = holdRow.GetMs();
                        m.HoldUnit = holdRow.UnitIndex;
                    }
                    // 界面上是一个「点击/移动」，存储仍沿用三种既有类型，旧方案照常可读：
                    //   仅移动          → MouseMove
                    //   按钮 + 设坐标   → MouseClickAt
                    //   按钮 + 不设坐标 → MouseClick
                    if (a == "滚轮")
                    {
                        result = new MacroStep { Type = "MouseWheel", Wheel = ParseInt(wheelText.Text, 0) };
                        mouseRepeat.Apply(result);
                    }
                    else if (a == "移动")
                    {
                        result = new MacroStep { Type = "MouseMove" };
                        FillMove(result);
                        result.LoopCount = 1;   // 移动没有次数概念
                    }
                    else if (a == "拖动")
                    {
                        // 拖动 = 移到起点 → 按下 → 移到终点 → 松开。起点存 MoveXxx，终点存 DragEndXxx。
                        result = new MacroStep { Type = "MouseDrag", Button = ButtonToInternal(buttonCombo.SelectedItem?.ToString() ?? "左键") };
                        FillMove(result);
                        var (devE, nxE, nyE) = dragEnd.Read();
                        result.DragEndMonitor = devE; result.DragEndNormX = nxE; result.DragEndNormY = nyE;
                        result.LoopCount = 1;
                    }
                    else if (a == "点击")
                    {
                        bool withCoord = coordCheck.IsChecked == true;
                        result = new MacroStep { Type = withCoord ? "MouseClickAt" : "MouseClick" };
                        if (withCoord) FillMove(result);
                        FillButton(result);
                        mouseRepeat.Apply(result);
                        if (holdRow.SetAsDefault)
                        {
                            var ms = holdRow.GetMs();
                            if (_doc.DefaultHoldMs != ms) { _doc.DefaultHoldMs = ms; settingsChanged = true; }
                        }
                    }
                    else throw new InvalidOperationException("请选择鼠标动作。");
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
                        throw new InvalidOperationException("请选择跳转的目标动作。");
                    result = new MacroStep { Type = "Jump", JumpTarget = jumpTargetCombo.SelectedIndex, JumpTimes = Math.Max(0, ParseInt(jumpMaxText.Text, 0)) };
                }
                else // 激活窗口
                {
                    if (selPid <= 0 && selProc.Length == 0 && selTitle.Length == 0)
                        throw new InvalidOperationException("请从列表选择目标窗口。");
                    result = new MacroStep { Type = "ActivateWindow", TargetProcess = selProc, TargetTitle = selTitle, TargetPid = selPid };
                }
                {
                    if (ra is "等待" or "激活窗口") runRepeat.Apply(result);   // 鼠标/键盘的次数已由各自 RepeatBlock 写过
                    ApplyRunCondition(cond, result);   // 与方案级同一份写回逻辑（校验失败抛异常，下面统一提示）
                    result.SuccessAction = hookSuccess; result.CompleteAction = hookComplete; result.FailAction = hookFail;
                    result.Note = noteText.Text.Trim();
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
            }
            // 回填按钮/按住（点击 / 点击坐标共用）
            void LoadButtonFields()
            {
                buttonCombo.SelectedItem = TranslateButtonToDisplay(source.Button);
                holdRow.SetMs(source.HoldMs, source.HoldUnit);
            }
            switch (source.Type)
            {
                case "MouseMove":     typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "鼠标"; mouseActionCombo.SelectedItem = "移动"; coordCheck.IsChecked = true; LoadMoveFields(); break;
                case "MouseClick":    typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "鼠标"; mouseActionCombo.SelectedItem = "点击"; coordCheck.IsChecked = false; LoadButtonFields(); break;
                case "MouseClickAt":  typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "鼠标"; mouseActionCombo.SelectedItem = "点击"; coordCheck.IsChecked = true; LoadMoveFields(); LoadButtonFields(); break;
                case "MouseDrag":
                    typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "鼠标"; mouseActionCombo.SelectedItem = "拖动";
                    coordCheck.IsChecked = true; LoadMoveFields(); LoadButtonFields();
                    dragEnd.Write(source.DragEndMonitor, source.DragEndNormX, source.DragEndNormY);
                    break;
                case "MouseWheel":    typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "鼠标"; mouseActionCombo.SelectedItem = "滚轮"; wheelText.Text = source.Wheel.ToString(); break;
                case "KeyTap":        typeCombo.SelectedItem = "输入"; deviceCombo.SelectedItem = "键盘"; capturedKey = source.Key; capturedModifier = source.Modifier; capturedText.Text = FormatCapturedKey(source.Key, source.Modifier); keyboardHoldRow.SetMs(source.HoldMs, source.HoldUnit); break;
                case "Wait":          typeCombo.SelectedItem = "运行"; runActionCombo.SelectedItem = "等待"; waitRow.SetMs(source.DurationMs, source.DurationUnit); break;
                case "ActivateWindow":typeCombo.SelectedItem = "运行"; runActionCombo.SelectedItem = "激活窗口"; selPid = source.TargetPid; selProc = source.TargetProcess; selTitle = source.TargetTitle; UpdateSelLabel(); break;
                case "Jump":          typeCombo.SelectedItem = "运行"; runActionCombo.SelectedItem = "跳转"; break;   // 目标/次数由下方通用回填写入
            }
            mouseRepeat.Load(source); kbRepeat.Load(source); runRepeat.Load(source);
            LoadRunCondition(cond, source);   // 与方案级同一份回填逻辑
            if (source.JumpTarget >= 1 && source.JumpTarget <= count) jumpTargetCombo.SelectedIndex = source.JumpTarget;
            jumpMaxText.Text = Math.Max(0, source.JumpTimes).ToString();
            noteText.Text = source.Note;
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

        MacroStep? hookSuccess = source.SuccessAction, hookComplete = source.CompleteAction, hookFail = source.FailAction;
        var hookNote = new TextBlock { Text = "执行成功 / 结束 / 失败后追加执行一个完整动作（可含循环、运行条件、组合，并能继续挂自己的监听）。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        sp.Children.Add(GroupCard("事件监听",
            hookNote,
            BuildHookRow("成功后", () => hookSuccess, v => hookSuccess = v),
            BuildHookRow("结束后", () => hookComplete, v => hookComplete = v),
            BuildHookRow("失败后", () => hookFail, v => hookFail = v)));

        var noteText = new TextBox { Text = source.Note, Margin = new Thickness(0, 0, 0, 14), Height = 32 };
        sp.Children.Add(GroupCard("备注（可选）", noteText));

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
                SuccessAction = hookSuccess, CompleteAction = hookComplete, FailAction = hookFail,
                Note = noteText.Text.Trim(),
            };
            try { groupRepeat.Apply(result); }
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
    private sealed class CoordBlock
    {
        private readonly MainWindow _o;
        private readonly Window _win;
        private readonly ComboBox _monitor = new() { Height = 32 };
        private readonly TextBox _x = new() { Text = "50", Width = 86, Height = 32 };
        private readonly TextBox _y = new() { Text = "50", Width = 86, Height = 32 };
        private readonly TextBlock _status;
        private readonly Border _wrap;
        public readonly CheckBox Enabled = new() { VerticalAlignment = VerticalAlignment.Center };
        public readonly Border Panel;

        public CoordBlock(MainWindow o, Window win, string title, bool showCheck)
        {
            _o = o; _win = win;
            var detail = new StackPanel();

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
            var pickBtn = new Button { Style = (Style)o.FindResource("IconButton"), FontSize = 16, Content = "\uE81D", ToolTip = "在屏幕上点选坐标" };
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
                var r = o.PickOnMonitor(ScreenInfo.ByDevice(Device), win);
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
    private sealed class RepeatBlock
    {
        public readonly Border Panel;
        public readonly TextBlock CountLabel;
        public readonly TextBox Count = new() { Text = "1", Height = 32 };
        private readonly TextBox _delayVal = new() { Width = 96, Height = 32, Text = "1", VerticalAlignment = VerticalAlignment.Center };
        private readonly ComboBox _delayUnit = new() { Width = 84, Height = 32, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _delayBlock;

        public RepeatBlock(MainWindow owner, string label)
        {
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
            inner.Children.Add(_delayBlock);
            Panel = owner.SubGroup(null, inner);
            Count.TextChanged += (_, _) => Refresh();
            Refresh();
        }

        private void Refresh() => _delayBlock.Visibility = ParseInt(Count.Text, 1) == 1 ? Visibility.Collapsed : Visibility.Visible;
        private double UnitFactor => _delayUnit.SelectedIndex switch { 0 => 1, 2 => 60000, 3 => 3600000, _ => 1000 };

        private int DelayMs()
        {
            if (string.IsNullOrWhiteSpace(_delayVal.Text)) throw new InvalidOperationException("请填写重复间隔。");
            if (!double.TryParse(_delayVal.Text.Trim(), out var v) || v < 0) throw new InvalidOperationException("重复间隔需为不小于 0 的数字。");
            return (int)Math.Round(v * UnitFactor);
        }

        public void Apply(MacroStep r)
        {
            r.LoopCount = Math.Max(0, ParseInt(Count.Text, 1));
            if (r.LoopCount != 1) { r.LoopDelayMs = DelayMs(); r.LoopDelayUnit = Math.Max(0, _delayUnit.SelectedIndex); }
        }

        public void Load(MacroStep sSrc)
        {
            Count.Text = sSrc.LoopCount.ToString();
            int u = Math.Clamp(sSrc.LoopDelayUnit, 0, 3);
            _delayUnit.SelectedIndex = u;
            double f = u switch { 0 => 1, 2 => 60000, 3 => 3600000, _ => 1000 };
            double v = sSrc.LoopDelayMs / f;
            _delayVal.Text = v % 1.0 == 0 ? ((long)v).ToString() : v.ToString("0.###");
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
