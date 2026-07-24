using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Button = System.Windows.Controls.Button;

namespace MacroPilot;

public partial class MainWindow
{
    private RunHud? _hud;

    // 运行开始时按配置显示 HUD；复用运行器的 Pause/Resume/Stop。
    private void ShowHud(string planName)
    {
        if (!_doc.ShowRunHud) return;
        if (_hud == null)
        {
            _hud = new RunHud(this, () => _runner?.Pause(), () => _runner?.Resume(), () => _runner?.Stop(), HideHud, _doc.HudOpacity);
            Services.WindowMemory.Attach(_hud, "Hud");   // 记住位置（只挂一次，避免累积 Closing 处理器）
            if (!Services.WindowMemory.WasRestored(_hud))
            {
                var wa = SystemParameters.WorkArea;      // 首次默认右上角
                _hud.Left = wa.Right - 340; _hud.Top = wa.Top + 16;
            }
        }
        _hud.SetPlan(planName); _hud.SetLoop(""); _hud.SetAction(0, "准备中…"); _hud.SetStatus("Running", "");
        _hud.Show();
    }
    private void HideHud() { try { _hud?.Hide(); } catch { } }
    private void CloseHud() { try { _hud?.Close(); } catch { } _hud = null; }

    private void ShowHud_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _doc.ShowRunHud = ShowHudCheck.IsChecked == true;
        PersistSettings();
        if (!_doc.ShowRunHud) HideHud();
        else if (_runner is { IsRunning: true }) ShowHud(_runDisplayName);
    }

    private void HudOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        int pct = (int)System.Math.Round(e.NewValue);   // 滑块单位是百分比 10..100
        _doc.HudOpacity = System.Math.Clamp(pct / 100.0, 0.1, 1.0);
        HudOpacityValue.Text = pct + "%";
        _hud?.SetOpacity(_doc.HudOpacity);
        PersistSettings();
    }
}

/// <summary>
/// 运行悬浮 HUD：运行期间置顶显示当前动作 / 进度 / 循环 / 热键提示，并带暂停·继续·停止按钮。
/// 本体运行时下沉到底，长方案没有 HUD 就是黑盒——这个浮条让状态随时可见、也能就地控制。
/// 不抢焦点（WS_EX_NOACTIVATE），可拖动，位置由 WindowMemory 记住。
/// </summary>
public sealed class RunHud : Window
{
    private readonly System.Windows.Shapes.Ellipse _dot;
    private readonly TextBlock _plan, _action, _pct, _loop;
    private readonly ProgressBar _bar;
    private readonly Button _pause, _resume, _stop;

    private double _baseOpacity = 0.5;
    private readonly System.Windows.Threading.DispatcherTimer _topmostTimer;

    private readonly Window _owner;

    public RunHud(Window owner, Action onPause, Action onResume, Action onStop, Action onClose, double opacity)
    {
        _owner = owner;   // 只用于取主题画刷；【不】设 Window.Owner，否则主窗口最小化会连带隐藏 HUD
        _baseOpacity = System.Math.Clamp(opacity, 0.3, 1.0);
        Opacity = _baseOpacity;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI");

        Brush B(string k) => (Brush)(owner.TryFindResource(k) ?? System.Windows.Media.Brushes.Gray);

        _dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = B("Success"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _plan = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = B("Ink"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _loop = new TextBlock { FontSize = 11, Foreground = B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var closeBtn = new Button
        {
            Content = "\uE711", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11,
            Width = 22, Height = 22, ToolTip = "关闭悬浮窗（运行不受影响，可用热键或托盘控制）",
            Foreground = B("Muted"), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
        };
        closeBtn.Click += (_, _) => onClose();
        var head = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_dot, Dock.Left); head.Children.Add(_dot);
        DockPanel.SetDock(closeBtn, Dock.Right); head.Children.Add(closeBtn);
        DockPanel.SetDock(_loop, Dock.Right); head.Children.Add(_loop);
        head.Children.Add(_plan);

        _action = new TextBlock { FontSize = 12, Foreground = B("Muted"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0) };

        _bar = new ProgressBar { Height = 5, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 8, 0, 0), Foreground = B("Accent") };
        _pct = new TextBlock { FontSize = 11, Foreground = B("Muted"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };

        Button Mk(string glyph, string tip, Action act)
        {
            var b = new Button
            {
                Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14,
                Width = 34, Height = 30, Margin = new Thickness(0, 0, 6, 0), ToolTip = tip,
                Foreground = B("Ink"), Background = B("Bg"), BorderBrush = B("Line"), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Click += (_, _) => act();
            return b;
        }
        _pause = Mk("", "暂停 (F9)", onPause);
        _resume = Mk("", "继续 (F10)", onResume);
        _stop = Mk("", "停止 (F11)", onStop);
        _resume.Visibility = Visibility.Collapsed;
        var hint = new TextBlock { FontSize = 10, Foreground = B("Subtext"), VerticalAlignment = VerticalAlignment.Center, Text = "F9 暂停 · F10 继续 · F11 停止" };
        var btnRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(_pause); btns.Children.Add(_resume); btns.Children.Add(_stop);
        DockPanel.SetDock(btns, Dock.Left); btnRow.Children.Add(btns);
        DockPanel.SetDock(hint, Dock.Right); btnRow.Children.Add(hint);

        var body = new StackPanel { Width = 300 };
        body.Children.Add(head);
        body.Children.Add(_action);
        body.Children.Add(_bar);
        body.Children.Add(_pct);
        body.Children.Add(btnRow);

        Content = new Border
        {
            Background = B("Panel"), BorderBrush = B("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.28, Color = Colors.Black },
        };

        // 拖动：按住浮条任意非按钮处即可移动
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) { try { DragMove(); } catch { } } };
        // 鼠标悬停时临时变不透明，便于看清；移开恢复设定透明度。
        MouseEnter += (_, _) => Opacity = 1.0;
        MouseLeave += (_, _) => Opacity = _baseOpacity;
        // 不抢焦点：置 WS_EX_NOACTIVATE + TOOLWINDOW，运行时不打断用户在目标程序里的操作
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
        // 永远置顶：Topmost 仍可能被其它置顶窗口/全屏程序压下，定时重申 HWND_TOPMOST（不激活、不移位）。
        _topmostTimer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(700) };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
        IsVisibleChanged += (_, _) => { if (IsVisible) _topmostTimer.Start(); else _topmostTimer.Stop(); };
    }

    public void SetOpacity(double opacity) { _baseOpacity = System.Math.Clamp(opacity, 0.3, 1.0); if (!IsMouseOver) Opacity = _baseOpacity; }

    private void ReassertTopmost()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void SetPlan(string name) => _plan.Text = name;
    public void SetLoop(string loop) => _loop.Text = loop;
    public void SetAction(int pct, string text)
    {
        _bar.Value = Math.Clamp(pct, 0, 100);
        _pct.Text = pct + "%";
        if (!string.IsNullOrEmpty(text)) _action.Text = text;
    }

    /// <summary>kind: Running / Paused / Success / Stopped / Error / Done。</summary>
    public void SetStatus(string kind, string text)
    {
        string dotKey = kind switch { "Running" or "Success" or "Done" => "Success", "Paused" => "Warning", _ => "Danger" };
        _dot.Fill = (Brush)(_owner.TryFindResource(dotKey) ?? System.Windows.Media.Brushes.Gray);
        bool paused = kind == "Paused";
        _pause.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        _resume.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrEmpty(text)) _action.Text = text;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int idx, int val);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
