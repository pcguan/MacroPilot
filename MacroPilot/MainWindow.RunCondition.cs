using System;
using System.Windows;
using System.Windows.Controls;
using MacroPilot.Models;
using MacroPilot.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace MacroPilot;

/// <summary>
/// 运行条件编辑器：把"一套条件控件 + 回填 + 写回"打成一个整体，
/// **方案级与动作级共用同一份**（模型侧对应 <see cref="IRunCondition"/>，执行侧对应 MacroRunner.Evaluate）。
/// 以后新增条件类型，只要改 BuildRunConditionPanel / Load / Apply 这三处，两级就自动一致。
/// </summary>
public partial class MainWindow
{
    private sealed class RunConditionEditor
    {
        public readonly CheckBox Enabled = new();
        public readonly CheckBox Invert = new();
        public readonly ComboBox StartHour = new(), StartMinute = new(), EndHour = new(), EndMinute = new();
        public readonly ComboBox TypeCombo = new();   // 时间段 / 图片出现
        public ClickImagePanel Img = null!;           // 图片出现＝与「点击图片」共用同一编辑器（无「匹配第几」、不套卡片底）
        public readonly CheckBox Retry = new();       // 条件不满足时重复检查
        public readonly System.Windows.Controls.TextBox RetryInterval = new(), RetryMax = new();
        public StackPanel Panel = null!;
    }

    /// <summary>建一套完整的运行条件控件并回填 source（source 为 null 即新建）。</summary>
    private RunConditionEditor BuildRunConditionEditor(IRunCondition? source)
    {
        var ed = new RunConditionEditor();
        // 宿主窗口懒解析（win=null）：本编辑器在对话框组装前构建；notchBg 用 Hover——条件明细区的底色。
        ed.Img = new ClickImagePanel(this, null, withIndex: false, boxed: false, notchBgKey: "Hover");
        ed.Panel = BuildRunConditionPanel(ed.Enabled, ed.Invert, ed.StartHour, ed.StartMinute,
                                          ed.EndHour, ed.EndMinute, ed.TypeCombo, ed.Img,
                                          ed.Retry, ed.RetryInterval, ed.RetryMax);
        if (source != null) LoadRunCondition(ed, source);
        return ed;
    }

    private static void LoadRunCondition(RunConditionEditor ed, IRunCondition src)
    {
        ed.Enabled.IsChecked = RunCondition.Has(src);
        ed.Invert.IsChecked = src.RunConditionInvert;
        SetTimeSelection(ed.StartHour, ed.StartMinute, src.RunConditionStartMinute);
        SetTimeSelection(ed.EndHour, ed.EndMinute, src.RunConditionEndMinute);
        ed.Retry.IsChecked = src.RunConditionRetry;
        ed.RetryInterval.Text = (src.RunConditionRetryIntervalMs <= 0 ? 1000 : src.RunConditionRetryIntervalMs).ToString();
        ed.RetryMax.Text = Math.Max(0, src.RunConditionRetryMax).ToString();
        if (src.RunConditionType == "ImageMatch")
        {
            ed.Img.LoadCond(src);             // 引用(file:hash)/旧内联 base64 都能解析；面板自行回显缩略图与区域
            ed.TypeCombo.SelectedIndex = 1;   // 触发切到图片视图
        }
        else ed.TypeCombo.SelectedIndex = 0;
    }

    /// <summary>把编辑结果写回 dst。输入不合法时抛 InvalidOperationException，由调用方统一提示。</summary>
    private static void ApplyRunCondition(RunConditionEditor ed, IRunCondition dst)
    {
        if (ed.Enabled.IsChecked != true) { RunCondition.Clear(dst); return; }

        if (ed.TypeCombo.SelectedIndex == 1)   // 图片出现
        {
            RunCondition.Clear(dst);
            dst.RunConditionType = "ImageMatch";
            dst.RunConditionInvert = ed.Invert.IsChecked == true;
            ed.Img.ApplyCond(dst);   // 图片/锚定屏/限制区域/阈值（缺图会抛异常，由调用方统一提示）
            ApplyRetry(ed, dst);
            return;
        }

        var start = SelectedMinute(ed.StartHour, ed.StartMinute);
        var end = SelectedMinute(ed.EndHour, ed.EndMinute);
        if (!start.HasValue && !end.HasValue)
            throw new InvalidOperationException("运行条件启用后，请至少选择开始时间或结束时间。");
        RunCondition.Clear(dst);
        dst.RunConditionType = "TimeRange";
        dst.RunConditionInvert = ed.Invert.IsChecked == true;
        dst.RunConditionStartMinute = start;
        dst.RunConditionEndMinute = end;
        ApplyRetry(ed, dst);
    }

    // 重复检查三项（两种条件类型共用；放在 RunCondition.Clear 之后写，否则会被清掉）。
    private static void ApplyRetry(RunConditionEditor ed, IRunCondition dst)
    {
        dst.RunConditionRetry = ed.Retry.IsChecked == true;
        int iv = ParseInt(ed.RetryInterval.Text, 1000);
        dst.RunConditionRetryIntervalMs = iv <= 0 ? 1000 : iv;
        dst.RunConditionRetryMax = Math.Max(0, ParseInt(ed.RetryMax.Text, 0));
    }

    // ================= 方案设置对话框（循环次数 / 间隔 / 运行条件三合一） =================
    // 原先这三项散在"动作流程"标题栏里占一长条，改为一个图标按钮打开本窗口统一编辑。
    private bool ShowPlanSettingsDialog(MacroPlan plan)
    {
        var win = MakeDialog("方案设置");
        var grid = new Grid { Margin = new Thickness(20, 20, 6, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sp = new StackPanel();
        var scroller = MakeScrollHost(sp); Grid.SetRow(scroller, 0); grid.Children.Add(scroller);

        // ---- 循环 ----
        var loopCountText = new TextBox { Text = plan.LoopCount.ToString(), Height = 32, Margin = new Thickness(0, 0, 0, 14) };
        var delayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var delayText = new TextBox { Width = 110, Height = 32 };
        var delayUnit = new ComboBox { Width = 92, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var n in new[] { "毫秒", "秒", "分钟", "小时" }) delayUnit.Items.Add(n);
        int unit = Math.Clamp(plan.LoopDelayUnit, 0, 3);
        delayUnit.SelectedIndex = unit;
        delayText.Text = FormatDelayValue(plan.LoopDelayMs, unit);
        delayRow.Children.Add(delayText); delayRow.Children.Add(delayUnit);
        sp.Children.Add(GroupCard("循环",
            FieldLabel("循环次数（0 为无限）"), loopCountText,
            FieldLabel("每轮之间的间隔"), delayRow));

        // ---- 运行条件（与动作级同一套控件与逻辑）----
        var ed = BuildRunConditionEditor(plan);
        ed.Panel.Margin = new Thickness(0, 0, 0, 6);
        sp.Children.Add(GroupCard("运行条件",
            new TextBlock
            {
                Text = "对整个方案生效：条件不满足时方案空转等待，满足后才执行全部动作（不消耗循环次数）。",
                Foreground = (System.Windows.Media.Brush)FindResource("Muted"),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            },
            ed.Panel));

        var okBtn = new Button { Content = "确定", Width = 88, Height = 36, IsDefault = true, Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 10, 0) };
        var cancelBtn = new Button { Content = "取消", Width = 88, Height = 36, IsCancel = true, Style = (Style)FindResource("GhostButton") };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(okBtn); bar.Children.Add(cancelBtn);
        var footer = new Border { BorderBrush = (System.Windows.Media.Brush)FindResource("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 14, 0), Child = bar };
        Grid.SetRow(footer, 1); grid.Children.Add(footer);

        bool changed = false;
        okBtn.Click += (_, _) =>
        {
            try
            {
                int loops = Math.Max(0, ParseInt(loopCountText.Text, plan.LoopCount));
                int u = Math.Clamp(delayUnit.SelectedIndex, 0, 3);
                double raw = double.TryParse(delayText.Text, out var d) ? d : plan.LoopDelayMs / LoopUnitFactor(u);
                int delayMs = (int)Math.Round(Math.Max(0, raw) * LoopUnitFactor(u));

                // 先写到一个临时对象上校验，避免"条件校验失败但循环次数已经改掉"的半套用。
                var probe = new MacroPlan();
                ApplyRunCondition(ed, probe);

                changed = loops != plan.LoopCount || delayMs != plan.LoopDelayMs || u != plan.LoopDelayUnit
                          || probe.RunConditionType != plan.RunConditionType
                          || probe.RunConditionInvert != plan.RunConditionInvert
                          || probe.RunConditionStartMinute != plan.RunConditionStartMinute
                          || probe.RunConditionEndMinute != plan.RunConditionEndMinute
                          || probe.RunConditionImage != plan.RunConditionImage
                          || probe.RunConditionMonitor != plan.RunConditionMonitor
                          || probe.RunConditionRectX != plan.RunConditionRectX
                          || probe.RunConditionRectY != plan.RunConditionRectY
                          || probe.RunConditionRectW != plan.RunConditionRectW
                          || probe.RunConditionRectH != plan.RunConditionRectH
                          || Math.Abs(probe.RunConditionThreshold - plan.RunConditionThreshold) > 1e-9
                          || probe.RunConditionRetry != plan.RunConditionRetry
                          || probe.RunConditionRetryIntervalMs != plan.RunConditionRetryIntervalMs
                          || probe.RunConditionRetryMax != plan.RunConditionRetryMax;

                plan.LoopCount = loops; plan.LoopDelayMs = delayMs; plan.LoopDelayUnit = u;
                RunCondition.Copy(probe, plan);
                win.DialogResult = true;
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(ex.Message, "设置失败", MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
        };

        win.Content = grid;
        return win.ShowDialog() == true && changed;
    }
}
