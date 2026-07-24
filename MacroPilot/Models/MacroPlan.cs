using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MacroPilot.Models;

/// <summary>一个方案：一组按顺序执行的动作，方案本身可整体循环。</summary>
public sealed class MacroPlan : INotifyPropertyChanged, IRunCondition
{
    private string _name = "新方案";
    public string Name { get => _name; set { if (_name != value) { _name = value; Raise(nameof(Name)); } } }

    public int LoopCount { get; set; } = 1;      // 0=无限
    public int LoopDelayMs { get; set; }         // 每圈之间的延时（始终以毫秒存储）
    public int LoopDelayUnit { get; set; }       // 仅 UI 显示单位：0=毫秒 1=秒 2=分钟 3=小时

    // 方案级运行条件（对整个方案生效）。字段与动作级完全一致（见 IRunCondition），
    // 因此时间段 / 图片出现两类条件两级通用，编辑界面与执行判定也是同一份代码。
    public string RunConditionType { get; set; } = "";
    public bool RunConditionInvert { get; set; }
    public int? RunConditionStartMinute { get; set; }
    public int? RunConditionEndMinute { get; set; }
    public string RunConditionImage { get; set; } = "";
    public string RunConditionMonitor { get; set; } = "";
    public int RunConditionRectX { get; set; }
    public int RunConditionRectY { get; set; }
    public int RunConditionRectW { get; set; }
    public int RunConditionRectH { get; set; }
    public double RunConditionThreshold { get; set; } = 0.9;
    [JsonIgnore] public bool HasRunCondition => RunCondition.Has(this);

    // 定时启动（每方案独立）：到设定时刻（可选星期）自动运行本方案。随方案存 plans.json。
    public bool ScheduleEnabled { get; set; }
    public int ScheduleTimeMinutes { get; set; }   // 一天内分钟数 0..1439
    public int ScheduleDays { get; set; }          // 位掩码：周日=1<<0 … 周六=1<<6；0=每天

    public ObservableCollection<MacroStep> Steps { get; set; } = new();

    // 仅 UI：未保存标记
    private bool _dirty;
    [JsonIgnore] public bool Dirty { get => _dirty; set { if (_dirty != value) { _dirty = value; Raise(nameof(Dirty)); } } }

    // 仅 UI：上次保存时该方案的 JSON 快照，用于按内容比较计算"是否有未保存修改"（撤销回到已保存内容即不再标脏）。
    [JsonIgnore] public string? SavedSnapshot { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
