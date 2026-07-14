using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MacroPilot.Models;

/// <summary>一个方案：一组按顺序执行的动作，方案本身可整体循环。</summary>
public sealed class MacroPlan : INotifyPropertyChanged
{
    private string _name = "新方案";
    public string Name { get => _name; set { if (_name != value) { _name = value; Raise(nameof(Name)); } } }

    public int LoopCount { get; set; } = 1;      // 0=无限
    public int LoopDelayMs { get; set; }         // 每圈之间的延时（始终以毫秒存储）
    public int LoopDelayUnit { get; set; }       // 仅 UI 显示单位：0=毫秒 1=秒 2=分钟 3=小时

    // 方案级运行条件（对整个方案生效）：目前仅 TimeRange；Invert=true 表示取反（不在时间段内才运行）。
    public string RunConditionType { get; set; } = "";
    public bool RunConditionInvert { get; set; }
    public int? RunConditionStartMinute { get; set; }
    public int? RunConditionEndMinute { get; set; }
    [JsonIgnore] public bool HasRunCondition => RunConditionType == "TimeRange" && (RunConditionStartMinute.HasValue || RunConditionEndMinute.HasValue);

    public ObservableCollection<MacroStep> Steps { get; set; } = new();

    // 仅 UI：未保存标记
    private bool _dirty;
    [JsonIgnore] public bool Dirty { get => _dirty; set { if (_dirty != value) { _dirty = value; Raise(nameof(Dirty)); } } }

    // 仅 UI：上次保存时该方案的 JSON 快照，用于按内容比较计算"是否有未保存修改"（撤销回到已保存内容即不再标脏）。
    [JsonIgnore] public string? SavedSnapshot { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
