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
        public readonly List<UIElement> Visuals = new();   // 预览用的 WPF 元素（撤销/重画时从画布移除）

        /// <summary>矩形语义的标注（A/B 就是对角）：可用 8 向手柄调整大小。其余只能整体移动或拖端点。</summary>
        public bool Boxy => Kind is "rect" or "ellipse" or "mosaic";

        public WRect Bounds
        {
            get
            {
                if (Kind == "pen" && Pen is { Count: > 0 })
                {
                    double x1 = Pen.Min(p => p.X), y1 = Pen.Min(p => p.Y);
                    return new WRect(x1, y1, Pen.Max(p => p.X) - x1, Pen.Max(p => p.Y) - y1);
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

    private static readonly Color AnnotColor = Color.FromRgb(0xFF, 0x3B, 0x30);   // 标注统一红色（截图工具惯例）

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

        public Button Add(string tip, UIElement glyph, Action? onClick = null)
        {
            var b = new Button
            {
                Content = glyph, Width = 30, Height = 28, Margin = new Thickness(1, 0, 1, 0), ToolTip = tip,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, Cursor = Cursors.Hand,
                Template = FlatButtonTemplate(),
            };
            if (onClick != null) b.Click += (_, _) => onClick();
            _row.Children.Add(b);
            return b;
        }

        public void Sep() => _row.Children.Add(new Border { Width = 1, Margin = new Thickness(5, 4, 5, 4), Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)) });

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
        var selBox = new Rectangle
        {
            Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        Panel.SetZIndex(selBox, 90);
        canvas.Children.Add(selBox);
        var annotHandles = new Rectangle[8];
        for (int i = 0; i < 8; i++)
        {
            annotHandles[i] = new Rectangle
            {
                Width = 8, Height = 8, Fill = Brushes.White, Stroke = new SolidColorBrush(AnnotColor), StrokeThickness = 1.5,
                Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            };
            Panel.SetZIndex(annotHandles[i], 90);
            canvas.Children.Add(annotHandles[i]);
        }

        // ---- 工具条（选好区域后出现，跟随选区）----
        var toolbar = new OverlayToolbar(canvas);
        var toolBtns = new Dictionary<string, Button>();
        void SetTool(string t)
        {
            tool = tool == t ? "" : t;                       // 再点一次同一个工具＝取消，回到选区调整
            foreach (var kv in toolBtns)
                kv.Value.Background = kv.Key == tool ? new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0x3B, 0x30)) : Brushes.Transparent;
            overlay.Cursor = tool.Length == 0 ? Cursors.Cross : Cursors.Pen;
            CommitText();
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
        ToolBtn("mosaic", "马赛克（会画进目标图）", GlyphMosaic());
        ToolBtn("text", "文字（会画进目标图）", GlyphText());
        toolbar.Sep();
        var undoBtn = ToolBtn("", "撤销上一步标注（Ctrl+Z）", GlyphUndo());
        toolbar.Sep();
        var cancelBtn = ToolBtn("", "取消（Esc）", GlyphCross());
        var okBtn = ToolBtn("", "完成（回车）", GlyphCheck());

        // ---- 布局 ----
        void LayoutToolbar()
        {
            if (!pick.Has || pick.W < 2 || pick.H < 2) { toolbar.Hide(); return; }
            var selR = new WRect(pick.X, pick.Y, pick.W, pick.H);
            var mid = new WPoint(selR.X + selR.Width / 2, selR.Y + selR.Height / 2);
            toolbar.LayoutFor(selR, WorkAreaOnCanvas(mid, ox, oy, PixPerDip(), snapImg.ActualWidth, snapImg.ActualHeight));
        }
        void LayoutAnnotSel()
        {
            if (selAnnot == null)
            {
                selBox.Visibility = Visibility.Collapsed;
                foreach (var h in annotHandles) h.Visibility = Visibility.Collapsed;
                return;
            }
            var b = selAnnot.Bounds;
            selBox.Visibility = Visibility.Visible;
            Canvas.SetLeft(selBox, b.X - 3); Canvas.SetTop(selBox, b.Y - 3);
            selBox.Width = Math.Max(0, b.Width + 6); selBox.Height = Math.Max(0, b.Height + 6);
            // 矩形语义 → 8 向手柄；箭头 → 两个端点；画笔/文字 → 只能整体移动，不给手柄
            WPoint[] pts;
            if (selAnnot.Boxy)
                pts = new[]
                {
                    new WPoint(b.X, b.Y), new WPoint(b.X + b.Width / 2, b.Y), new WPoint(b.Right, b.Y),
                    new WPoint(b.X, b.Y + b.Height / 2), new WPoint(b.Right, b.Y + b.Height / 2),
                    new WPoint(b.X, b.Bottom), new WPoint(b.X + b.Width / 2, b.Bottom), new WPoint(b.Right, b.Bottom),
                };
            else if (selAnnot.Kind == "arrow") pts = new[] { selAnnot.A, selAnnot.B };
            else pts = Array.Empty<WPoint>();
            for (int i = 0; i < annotHandles.Length; i++)
            {
                bool on = i < pts.Length;
                annotHandles[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (!on) continue;
                Canvas.SetLeft(annotHandles[i], pts[i].X - 4); Canvas.SetTop(annotHandles[i], pts[i].Y - 4);
            }
        }
        void SelectAnnot(Annot? a) { selAnnot = a; LayoutAnnotSel(); }
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
            var a = new Annot { Kind = "text", A = pos, B = pos, Text = t };
            AddVisual(a);
            annots.Add(a);
            PushAdd(a);
            SelectAnnot(a);
        }
        void StartText(WPoint p)
        {
            CommitText();
            textEditor = new TextBox
            {
                MinWidth = 120, FontSize = 16, Foreground = new SolidColorBrush(AnnotColor), CaretBrush = new SolidColorBrush(AnnotColor),
                Background = new SolidColorBrush(Color.FromArgb(0x28, 0, 0, 0)), BorderBrush = new SolidColorBrush(AnnotColor), BorderThickness = new Thickness(1),
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
            var brush = new SolidColorBrush(AnnotColor);
            double x1 = Math.Min(a.A.X, a.B.X), y1 = Math.Min(a.A.Y, a.B.Y);
            double w = Math.Abs(a.B.X - a.A.X), h = Math.Abs(a.B.Y - a.A.Y);
            switch (a.Kind)
            {
                case "rect":
                    var rc = new Rectangle { Stroke = brush, StrokeThickness = 2, Width = w, Height = h, IsHitTestVisible = false };
                    Canvas.SetLeft(rc, x1); Canvas.SetTop(rc, y1); a.Visuals.Add(rc);
                    break;
                case "ellipse":
                    var el = new Ellipse { Stroke = brush, StrokeThickness = 2, Width = w, Height = h, IsHitTestVisible = false };
                    Canvas.SetLeft(el, x1); Canvas.SetTop(el, y1); a.Visuals.Add(el);
                    break;
                case "arrow":
                    foreach (var seg in ArrowSegments(a.A, a.B))
                        a.Visuals.Add(new Line { X1 = seg.Item1.X, Y1 = seg.Item1.Y, X2 = seg.Item2.X, Y2 = seg.Item2.Y, Stroke = brush, StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false });
                    break;
                case "pen":
                    if (a.Pen is { Count: > 1 })
                    {
                        var pl = new Polyline { Stroke = brush, StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
                        foreach (var pt in a.Pen) pl.Points.Add(pt);
                        a.Visuals.Add(pl);
                    }
                    break;
                case "mosaic":
                    // 实时块化预览：从冻结快照里裁出该区域 → 按块尺寸缩小（缩小即块内均色）→ 最近邻放大回原尺寸，
                    // 与确认时 GDI 烧录用的是同一个块尺寸，所见即所得。
                    var mo = MosaicPreview(snapSrc, x1, y1, w, h, PixPerDip());
                    if (mo != null) { Canvas.SetLeft(mo, x1); Canvas.SetTop(mo, y1); a.Visuals.Add(mo); }
                    break;
                case "text":
                    var tb = new TextBlock { Text = a.Text, Foreground = brush, FontSize = 16, IsHitTestVisible = false };
                    tb.Measure(new WSize(double.PositiveInfinity, double.PositiveInfinity));
                    a.B = new WPoint(a.A.X + tb.DesiredSize.Width + 4, a.A.Y + tb.DesiredSize.Height);   // 供选中/命中判定用的包围盒
                    Canvas.SetLeft(tb, a.A.X + 2); Canvas.SetTop(tb, a.A.Y); a.Visuals.Add(tb);
                    break;
            }
            foreach (var v in a.Visuals) canvas.Children.Add(v);
        }

        // ---- 撤销：每一步（新增 / 改动 / 删除）压一个回滚闭包 ----
        void PushAdd(Annot a) => undoStack.Add(() =>
        {
            foreach (var v in a.Visuals) canvas.Children.Remove(v);
            annots.Remove(a);
            if (selAnnot == a) SelectAnnot(null);
        });
        void PushGeom(Annot a, WPoint oa, WPoint ob, List<WPoint>? opn) => undoStack.Add(() =>
        {
            a.A = oa; a.B = ob; if (opn != null) a.Pen = opn;
            AddVisual(a);
            if (selAnnot == a) LayoutAnnotSel();
        });
        void PushDelete(Annot a, int idx) => undoStack.Add(() =>
        {
            annots.Insert(Math.Min(idx, annots.Count), a);
            AddVisual(a);
            SelectAnnot(a);
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

        // ---- 标注命中判定 ----
        var scratch = new RectPicker();
        string BoxHit(WRect b, WPoint p) { scratch.X = b.X; scratch.Y = b.Y; scratch.W = b.Width; scratch.H = b.Height; scratch.Has = true; return scratch.HitTest(p); }
        static double DistToSeg(WPoint p, WPoint a, WPoint b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 <= 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
            double qx = a.X + t * dx, qy = a.Y + t * dy;
            return Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
        }
        // 返回 "new"（没命中）/ "move" / 矩形手柄名 / 箭头端点 "a"|"b"
        string AnnotHit(Annot a, WPoint p)
        {
            const double Near = 8;
            if (a.Boxy) return BoxHit(a.Bounds, p);
            if (a.Kind == "arrow")
            {
                if (Math.Abs(p.X - a.A.X) <= Near && Math.Abs(p.Y - a.A.Y) <= Near) return "a";
                if (Math.Abs(p.X - a.B.X) <= Near && Math.Abs(p.Y - a.B.Y) <= Near) return "b";
                return DistToSeg(p, a.A, a.B) <= 6 ? "move" : "new";
            }
            if (a.Kind == "pen")
            {
                if (a.Pen is { Count: > 0 })
                    for (int i = 1; i < a.Pen.Count; i++)
                        if (DistToSeg(p, a.Pen[i - 1], a.Pen[i]) <= 6) return "move";
                return "new";
            }
            var bb = a.Bounds;   // text
            return p.X >= bb.X - 2 && p.X <= bb.Right + 2 && p.Y >= bb.Y - 2 && p.Y <= bb.Bottom + 2 ? "move" : "new";
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
        bool BeginAnnotEdit(Annot a, WPoint p)
        {
            string hit = AnnotHit(a, p);
            if (hit == "new") return false;
            editing = a; editGrab = hit; editDown = p;
            origA = a.A; origB = a.B; origPen = a.Pen?.ToList();
            if (a.Boxy)
            {
                var b = a.Bounds;
                boxPick.X = b.X; boxPick.Y = b.Y; boxPick.W = b.Width; boxPick.H = b.Height; boxPick.Has = true;
                boxPick.Begin(p);
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
                editing.SetBounds(new WRect(boxPick.X, boxPick.Y, boxPick.W, boxPick.H));
            }
            else if (editing.Kind == "arrow" && editGrab == "a") editing.A = new WPoint(origA.X + dx, origA.Y + dy);
            else if (editing.Kind == "arrow" && editGrab == "b") editing.B = new WPoint(origB.X + dx, origB.Y + dy);
            else
            {
                editing.A = origA; editing.B = origB;
                if (origPen != null) editing.Pen = origPen.Select(q => new WPoint(q.X, q.Y)).ToList();
                editing.Offset(dx, dy);
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
            if (toolbar.HitTest(e.GetPosition(toolbar.Bar))) return;   // 点在工具条上交给按钮
            // 已选中的标注优先接管：画完立刻就能拖动/改大小，不用先切回指针模式。
            // 但手上还握着绘制工具时【只让手柄接管】——否则在已画的图形里再画一个就变成了拖走它。
            if (selAnnot != null)
            {
                string hs = AnnotHit(selAnnot, p);
                if (hs != "new" && !(tool.Length > 0 && hs == "move") && BeginAnnotEdit(selAnnot, p))
                { overlay.CaptureMouse(); return; }
            }
            if (tool.Length > 0)
            {
                SelectAnnot(null);
                if (tool == "text") { StartText(p); return; }
                drawing = new Annot { Kind = tool, A = p, B = p };
                if (tool == "pen") drawing.Pen = new List<WPoint> { p };
                overlay.CaptureMouse();
                return;
            }
            // 指针模式：点中任何一个标注即选中并开始拖动，点空白才回到选区调整
            var hitA = HitAnnot(p);
            if (hitA != null) { SelectAnnot(hitA); BeginAnnotEdit(hitA, p); overlay.CaptureMouse(); return; }
            SelectAnnot(null);
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
                drawing.B = p;
                if (drawing.Kind == "pen") drawing.Pen!.Add(p);
                AddVisual(drawing);
                return;
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
        overlay.MouseLeftButtonUp += (_, _) =>
        {
            if (editing != null) { EndAnnotEdit(); overlay.ReleaseMouseCapture(); return; }
            if (drawing != null)
            {
                overlay.ReleaseMouseCapture();
                bool tiny = drawing.Kind != "pen" && Math.Abs(drawing.B.X - drawing.A.X) < 3 && Math.Abs(drawing.B.Y - drawing.A.Y) < 3;
                if (tiny) foreach (var v in drawing.Visuals) canvas.Children.Remove(v);
                else { annots.Add(drawing); PushAdd(drawing); SelectAnnot(drawing); }   // 画完即选中，可继续调整
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
                    hint.Text = "拖动区域内移动 · 拖边/角调整 · 区域外拖拽重画 · 标注画完可再拖动/改大小（Delete 删除）· 方向键微调 · 回车完成 · Esc 取消";
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
                foreach (var v in a.Visuals) canvas.Children.Remove(v);
                annots.Remove(a); SelectAnnot(null);
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
                a.Offset(dx * step, dy * step);
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
    private static UIElement? MosaicPreview(BitmapSource src, double x, double y, double w, double h, double r)
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
        var color = System.Drawing.Color.FromArgb(AnnotColor.R, AnnotColor.G, AnnotColor.B);
        using var pen = new System.Drawing.Pen(color, Math.Max(1f, S(2.5)))
        { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        using var brush = new System.Drawing.SolidBrush(color);

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
                var p1 = P(a.A); var p2 = P(a.B);
                float x = Math.Min(p1.X, p2.X), y = Math.Min(p1.Y, p2.Y);
                float w = Math.Abs(p2.X - p1.X), h = Math.Abs(p2.Y - p1.Y);
                switch (a.Kind)
                {
                    case "rect": if (w > 0 && h > 0) G().DrawRectangle(pen, x, y, w, h); break;
                    case "ellipse": if (w > 0 && h > 0) G().DrawEllipse(pen, x, y, w, h); break;
                    case "arrow":
                        foreach (var seg in ArrowSegments(a.A, a.B)) G().DrawLine(pen, P(seg.Item1), P(seg.Item2));
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
                        using (var font = new System.Drawing.Font("Microsoft YaHei UI", Math.Max(6f, S(12)), System.Drawing.GraphicsUnit.Pixel))
                            G().DrawString(a.Text, font, brush, P(a.A));
                        break;
                    case "mosaic":
                        g?.Flush(); g?.Dispose(); g = null;
                        Mosaic(bmp, (int)x, (int)y, (int)w, (int)h, MosaicBlock(r));
                        break;
                }
            }
        }
        finally { g?.Dispose(); }
    }

    // 像素块化：每 block×block 取平均色填回去。走 LockBits——GetPixel/SetPixel 在大区域上要几秒（每像素两次 GDI+ 调用）。
    private static void Mosaic(System.Drawing.Bitmap bmp, int x0, int y0, int w, int h, int block)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        int x1 = Math.Min(bmp.Width, x0 + w), y1 = Math.Min(bmp.Height, y0 + h);
        if (x1 <= x0 || y1 <= y0 || block < 1) return;
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
                        { int i = row + xx * 4; buf[i] = cb; buf[i + 1] = cg; buf[i + 2] = cr; buf[i + 3] = 0xFF; }
                    }
                }
            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally { bmp.UnlockBits(data); }
    }

    // 箭头 = 主干 + 两条头翼，返回若干线段（WPF 预览与 GDI 烧录共用同一份几何）。
    private static List<(WPoint, WPoint)> ArrowSegments(WPoint a, WPoint b)
    {
        var segs = new List<(WPoint, WPoint)> { (a, b) };
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return segs;
        double ux = dx / len, uy = dy / len;
        double head = Math.Min(16, len * 0.35);
        const double ang = 0.45;   // 头翼张角（弧度）
        double c = Math.Cos(ang), s = Math.Sin(ang);
        segs.Add((b, new WPoint(b.X - head * (ux * c - uy * s), b.Y - head * (uy * c + ux * s))));
        segs.Add((b, new WPoint(b.X - head * (ux * c + uy * s), b.Y - head * (uy * c - ux * s))));
        return segs;
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
