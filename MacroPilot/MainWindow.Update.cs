using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacroPilot.Services;
using Button = System.Windows.Controls.Button;

namespace MacroPilot;

/// <summary>
/// 在线更新的可视化：从点「立即更新」到本体退出这一段由本窗口负责显示进度；
/// 本体退出后到重启前那一段由 PowerShell 助手自带的小窗负责（见 UpdateService.ZipUpdateScript）。
/// 目标是任何时刻用户都能看到"进行到哪一步、卡在哪"，不再出现点完没反应。
/// </summary>
public partial class MainWindow
{
    private sealed class UpdateProgressUi
    {
        private readonly Window _win;
        private readonly TextBlock _title, _detail;
        private readonly ProgressBar _bar;
        private readonly Button _cancel;
        private readonly StackPanel _steps;
        private readonly TextBlock[] _stepTexts;
        private static readonly string[] StepNames = { "下载更新包", "校验完整性", "退出并覆盖安装", "重启新版本" };

        public bool Canceled { get; private set; }

        public UpdateProgressUi(Window owner, string versionText)
        {
            _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            _detail = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            _detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _bar = new ProgressBar { Height = 6, Margin = new Thickness(0, 14, 0, 0), IsIndeterminate = true, Minimum = 0, Maximum = 100 };

            _steps = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            _stepTexts = new TextBlock[StepNames.Length];
            for (int i = 0; i < StepNames.Length; i++)
            {
                var t = new TextBlock { FontSize = 12, Margin = new Thickness(0, 3, 0, 0) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                _stepTexts[i] = t;
                _steps.Children.Add(t);
            }

            _cancel = new Button { Content = "取消", Width = 84, Height = 32, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            _cancel.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
            _cancel.Click += (_, _) => { Canceled = true; _cancel.IsEnabled = false; _detail.Text = "正在取消…"; };

            var sp = new StackPanel { Margin = new Thickness(22) };
            sp.Children.Add(_title);
            sp.Children.Add(_detail);
            sp.Children.Add(_bar);
            sp.Children.Add(_steps);
            sp.Children.Add(_cancel);

            _win = new Window
            {
                Title = "更新到 " + versionText,
                Owner = owner, Width = 430, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Background = owner.Background, Content = sp,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI"),
            };
            _win.SetResourceReference(Control.ForegroundProperty, "Ink");
            _win.SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBar(_win, ThemeManager.EffectiveDark);
            // 更新过程中不许直接关掉本窗（要么走取消，要么等它自己关）
            _win.Closing += (_, e) => { if (!_closable) { e.Cancel = true; Canceled = true; } };
            SetStep(0, "准备下载…");
            _win.Show();
        }

        private bool _closable;

        /// <summary>切到第 index 步（0 起），前面的步骤标记为已完成。</summary>
        public void SetStep(int index, string title, string detail = "")
        {
            _title.Text = title;
            _detail.Text = detail;
            for (int i = 0; i < _stepTexts.Length; i++)
            {
                string mark = i < index ? "✓" : i == index ? "▶" : "○";
                _stepTexts[i].Text = $"{mark} {StepNames[i]}";
                _stepTexts[i].SetResourceReference(TextBlock.ForegroundProperty, i <= index ? "Ink" : "Muted");
                _stepTexts[i].FontWeight = i == index ? FontWeights.SemiBold : FontWeights.Normal;
            }
            _cancel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>下载百分比；传负数表示总长未知（保持不确定态）。</summary>
        public void SetProgress(double pct)
        {
            if (pct < 0) { _bar.IsIndeterminate = true; _detail.Text = "正在下载…（服务器未提供文件大小）"; return; }
            _bar.IsIndeterminate = false;
            _bar.Value = Math.Clamp(pct * 100, 0, 100);
            _detail.Text = $"已下载 {pct * 100:0}%";
        }

        public void Indeterminate() => _bar.IsIndeterminate = true;

        public void Fail(string msg)
        {
            _title.Text = "更新失败";
            _detail.Text = msg;
            _bar.IsIndeterminate = false; _bar.Value = 0;
            _cancel.Content = "关闭"; _cancel.IsEnabled = true; _cancel.Visibility = Visibility.Visible;
            _cancel.Click += (_, _) => Close();
        }

        public void Close() { _closable = true; try { _win.Close(); } catch { } }
    }

    /// <summary>
    /// 启动时检查上一次就地更新是否失败过，失败则把原因摆到概况页，不让它默默过去。
    /// </summary>
    private void ReportLastUpdateFailure()
    {
        var msg = UpdateService.LastUpdateFailure();
        if (string.IsNullOrEmpty(msg)) return;
        UpdateStatusText.Text = "上次更新未成功：" + msg;
        AddLog("Warning", "上次在线更新未成功（已回滚）：" + msg);
    }
}
