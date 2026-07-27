using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using WRect = System.Windows.Rect;
using WPoint = System.Windows.Point;

namespace MacroPilot;

/// <summary>
/// 截图覆盖层（截取目标图片）：冻屏 → 框选 → 可选标注 → 裁剪出 PNG。
/// 交互对齐常见截图工具：悬停自动框住窗口、8 向调整选区、方向键微调、底部工具条。
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
        public readonly List<UIElement> Visuals = new();   // 预览用的 WPF 元素（撤销时从画布移除）
    }

    private static readonly Color AnnotColor = Color.FromRgb(0xFF, 0x3B, 0x30);   // 标注统一红色（截图工具惯例）

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

        (int x, int y, int w, int h)? sel = null;
        var accent = (Brush)FindResource("Accent");
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Cursor = Cursors.Cross, Background = Brushes.Transparent,
        };
        var snapImg = new System.Windows.Controls.Image { Source = ToBitmapSource(snapshot), Stretch = Stretch.Fill };
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
        string tool = "";              // "" = 选区模式；否则为标注工具名
        Annot? drawing = null;         // 正在画的标注
        TextBox? textEditor = null;    // 文字工具的输入框

        // ---- 工具条（选好区域后出现，跟随选区下方）----
        var toolbar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x24, 0x24, 0x26)),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 4, 6, 4),
            Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black },
        };
        var tbRow = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Child = tbRow;
        canvas.Children.Add(toolbar);

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
            var b = new Button
            {
                Content = glyph, Width = 30, Height = 28, Margin = new Thickness(1, 0, 1, 0), ToolTip = tip,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, Cursor = Cursors.Hand,
            };
            b.Template = FlatButtonTemplate();
            if (kind.Length > 0) { toolBtns[kind] = b; b.Click += (_, _) => SetTool(kind); }
            tbRow.Children.Add(b);
            return b;
        }
        void Sep() => tbRow.Children.Add(new Border { Width = 1, Margin = new Thickness(5, 4, 5, 4), Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)) });

        ToolBtn("rect", "矩形（会画进目标图）", GlyphRect());
        ToolBtn("ellipse", "椭圆（会画进目标图）", GlyphEllipse());
        ToolBtn("arrow", "箭头（会画进目标图）", GlyphArrow());
        ToolBtn("pen", "画笔（会画进目标图）", GlyphPen());
        ToolBtn("mosaic", "马赛克（会画进目标图）", GlyphMosaic());
        ToolBtn("text", "文字（会画进目标图）", GlyphText());
        Sep();
        var undoBtn = ToolBtn("", "撤销上一步标注", GlyphUndo());
        Sep();
        var cancelBtn = ToolBtn("", "取消（Esc）", GlyphCross());
        var okBtn = ToolBtn("", "完成（回车）", GlyphCheck());

        // ---- 布局 ----
        void LayoutToolbar()
        {
            if (!pick.Has || pick.W < 2 || pick.H < 2) { toolbar.Visibility = Visibility.Collapsed; return; }
            toolbar.Visibility = Visibility.Visible;
            toolbar.UpdateLayout();
            double tw = toolbar.ActualWidth, th = toolbar.ActualHeight;
            double x = Math.Min(pick.X + pick.W - tw, Math.Max(0, snapImg.ActualWidth - tw));
            double y = pick.Y + pick.H + 8;                                  // 默认贴选区下方
            if (y + th > snapImg.ActualHeight) y = Math.Max(0, pick.Y - th - 8);   // 下方放不下就翻到上方
            Canvas.SetLeft(toolbar, Math.Max(0, x)); Canvas.SetTop(toolbar, y);
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
            var a = new Annot { Kind = "text", A = pos, Text = t };
            AddVisual(a);
            annots.Add(a);
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
                    // 预览用半透明格子示意，真正的像素块化在确认时做（预览要实时块化太重）
                    var mo = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0x80, 0x80, 0x80)), Width = w, Height = h, IsHitTestVisible = false };
                    Canvas.SetLeft(mo, x1); Canvas.SetTop(mo, y1); a.Visuals.Add(mo);
                    break;
                case "text":
                    var tb = new TextBlock { Text = a.Text, Foreground = brush, FontSize = 16, IsHitTestVisible = false };
                    Canvas.SetLeft(tb, a.A.X + 2); Canvas.SetTop(tb, a.A.Y); a.Visuals.Add(tb);
                    break;
            }
            foreach (var v in a.Visuals) canvas.Children.Add(v);
        }

        undoBtn.Click += (_, _) =>
        {
            CommitText();
            if (annots.Count == 0) return;
            var last = annots[^1];
            foreach (var v in last.Visuals) canvas.Children.Remove(v);
            annots.RemoveAt(annots.Count - 1);
        };

        // ---- 鼠标交互 ----
        bool autoPickable = true;   // 还没框过选区时，悬停自动框窗口
        overlay.MouseLeftButtonDown += (_, e) =>
        {
            var p = e.GetPosition(snapImg);
            if (toolbar.IsVisible && toolbar.InputHitTest(e.GetPosition(toolbar)) != null) return;   // 点在工具条上交给按钮
            if (tool.Length > 0)
            {
                if (tool == "text") { StartText(p); return; }
                drawing = new Annot { Kind = tool, A = p, B = p };
                if (tool == "pen") drawing.Pen = new List<WPoint> { p };
                overlay.CaptureMouse();
                return;
            }
            // 选区模式：悬停在自动框上直接点，等于采用该窗口矩形
            if (autoPickable && autoBox.Visibility == Visibility.Visible && !pick.Has)
            {
                pick.X = Canvas.GetLeft(autoBox); pick.Y = Canvas.GetTop(autoBox);
                pick.W = autoBox.Width; pick.H = autoBox.Height; pick.Has = true;
                autoBox.Visibility = Visibility.Collapsed; autoPickable = false;
                LayoutSel();
                return;
            }
            autoPickable = false;
            autoBox.Visibility = Visibility.Collapsed;
            pick.Begin(p);
            overlay.CaptureMouse();
            LayoutSel();
        };
        overlay.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(snapImg);
            if (drawing != null)
            {
                drawing.B = p;
                if (drawing.Kind == "pen") drawing.Pen!.Add(p);
                AddVisual(drawing);
                return;
            }
            if (tool.Length > 0) return;
            if (pick.Dragging) { pick.Drag(p, snapImg.ActualWidth, snapImg.ActualHeight); LayoutSel(); return; }
            if (pick.Has) { overlay.Cursor = RectPicker.CursorFor(pick.HitTest(p)); return; }
            // 未框选：找光标下最上层的窗口，高亮它
            if (!autoPickable) return;
            double r = PixPerDip();
            int vx = ox + (int)Math.Round(p.X * r), vy = oy + (int)Math.Round(p.Y * r);
            var hitR = HitWindow(winRects, vx, vy);
            if (hitR is { } wr)
            {
                Canvas.SetLeft(autoBox, (wr.X - ox) / r); Canvas.SetTop(autoBox, (wr.Y - oy) / r);
                autoBox.Width = wr.Width / r; autoBox.Height = wr.Height / r;
                autoBox.Visibility = Visibility.Visible;
                sizeLbl.Visibility = Visibility.Visible;
                sizeLbl.Text = $"{(int)wr.Width} × {(int)wr.Height}";
                Canvas.SetLeft(sizeLbl, (wr.X - ox) / r); Canvas.SetTop(sizeLbl, Math.Max(0, (wr.Y - oy) / r - 24));
            }
            else { autoBox.Visibility = Visibility.Collapsed; sizeLbl.Visibility = Visibility.Collapsed; }
        };
        overlay.MouseLeftButtonUp += (_, _) =>
        {
            if (drawing != null)
            {
                overlay.ReleaseMouseCapture();
                bool tiny = drawing.Kind != "pen" && Math.Abs(drawing.B.X - drawing.A.X) < 3 && Math.Abs(drawing.B.Y - drawing.A.Y) < 3;
                if (tiny) foreach (var v in drawing.Visuals) canvas.Children.Remove(v);
                else annots.Add(drawing);
                drawing = null;
                return;
            }
            if (pick.Dragging) { pick.End(); overlay.ReleaseMouseCapture(); LayoutSel(); }
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
                if (tool.Length > 0) { SetTool(tool); return; }   // 先退出标注工具
                sel = null; overlay.Close(); return;
            }
            if (e.Key == Key.Enter) { e.Handled = true; Finish(); return; }
            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; undoBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); return; }
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
            if (!pick.Has || pick.Dragging || tool.Length > 0) return;
            double stepDip = ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1) / Math.Max(0.0001, PixPerDip());
            pick.Nudge(dx * stepDip, dy * stepDip, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
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

    // 把标注按 DIP→物理像素 换算后画到裁剪出的位图上（selX/selY 为选区左上角的 DIP 坐标）。
    private static void BurnAnnotations(System.Drawing.Bitmap bmp, List<Annot> annots, double r, double selX, double selY)
    {
        float S(double dip) => (float)(dip * r);
        System.Drawing.PointF P(WPoint p) => new((float)((p.X - selX) * r), (float)((p.Y - selY) * r));
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        var color = System.Drawing.Color.FromArgb(AnnotColor.R, AnnotColor.G, AnnotColor.B);
        using var pen = new System.Drawing.Pen(color, Math.Max(1f, S(2.5)))
        { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        using var brush = new System.Drawing.SolidBrush(color);

        foreach (var a in annots)
        {
            var p1 = P(a.A); var p2 = P(a.B);
            float x = Math.Min(p1.X, p2.X), y = Math.Min(p1.Y, p2.Y);
            float w = Math.Abs(p2.X - p1.X), h = Math.Abs(p2.Y - p1.Y);
            switch (a.Kind)
            {
                case "rect": if (w > 0 && h > 0) g.DrawRectangle(pen, x, y, w, h); break;
                case "ellipse": if (w > 0 && h > 0) g.DrawEllipse(pen, x, y, w, h); break;
                case "arrow":
                    foreach (var seg in ArrowSegments(a.A, a.B)) g.DrawLine(pen, P(seg.Item1), P(seg.Item2));
                    break;
                case "pen":
                    if (a.Pen is { Count: > 1 })
                    {
                        var pts = new System.Drawing.PointF[a.Pen.Count];
                        for (int i = 0; i < a.Pen.Count; i++) pts[i] = P(a.Pen[i]);
                        g.DrawLines(pen, pts);
                    }
                    break;
                case "text":
                    using (var font = new System.Drawing.Font("Microsoft YaHei UI", Math.Max(6f, S(12)), System.Drawing.GraphicsUnit.Pixel))
                        g.DrawString(a.Text, font, brush, P(a.A));
                    break;
                case "mosaic":
                    Mosaic(bmp, (int)x, (int)y, (int)w, (int)h, Math.Max(4, (int)S(6)));
                    break;
            }
        }
    }

    // 像素块化：每 block×block 取平均色填回去。
    private static void Mosaic(System.Drawing.Bitmap bmp, int x0, int y0, int w, int h, int block)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        int x1 = Math.Min(bmp.Width, x0 + w), y1 = Math.Min(bmp.Height, y0 + h);
        for (int by = y0; by < y1; by += block)
            for (int bx = x0; bx < x1; bx += block)
            {
                int ex = Math.Min(bx + block, x1), ey = Math.Min(by + block, y1);
                long sr = 0, sg = 0, sb = 0; int n = 0;
                for (int yy = by; yy < ey; yy++)
                    for (int xx = bx; xx < ex; xx++)
                    { var c = bmp.GetPixel(xx, yy); sr += c.R; sg += c.G; sb += c.B; n++; }
                if (n == 0) continue;
                var avg = System.Drawing.Color.FromArgb((int)(sr / n), (int)(sg / n), (int)(sb / n));
                for (int yy = by; yy < ey; yy++)
                    for (int xx = bx; xx < ex; xx++) bmp.SetPixel(xx, yy, avg);
            }
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
