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
        public readonly ImageCond Img = new();
        public StackPanel Panel = null!;
    }

    /// <summary>建一套完整的运行条件控件并回填 source（source 为 null 即新建）。</summary>
    private RunConditionEditor BuildRunConditionEditor(IRunCondition? source)
    {
        var ed = new RunConditionEditor();
        ed.Panel = BuildRunConditionPanel(ed.Enabled, ed.Invert, ed.StartHour, ed.StartMinute,
                                          ed.EndHour, ed.EndMinute, ed.TypeCombo, ed.Img);
        if (source != null) LoadRunCondition(ed, source);
        return ed;
    }

    private static void LoadRunCondition(RunConditionEditor ed, IRunCondition src)
    {
        ed.Enabled.IsChecked = RunCondition.Has(src);
        ed.Invert.IsChecked = src.RunConditionInvert;
        SetTimeSelection(ed.StartHour, ed.StartMinute, src.RunConditionStartMinute);
        SetTimeSelection(ed.EndHour, ed.EndMinute, src.RunConditionEndMinute);
        if (src.RunConditionType == "ImageMatch")
        {
            ed.Img.Png = ImageStore.Bytes(src.RunConditionImage);   // 引用(file:hash)/旧内联 base64 都能解析
            ed.Img.Monitor = src.RunConditionMonitor;
            ed.Img.RelX = src.RunConditionRectX; ed.Img.RelY = src.RunConditionRectY;
            ed.Img.W = src.RunConditionRectW; ed.Img.H = src.RunConditionRectH;
            ed.Img.Threshold = src.RunConditionThreshold > 0 ? src.RunConditionThreshold : 0.9;
            ed.TypeCombo.SelectedIndex = 1;   // 放最后：触发切到图片视图并回显缩略图
        }
        else ed.TypeCombo.SelectedIndex = 0;
    }

    /// <summary>把编辑结果写回 dst。输入不合法时抛 InvalidOperationException，由调用方统一提示。</summary>
    private static void ApplyRunCondition(RunConditionEditor ed, IRunCondition dst)
    {
        if (ed.Enabled.IsChecked != true) { RunCondition.Clear(dst); return; }

        if (ed.TypeCombo.SelectedIndex == 1)   // 图片出现
        {
            if (!ed.Img.Has) throw new InvalidOperationException("请先截取目标图片。");
            RunCondition.Clear(dst);
            dst.RunConditionType = "ImageMatch";
            dst.RunConditionInvert = ed.Invert.IsChecked == true;
            dst.RunConditionImage = ImageStore.Ref(ed.Img.Png!);   // 立即外置成 file:hash 引用
            dst.RunConditionMonitor = ed.Img.Monitor;
            dst.RunConditionRectX = ed.Img.RelX; dst.RunConditionRectY = ed.Img.RelY;
            dst.RunConditionRectW = ed.Img.W; dst.RunConditionRectH = ed.Img.H;
            dst.RunConditionThreshold = ed.Img.Threshold;
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

        // ---- 定时启动（每方案独立）----
        var schedEnable = new CheckBox { Style = (Style)FindResource("ToggleSwitch"), IsChecked = plan.ScheduleEnabled, VerticalAlignment = VerticalAlignment.Center };
        var schedHead = new DockPanel { LastChildFill = false };
        var schedTitle = new TextBlock { Text = "到点自动运行本方案", VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(schedTitle, Dock.Left); schedHead.Children.Add(schedTitle);
        DockPanel.SetDock(schedEnable, Dock.Right); schedHead.Children.Add(schedEnable);

        var schedHour = new ComboBox { Width = 66, Height = 32 };
        for (int hh = 0; hh < 24; hh++) schedHour.Items.Add(hh.ToString("00"));
        var schedMinute = new ComboBox { Width = 66, Height = 32, Margin = new Thickness(6, 0, 0, 0) };
        for (int mm = 0; mm < 60; mm++) schedMinute.Items.Add(mm.ToString("00"));
        schedHour.SelectedIndex = Math.Clamp(plan.ScheduleTimeMinutes / 60, 0, 23);
        schedMinute.SelectedIndex = Math.Clamp(plan.ScheduleTimeMinutes % 60, 0, 59);
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
        timeRow.Children.Add(new TextBlock { Text = "时间", Width = 40, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        timeRow.Children.Add(schedHour);
        timeRow.Children.Add(new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        timeRow.Children.Add(schedMinute);

        var dayChecks = new CheckBox[7];
        var dayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        dayRow.Children.Add(new TextBlock { Text = "星期", Width = 40, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        for (int d = 0; d < 7; d++)
        {
            var c = new CheckBox { Content = WeekCn[d], IsChecked = plan.ScheduleDays == 0 || (plan.ScheduleDays & (1 << d)) != 0, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            dayChecks[d] = c; dayRow.Children.Add(c);
        }
        var schedDetail = new StackPanel();
        schedDetail.Children.Add(timeRow);
        schedDetail.Children.Add(dayRow);
        var schedWrap = new Border
        {
            BorderBrush = (System.Windows.Media.Brush)FindResource("Accent"), BorderThickness = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 6, 6, 0), Padding = new Thickness(12, 10, 0, 2),
            Margin = new Thickness(2, 10, 0, 0), Child = schedDetail,
            Visibility = plan.ScheduleEnabled ? Visibility.Visible : Visibility.Collapsed,
        };
        schedEnable.Checked += (_, _) => schedWrap.Visibility = Visibility.Visible;
        schedEnable.Unchecked += (_, _) => schedWrap.Visibility = Visibility.Collapsed;
        sp.Children.Add(GroupCard("定时启动",
            new TextBlock
            {
                Text = "到设定时刻（可选星期）自动运行本方案；到点若正在运行会跳过并记入日志。设置随方案一起保存。",
                Foreground = (System.Windows.Media.Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            },
            schedHead, schedWrap));

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
                          || Math.Abs(probe.RunConditionThreshold - plan.RunConditionThreshold) > 1e-9;

                bool schedEn = schedEnable.IsChecked == true;
                int schedMin = Math.Max(0, schedHour.SelectedIndex) * 60 + Math.Max(0, schedMinute.SelectedIndex);
                int mask = 0; for (int k = 0; k < 7; k++) if (dayChecks[k].IsChecked == true) mask |= (1 << k);
                int schedDays = (mask == 0x7F) ? 0 : mask;   // 全选 → 0（每天）

                changed = changed || schedEn != plan.ScheduleEnabled || schedMin != plan.ScheduleTimeMinutes || schedDays != plan.ScheduleDays;

                plan.LoopCount = loops; plan.LoopDelayMs = delayMs; plan.LoopDelayUnit = u;
                RunCondition.Copy(probe, plan);
                plan.ScheduleEnabled = schedEn; plan.ScheduleTimeMinutes = schedMin; plan.ScheduleDays = schedDays;
                _lastFired.Remove(plan);   // 改了定时，重置本方案触发记录
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
