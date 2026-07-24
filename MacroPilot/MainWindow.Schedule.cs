using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacroPilot.Models;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace MacroPilot;

/// <summary>
/// 定时启动：到设定时刻（可选星期）自动运行指定方案。每 20 秒查一次，同一分钟只触发一次；
/// 运行中或找不到同名方案则跳过。时间段运行条件只能"拦"，这个才能"发起"。
/// </summary>
public partial class MainWindow
{
    private System.Windows.Threading.DispatcherTimer? _scheduleTimer;
    private readonly Dictionary<ScheduleEntry, string> _lastFired = new();   // 项 → 上次触发的 "yyyy-MM-dd HH:mm"

    private void StartScheduleTimer()
    {
        _scheduleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _scheduleTimer.Tick += (_, _) => CheckSchedules();
        _scheduleTimer.Start();
    }

    private void CheckSchedules()
    {
        if (_doc.Schedules.Count == 0) return;
        var now = DateTime.Now;
        int minuteOfDay = now.Hour * 60 + now.Minute;
        string stamp = now.ToString("yyyy-MM-dd HH:mm");
        foreach (var sc in _doc.Schedules)
        {
            if (!sc.Enabled || sc.TimeMinutes != minuteOfDay) continue;
            if (sc.Days != 0 && (sc.Days & (1 << (int)now.DayOfWeek)) == 0) continue;   // 星期不匹配
            if (_lastFired.TryGetValue(sc, out var last) && last == stamp) continue;    // 本分钟已触发过
            _lastFired[sc] = stamp;

            if (_runner is { IsRunning: true } || _startingRun) { AddLog("Warning", $"⏰ 定时任务「{sc.PlanName}」到点但正在运行，已跳过。"); continue; }
            var plan = _plans.FirstOrDefault(p => p.Name == sc.PlanName);
            if (plan == null) { AddLog("Warning", $"⏰ 定时任务找不到方案「{sc.PlanName}」，已跳过。"); continue; }
            if (plan.Steps.Count == 0) { AddLog("Warning", $"⏰ 定时方案「{sc.PlanName}」没有动作，已跳过。"); continue; }
            AddLog("Info", $"⏰ 定时启动：{sc.PlanName}");
            RunPlan(BuildRunPlan(plan), plan.Name);
        }
    }

    // 配置页「定时启动」摘要行
    private void RefreshScheduleSummary()
    {
        if (ScheduleSummaryText == null) return;
        int total = _doc.Schedules.Count, on = _doc.Schedules.Count(s => s.Enabled);
        ScheduleSummaryText.Text = total == 0 ? "未设置定时任务" : $"共 {total} 个，{on} 个已启用";
    }

    private void ManageSchedules_Click(object sender, RoutedEventArgs e)
    {
        if (ShowScheduleDialog()) { RefreshScheduleSummary(); }
    }

    private static readonly string[] WeekCn = { "日", "一", "二", "三", "四", "五", "六" };

    private static string DaysText(int days) => days == 0 || days == 0x7F ? "每天"
        : string.Join(" ", Enumerable.Range(0, 7).Where(i => (days & (1 << i)) != 0).Select(i => "周" + WeekCn[i]));

    private bool ShowScheduleDialog()
    {
        var win = MakeDialog("定时启动");
        var grid = new Grid { Margin = new Thickness(20, 20, 6, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sp = new StackPanel();
        var scroller = MakeScrollHost(sp); Grid.SetRow(scroller, 0); grid.Children.Add(scroller);

        // 工作副本：确定才写回
        var work = new System.Collections.ObjectModel.ObservableCollection<ScheduleEntry>(
            _doc.Schedules.Select(s => new ScheduleEntry { PlanName = s.PlanName, TimeMinutes = s.TimeMinutes, Days = s.Days, Enabled = s.Enabled }));

        var list = new StackPanel();
        void Rebuild()
        {
            list.Children.Clear();
            if (work.Count == 0)
                list.Children.Add(new TextBlock { Text = "（暂无定时任务，点下方「添加」）", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 4, 0, 4) });
            for (int idx = 0; idx < work.Count; idx++)
            {
                int i = idx; var sc = work[i];
                var card = new Border { Background = (Brush)FindResource("Bg"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 12), Margin = new Thickness(0, 0, 0, 8) };
                var inner = new StackPanel();

                // 第一行：启用开关 + 方案下拉 + 删除
                var top = new DockPanel { LastChildFill = true };
                var en = new CheckBox { Style = (Style)FindResource("ToggleSwitch"), IsChecked = sc.Enabled, VerticalAlignment = VerticalAlignment.Center };
                en.Checked += (_, _) => sc.Enabled = true; en.Unchecked += (_, _) => sc.Enabled = false;
                DockPanel.SetDock(en, Dock.Left); top.Children.Add(en);
                var del = new Button { Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), Width = 32, Height = 30, Style = (Style)FindResource("IconButton"), ToolTip = "删除此定时任务" };
                del.Click += (_, _) => { work.RemoveAt(i); Rebuild(); };
                DockPanel.SetDock(del, Dock.Right); top.Children.Add(del);
                var planCombo = new ComboBox { Height = 32, Margin = new Thickness(10, 0, 8, 0) };
                foreach (var p in _plans) planCombo.Items.Add(p.Name);
                if (!string.IsNullOrEmpty(sc.PlanName) && !_plans.Any(p => p.Name == sc.PlanName)) planCombo.Items.Add(sc.PlanName);   // 保留已失效的名字
                planCombo.SelectedItem = sc.PlanName;
                if (planCombo.SelectedIndex < 0 && planCombo.Items.Count > 0) { planCombo.SelectedIndex = 0; sc.PlanName = (string)planCombo.Items[0]; }
                planCombo.SelectionChanged += (_, _) => sc.PlanName = planCombo.SelectedItem as string ?? "";
                top.Children.Add(planCombo);
                inner.Children.Add(top);

                // 第二行：时间 HH:MM
                var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
                timeRow.Children.Add(new TextBlock { Text = "时间", Width = 40, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
                var hour = new ComboBox { Width = 66, Height = 32 };
                for (int hh = 0; hh < 24; hh++) hour.Items.Add(hh.ToString("00"));
                var minute = new ComboBox { Width = 66, Height = 32, Margin = new Thickness(6, 0, 0, 0) };
                for (int mm = 0; mm < 60; mm++) minute.Items.Add(mm.ToString("00"));
                hour.SelectedIndex = Math.Clamp(sc.TimeMinutes / 60, 0, 23);
                minute.SelectedIndex = Math.Clamp(sc.TimeMinutes % 60, 0, 59);
                void SyncTime() => sc.TimeMinutes = Math.Max(0, hour.SelectedIndex) * 60 + Math.Max(0, minute.SelectedIndex);
                hour.SelectionChanged += (_, _) => SyncTime(); minute.SelectionChanged += (_, _) => SyncTime();
                timeRow.Children.Add(hour);
                timeRow.Children.Add(new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
                timeRow.Children.Add(minute);
                inner.Children.Add(timeRow);

                // 第三行：星期（每天 / 逐个）
                var dayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
                dayRow.Children.Add(new TextBlock { Text = "星期", Width = 40, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
                var dayChecks = new CheckBox[7];
                for (int d = 0; d < 7; d++)
                {
                    int dd = d;
                    var c = new CheckBox { Content = WeekCn[d], IsChecked = sc.Days == 0 || (sc.Days & (1 << d)) != 0, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                    void SyncDays()
                    {
                        int mask = 0;
                        for (int k = 0; k < 7; k++) if (dayChecks[k].IsChecked == true) mask |= (1 << k);
                        sc.Days = (mask == 0x7F) ? 0 : mask;   // 全选 → 存 0（每天）
                    }
                    c.Checked += (_, _) => SyncDays(); c.Unchecked += (_, _) => SyncDays();
                    dayChecks[d] = c; dayRow.Children.Add(c);
                }
                inner.Children.Add(dayRow);

                card.Child = inner;
                list.Children.Add(card);
            }
        }
        Rebuild();
        sp.Children.Add(list);
        var addBtn = new Button { Content = "＋ 添加定时任务", Height = 34, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 4, 0, 0) };
        addBtn.Click += (_, _) =>
        {
            var e = new ScheduleEntry { PlanName = _plans.FirstOrDefault()?.Name ?? "", TimeMinutes = 9 * 60, Days = 0, Enabled = true };
            work.Add(e); Rebuild();
        };
        sp.Children.Add(addBtn);
        sp.Children.Add(new TextBlock { Text = "到点若正在运行、或找不到同名方案，会跳过本次并记入日志。", Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });

        var okBtn = new Button { Content = "确定", Width = 88, Height = 36, IsDefault = true, Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 10, 0) };
        var cancelBtn = new Button { Content = "取消", Width = 88, Height = 36, IsCancel = true, Style = (Style)FindResource("GhostButton") };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(okBtn); bar.Children.Add(cancelBtn);
        var footer = new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 14, 0), Child = bar };
        Grid.SetRow(footer, 1); grid.Children.Add(footer);

        bool changed = false;
        okBtn.Click += (_, _) =>
        {
            _doc.Schedules = work.ToList();
            _lastFired.Clear();   // 换了配置，重置触发记录
            PersistSettings();
            changed = true;
            win.DialogResult = true;
        };
        win.Content = grid;
        return win.ShowDialog() == true && changed;
    }
}
