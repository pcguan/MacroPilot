using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacroPilot.Input;
using MacroPilot.Models;
using MacroPilot.Services;
using Button = System.Windows.Controls.Button;

namespace MacroPilot;

/// <summary>
/// 宏录制：点「录制」→ 主窗口最小化、屏幕顶部出一个固定悬浮条 → 用户在目标程序里做一遍操作 →
/// 点「完成」→ 事件流经 RecordingCompiler 归纳成动作列表 → 生成未保存的新方案进编辑页。
/// 开始/结束都是手动点按钮（不占热键）；悬浮条固定不可拖动——采集层靠它的矩形排除"点完成这一下"。
/// </summary>
public partial class MainWindow
{
    private readonly InputRecorder _inputRec = new();
    private Window? _recBar;
    private DispatcherTimer? _recBarTimer;
    private bool _restoreAfterRec;   // 录制前主窗口是否可见（从托盘状态开录就不还原）

    private void RecordPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_inputRec.IsRecording) return;
        if (_runner is { IsRunning: true }) { ShowToast("方案运行中，请先停止再录制"); return; }
        if (!EnsureCurrentPlanClean()) return;   // 录完要生成"未保存"的新方案，先保证当前最多一个未保存

        // 悬浮条：固定在主屏顶部居中，不可拖动（采集层按它的矩形排除自身，位置必须整场稳定）
        var count = new TextBlock { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 14, 0), Text = "已录 0 个动作" };
        var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var title = new TextBlock { Text = "录制中", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var okBtn = new Button { Content = "完成", Style = (Style)FindResource("PrimaryButton"), Height = 28, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 6, 0) };
        var cancelBtn = new Button { Content = "取消", Style = (Style)FindResource("GhostButton"), Height = 28, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot); row.Children.Add(title); row.Children.Add(count); row.Children.Add(okBtn); row.Children.Add(cancelBtn);

        var bar = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, SizeToContent = SizeToContent.WidthAndHeight,
            Background = Brushes.Transparent, WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x24, 0x24, 0x26)),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 10, 8), Child = row,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black },
            },
        };
        // 不抢焦点：录制的就是用户在目标程序里的操作，悬浮条抢了前台就把目标程序挤下去了
        bar.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(bar).Handle;
            SetWindowLongPtr(h, -20, new IntPtr(GetWindowLongPtr(h, -20).ToInt64() | 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/));
        };
        bar.Loaded += (_, _) =>
        {
            // 摆到主屏顶部居中（DIP），再换算出物理像素矩形交给采集层做排除
            var wa = SystemParameters.WorkArea;
            bar.Left = wa.Left + (wa.Width - bar.ActualWidth) / 2;
            bar.Top = wa.Top + 10;
            bar.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dpi = VisualTreeHelper.GetDpi(bar);
                int l = (int)Math.Floor(bar.Left * dpi.DpiScaleX) - 4, t = (int)Math.Floor(bar.Top * dpi.DpiScaleY) - 4;
                int r = (int)Math.Ceiling((bar.Left + bar.ActualWidth) * dpi.DpiScaleX) + 4;
                int b = (int)Math.Ceiling((bar.Top + bar.ActualHeight) * dpi.DpiScaleY) + 4;
                if (!_inputRec.Start(new[] { (l, t, r, b) }, out var err))
                {
                    bar.Close(); _recBar = null;
                    RestoreAfterRecording();
                    ThemedDialog.Show(err, "无法开始录制", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                }
            }), DispatcherPriority.Loaded);
        };
        okBtn.Click += (_, _) => FinishRecording(commit: true);
        cancelBtn.Click += (_, _) => FinishRecording(commit: false);

        _recBar = bar;
        _restoreAfterRec = IsVisible;
        bar.Show();
        WindowState = WindowState.Minimized;   // 让开屏幕；MinimizeToTray 开着就顺势收进托盘，结束时按原状态还原

        _recBarTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _recBarTimer.Tick += (_, _) => count.Text = $"已录 {_inputRec.ActionCount} 个动作";
        _recBarTimer.Start();
    }

    private void FinishRecording(bool commit)
    {
        _recBarTimer?.Stop(); _recBarTimer = null;
        var events = _inputRec.Stop();
        try { _recBar?.Close(); } catch { }
        _recBar = null;
        RestoreAfterRecording();
        if (!commit) { ShowToast("已取消录制"); return; }

        var steps = RecordingCompiler.Compile(events, ScreenInfo.FromPoint);
        if (steps.Count == 0) { ShowToast("没有录到任何动作"); return; }

        // 与"新建方案"同一套未保存语义：不落盘、脏标记、可撤销，由用户编辑后手动保存
        string name = "录制的方案";
        for (int i = 2; _plans.Any(p => p.Name == name); i++) name = $"录制的方案 {i}";
        var plan = new MacroPlan { Name = name };
        foreach (var s in steps) plan.Steps.Add(s);
        AddPlanUnsaved(plan, -1);
        ShowToast($"录制完成：生成 {steps.Count} 个动作");
    }

    private void RestoreAfterRecording()
    {
        if (!_restoreAfterRec) return;
        Show();
        WindowState = WindowState.Normal;
        Services.WindowActivator.ActivateHwnd(new WindowInteropHelper(this).Handle);
    }
}
