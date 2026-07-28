using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using WRect = System.Windows.Rect;
using WPoint = System.Windows.Point;
using WSize = System.Windows.Size;

namespace MacroPilot;

/// <summary>
/// 截图覆盖层（截取目标图片）：冻屏 → 框选 → 可选标注 → 裁剪出 PNG。
/// 交互对齐常见截图工具：悬停自动框住窗口、8 向调整选区、方向键微调、跟随选区的工具条、
/// 画完的标注可再次选中拖动 / 改大小 / 删除。
///
/// 【重要】这里截出来的图是拿去做【图像匹配模板】的：标注（矩形/箭头/画笔/文字/马赛克）会
/// 烧进模板，导致模板与屏幕实际画面不一致而匹配不上。工具条上已就此给出提示——标注更适合
/// 用在"截下来另作他用"的场合。
/// </summary>
public partial class MainWindow
{
    // 标注：几何一律存 DIP（与覆盖层画布同一坐标系），确认时再按比例烧到物理像素的位图上。
    private sealed class Annot
    {
        public string Kind = "";                  // rect / ellipse / arrow / pen / mosaic / text
        public WPoint A, B;
        public List<WPoint>? Pen;                 // 画笔轨迹
        public string Text = "";
        public Color Color = AnnotColor;          // 每个标注各自的颜色
        public double Thick = 4;                  // 描边粗细（DIP）；文字用它换算字号；马赛克不用
        public readonly List<UIElement> Visuals = new();   // 预览用的 WPF 元素（撤销/重画时从画布移除）

        public double FontSize => Math.Max(12, Thick * 5);   // 文字没有描边，粗细档位改成字号

        /// <summary>矩形语义的标注（A/B 就是对角）：可用 8 向手柄调整大小。其余只能整体移动或拖端点。</summary>
        public bool Boxy => Kind is "rect" or "ellipse";

        /// <summary>笔迹类（沿轨迹涂抹）：画笔与马赛克。几何存在 <see cref="Pen"/> 里。</summary>
        public bool IsStroke => Kind is "pen" or "mosaic";

        /// <summary>马赛克笔刷半径（DIP）：跟着粗细档位走，与画笔的"线宽"是一回事。</summary>
        public double BrushRadius => Math.Max(3, Thick * 2.5);

        public WRect Bounds
        {
            get
            {
                if (IsStroke && Pen is { Count: > 0 })
                {
                    double x1 = Pen.Min(p => p.X), y1 = Pen.Min(p => p.Y);
                    var r = new WRect(x1, y1, Pen.Max(p => p.X) - x1, Pen.Max(p => p.Y) - y1);
                    double pad = Kind == "mosaic" ? BrushRadius : Thick / 2;   // 笔刷是有粗细的，包围盒要算上
                    r.Inflate(pad, pad);
                    return r;
                }
                return new WRect(Math.Min(A.X, B.X), Math.Min(A.Y, B.Y), Math.Abs(B.X - A.X), Math.Abs(B.Y - A.Y));
            }
        }

        public void SetBounds(WRect r) { A = new WPoint(r.X, r.Y); B = new WPoint(r.Right, r.Bottom); }

        public void Offset(double dx, double dy)
        {
            A = new WPoint(A.X + dx, A.Y + dy);
            B = new WPoint(B.X + dx, B.Y + dy);
            if (Pen != null)
                for (int i = 0; i < Pen.Count; i++) Pen[i] = new WPoint(Pen[i].X + dx, Pen[i].Y + dy);
        }
    }

    private static readonly Color AnnotColor = Color.FromRgb(0xFF, 0x3B, 0x30);   // 标注默认红色（截图工具惯例）
    private static readonly double[] AnnotThicks = { 1.5, 2.5, 4 };               // 细 / 中 / 粗
    private static readonly Color[] AnnotPalette =
    {
        Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0x95, 0x00), Color.FromRgb(0xFF, 0xCC, 0x00),
        Color.FromRgb(0x34, 0xC7, 0x59), Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0xFF, 0xFF, 0xFF),
    };

    /// <summary>
    /// 箭头轮廓：尾细头宽的实心多边形（尾 → 杆 → 三角头 → 杆 → 尾），WPF 预览与 GDI 烧录共用同一份几何。
    /// 旧版是"一条线 + 两根头翼"，看起来像手绘草稿；成熟截图工具都是这种收尾渐宽的实心箭头。
    /// </summary>
    private static WPoint[] ArrowPolygon(WPoint a, WPoint b, double thick)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return Array.Empty<WPoint>();
        double ux = dx / len, uy = dy / len, nx = -uy, ny = ux;
        double headHalf = Math.Max(4, thick * 2.4);              // 箭头最宽处的一半
        double headLen = Math.Min(len * 0.55, headHalf * 2.0);   // 头部长度（短箭头按比例缩，不会只剩一个头）
        double bodyHalf = Math.Min(headHalf * 0.45, Math.Max(1.2, thick * 0.85));   // 杆与头交界处
        double tailHalf = Math.Max(0.6, thick * 0.3);            // 尾端最细处
        var c = new WPoint(b.X - ux * headLen, b.Y - uy * headLen);
        WPoint Off(WPoint o, double s) => new(o.X + nx * s, o.Y + ny * s);
        return new[] { Off(a, tailHalf), Off(c, bodyHalf), Off(c, headHalf), b, Off(c, -headHalf), Off(c, -bodyHalf), Off(a, -tailHalf) };
    }

    /// <summary>
    /// 覆盖层上的深色图标工具条（截图 / 编辑限制区域共用，保证两处一致）。
    /// 摆放规则见 <see cref="LayoutFor"/>：选区占满屏幕时会压进选区内部，不会被挤出屏幕外。
    /// </summary>
    private sealed class OverlayToolbar
    {
        private const double Gap = 8;
        public readonly Border Bar;
        private readonly StackPanel _row;

        public OverlayToolbar(Canvas canvas, bool visible = false)
        {
            _row = new StackPanel { Orientation = Orientation.Horizontal };
            Bar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x24, 0x24, 0x26)),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 4, 6, 4), Child = _row,
                Visibility = visible ? Visibility.Visible : Visibility.Collapsed,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black },
            };
            Panel.SetZIndex(Bar, 100);   // 标注/手柄是后加进画布的，不置顶会被盖住
            canvas.Children.Add(Bar);
        }

        public Button Add(string tip, UIElement glyph, Action? onClick = null, Panel? host = null, double width = 30)
        {
            var b = new Button
            {
                Content = glyph, Width = width, Height = 28, Margin = new Thickness(1, 0, 1, 0), ToolTip = tip,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, Cursor = Cursors.Hand,
                Template = FlatButtonTemplate(),
            };
            if (onClick != null) b.Click += (_, _) => onClick();
            (host ?? _row).Children.Add(b);
            return b;
        }

        public void Sep(Panel? host = null) => (host ?? _row).Children.Add(new Border { Width = 1, Margin = new Thickness(5, 4, 5, 4), Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)) });

        public bool HitTest(WPoint pInBar) => Bar.IsVisible && Bar.InputHitTest(pInBar) != null;

        // 刚从 Collapsed 变可见的那一帧 ActualWidth/Height 还是 0，直接按 0 高度摆位会把工具条推到屏幕外
        // （全屏选区时表现为"工具条在最下面看不见"）——拿不到实际尺寸就退回 DesiredSize。
        private (double w, double h) Size()
        {
            Bar.UpdateLayout();
            double w = Bar.ActualWidth, h = Bar.ActualHeight;
            if (w < 1 || h < 1)
            {
                Bar.Measure(new WSize(double.PositiveInfinity, double.PositiveInfinity));
                w = Bar.DesiredSize.Width; h = Bar.DesiredSize.Height;
            }
            return (w, h);
        }

        /// <summary>
        /// 贴着选区摆：优先下方，放不下翻到上方，再放不下（选区占满屏）就压进选区内部右下角。
        /// <paramref name="bounds"/> 是可摆放范围，必须传【选区所在那块屏的工作区】而不是整个画布：
        /// ① 跨屏画布上"选区下方"可能落到另一块屏甚至屏幕之间的空档里；
        /// ② 最大化窗口的下边缘正好压着任务栏，塞进那条带子会被任务栏（同为置顶窗口）盖住。
        /// </summary>
        public void LayoutFor(WRect sel, WRect bounds)
        {
            Bar.Visibility = Visibility.Visible;
            var (tw, th) = Size();
            double bx = Math.Clamp(sel.Right - tw, bounds.X, Math.Max(bounds.X, bounds.Right - tw));
            double by;
            if (sel.Bottom + Gap + th <= bounds.Bottom) by = sel.Bottom + Gap;                 // 选区下方
            else if (sel.Y - Gap - th >= bounds.Y) by = sel.Y - Gap - th;                      // 选区上方
            else by = Math.Clamp(sel.Bottom - th - Gap, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - th - Gap));   // 压进选区内部
            Canvas.SetLeft(Bar, bx); Canvas.SetTop(Bar, by);
        }

        /// <summary>挂在另一条工具条的正下方（样式行）；下面放不下就翻到它上面。左边缘对齐。</summary>
        public void LayoutUnder(OverlayToolbar main, WRect bounds)
        {
            Bar.Visibility = Visibility.Visible;
            var (tw, th) = Size();
            var (_, mh) = main.Size();
            double mx = Canvas.GetLeft(main.Bar), my = Canvas.GetTop(main.Bar);
            if (double.IsNaN(mx)) mx = bounds.X;
            if (double.IsNaN(my)) my = bounds.Y;
            double by = my + mh + 6;
            if (by + th > bounds.Bottom) by = Math.Max(bounds.Y, my - th - 6);
            Canvas.SetLeft(Bar, Math.Clamp(mx, bounds.X, Math.Max(bounds.X, bounds.Right - tw)));
            Canvas.SetTop(Bar, by);
        }

        /// <summary>没有选区时的落位：可摆放范围内底部居中。</summary>
        public void LayoutBottomCenter(WRect bounds)
        {
            Bar.Visibility = Visibility.Visible;
            var (tw, th) = Size();
            Canvas.SetLeft(Bar, Math.Max(bounds.X, bounds.X + (bounds.Width - tw) / 2));
            Canvas.SetTop(Bar, Math.Max(bounds.Y, bounds.Bottom - th - 40));
        }

        public void Hide() => Bar.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 画布(DIP)上某点所在那块屏的【工作区】矩形，同样换算到画布坐标系；点不在任何屏内则退回整个画布。
    /// 覆盖层铺满整个虚拟桌面，直接拿画布边界摆浮动条会摆到别的屏或任务栏底下，见 OverlayToolbar.LayoutFor。
    /// </summary>
    private static WRect WorkAreaOnCanvas(WPoint p, int ox, int oy, double r, double canvasW, double canvasH)
    {
        foreach (var m in Input.ScreenInfo.All())
        {
            double x = (m.Left - ox) / r, y = (m.Top - oy) / r, w = m.Width / r, h = m.Height / r;
            if (p.X < x || p.X > x + w || p.Y < y || p.Y > y + h) continue;
            double ww = (m.WorkRight - m.WorkLeft) / r, wh = (m.WorkBottom - m.WorkTop) / r;
            return ww > 1 && wh > 1 ? new WRect((m.WorkLeft - ox) / r, (m.WorkTop - oy) / r, ww, wh) : new WRect(x, y, w, h);
        }
        return new WRect(0, 0, canvasW, canvasH);
    }

    /// <summary>当前光标位置换算到覆盖层画布(DIP)坐标。</summary>
    private static WPoint CursorOnCanvas(int ox, int oy, double r)
    {
        var (cx, cy) = Input.ScreenInfo.CursorPos();
        return new WPoint((cx - ox) / r, (cy - oy) / r);
    }

    private (byte[] png, int vx, int vy, int w, int h)? CaptureTargetImage(Window dialog)
    {
        // 屏幕序号标签是常显置顶窗口，会被拍进冻结快照/目标图——截图期间先藏起来，截完若原本在显示则恢复。
        bool hadIds = _idScreensByDialog.TryGetValue(dialog, out var ids) && ids.Count > 0;
        HideIdScreens(dialog);

        var mainH = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dlgH = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
        SetWindowPos(dlgH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);
        SetWindowPos(mainH, HWND_BOTTOM, 0, 0, 0, 0, 0x13);
        System.Threading.Thread.Sleep(150);   // 等被下沉的窗口重绘完，别把自己拍进冻屏

        // 自动框选用的窗口矩形表：必须在【覆盖层显示之前】枚举——覆盖层一旦置顶，
        // WindowFromPoint 只会命中覆盖层自己。背景是冻结画面，窗口位置也就此固定，预先枚举正好对得上。
        var winRects = EnumWindowRects();

        var (ox, oy, vw, vh) = VirtualBounds();
        var snapshot = Services.ScreenMatch.CaptureRegion(ox, oy, vw, vh);
        var snapSrc = ToBitmapSource(snapshot);   // 冻结的 BitmapSource：马赛克实时预览直接从它取像素

        (int x, int y, int w, int h)? sel = null;
        var accent = (Brush)FindResource("Accent");
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Cursor = Cursors.Cross, Background = Brushes.Transparent,
        };
        var snapImg = new System.Windows.Controls.Image { Source = snapSrc, Stretch = Stretch.Fill };
        var dim = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)) };
        var canvas = new Canvas();
        // 自动框选的高亮框（悬停在某个窗口上时显示）
        var autoBox = new Rectangle { Stroke = accent, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x8A, 0x78, 0x60)), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var rubber = new Rectangle { Stroke = accent, StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x8A, 0x78, 0x60)), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var sizeLbl = new TextBlock { Foreground = Brushes.White, FontSize = 12, Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(6, 3, 6, 3), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        canvas.Children.Add(autoBox); canvas.Children.Add(rubber); canvas.Children.Add(sizeLbl);
        // 选区 8 向手柄（纯视觉，命中判定由 RectPicker 几何算）
        var handles = new Rectangle[8];
        for (int i = 0; i < 8; i++)
        {
            handles[i] = new Rectangle { Width = 8, Height = 8, Fill = accent, Stroke = Brushes.White, StrokeThickness = 1.5, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            canvas.Children.Add(handles[i]);
        }
        var hint = new TextBlock
        {
            Text = "移动到目标窗口上会自动框选 · 也可直接拖拽框选 · 方向键微调 · 回车完成 · Esc 取消",
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 28, 0, 0),
            Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), Padding = new Thickness(12, 6, 12, 6), IsHitTestVisible = false,
        };
        var root = new Grid();
        root.Children.Add(snapImg); root.Children.Add(dim); root.Children.Add(canvas); root.Children.Add(hint);
        overlay.Content = root;
        overlay.SourceInitialized += (_, _) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            SetWindowPos(h, HWND_TOPMOST, ox, oy, vw, vh, 0x0040);
        };
        overlay.Loaded += (_, _) => { overlay.Activate(); overlay.Focus(); };

        double PixPerDip() => vw / Math.Max(1.0, snapImg.ActualWidth);   // 快照像素 / DIP
        var pick = new RectPicker();
        var annots = new List<Annot>();
        var undoStack = new List<Action>();   // 每步一个回滚闭包（新增标注 / 改动标注 / 删除标注）
        string tool = "";              // "" = 选区模式；否则为标注工具名
        Annot? drawing = null;         // 正在画的标注
        Annot? selAnnot = null;        // 当前选中、可拖动/改大小的标注
        TextBox? textEditor = null;    // 文字工具的输入框

        // 选中标注的装饰（虚线包围盒 + 手柄）：与选区手柄分开，颜色也不同，避免看混。
        // 选中提示：矩形类不画虚线框（贴轮廓的手柄已经够了，多一圈外扩虚线反而显得图形变大了）；
        // 画笔/文字没有可拖的手柄，才用一圈【正好贴着包围盒】的虚线；箭头则用一条沿轴线的虚线。
        var selBox = new Rectangle
        {
            Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        var selLine = new Line
        {
            Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        Panel.SetZIndex(selBox, 90); Panel.SetZIndex(selLine, 90);
        canvas.Children.Add(selBox); canvas.Children.Add(selLine);
        var annotHandles = new Rectangle[8];
        for (int i = 0; i < 8; i++)
        {
            annotHandles[i] = new Rectangle
            {
                Width = 8, Height = 8, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), StrokeThickness = 1,
                Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            };
            Panel.SetZIndex(annotHandles[i], 90);
            canvas.Children.Add(annotHandles[i]);
        }

        // 标注样式状态（粗细档位 / 颜色）：笔刷光标与样式条都要用，声明在两者之前
        bool styleShown = false;                   // 样式行是否显示（握着工具 / 选中标注时）
        int thickIdx = 1;                          // 默认"中"
        var curColor = AnnotColor;
        var thickBtns = new List<Button>();
        var colorBtns = new List<Button>();

        // 样式条顶边的小三角，指向当前工具那颗按钮（对齐成熟截图工具）。用工具条同色，看起来像是从条上长出来的。
        var caret = new Polygon
        {
            Fill = new SolidColorBrush(Color.FromArgb(0xF2, 0x24, 0x24, 0x26)),
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        Panel.SetZIndex(caret, 101);
        canvas.Children.Add(caret);

        // 笔刷光标：马赛克/画笔时把系统光标藏掉，改画一个与实际笔刷等大的圆圈——
        // 十字光标看不出会糊多大一片，圆圈就是"所见即所涂"。
        var brushRing = new Ellipse
        {
            Stroke = Brushes.White, StrokeThickness = 1.2, Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        Panel.SetZIndex(brushRing, 95);
        canvas.Children.Add(brushRing);
        double BrushR() => tool == "mosaic" ? Math.Max(3, AnnotThicks[thickIdx] * 2.5) : Math.Max(2, AnnotThicks[thickIdx] / 2);
        void MoveBrushRing(WPoint p)
        {
            if (tool is not ("mosaic" or "pen")) { brushRing.Visibility = Visibility.Collapsed; return; }
            double r2 = BrushR();
            brushRing.Width = r2 * 2; brushRing.Height = r2 * 2;
            Canvas.SetLeft(brushRing, p.X - r2); Canvas.SetTop(brushRing, p.Y - r2);
            brushRing.Visibility = Visibility.Visible;
        }

        // ---- 工具条（选好区域后出现，跟随选区）----
        var toolbar = new OverlayToolbar(canvas);
        // 样式行是【独立的第二条】，挂在主工具条正下方（对齐成熟截图工具的排布），
        // 只在握着工具或选中标注时出现，平时不占地方。
        var styleBar = new OverlayToolbar(canvas);
        var toolBtns = new Dictionary<string, Button>();
        Brush SelBg() => new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        void SyncStyleBtns()
        {
            for (int i = 0; i < thickBtns.Count; i++) thickBtns[i].Background = i == thickIdx ? SelBg() : Brushes.Transparent;
            for (int i = 0; i < colorBtns.Count; i++) colorBtns[i].Background = AnnotPalette[i] == curColor ? SelBg() : Brushes.Transparent;
        }
        // 样式对【当前选中的标注】立即生效（并可撤销），没选中就只影响接下来要画的
        void ApplyStyle()
        {
            var a = selAnnot;
            if (a == null) { SyncStyleBtns(); return; }
            var oc = a.Color; double ot = a.Thick;
            a.Color = curColor; a.Thick = AnnotThicks[thickIdx];
            if (oc != a.Color || Math.Abs(ot - a.Thick) > 0.01)
                PushStyle(a, oc, ot);
            AddVisual(a); LayoutAnnotSel(); SyncStyleBtns();
        }
        void RefreshBrushRing()
        {
            if (brushRing.Visibility != Visibility.Visible) return;
            double r2 = BrushR();
            double cx = Canvas.GetLeft(brushRing) + brushRing.Width / 2, cy = Canvas.GetTop(brushRing) + brushRing.Height / 2;
            brushRing.Width = r2 * 2; brushRing.Height = r2 * 2;
            Canvas.SetLeft(brushRing, cx - r2); Canvas.SetTop(brushRing, cy - r2);
        }
        void SetTool(string t)
        {
            tool = tool == t ? "" : t;                       // 再点一次同一个工具＝取消，回到选区调整
            foreach (var kv in toolBtns)
                kv.Value.Background = kv.Key == tool ? new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0x3B, 0x30)) : Brushes.Transparent;
            // 画标注时用十字光标（笔形光标的"笔尖"落点看不准）；笔迹类改用圆圈笔刷光标
            bool brushy = tool is "mosaic" or "pen";
            overlay.Cursor = brushy ? Cursors.None : Cursors.Cross;
            if (!brushy) brushRing.Visibility = Visibility.Collapsed;
            CommitText();
            SyncStyle();
        }
        void SyncStyle()   // 样式区显隐：握着工具（马赛克除外）或选中了非马赛克标注时才有意义
        {
            styleShown = tool.Length > 0 || selAnnot != null;
            LayoutToolbar();
        }
        Button ToolBtn(string kind, string tip, UIElement glyph)
        {
            var b = toolbar.Add(tip, glyph);
            if (kind.Length > 0) { toolBtns[kind] = b; b.Click += (_, _) => SetTool(kind); }
            return b;
        }

        ToolBtn("rect", "矩形（会画进目标图）", GlyphRect());
        ToolBtn("ellipse", "椭圆（会画进目标图）", GlyphEllipse());
        ToolBtn("arrow", "箭头（会画进目标图）", GlyphArrow());
        ToolBtn("pen", "画笔（会画进目标图）", GlyphPen());
        ToolBtn("mosaic", "马赛克：像笔刷一样涂抹，粗细档位＝笔刷大小（会画进目标图）", GlyphMosaic());
        ToolBtn("text", "文字（会画进目标图）", GlyphText());

        toolbar.Sep();
        var undoBtn = ToolBtn("", "撤销上一步标注（Ctrl+Z）", GlyphUndo());
        toolbar.Sep();
        var cancelBtn = ToolBtn("", "取消（Esc）", GlyphCross());
        var okBtn = ToolBtn("", "完成（回车）", GlyphCheck());

        // —— 第二条：粗细三档 + 六色 ——
        for (int i = 0; i < AnnotThicks.Length; i++)
        {
            int idx = i;
            string tip = i == 0 ? "细" : i == 1 ? "中" : "粗";
            thickBtns.Add(styleBar.Add(tip + "（马赛克＝笔刷大小；也会应用到当前选中的标注）",
                GlyphDot(5 + i * 3, Brushes.White), () => { thickIdx = idx; ApplyStyle(); RefreshBrushRing(); }, null, 28));
        }
        styleBar.Sep();
        for (int i = 0; i < AnnotPalette.Length; i++)
        {
            var c = AnnotPalette[i];
            colorBtns.Add(styleBar.Add("颜色（也会应用到当前选中的标注）", GlyphDot(14, new SolidColorBrush(c)),
                () => { curColor = c; ApplyStyle(); }, null, 26));
        }
        SyncStyleBtns();

        // ---- 布局 ----
        void LayoutToolbar()
        {
            if (!pick.Has || pick.W < 2 || pick.H < 2) { toolbar.Hide(); styleBar.Hide(); return; }
            var selR = new WRect(pick.X, pick.Y, pick.W, pick.H);
            var mid = new WPoint(selR.X + selR.Width / 2, selR.Y + selR.Height / 2);
            var area = WorkAreaOnCanvas(mid, ox, oy, PixPerDip(), snapImg.ActualWidth, snapImg.ActualHeight);
            toolbar.LayoutFor(selR, area);
            if (styleShown) { styleBar.LayoutUnder(toolbar, area); LayoutCaret(); }
            else { styleBar.Hide(); caret.Visibility = Visibility.Collapsed; }
        }
        // 小三角：横向对准当前工具按钮的中心，纵向贴在样式条靠工具条的那一边（样式条翻到上方时三角朝下）
        void LayoutCaret()
        {
            string kind = tool.Length > 0 ? tool : (selAnnot?.Kind ?? "");
            if (!toolBtns.TryGetValue(kind, out var btn) || !btn.IsVisible || btn.ActualWidth < 1)
            { caret.Visibility = Visibility.Collapsed; return; }
            try
            {
                var c = btn.TransformToAncestor(canvas).Transform(new WPoint(btn.ActualWidth / 2, 0));
                double sTop = Canvas.GetTop(styleBar.Bar), tTop = Canvas.GetTop(toolbar.Bar);
                bool below = sTop > tTop;                    // 样式条在工具条下方 → 三角朝上
                const double w = 14, h = 7;
                caret.Points.Clear();
                if (below) { caret.Points.Add(new WPoint(0, h)); caret.Points.Add(new WPoint(w / 2, 0)); caret.Points.Add(new WPoint(w, h)); }
                else { caret.Points.Add(new WPoint(0, 0)); caret.Points.Add(new WPoint(w / 2, h)); caret.Points.Add(new WPoint(w, 0)); }
                double left = Math.Clamp(c.X - w / 2, Canvas.GetLeft(styleBar.Bar) + 4,
                                         Canvas.GetLeft(styleBar.Bar) + Math.Max(8, styleBar.Bar.ActualWidth) - w - 4);
                Canvas.SetLeft(caret, left);
                Canvas.SetTop(caret, below ? sTop - h + 0.5 : sTop + styleBar.Bar.ActualHeight - 0.5);
                caret.Visibility = Visibility.Visible;
            }
            catch { caret.Visibility = Visibility.Collapsed; }   // 布局还没就绪时不画
        }
        void LayoutAnnotSel()
        {
            selBox.Visibility = Visibility.Collapsed;
            selLine.Visibility = Visibility.Collapsed;
            if (selAnnot == null)
            {
                foreach (var h in annotHandles) h.Visibility = Visibility.Collapsed;
                return;
            }
            var b = selAnnot.Bounds;
            WPoint[] pts;
            if (selAnnot.Boxy)
            {
                selBox.Visibility = Visibility.Visible;              // 虚线【与轮廓重合】：不外扩，图形看起来不会变大
                Canvas.SetLeft(selBox, b.X); Canvas.SetTop(selBox, b.Y);
                selBox.Width = Math.Max(0, b.Width); selBox.Height = Math.Max(0, b.Height);
                // 手柄同样正好落在轮廓上
                pts = new[]
                {
                    new WPoint(b.X, b.Y), new WPoint(b.X + b.Width / 2, b.Y), new WPoint(b.Right, b.Y),
                    new WPoint(b.X, b.Y + b.Height / 2), new WPoint(b.Right, b.Y + b.Height / 2),
                    new WPoint(b.X, b.Bottom), new WPoint(b.X + b.Width / 2, b.Bottom), new WPoint(b.Right, b.Bottom),
                };
            }
            else if (selAnnot.Kind == "arrow")
            {
                selLine.X1 = selAnnot.A.X; selLine.Y1 = selAnnot.A.Y;
                selLine.X2 = selAnnot.B.X; selLine.Y2 = selAnnot.B.Y;
                selLine.Visibility = Visibility.Visible;             // 沿箭头本身的虚线，不是方框
                pts = new[] { selAnnot.A, selAnnot.B };
            }
            else
            {
                selBox.Visibility = Visibility.Visible;              // 画笔 / 文字：虚线贴着包围盒
                Canvas.SetLeft(selBox, b.X); Canvas.SetTop(selBox, b.Y);
                selBox.Width = Math.Max(0, b.Width); selBox.Height = Math.Max(0, b.Height);
                pts = Array.Empty<WPoint>();
            }
            for (int i = 0; i < annotHandles.Length; i++)
            {
                bool on = i < pts.Length;
                annotHandles[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (!on) continue;
                Canvas.SetLeft(annotHandles[i], pts[i].X - 4); Canvas.SetTop(annotHandles[i], pts[i].Y - 4);
            }
        }
        // 选中要重绘新旧两个标注——选中态是画在图形【自身描边】上的（加粗 + 白色光晕），不只是外面那圈虚线框。
        void SelectAnnot(Annot? a)
        {
            var old = selAnnot;
            selAnnot = a;
            // 只重绘仍在列表里的旧选中项：已被删除的不能再画回去（否则留下撤销也删不掉的残影）
            if (old != null && !ReferenceEquals(old, a) && annots.Contains(old)) AddVisual(old);
            if (a != null)
            {
                AddVisual(a);
                curColor = a.Color;   // 样式按钮跟着显示选中项当前的颜色/粗细
                int bi = 0;
                for (int i = 1; i < AnnotThicks.Length; i++)
                    if (Math.Abs(AnnotThicks[i] - a.Thick) < Math.Abs(AnnotThicks[bi] - a.Thick)) bi = i;
                thickIdx = bi;
            }
            LayoutAnnotSel();
            SyncStyleBtns();
            SyncStyle();
        }
        void LayoutSel()
        {
            pick.Clamp(snapImg.ActualWidth, snapImg.ActualHeight);
            var vis = pick.Has ? Visibility.Visible : Visibility.Collapsed;
            rubber.Visibility = vis; sizeLbl.Visibility = vis;
            Canvas.SetLeft(rubber, pick.X); Canvas.SetTop(rubber, pick.Y); rubber.Width = pick.W; rubber.Height = pick.H;
            double[] hx = { pick.X, pick.X + pick.W / 2, pick.X + pick.W, pick.X, pick.X + pick.W, pick.X, pick.X + pick.W / 2, pick.X + pick.W };
            double[] hy = { pick.Y, pick.Y, pick.Y, pick.Y + pick.H / 2, pick.Y + pick.H / 2, pick.Y + pick.H, pick.Y + pick.H, pick.Y + pick.H };
            for (int i = 0; i < 8; i++)
            {
                handles[i].Visibility = vis;
                Canvas.SetLeft(handles[i], hx[i] - 4); Canvas.SetTop(handles[i], hy[i] - 4);
            }
            double r = PixPerDip();
            sizeLbl.Text = $"{(int)Math.Round(pick.W * r)} × {(int)Math.Round(pick.H * r)}";
            Canvas.SetLeft(sizeLbl, pick.X); Canvas.SetTop(sizeLbl, Math.Max(0, pick.Y - 24));
            LayoutToolbar();
        }

        // ---- 文字工具：就地放一个输入框，失焦/回车即定稿 ----
        void CommitText()
        {
            if (textEditor == null) return;
            var t = textEditor.Text ?? "";
            var pos = new WPoint(Canvas.GetLeft(textEditor), Canvas.GetTop(textEditor));
            canvas.Children.Remove(textEditor);
            textEditor = null;
            if (t.Trim().Length == 0) return;
            var a = new Annot { Kind = "text", A = pos, B = pos, Text = t, Color = curColor, Thick = AnnotThicks[thickIdx] };
            AddVisual(a);
            annots.Add(a);
            PushAdd(a);
        }
        void StartText(WPoint p)
        {
            CommitText();
            textEditor = new TextBox
            {
                MinWidth = 120, FontSize = Math.Max(12, AnnotThicks[thickIdx] * 4), Foreground = new SolidColorBrush(curColor), CaretBrush = new SolidColorBrush(curColor),
                Background = new SolidColorBrush(Color.FromArgb(0x28, 0, 0, 0)), BorderBrush = new SolidColorBrush(curColor), BorderThickness = new Thickness(1),
                AcceptsReturn = false, Padding = new Thickness(2, 0, 2, 0),
            };
            Canvas.SetLeft(textEditor, p.X); Canvas.SetTop(textEditor, p.Y);
            canvas.Children.Add(textEditor);
            textEditor.Focus();
            textEditor.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) { ev.Handled = true; CommitText(); overlay.Focus(); } };
        }

        // ---- 标注的预览元素 ----
        void AddVisual(Annot a)
        {
            foreach (var v in a.Visuals) canvas.Children.Remove(v);
            a.Visuals.Clear();
            var brush = new SolidColorBrush(a.Color);
            // 选中态【不改图形本身的样子】：不加粗、不加白色光晕（那样看起来像描了一圈脏边）。
            // 选中反馈全交给贴着轮廓的白色小手柄（LayoutAnnotSel），与成熟截图工具一致。
            double x1 = Math.Min(a.A.X, a.B.X), y1 = Math.Min(a.A.Y, a.B.Y);
            double w = Math.Abs(a.B.X - a.A.X), h = Math.Abs(a.B.Y - a.A.Y);
            switch (a.Kind)
            {
                case "rect":
                    var rc = new Rectangle { Stroke = brush, StrokeThickness = a.Thick, Width = w, Height = h, IsHitTestVisible = false };
                    Canvas.SetLeft(rc, x1); Canvas.SetTop(rc, y1); a.Visuals.Add(rc);
                    break;
                case "ellipse":
                    var el = new Ellipse { Stroke = brush, StrokeThickness = a.Thick, Width = w, Height = h, IsHitTestVisible = false };
                    Canvas.SetLeft(el, x1); Canvas.SetTop(el, y1); a.Visuals.Add(el);
                    break;
                case "arrow":
                    var pts = ArrowPolygon(a.A, a.B, a.Thick);
                    if (pts.Length > 0)
                    {
                        var poly = new Polygon { Fill = brush, IsHitTestVisible = false };
                        foreach (var pt in pts) poly.Points.Add(pt);
                        a.Visuals.Add(poly);
                    }
                    break;
                case "pen":
                    if (a.Pen is { Count: > 1 })
                    {
                        var pl = new Polyline
                        {
                            Stroke = brush, StrokeThickness = a.Thick, StrokeLineJoin = PenLineJoin.Round,
                            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false,
                        };
                        foreach (var pt in a.Pen) pl.Points.Add(pt);
                        a.Visuals.Add(pl);
                    }
                    break;
                case "mosaic":
                    // 涂抹式马赛克：把笔迹外扩成一条"粗线形状"，用它裁剪一张整块像素化的图 →
                    // 效果就是沿着笔刷划过的地方打码。块尺寸与确认时 GDI 烧录用的是同一个，所见即所得。
                    if (a.Pen is { Count: > 0 })
                    {
                        var bb = a.Bounds;
                        var mo = MosaicPreview(snapSrc, bb.X, bb.Y, bb.Width, bb.Height, PixPerDip());
                        if (mo != null)
                        {
                            mo.Clip = StrokeGeometry(a.Pen, a.BrushRadius, -bb.X, -bb.Y);   // Clip 用元素自身坐标
                            Canvas.SetLeft(mo, bb.X); Canvas.SetTop(mo, bb.Y); a.Visuals.Add(mo);
                        }
                    }
                    break;
                case "text":
                    var tb = new TextBlock { Text = a.Text, Foreground = brush, FontSize = a.FontSize, IsHitTestVisible = false };
                    tb.Measure(new WSize(double.PositiveInfinity, double.PositiveInfinity));
                    a.B = new WPoint(a.A.X + tb.DesiredSize.Width + 4, a.A.Y + tb.DesiredSize.Height);   // 供选中/命中判定用的包围盒
                    Canvas.SetLeft(tb, a.A.X + 2); Canvas.SetTop(tb, a.A.Y); a.Visuals.Add(tb);
                    break;
            }
            foreach (var v in a.Visuals) canvas.Children.Add(v);
        }

        // ---- 撤销：每一步（新增 / 改动 / 删除）压一个回滚闭包 ----
        // 【删除必须先摘选中、且不能走 SelectAnnot】：SelectAnnot 会给"上一个选中项"重绘一遍高亮，
        // 而这时它已经被删了 —— 视觉元素又被加回画布，且它已不在 annots 里，再撤销也删不掉
        // （表现就是"撤销到底还剩几个图形赖着不走"）。
        void Detach(Annot a)
        {
            if (ReferenceEquals(selAnnot, a)) { selAnnot = null; LayoutAnnotSel(); }
            foreach (var v in a.Visuals) canvas.Children.Remove(v);
            a.Visuals.Clear();
        }
        void PushAdd(Annot a) => undoStack.Add(() =>
        {
            Detach(a);
            annots.Remove(a);
        });
        void PushGeom(Annot a, WPoint oa, WPoint ob, List<WPoint>? opn) => undoStack.Add(() =>
        {
            a.A = oa; a.B = ob; if (opn != null) a.Pen = opn;
            AddVisual(a);
            if (ReferenceEquals(selAnnot, a)) LayoutAnnotSel();
        });
        void PushStyle(Annot a, Color oc, double ot) => undoStack.Add(() =>
        {
            a.Color = oc; a.Thick = ot;
            AddVisual(a);
            if (ReferenceEquals(selAnnot, a)) LayoutAnnotSel();
        });
        void PushDelete(Annot a, int idx) => undoStack.Add(() =>
        {
            annots.Insert(Math.Min(idx, annots.Count), a);
            SelectAnnot(a);   // 内部会 AddVisual
        });
        void Undo()
        {
            CommitText();
            if (undoStack.Count == 0) return;
            var act = undoStack[^1];
            undoStack.RemoveAt(undoStack.Count - 1);
            act();
        }
        undoBtn.Click += (_, _) => Undo();

        // ---- 标注不得越出截图选区（越出的部分本来也会被裁掉，等于白画）----
        WRect SelRect() => new(pick.X, pick.Y, Math.Max(0, pick.W), Math.Max(0, pick.H));
        WPoint ClampPt(WPoint p)
        {
            if (!pick.Has) return p;
            var s2 = SelRect();
            return new WPoint(Math.Clamp(p.X, s2.X, s2.Right), Math.Clamp(p.Y, s2.Y, s2.Bottom));
        }
        // 整体移动时的位移夹取：先把包围盒挪进选区，再反推允许的位移
        (double dx, double dy) ClampOffset(WRect bounds, double dx, double dy)
        {
            if (!pick.Has) return (dx, dy);
            var s2 = SelRect();
            double nx = Math.Clamp(bounds.X + dx, s2.X, Math.Max(s2.X, s2.Right - bounds.Width));
            double ny = Math.Clamp(bounds.Y + dy, s2.Y, Math.Max(s2.Y, s2.Bottom - bounds.Height));
            return (nx - bounds.X, ny - bounds.Y);
        }
        // 改大小时逐边夹取（移动请用 ClampOffset，逐边夹会把图形压扁）
        WRect ClampRect(WRect r)
        {
            if (!pick.Has) return r;
            var s2 = SelRect();
            double l = Math.Clamp(r.X, s2.X, s2.Right), t = Math.Clamp(r.Y, s2.Y, s2.Bottom);
            double rr = Math.Clamp(r.Right, s2.X, s2.Right), bb2 = Math.Clamp(r.Bottom, s2.Y, s2.Bottom);
            return new WRect(l, t, Math.Max(0, rr - l), Math.Max(0, bb2 - t));
        }

        // ---- 标注命中判定 ----
        // 两段式，与常见画图工具一致：
        //   ① 没选中它时：**只认轮廓**——点边框才选中它，图形内部不响应（内部留给"接着画新图形"）；
        //   ② 选中之后：**内部＝整体移动、边/角＝缩放**（与选区框同一套语义，光标也一致）；点它以外的地方＝退出选中。
        // 于是"画新图形"与"编辑已有图形"永远互斥：有选中就不画，要画先点空白退出选中。
        const double HitTol = 7;
        var scratch = new RectPicker();
        // 返回 "new"（没命中）/ "select"（未选中态点到轮廓）/ "move" / 缩放抓取名 / 箭头端点 "a"|"b"
        string AnnotHit(Annot a, WPoint p)
        {
            bool sel = ReferenceEquals(a, selAnnot);
            if (a.Boxy)
            {
                var b = a.Bounds;
                if (!sel)
                {
                    bool onEdge = a.Kind == "ellipse"
                        ? Services.HitGeometry.NearEllipseOutline(b, p, HitTol)
                        : Services.HitGeometry.NearRectOutline(b, p, HitTol);
                    return onEdge ? "select" : "new";
                }
                scratch.X = b.X; scratch.Y = b.Y; scratch.W = b.Width; scratch.H = b.Height; scratch.Has = true;
                return scratch.HitTest(p);   // 角/边→缩放，内部→move，外面→new
            }
            if (a.Kind == "arrow")
            {
                if (sel && Math.Abs(p.X - a.A.X) <= HitTol && Math.Abs(p.Y - a.A.Y) <= HitTol) return "a";
                if (sel && Math.Abs(p.X - a.B.X) <= HitTol && Math.Abs(p.Y - a.B.Y) <= HitTol) return "b";
                if (Services.HitGeometry.DistToSegment(p, a.A, a.B) > HitTol) return "new";
                return sel ? "move" : "select";
            }
            // 画笔 / 马赛克 / 文字【不参与选中】：它们是"涂上去就完事"的笔迹，
            // 要改就撤销重画；参与选中只会在连续涂抹时误抓。
            return "new";
        }
        Annot? HitAnnot(WPoint p)
        {
            for (int i = annots.Count - 1; i >= 0; i--)     // 后画的在上面，先判它
                if (AnnotHit(annots[i], p) != "new") return annots[i];
            return null;
        }

        // ---- 标注的移动 / 改大小 ----
        Annot? editing = null;
        string editGrab = "";
        WPoint editDown = default, origA = default, origB = default;
        List<WPoint>? origPen = null;
        var boxPick = new RectPicker();
        bool BeginAnnotEdit(Annot a, WPoint p, string hit)
        {
            if (hit is "new" or "select") return false;
            editing = a; editGrab = hit; editDown = p;
            origA = a.A; origB = a.B; origPen = a.Pen?.ToList();
            if (a.Boxy)
            {
                var b = a.Bounds;
                boxPick.X = b.X; boxPick.Y = b.Y; boxPick.W = b.Width; boxPick.H = b.Height; boxPick.Has = true;
                boxPick.BeginWith(p, hit);   // 用调用方已判好的 hit，别让 RectPicker 自己再判一次
            }
            return true;
        }
        void DragAnnotEdit(WPoint p)
        {
            if (editing == null) return;
            double dx = p.X - editDown.X, dy = p.Y - editDown.Y;
            if (editing.Boxy)
            {
                boxPick.Drag(p, snapImg.ActualWidth, snapImg.ActualHeight);
                var nb = new WRect(boxPick.X, boxPick.Y, boxPick.W, boxPick.H);
                if (editGrab == "move")
                {
                    var (mx, my) = ClampOffset(new WRect(boxPick.X, boxPick.Y, boxPick.W, boxPick.H), 0, 0);
                    nb = new WRect(boxPick.X + mx, boxPick.Y + my, boxPick.W, boxPick.H);
                }
                else nb = ClampRect(nb);
                editing.SetBounds(nb);
            }
            else if (editing.Kind == "arrow" && editGrab == "a") editing.A = ClampPt(new WPoint(origA.X + dx, origA.Y + dy));
            else if (editing.Kind == "arrow" && editGrab == "b") editing.B = ClampPt(new WPoint(origB.X + dx, origB.Y + dy));
            else
            {
                editing.A = origA; editing.B = origB;
                if (origPen != null) editing.Pen = origPen.Select(q => new WPoint(q.X, q.Y)).ToList();
                var (ox2, oy2) = ClampOffset(editing.Bounds, dx, dy);
                editing.Offset(ox2, oy2);
            }
            AddVisual(editing);
            LayoutAnnotSel();
        }
        void EndAnnotEdit()
        {
            if (editing == null) return;
            if (boxPick.Dragging) boxPick.End();
            bool changed = editing.A != origA || editing.B != origB
                           || (origPen != null && editing.Pen != null && origPen.Count > 0 && editing.Pen[0] != origPen[0]);
            if (changed) PushGeom(editing, origA, origB, origPen);
            editing = null; editGrab = "";
        }

        // ---- 鼠标交互 ----
        // 自动识别窗口 vs 手动框选：按下时【两种意图都先保留】，松手时按位移判定——
        // 位移超过阈值＝用户在拖拽，说明主动放弃了自动识别，用拖出来的区域；
        // 几乎没动＝一次点击，才采用按下瞬间高亮的那个窗口。
        // （旧实现在 MouseDown 里直接采用窗口并 return，拖拽根本没机会开始。）
        bool autoPickable = true;          // 尚未确定选区：悬停高亮窗口、点击可采用
        WRect? autoCandidate = null;       // 当前高亮窗口的矩形（DIP）
        WPoint downPt = default;
        bool dragMoved = false;            // 本次按下是否已判定为拖拽
        const double DragSlop = 4;         // 超过这个位移（DIP）才算拖拽，容忍点击时的手抖

        overlay.MouseLeftButtonDown += (_, e) =>
        {
            var p = e.GetPosition(snapImg);
            if (toolbar.HitTest(e.GetPosition(toolbar.Bar)) || styleBar.HitTest(e.GetPosition(styleBar.Bar))) return;   // 点在工具条上交给按钮
            // ① 有选中项时，它独占整个图形范围：内部＝整体移动、边/角＝缩放
            if (selAnnot != null)
            {
                string hs = AnnotHit(selAnnot, p);
                if (hs != "new" && BeginAnnotEdit(selAnnot, p, hs)) { overlay.CaptureMouse(); return; }
                // 点到它以外 → 这一下【只负责退出选中】（顺手改选另一个图形）：不画新的、也不动截图选区。
                // 「有选中就不画」是硬规则，想画先点空白退出选中。
                SelectAnnot(HitAnnot(p));
                return;
            }
            // ② 没有选中项：点某个图形的轮廓＝选中它（只选中，不拖动）
            var hitA = HitAnnot(p);
            if (hitA != null) { SelectAnnot(hitA); return; }
            // ③ 握着工具：画新的（此时必然无选中，内部/外部都能画）
            if (tool.Length > 0)
            {
                if (tool == "text") { StartText(ClampPt(p)); return; }
                var cp = ClampPt(p);
                drawing = new Annot { Kind = tool, A = cp, B = cp, Color = curColor, Thick = AnnotThicks[thickIdx] };
                if (drawing.IsStroke) drawing.Pen = new List<WPoint> { p };   // 画笔 / 马赛克都是笔迹
                overlay.CaptureMouse();
                return;
            }
            // ④ 指针模式点空白：调整截图选区
            downPt = p; dragMoved = false;
            pick.Begin(p);                 // 先按"可能要拖拽"起手；若最终判定是点击，松手时用候选窗口覆盖
            overlay.CaptureMouse();
            if (!autoPickable) LayoutSel();   // 已有选区时立即反馈；自动识别阶段先不画 0 尺寸的框
        };
        overlay.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(snapImg);
            if (editing != null) { DragAnnotEdit(p); return; }
            if (drawing != null)
            {
                var cp2 = ClampPt(p);
                drawing.B = cp2;
                if (drawing.IsStroke) { drawing.Pen!.Add(cp2); MoveBrushRing(p); }
                AddVisual(drawing);
                return;
            }
            // 光标反馈（握着工具时也要给）：选中项内部→四向移动、边/角→对应缩放；其它图形的轮廓→手型（可点选）
            if (!pick.Dragging)
            {
                if (selAnnot != null)
                {
                    string hs = AnnotHit(selAnnot, p);
                    if (hs is "a" or "b") { overlay.Cursor = Cursors.SizeAll; return; }
                    if (hs != "new") { overlay.Cursor = RectPicker.CursorFor(hs); return; }
                }
                if (HitAnnot(p) != null) { overlay.Cursor = Cursors.Hand; return; }
                if (tool is "mosaic" or "pen") { overlay.Cursor = Cursors.None; MoveBrushRing(p); return; }
                if (tool.Length > 0) { overlay.Cursor = Cursors.Cross; return; }   // 画标注也用十字，落点看得准
            }
            if (tool.Length > 0) return;
            if (pick.Dragging)
            {
                if (!dragMoved && (Math.Abs(p.X - downPt.X) > DragSlop || Math.Abs(p.Y - downPt.Y) > DragSlop))
                {
                    dragMoved = true;                        // 开始拖拽＝放弃自动识别
                    autoCandidate = null;
                    autoBox.Visibility = Visibility.Collapsed;
                }
                if (dragMoved || !autoPickable) { pick.Drag(p, snapImg.ActualWidth, snapImg.ActualHeight); LayoutSel(); }
                return;
            }
            // 光标反馈：选中的标注上给调整手势，其它标注上给移动手势
            if (selAnnot != null)
            {
                string hs = AnnotHit(selAnnot, p);
                if (hs != "new") { overlay.Cursor = hs is "a" or "b" ? Cursors.SizeAll : RectPicker.CursorFor(hs); return; }
            }
            if (HitAnnot(p) != null) { overlay.Cursor = Cursors.SizeAll; return; }
            if (pick.Has) { overlay.Cursor = RectPicker.CursorFor(pick.HitTest(p)); return; }
            // 未框选：找光标下最上层的窗口，高亮它
            if (!autoPickable) return;
            double r = PixPerDip();
            int vx = ox + (int)Math.Round(p.X * r), vy = oy + (int)Math.Round(p.Y * r);
            var hitR = HitWindow(winRects, vx, vy);
            if (hitR is { } wr)
            {
                double bx = (wr.X - ox) / r, by = (wr.Y - oy) / r, bw = wr.Width / r, bh = wr.Height / r;
                autoCandidate = new WRect(bx, by, bw, bh);   // 记下候选，供"点击即采用"使用
                Canvas.SetLeft(autoBox, bx); Canvas.SetTop(autoBox, by);
                autoBox.Width = bw; autoBox.Height = bh;
                autoBox.Visibility = Visibility.Visible;
                sizeLbl.Visibility = Visibility.Visible;
                sizeLbl.Text = $"{(int)wr.Width} × {(int)wr.Height}";
                Canvas.SetLeft(sizeLbl, bx); Canvas.SetTop(sizeLbl, Math.Max(0, by - 24));
            }
            else { autoCandidate = null; autoBox.Visibility = Visibility.Collapsed; sizeLbl.Visibility = Visibility.Collapsed; }
        };
        overlay.MouseLeave += (_, _) => brushRing.Visibility = Visibility.Collapsed;
        overlay.MouseLeftButtonUp += (_, _) =>
        {
            if (editing != null) { EndAnnotEdit(); overlay.ReleaseMouseCapture(); return; }
            if (drawing != null)
            {
                overlay.ReleaseMouseCapture();
                bool tiny = !drawing.IsStroke && Math.Abs(drawing.B.X - drawing.A.X) < 3 && Math.Abs(drawing.B.Y - drawing.A.Y) < 3;
                if (tiny) foreach (var v in drawing.Visuals) canvas.Children.Remove(v);
                else
                {
                    annots.Add(drawing); PushAdd(drawing);
                    // 形状类画完即选中，方便立刻调位置/大小/样式；笔迹类（画笔·马赛克）不选中，好连着涂
                    var done = drawing;
                    if (!done.IsStroke) SelectAnnot(done);
                }
                drawing = null;
                return;
            }
            if (!pick.Dragging) return;
            pick.End();
            overlay.ReleaseMouseCapture();
            if (autoPickable)
            {
                if (!dragMoved && autoCandidate is { } wr)
                {
                    // 一次点击（没拖动）→ 采用高亮的那个窗口
                    pick.X = wr.X; pick.Y = wr.Y; pick.W = wr.Width; pick.H = wr.Height; pick.Has = true;
                }
                if (pick.W >= 2 && pick.H >= 2)
                {
                    autoPickable = false;                    // 选区已定，退出自动识别阶段
                    autoCandidate = null;
                    autoBox.Visibility = Visibility.Collapsed;
                    hint.Text = "拖动区域内移动 · 拖边/角调整 · 区域外拖拽重画 · 标注：点边框选中（内部拖动＝移动、边角＝改大小、Delete 删除、点空白退出选中）· 回车完成 · Esc 取消";
                }
                else pick.Has = false;                       // 空点一下（没命中窗口也没拖出区域）：维持可继续识别
            }
            LayoutSel();
        };

        // ---- 确认 / 取消 / 键盘 ----
        void Finish()
        {
            CommitText();
            if (pick.Has && pick.W >= 2 && pick.H >= 2)
            {
                double r = PixPerDip();
                sel = (ox + (int)Math.Round(pick.X * r), oy + (int)Math.Round(pick.Y * r),
                       (int)Math.Round(pick.W * r), (int)Math.Round(pick.H * r));
            }
            overlay.Close();
        }
        okBtn.Click += (_, _) => Finish();
        cancelBtn.Click += (_, _) => { sel = null; overlay.Close(); };
        overlay.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (textEditor != null) { canvas.Children.Remove(textEditor); textEditor = null; return; }
                if (selAnnot != null) { SelectAnnot(null); return; }   // 先取消标注选中
                if (tool.Length > 0) { SetTool(tool); return; }        // 再退出标注工具
                sel = null; overlay.Close(); return;
            }
            if (e.Key == Key.Enter) { e.Handled = true; Finish(); return; }
            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; Undo(); return; }
            if ((e.Key == Key.Delete || e.Key == Key.Back) && selAnnot != null && textEditor == null)
            {
                e.Handled = true;
                var a = selAnnot; int idx = annots.IndexOf(a);
                Detach(a);            // 同样不能走 SelectAnnot：它会把刚删掉的图形重绘回画布
                annots.Remove(a);
                if (idx >= 0) PushDelete(a, idx);
                return;
            }
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
            double step = ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1) / Math.Max(0.0001, PixPerDip());
            if (selAnnot != null)   // 选中标注时方向键微调的是标注，不是选区
            {
                var a = selAnnot;
                var oa = a.A; var ob = a.B; var opn = a.Pen?.ToList();
                var (kx, ky) = ClampOffset(a.Bounds, dx * step, dy * step);
                a.Offset(kx, ky);
                AddVisual(a); LayoutAnnotSel();
                PushGeom(a, oa, ob, opn);
                return;
            }
            if (!pick.Has || pick.Dragging || tool.Length > 0) return;
            pick.Nudge(dx * step, dy * step, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            LayoutSel();
        };
        overlay.ShowDialog();

        // ---- 裁剪 + 烧录标注 ----
        byte[]? png = null; int rx = 0, ry = 0, rw = 0, rh = 0;
        if (sel is { } s && s.w >= 4 && s.h >= 4)
        {
            rx = s.x; ry = s.y; rw = s.w; rh = s.h;
            try
            {
                using var crop = new System.Drawing.Bitmap(rw, rh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(crop))
                    g.DrawImage(snapshot, new System.Drawing.Rectangle(0, 0, rw, rh), new System.Drawing.Rectangle(rx - ox, ry - oy, rw, rh), System.Drawing.GraphicsUnit.Pixel);
                if (annots.Count > 0)
                {
                    double r = vw / Math.Max(1.0, snapImg.ActualWidth);
                    BurnAnnotations(crop, annots, r, (rx - ox) / r, (ry - oy) / r);
                }
                png = Services.ScreenMatch.ToPng(crop);
            }
            catch { png = null; }
        }
        snapshot.Dispose();
        Services.WindowActivator.ActivateHwnd(mainH);
        Services.WindowActivator.ActivateHwnd(dlgH);
        if (hadIds) ShowIdScreens(dialog);
        return png == null ? null : (png, rx, ry, rw, rh);
    }

    /// <summary>马赛克块尺寸（物理像素）——预览与烧录必须用同一个，否则所见非所得。</summary>
    private static int MosaicBlock(double pixPerDip) => Math.Max(4, (int)Math.Round(6 * pixPerDip));

    /// <summary>
    /// 马赛克的实时预览元素：裁 → 缩小（缩小时的重采样即块内均色）→ 最近邻放大回原尺寸。
    /// 走 WPF 的位图管线，拖拽时每帧重建也不卡；老实现只画一块灰色半透明矩形，看不到实际效果。
    /// </summary>
    /// <summary>把一串笔迹点外扩成"粗线形状"（圆头圆角），用作马赛克预览的裁剪形状。dx/dy 是平移到元素本地坐标的偏移。</summary>
    private static Geometry StrokeGeometry(List<WPoint> pts, double radius, double dx, double dy)
    {
        var fig = new PathFigure { StartPoint = new WPoint(pts[0].X + dx, pts[0].Y + dy), IsClosed = false };
        for (int i = 1; i < pts.Count; i++) fig.Segments.Add(new LineSegment(new WPoint(pts[i].X + dx, pts[i].Y + dy), true));
        var path = new PathGeometry(new[] { fig });
        var pen = new System.Windows.Media.Pen(Brushes.Black, radius * 2)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var outline = path.GetWidenedPathGeometry(pen);
        if (pts.Count == 1)   // 单击一下＝一个圆点，加宽退化路径得不到形状
            return new EllipseGeometry(new WPoint(pts[0].X + dx, pts[0].Y + dy), radius, radius);
        outline.Freeze();
        return outline;
    }

    private static System.Windows.Controls.Image? MosaicPreview(BitmapSource src, double x, double y, double w, double h, double r)
    {
        if (w < 1 || h < 1) return null;
        int px = (int)Math.Round(x * r), py = (int)Math.Round(y * r);
        int pw = (int)Math.Round(w * r), ph = (int)Math.Round(h * r);
        px = Math.Clamp(px, 0, Math.Max(0, src.PixelWidth - 1));
        py = Math.Clamp(py, 0, Math.Max(0, src.PixelHeight - 1));
        pw = Math.Clamp(pw, 1, src.PixelWidth - px);
        ph = Math.Clamp(ph, 1, src.PixelHeight - py);
        try
        {
            double scale = 1.0 / MosaicBlock(r);
            var small = new TransformedBitmap(new CroppedBitmap(src, new Int32Rect(px, py, pw, ph)), new ScaleTransform(scale, scale));
            var img = new System.Windows.Controls.Image { Source = small, Width = w, Height = h, Stretch = Stretch.Fill, IsHitTestVisible = false };
            // 注：Width/Height 用的是请求的 DIP 尺寸，裁剪形状也在同一坐标系里，两者对得上
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);   // 放大回去要保持块状，别插值糊掉
            return img;
        }
        catch { return null; }
    }

    // 把标注按 DIP→物理像素 换算后画到裁剪出的位图上（selX/selY 为选区左上角的 DIP 坐标）。
    private static void BurnAnnotations(System.Drawing.Bitmap bmp, List<Annot> annots, double r, double selX, double selY)
    {
        float S(double dip) => (float)(dip * r);
        System.Drawing.PointF P(WPoint p) => new((float)((p.X - selX) * r), (float)((p.Y - selY) * r));

        // 马赛克走 LockBits 直接改像素，与 Graphics 不能同时持有同一张位图 → 遇到马赛克先收起画笔，
        // 画完再开新的。按标注顺序处理，叠放次序与预览一致。
        System.Drawing.Graphics? g = null;
        System.Drawing.Graphics G()
        {
            if (g != null) return g;
            g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            return g;
        }
        try
        {
            foreach (var a in annots)
            {
                // 颜色/粗细逐个标注取（每个都可以不一样），别再用全局常量
                var color = System.Drawing.Color.FromArgb(a.Color.R, a.Color.G, a.Color.B);
                using var pen = new System.Drawing.Pen(color, Math.Max(1f, S(a.Thick)))
                { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
                using var brush = new System.Drawing.SolidBrush(color);
                var p1 = P(a.A); var p2 = P(a.B);
                float x = Math.Min(p1.X, p2.X), y = Math.Min(p1.Y, p2.Y);
                float w = Math.Abs(p2.X - p1.X), h = Math.Abs(p2.Y - p1.Y);
                switch (a.Kind)
                {
                    case "rect": if (w > 0 && h > 0) G().DrawRectangle(pen, x, y, w, h); break;
                    case "ellipse": if (w > 0 && h > 0) G().DrawEllipse(pen, x, y, w, h); break;
                    case "arrow":
                        var ap = ArrowPolygon(a.A, a.B, a.Thick);
                        if (ap.Length > 0)
                        {
                            var gp = new System.Drawing.PointF[ap.Length];
                            for (int i = 0; i < ap.Length; i++) gp[i] = P(ap[i]);
                            G().FillPolygon(brush, gp);      // 实心多边形，与预览同一份几何
                        }
                        break;
                    case "pen":
                        if (a.Pen is { Count: > 1 })
                        {
                            var pts = new System.Drawing.PointF[a.Pen.Count];
                            for (int i = 0; i < a.Pen.Count; i++) pts[i] = P(a.Pen[i]);
                            G().DrawLines(pen, pts);
                        }
                        break;
                    case "text":
                        using (var font = new System.Drawing.Font("Microsoft YaHei UI", Math.Max(6f, S(a.FontSize * 0.75)), System.Drawing.GraphicsUnit.Pixel))
                            G().DrawString(a.Text, font, brush, P(a.A));
                        break;
                    case "mosaic":
                        if (a.Pen is { Count: > 0 })
                        {
                            g?.Flush(); g?.Dispose(); g = null;   // LockBits 与 Graphics 不能同时持有同一张位图
                            var mp = new System.Drawing.PointF[a.Pen.Count];
                            for (int i = 0; i < a.Pen.Count; i++) mp[i] = P(a.Pen[i]);
                            MosaicStroke(bmp, mp, (float)(a.BrushRadius * r), MosaicBlock(r));
                        }
                        break;
                }
            }
        }
        finally { g?.Dispose(); }
    }

    /// <summary>
    /// 沿笔迹涂抹马赛克：块均色仍按【整个包围盒】的网格算（与预览的裁剪式渲染完全一致），
    /// 但只把笔刷圆覆盖到的像素写回去，于是效果就是"划到哪儿糊到哪儿"。
    /// </summary>
    private static void MosaicStroke(System.Drawing.Bitmap bmp, System.Drawing.PointF[] pts, float radius, int block)
    {
        if (pts.Length == 0 || radius < 1 || block < 1) return;
        float minX = pts[0].X, maxX = pts[0].X, minY = pts[0].Y, maxY = pts[0].Y;
        foreach (var p in pts)
        { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
        int x0 = Math.Max(0, (int)Math.Floor(minX - radius)), y0 = Math.Max(0, (int)Math.Floor(minY - radius));
        int x1 = Math.Min(bmp.Width, (int)Math.Ceiling(maxX + radius)), y1 = Math.Min(bmp.Height, (int)Math.Ceiling(maxY + radius));
        if (x1 <= x0 || y1 <= y0) return;
        int bw = x1 - x0, bh = y1 - y0;

        // 笔刷掩码：沿折线按半径的一半重采样后逐点盖圆（比逐像素求"到折线的距离"快得多）
        var mask = new bool[bw * bh];
        void Stamp(float cx, float cy)
        {
            int sx = Math.Max(x0, (int)(cx - radius)), ex = Math.Min(x1 - 1, (int)(cx + radius));
            int sy = Math.Max(y0, (int)(cy - radius)), ey = Math.Min(y1 - 1, (int)(cy + radius));
            float r2 = radius * radius;
            for (int yy = sy; yy <= ey; yy++)
                for (int xx = sx; xx <= ex; xx++)
                {
                    float ddx = xx + 0.5f - cx, ddy = yy + 0.5f - cy;
                    if (ddx * ddx + ddy * ddy <= r2) mask[(yy - y0) * bw + (xx - x0)] = true;
                }
        }
        Stamp(pts[0].X, pts[0].Y);
        for (int i = 1; i < pts.Length; i++)
        {
            float dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            int steps = Math.Max(1, (int)(len / Math.Max(1f, radius / 2)));
            for (int k = 1; k <= steps; k++) Stamp(pts[i - 1].X + dx * k / steps, pts[i - 1].Y + dy * k / steps);
        }

        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
            System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var buf = new byte[stride * bmp.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int by = y0; by < y1; by += block)
                for (int bx = x0; bx < x1; bx += block)
                {
                    int ex = Math.Min(bx + block, x1), ey = Math.Min(by + block, y1);
                    long sb = 0, sg = 0, sr = 0; int n = 0;
                    for (int yy = by; yy < ey; yy++)
                    {
                        int row = yy * stride;
                        for (int xx = bx; xx < ex; xx++)
                        { int i = row + xx * 4; sb += buf[i]; sg += buf[i + 1]; sr += buf[i + 2]; n++; }
                    }
                    if (n == 0) continue;
                    byte cb = (byte)(sb / n), cg = (byte)(sg / n), cr = (byte)(sr / n);
                    for (int yy = by; yy < ey; yy++)
                    {
                        int row = yy * stride;
                        for (int xx = bx; xx < ex; xx++)
                        {
                            if (!mask[(yy - y0) * bw + (xx - x0)]) continue;   // 只写笔刷划到的地方
                            int i = row + xx * 4; buf[i] = cb; buf[i + 1] = cg; buf[i + 2] = cr; buf[i + 3] = 0xFF;
                        }
                    }
                }
            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally { bmp.UnlockBits(data); }
    }

    // ---- 窗口矩形枚举（自动框选）----
    private static WRect? HitWindow(List<WRect> rects, int vx, int vy)
    {
        foreach (var r in rects)                      // 列表已按 Z 序（顶层在前）
            if (vx >= r.X && vx < r.Right && vy >= r.Y && vy < r.Bottom) return r;
        return null;
    }

    private static List<WRect> EnumWindowRects()
    {
        var list = new List<WRect>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            int ex = GetWindowLong(h, -20);            // GWL_EXSTYLE
            if ((ex & 0x00000080) != 0) return true;   // WS_EX_TOOLWINDOW：工具窗不参与
            // 优先用 DWM 的实际可见边界：GetWindowRect 在 Win10+ 会带上不可见的阴影边距，框出来偏大。
            RECT rc;
            if (DwmGetWindowAttribute(h, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out rc, Marshal.SizeOf<RECT>()) != 0
                && !GetWindowRect(h, out rc)) return true;
            int w = rc.Right - rc.Left, hh = rc.Bottom - rc.Top;
            if (w < 24 || hh < 24) return true;
            list.Add(new WRect(rc.Left, rc.Top, w, hh));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ---- 工具条图标（自绘，跨系统一致；与项目其它细线图标同风格）----
    private static ControlTemplate FlatButtonTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        var tr = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        tr.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))));
        t.Triggers.Add(tr);
        return t;
    }

    private static Brush Ice => Brushes.White;
    private static UIElement GlyphRect() => new Rectangle { Width = 13, Height = 11, Stroke = Ice, StrokeThickness = 1.4 };
    private static UIElement GlyphEllipse() => new Ellipse { Width = 13, Height = 11, Stroke = Ice, StrokeThickness = 1.4 };
    private static UIElement GlyphArrow() => new Path
    {
        Stroke = Ice, StrokeThickness = 1.4, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round,
        Data = Geometry.Parse("M 1,13 L 12,2 M 12,2 L 6,3 M 12,2 L 11,8"),
    };
    private static UIElement GlyphPen() => new Path
    {
        Stroke = Ice, StrokeThickness = 1.4, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        Data = Geometry.Parse("M 2,13 L 3.5,9 L 11,1.5 L 13,3.5 L 5,11 Z M 11,1.5 L 13,3.5"),
    };
    private static UIElement GlyphMosaic()
    {
        var g = new Grid { Width = 13, Height = 13 };
        var uni = new UniformGrid { Rows = 3, Columns = 3 };
        for (int i = 0; i < 9; i++)
            uni.Children.Add(new Rectangle { Fill = (i % 2 == 0) ? Ice : Brushes.Transparent, Margin = new Thickness(0.4) });
        g.Children.Add(uni);
        return g;
    }
    private static UIElement GlyphDot(double size, Brush fill) => new Ellipse { Width = size, Height = size, Fill = fill };
    private static UIElement GlyphText() => new TextBlock { Text = "T", Foreground = Ice, FontSize = 13, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Segoe UI") };
    private static UIElement GlyphUndo() => new Path
    {
        Stroke = Ice, StrokeThickness = 1.4, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        Data = Geometry.Parse("M 3,6 A 5,5 0 1 1 3,10 M 3,6 L 3,2 M 3,6 L 7,6"),
    };
    private static UIElement GlyphCross() => new Path
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)), StrokeThickness = 1.6, StrokeEndLineCap = PenLineCap.Round,
        Data = Geometry.Parse("M 2,2 L 12,12 M 12,2 L 2,12"),
    };
    private static UIElement GlyphCheck() => new Path
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84)), StrokeThickness = 1.9, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        Data = Geometry.Parse("M 2,7.5 L 6,11.5 L 13,3"),
    };

    // ---- P/Invoke ----
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
}
