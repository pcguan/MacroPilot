using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacroPilot.Models;
using MacroPilot.Services;

namespace MacroPilot;

/// <summary>
/// 定时启动（全局单一：整个程序只允许一个方案定时）。两种模式二选一：
///   Daily = 每日定时（时:分:秒 + 可选星期，重复）；Once = 指定时间（年月日时分秒，只跑一次，须晚于当前时间）。
/// 精确到秒：0.5s 轮询。到点若正在运行则跳过并记日志；Once 触发或错过后自动清除。
/// </summary>
public partial class MainWindow
{
    private System.Windows.Threading.DispatcherTimer? _scheduleTimer;
    private string _lastFiredDay = "";   // Daily 每天只触发一次：记上次触发日期 yyyy-MM-dd

    internal static readonly string[] WeekCn = { "日", "一", "二", "三", "四", "五", "六" };

    internal static string DaysText(int days) => days == 0 || days == 0x7F ? "每天"
        : string.Join(" ", Enumerable.Range(0, 7).Where(i => (days & (1 << i)) != 0).Select(i => "周" + WeekCn[i]));

    private void StartScheduleTimer()
    {
        _scheduleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _scheduleTimer.Tick += (_, _) => CheckSchedule();
        _scheduleTimer.Start();
    }

    // 给每个方案打上"是否为当前定时目标"的标记（列表里显示时钟）。
    private void RefreshScheduleMark()
    {
        bool on = _doc.ScheduleMode is "Daily" or "Once" && !string.IsNullOrEmpty(_doc.ScheduledPlan);
        foreach (var p in _plans) p.IsScheduled = on && p.Name == _doc.ScheduledPlan;
    }

    // 删除方案时若删的正是定时目标，清掉定时设置。
    private void ClearScheduleIfPlan(string planName)
    {
        if (_doc.ScheduleMode != "" && _doc.ScheduledPlan == planName)
        {
            _doc.ScheduleMode = ""; _doc.ScheduledPlan = "";
            PersistSettings(); RefreshScheduleMark();
        }
    }

    private void CheckSchedule()
    {
        if (string.IsNullOrEmpty(_doc.ScheduleMode) || string.IsNullOrEmpty(_doc.ScheduledPlan)) return;
        var now = DateTime.Now;

        if (_doc.ScheduleMode == "Daily")
        {
            if (_doc.ScheduleDays != 0 && (_doc.ScheduleDays & (1 << (int)now.DayOfWeek)) == 0) return;   // 星期不匹配
            int nowSec = now.Hour * 3600 + now.Minute * 60 + now.Second;
            int diff = nowSec - _doc.ScheduleSecondOfDay;
            string today = now.ToString("yyyy-MM-dd");
            if (diff >= 0 && diff <= 1 && _lastFiredDay != today)   // 命中该秒（含 1s 容差），当天只触发一次
            {
                _lastFiredDay = today;
                FireSchedule();
            }
        }
        else if (_doc.ScheduleMode == "Once")
        {
            if (!DateTime.TryParse(_doc.ScheduleOnceAt, out var at)) { ClearOnce(); return; }
            double past = (now - at).TotalSeconds;
            if (past < 0) return;                          // 还没到
            if (past <= 60) FireSchedule();                // 到点（60s 宽限，防本体刚好错过一两秒）
            else AddLog("Warning", $"⏰ 指定时间 {at:yyyy-MM-dd HH:mm:ss} 已过（本体当时可能未运行），已取消。");
            ClearOnce();                                   // 一次性：无论触发/错过都清除
        }
    }

    private void ClearOnce()
    {
        _doc.ScheduleMode = ""; _doc.ScheduledPlan = ""; _doc.ScheduleOnceAt = "";
        PersistSettings(); RefreshScheduleMark();
    }

    private MacroPlan? _pendingScheduled;   // Interrupt 策略下：停止当前后待运行的定时方案

    private void FireSchedule()
    {
        var plan = _plans.FirstOrDefault(p => p.Name == _doc.ScheduledPlan);
        if (plan == null) { AddLog("Warning", $"⏰ 定时方案「{_doc.ScheduledPlan}」不存在，已跳过。"); return; }
        if (plan.Steps.Count == 0) { AddLog("Warning", $"⏰ 定时方案「{plan.Name}」没有动作，已跳过。"); return; }
        if (_runner is { IsRunning: true } || _startingRun)
        {
            if (_doc.ScheduleConflict == "Interrupt")
            {
                AddLog("Warning", $"⏰ 定时「{plan.Name}」到点，停止当前运行并改跑定时。");
                _pendingScheduled = plan;
                _runner?.Stop();   // 结束后由 OnRunFinished 拉起 _pendingScheduled
            }
            else AddLog("Warning", $"⏰ 定时「{plan.Name}」到点但正在运行，已忽略本次。");
            return;
        }
        AddLog("Info", $"⏰ 定时启动：{plan.Name}");
        RunPlan(BuildRunPlan(plan), plan.Name);
    }

    // ================= 定时启动设置对话框（全局单一）=================
    private void Schedule_Click(object sender, RoutedEventArgs e)
    {
        if (_plans.Count == 0) { ThemedDialog.Show("请先创建方案。", "定时启动"); return; }
        ShowScheduleDialog();
    }

    private void ShowScheduleDialog()
    {
        var win = MakeDialog("定时启动");
        var grid = new Grid { Margin = new Thickness(20, 20, 6, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sp = new StackPanel();
        var scroller = MakeScrollHost(sp); Grid.SetRow(scroller, 0); grid.Children.Add(scroller);

        Brush Muted() => (Brush)FindResource("Muted");

        // ---- 目标方案 ----
        var planCombo = new ComboBox { Height = 32, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var p in _plans) planCombo.Items.Add(p.Name);
        planCombo.SelectedItem = _plans.Any(p => p.Name == _doc.ScheduledPlan) ? _doc.ScheduledPlan : _plans[0].Name;
        sp.Children.Add(GroupCard("目标方案",
            new TextBlock { Text = "全局只允许一个方案定时启动。", Foreground = Muted(), FontSize = 12, Margin = new Thickness(0, 0, 0, 8) },
            planCombo));

        // ---- 模式（二选一 + 关闭）----
        var rbOff = new RadioButton { Content = "关闭", GroupName = "sched", Margin = new Thickness(0, 0, 0, 8) };
        var rbDaily = new RadioButton { Content = "每日定时（到点重复运行）", GroupName = "sched", Margin = new Thickness(0, 0, 0, 8) };
        var rbOnce = new RadioButton { Content = "指定时间（到点运行一次）", GroupName = "sched" };

        // Daily 明细：时:分:秒 + 星期
        ComboBox NumCombo(int count) { var c = new ComboBox { Width = 60, Height = 32 }; for (int i = 0; i < count; i++) c.Items.Add(i.ToString("00")); return c; }
        var dH = NumCombo(24); var dM = NumCombo(60); var dS = NumCombo(60);
        int cur = _doc.ScheduleSecondOfDay;
        dH.SelectedIndex = Math.Clamp(cur / 3600, 0, 23); dM.SelectedIndex = Math.Clamp(cur / 60 % 60, 0, 59); dS.SelectedIndex = Math.Clamp(cur % 60, 0, 59);
        var dTime = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
        dTime.Children.Add(new TextBlock { Text = "时间", Width = 40, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        void AddColon(StackPanel row) => row.Children.Add(new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        dTime.Children.Add(dH); AddColon(dTime); dTime.Children.Add(dM); AddColon(dTime); dTime.Children.Add(dS);
        var dayChecks = new CheckBox[7];
        var dayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        dayRow.Children.Add(new TextBlock { Text = "星期", Width = 40, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        for (int d = 0; d < 7; d++)
        {
            var c = new CheckBox { Content = WeekCn[d], IsChecked = _doc.ScheduleDays == 0 || (_doc.ScheduleDays & (1 << d)) != 0, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            dayChecks[d] = c; dayRow.Children.Add(c);
        }
        var dailyPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        dailyPanel.Children.Add(dTime); dailyPanel.Children.Add(dayRow);

        // Once 明细：日期 + 时:分:秒
        var datePick = new DatePicker { Width = 150, Height = 32, SelectedDate = DateTime.Today };
        var oH = NumCombo(24); var oM = NumCombo(60); var oS = NumCombo(60);
        if (_doc.ScheduleMode == "Once" && DateTime.TryParse(_doc.ScheduleOnceAt, out var onceAt) && onceAt > DateTime.Now)
        {
            datePick.SelectedDate = onceAt.Date; oH.SelectedIndex = onceAt.Hour; oM.SelectedIndex = onceAt.Minute; oS.SelectedIndex = onceAt.Second;
        }
        else { var t = DateTime.Now.AddMinutes(1); oH.SelectedIndex = t.Hour; oM.SelectedIndex = t.Minute; oS.SelectedIndex = t.Second; }
        var oTimeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        oTimeRow.Children.Add(new TextBlock { Text = "日期", Width = 40, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        oTimeRow.Children.Add(datePick);
        var oClock = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        oClock.Children.Add(new TextBlock { Text = "时间", Width = 40, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        oClock.Children.Add(oH); AddColon(oClock); oClock.Children.Add(oM); AddColon(oClock); oClock.Children.Add(oS);
        var oncePanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        oncePanel.Children.Add(oTimeRow); oncePanel.Children.Add(oClock);
        oncePanel.Children.Add(new TextBlock { Text = "必须晚于当前时间；到点运行一次后自动关闭。", Foreground = Muted(), FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });

        Border Wrap(UIElement child) => new()
        {
            BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 6, 6, 0), Padding = new Thickness(12, 6, 0, 4), Margin = new Thickness(2, 0, 0, 6), Child = child,
        };
        var dailyWrap = Wrap(dailyPanel);
        var onceWrap = Wrap(oncePanel);

        void Refresh()
        {
            dailyWrap.Visibility = rbDaily.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            onceWrap.Visibility = rbOnce.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        rbOff.Checked += (_, _) => Refresh(); rbDaily.Checked += (_, _) => Refresh(); rbOnce.Checked += (_, _) => Refresh();
        (_doc.ScheduleMode switch { "Daily" => rbDaily, "Once" => rbOnce, _ => rbOff }).IsChecked = true;

        var modeInner = new StackPanel();
        modeInner.Children.Add(rbOff);
        modeInner.Children.Add(rbDaily); modeInner.Children.Add(dailyWrap);
        modeInner.Children.Add(rbOnce); modeInner.Children.Add(onceWrap);
        sp.Children.Add(GroupCard("方式", modeInner));

        // ---- 到点冲突处理 ----
        var conflictCombo = new ComboBox { Height = 32 };
        conflictCombo.Items.Add("忽略本次定时");           // Ignore
        conflictCombo.Items.Add("停止当前，改跑定时");     // Interrupt
        conflictCombo.SelectedIndex = _doc.ScheduleConflict == "Interrupt" ? 1 : 0;
        sp.Children.Add(GroupCard("到点时已有方案在运行",
            new TextBlock { Text = "定时到点，但此刻正好有方案在跑时怎么办。", Foreground = Muted(), FontSize = 12, Margin = new Thickness(0, 0, 0, 8) },
            conflictCombo));

        var okBtn = new Button { Content = "确定", Width = 88, Height = 36, IsDefault = true, Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 10, 0) };
        var cancelBtn = new Button { Content = "取消", Width = 88, Height = 36, IsCancel = true, Style = (Style)FindResource("GhostButton") };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(okBtn); bar.Children.Add(cancelBtn);
        var footer = new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 14, 0), Child = bar };
        Grid.SetRow(footer, 1); grid.Children.Add(footer);

        okBtn.Click += (_, _) =>
        {
            try
            {
                if (rbOff.IsChecked == true) { _doc.ScheduleMode = ""; _doc.ScheduledPlan = ""; }
                else
                {
                    var planName = planCombo.SelectedItem as string;
                    if (string.IsNullOrEmpty(planName)) throw new InvalidOperationException("请选择目标方案。");
                    if (rbDaily.IsChecked == true)
                    {
                        int mask = 0; for (int k = 0; k < 7; k++) if (dayChecks[k].IsChecked == true) mask |= (1 << k);
                        if (mask == 0) throw new InvalidOperationException("请至少选择一个星期（或全选＝每天）。");
                        _doc.ScheduleMode = "Daily";
                        _doc.ScheduleSecondOfDay = dH.SelectedIndex * 3600 + dM.SelectedIndex * 60 + dS.SelectedIndex;
                        _doc.ScheduleDays = (mask == 0x7F) ? 0 : mask;
                        _lastFiredDay = "";   // 重置，允许今天再次触发
                    }
                    else   // Once
                    {
                        var date = datePick.SelectedDate ?? DateTime.Today;
                        var at = date.Date.AddHours(oH.SelectedIndex).AddMinutes(oM.SelectedIndex).AddSeconds(oS.SelectedIndex);
                        if (at <= DateTime.Now) throw new InvalidOperationException("指定时间不能早于当前时间。");
                        _doc.ScheduleMode = "Once";
                        _doc.ScheduleOnceAt = at.ToString("yyyy-MM-ddTHH:mm:ss");
                    }
                    _doc.ScheduledPlan = planName;
                }
                _doc.ScheduleConflict = conflictCombo.SelectedIndex == 1 ? "Interrupt" : "Ignore";
                PersistSettings();
                RefreshScheduleMark();
                win.DialogResult = true;
            }
            catch (Exception ex) { ThemedDialog.Show(ex.Message, "设置失败", MessageBoxButton.OK, MessageBoxImage.Exclamation); }
        };
        win.Content = grid;
        win.ShowDialog();
    }
}
