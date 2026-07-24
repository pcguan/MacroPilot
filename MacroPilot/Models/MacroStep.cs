using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Text.Json.Serialization;

namespace MacroPilot.Models;

/// <summary>
/// 单个动作。字段名与参考版 plans.json 完全一致（PascalCase），可直接互读。
/// Type（对应「动作类型」层级）：
///   输入 → 鼠标 → 移动 = MouseMove、点击 = MouseClick / MouseClickAt(带坐标)、拖动 = MouseDrag、滚轮 = MouseWheel
///   输入 → 键盘        = KeyTap
///   运行 → 等待 = Wait、激活窗口 = ActivateWindow、跳转 = Jump（原先挂在动作上的"执行后跳转"已剥离成它）
///   组合 = Group
/// MouseClickAt 复用 MouseMove 的坐标字段 + MouseClick 的按钮/按住字段，语义 = 先移动再点击。
/// </summary>
public sealed class MacroStep : INotifyPropertyChanged, IRunCondition
{
    // ---- 持久化字段（与 plans.json 对齐）----
    private string _type = "MouseClick";
    public string Type { get => _type; set { _type = value; Raise(nameof(Type)); Raise(nameof(Display)); } }

    public string Button { get; set; } = "Left";          // Left / Right / Middle
    public string Key { get; set; } = "";                 // 按键名，如 "F" "1" "Space"
    public byte Modifier { get; set; }                    // Ctrl/Shift/Alt/Win（左右共 8 位）
    public int HoldMs { get; set; } = 75;                 // 按住时长（点击/按键）。默认 75ms：低于此值部分应用会吞掉后续按键
    public int DurationMs { get; set; } = 1000;           // 等待时长（Wait）
    // 编辑时用户所选的时间单位下标（0毫秒/1秒/2分钟/3小时）。-1=未指定，编辑时按自动进位选单位。
    // 展示用 FormatMs 仍自动进位；这两个字段只用于"重新编辑时还原用户当初选的单位"。
    public int HoldUnit { get; set; } = -1;
    public int DurationUnit { get; set; } = -1;
    public int X { get; set; }                            // MouseMove 旧字段（主屏像素），兼容旧数据
    public int Y { get; set; }
    // MouseMove 新表示：目标显示器设备名 + 屏内归一化 (0~1)。为空表示用旧 X/Y。
    public string MoveMonitor { get; set; } = "";
    public double MoveNormX { get; set; }
    public double MoveNormY { get; set; }
    // MouseDrag 的终点：起点复用上面的 MoveMonitor/MoveNormX/MoveNormY。
    public string DragEndMonitor { get; set; } = "";
    public double DragEndNormX { get; set; }
    public double DragEndNormY { get; set; }
    // 拟人化移动（动作级）：true 则该"鼠标移动"走缓入缓出的弧线轨迹分多步逼近，而非瞬间跳到目标。
    public bool Humanize { get; set; }
    // 落点偏移（像素半径，0=精确命中）：在目标点周围该半径的圆盘内随机取落点。移动/点击坐标/拖动共用；
    // 与拟人化轨迹天然配套——轨迹拟人了、落点却每次分毫不差反而露馅。每次重复各自重新随机。
    public int ClickOffset { get; set; }
    // 禁用：true 则执行时整步跳过（组合/嵌套组合同理）。持久化；UI 用勾选框切换（绑 Enabled）。
    private bool _disabled;
    public bool Disabled { get => _disabled; set { if (_disabled != value) { _disabled = value; Raise(nameof(Disabled)); Raise(nameof(Enabled)); } } }
    [JsonIgnore] public bool Enabled { get => !_disabled; set => Disabled = !value; }
    public int Wheel { get; set; }                        // 滚轮量（MouseWheel）
    public string TargetProcess { get; set; } = "";       // ActivateWindow：目标进程名(校验/跨重启回退用)
    public string TargetTitle { get; set; } = "";         // ActivateWindow：目标窗口标题(回退/显示用)
    public int TargetPid { get; set; }                    // ActivateWindow：选定窗口的进程 PID(同名多开靠它区分)
    public int LoopCount { get; set; } = 1;               // 1=一次, 0=无限, N=N 次
    // 动作自身重复之间的间隔（毫秒）。点击/点击坐标/滚轮在界面上叫「重复间隔」，与「点击次数/滚动次数」配套。
    // 只在实际重复时生效（次数=1 时不产生等待）。LoopDelayUnit 仅记录用户选的显示单位。
    public int LoopDelayMs { get; set; } = 1000;
    public int LoopDelayUnit { get; set; } = 1;           // 0毫秒 1秒 2分钟 3小时

    // 执行后跳转：JumpTarget=目标动作序号(1 起, 0=不跳)；JumpTimes=次数(0=无限)
    public int JumpTarget { get; set; }
    public int JumpTimes { get; set; }

    // 监听动作：本身也是完整动作（可含循环/运行条件/组合，且能再挂自己的监听——递归）。
    public MacroStep? SuccessAction { get; set; }
    public MacroStep? CompleteAction { get; set; }
    public MacroStep? FailAction { get; set; }

    // 运行条件：RunConditionType 支持 TimeRange / ImageMatch；RunConditionInvert=true 表示条件取反。
    // 时间用当天分钟数保存（0-1439），null 表示开放边界：仅开始=开始及之后，仅结束=结束及之前。
    public string RunConditionType { get; set; } = "";
    public bool RunConditionInvert { get; set; }
    public int? RunConditionStartMinute { get; set; }
    public int? RunConditionEndMinute { get; set; }
    // ImageMatch：目标图片(base64 PNG) + 绑定的屏幕(设备名) + 屏内相对像素矩形 + 相似度阈值(0-1)。
    // RectX/Y 为相对该屏左上角的像素偏移；运行时按该屏当前位置还原绝对区域，故挪动显示器后区域跟着屏走。
    public string RunConditionImage { get; set; } = "";
    public string RunConditionMonitor { get; set; } = "";
    public int RunConditionRectX { get; set; }
    public int RunConditionRectY { get; set; }
    public int RunConditionRectW { get; set; }
    public int RunConditionRectH { get; set; }
    public double RunConditionThreshold { get; set; } = 0.9;

    // Group 时的子动作（顺序执行）
    private ObservableCollection<MacroStep> _children = new();
    public ObservableCollection<MacroStep> Children
    {
        get => _children;
        set
        {
            _children.CollectionChanged -= OnChildrenChanged;
            _children = value ?? new ObservableCollection<MacroStep>();
            _children.CollectionChanged += OnChildrenChanged;
            Raise(nameof(Display));
        }
    }

    public string Note { get; set; } = "";

    public MacroStep()
    {
        _children.CollectionChanged += OnChildrenChanged;
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e) => Raise(nameof(Display));

    // ---- 仅 UI 用，不持久化 ----
    [JsonIgnore] public bool IsGroup => Type == "Group";
    [JsonIgnore] public bool HasJump => JumpTarget >= 1;
    [JsonIgnore] public bool HasListener => SuccessAction is not null || CompleteAction is not null || FailAction is not null;
    [JsonIgnore] public bool HasRunCondition => RunCondition.Has(this);   // 与方案级同一判定

    private bool _isChecked, _isExpanded, _isExecuting, _isFocused;
    private int _displayIndex;
    [JsonIgnore] public bool IsChecked { get => _isChecked; set { if (_isChecked != value) { _isChecked = value; Raise(nameof(IsChecked)); } } }
    [JsonIgnore] public bool IsExpanded { get => _isExpanded; set { if (_isExpanded != value) { _isExpanded = value; Raise(nameof(IsExpanded)); } } }
    [JsonIgnore] public bool IsExecuting { get => _isExecuting; set { if (_isExecuting != value) { _isExecuting = value; Raise(nameof(IsExecuting)); } } }
    [JsonIgnore] public bool IsFocused { get => _isFocused; set { if (_isFocused != value) { _isFocused = value; Raise(nameof(IsFocused)); } } }
    [JsonIgnore] public int DisplayIndex { get => _displayIndex; set { if (_displayIndex != value) { _displayIndex = value; Raise(nameof(DisplayIndex)); } } }

    [JsonIgnore] public string Display => ToString();

    public MacroStep Clone()
    {
        return new MacroStep
        {
            Type = Type, Button = Button, Key = Key, Modifier = Modifier,
            HoldMs = HoldMs, DurationMs = DurationMs, HoldUnit = HoldUnit, DurationUnit = DurationUnit, X = X, Y = Y, Wheel = Wheel,
            MoveMonitor = MoveMonitor, MoveNormX = MoveNormX, MoveNormY = MoveNormY, Humanize = Humanize, ClickOffset = ClickOffset, Disabled = Disabled,
            DragEndMonitor = DragEndMonitor, DragEndNormX = DragEndNormX, DragEndNormY = DragEndNormY,
            TargetProcess = TargetProcess, TargetTitle = TargetTitle, TargetPid = TargetPid,
            LoopCount = LoopCount, LoopDelayMs = LoopDelayMs, LoopDelayUnit = LoopDelayUnit,
            JumpTarget = JumpTarget, JumpTimes = JumpTimes, Note = Note,
            DisplayIndex = DisplayIndex,   // 运行页跑的是克隆副本，带上序号否则运行列表全显 0.（编辑页会 RefreshIndices 重算，不受影响）
            RunConditionType = RunConditionType,
            RunConditionInvert = RunConditionInvert,
            RunConditionStartMinute = RunConditionStartMinute,
            RunConditionEndMinute = RunConditionEndMinute,
            RunConditionImage = RunConditionImage,
            RunConditionMonitor = RunConditionMonitor,
            RunConditionRectX = RunConditionRectX, RunConditionRectY = RunConditionRectY,
            RunConditionRectW = RunConditionRectW, RunConditionRectH = RunConditionRectH,
            RunConditionThreshold = RunConditionThreshold,
            SuccessAction = SuccessAction?.Clone(),
            CompleteAction = CompleteAction?.Clone(),
            FailAction = FailAction?.Clone(),
            Children = new ObservableCollection<MacroStep>(System.Linq.Enumerable.Select(Children, c => c.Clone())),
        };
    }

    /// <summary>简易描述：有备注用备注，否则用动作本身的简述（不带循环/监听等后缀）。跳转目标下拉等紧凑场景用。</summary>
    [JsonIgnore] public string Brief => string.IsNullOrWhiteSpace(Note) ? BaseDesc() : Note.Trim();

    public override string ToString()
    {
        string desc = BaseDesc();
        string res = LoopCount switch { 1 => desc, 0 => $"{desc}（无限循环）", _ => $"{desc}（循环 {LoopCount} 次）" };
        // 运行条件不在动作流程缩略图中显示（仍在运行时生效、编辑对话框里可配）。
        if (HasListener)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (SuccessAction is not null) parts.Add("成功");
            if (CompleteAction is not null) parts.Add("结束");
            if (FailAction is not null) parts.Add("失败");
            res += "　· 监听 " + string.Join("·", parts);
        }
        // 备注不再拼到描述里——动作行模板已在最右侧单独显示备注（避免重复出现 "// 备注"）。
        return res;
    }

    // 动作本身的简述（ToString 的主体部分，无循环/监听后缀）。
    private string BaseDesc() => Type switch
    {
        "Group" => $"组合（{Children.Count} 个动作）",
        "Wait" => $"等待 {FormatMs(DurationMs)}",
        "MouseClick" => $"鼠标{ButtonCn(Button)}点击，按住 {FormatMs(HoldMs)}",
        "MouseMove" => MoveDisplay(),
        "MouseClickAt" => $"{MoveDisplay("点击")}，{ButtonCn(Button)}按住 {FormatMs(HoldMs)}",
        "MouseDrag" => DragDisplay(),
        "MouseWheel" => $"滚轮 {Wheel} 格",
        "KeyTap" => $"按键 {KeyCn()}，按住 {FormatMs(HoldMs)}",
        "ActivateWindow" => $"激活窗口 {WindowTargetCn()}",
        "Jump" => JumpTarget >= 1 ? (JumpTimes > 0 ? $"跳转到动作 {JumpTarget}（最多 {JumpTimes} 次）" : $"跳转到动作 {JumpTarget}") : "跳转（未设置目标）",
        _ => Type
    };

    private static string FormatMs(int ms)
    {
        if (ms != 0 && ms % 3600000 == 0) return $"{ms / 3600000} 小时";
        if (ms != 0 && ms % 60000 == 0) return $"{ms / 60000} 分钟";
        if (ms != 0 && ms % 1000 == 0) return $"{ms / 1000} 秒";
        return $"{ms} 毫秒";
    }

    // 拟人 / 落点偏移后缀（移动·点击坐标·拖动共用）。
    private string MoveSuffix()
    {
        var p = new System.Collections.Generic.List<string>();
        if (Humanize) p.Add("拟人");
        if (ClickOffset > 0) p.Add($"偏移±{ClickOffset}px");
        return p.Count == 0 ? "" : " · " + string.Join(" · ", p);
    }

    // verb：移动类动作说"移动到"，点击坐标说"点击"。
    private string MoveDisplay(string verb = "移动")
    {
        string suffix = MoveSuffix();
        if (string.IsNullOrEmpty(MoveMonitor)) return $"鼠标{verb}到 ({X}, {Y}){suffix}"; // 旧数据
        int i = MoveMonitor.LastIndexOf('\\');
        string mon = i >= 0 ? MoveMonitor[(i + 1)..] : MoveMonitor;
        return $"鼠标{verb}到 {mon}（{MoveNormX * 100:0.#}%, {MoveNormY * 100:0.#}%）{suffix}";
    }

    // 拖动：起点 → 终点（同屏时终点只报百分比，跨屏才带屏名）。
    private string DragDisplay()
    {
        string Pct(double nx, double ny) => $"{nx * 100:0.#}%, {ny * 100:0.#}%";
        static string Short(string dev)
        {
            int i = dev.LastIndexOf('\\');
            return i >= 0 ? dev[(i + 1)..] : dev;
        }
        string from = string.IsNullOrEmpty(MoveMonitor) ? Pct(MoveNormX, MoveNormY) : $"{Short(MoveMonitor)}（{Pct(MoveNormX, MoveNormY)}）";
        string to = string.IsNullOrEmpty(DragEndMonitor) || DragEndMonitor == MoveMonitor
            ? $"（{Pct(DragEndNormX, DragEndNormY)}）"
            : $"{Short(DragEndMonitor)}（{Pct(DragEndNormX, DragEndNormY)}）";
        return $"{ButtonCn(Button)}拖动 {from} → {to}" + MoveSuffix();
    }

    private string WindowTargetCn()
    {
        if (TargetProcess == "__DESKTOP__")   // 桌面哨兵：TargetTitle 为屏幕设备名(空=不限屏)
            return string.IsNullOrEmpty(TargetTitle) ? "桌面（所有应用失活）" : $"桌面 · {TargetTitle}";
        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(TargetTitle)) parts.Add($"「{TargetTitle}」");
        if (!string.IsNullOrWhiteSpace(TargetProcess)) parts.Add($"{TargetProcess}.exe");
        if (TargetPid > 0) parts.Add($"(PID {TargetPid})");
        return parts.Count == 0 ? "(未指定)" : string.Join(" ", parts);
    }

    private string KeyCn() => Modifier == 0 ? Key : $"{ModifierCn(Modifier)} + {Key}";

    private static string ModifierCn(byte m)
    {
        var p = new System.Collections.Generic.List<string>();
        if ((m & 0x01) != 0) p.Add("左Ctrl");
        if ((m & 0x02) != 0) p.Add("左Shift");
        if ((m & 0x04) != 0) p.Add("左Alt");
        if ((m & 0x08) != 0) p.Add("左Win");
        if ((m & 0x10) != 0) p.Add("右Ctrl");
        if ((m & 0x20) != 0) p.Add("右Shift");
        if ((m & 0x40) != 0) p.Add("右Alt");
        if ((m & 0x80) != 0) p.Add("右Win");
        return string.Join("+", p);
    }

    private static string ButtonCn(string b) => b switch { "Left" => "左键", "Right" => "右键", "Middle" => "中键", _ => b };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
