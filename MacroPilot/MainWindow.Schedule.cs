using System;
using System.Collections.Generic;
using System.Linq;
using MacroPilot.Models;

namespace MacroPilot;

/// <summary>
/// 定时启动（每方案独立，配置在方案设置里）：到方案设定的时刻（可选星期）自动运行该方案。
/// 每 20 秒查一次，同一分钟只触发一次；运行中则跳过并记日志。时间段运行条件只能"拦"，这个才能"发起"。
/// </summary>
public partial class MainWindow
{
    private System.Windows.Threading.DispatcherTimer? _scheduleTimer;
    private readonly Dictionary<MacroPlan, string> _lastFired = new();   // 方案 → 上次触发的 "yyyy-MM-dd HH:mm"

    internal static readonly string[] WeekCn = { "日", "一", "二", "三", "四", "五", "六" };

    // 星期位掩码 → 中文（0/全选 = 每天）
    internal static string DaysText(int days) => days == 0 || days == 0x7F ? "每天"
        : string.Join(" ", Enumerable.Range(0, 7).Where(i => (days & (1 << i)) != 0).Select(i => "周" + WeekCn[i]));

    private void StartScheduleTimer()
    {
        _scheduleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _scheduleTimer.Tick += (_, _) => CheckSchedules();
        _scheduleTimer.Start();
    }

    private void CheckSchedules()
    {
        var now = DateTime.Now;
        int minuteOfDay = now.Hour * 60 + now.Minute;
        string stamp = now.ToString("yyyy-MM-dd HH:mm");
        foreach (var plan in _plans)
        {
            if (!plan.ScheduleEnabled || plan.ScheduleTimeMinutes != minuteOfDay) continue;
            if (plan.ScheduleDays != 0 && (plan.ScheduleDays & (1 << (int)now.DayOfWeek)) == 0) continue;   // 星期不匹配
            if (_lastFired.TryGetValue(plan, out var last) && last == stamp) continue;                      // 本分钟已触发过
            _lastFired[plan] = stamp;

            if (_runner is { IsRunning: true } || _startingRun) { AddLog("Warning", $"⏰ 定时任务「{plan.Name}」到点但正在运行，已跳过。"); continue; }
            if (plan.Steps.Count == 0) { AddLog("Warning", $"⏰ 定时方案「{plan.Name}」没有动作，已跳过。"); continue; }
            AddLog("Info", $"⏰ 定时启动：{plan.Name}");
            RunPlan(BuildRunPlan(plan), plan.Name);
        }
    }
}
