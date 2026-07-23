using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using MacroPilot.Input;
using MacroPilot.Models;
using MacroPilot.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Button = System.Windows.Controls.Button;

namespace MacroPilot;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MacroDocument _doc;
    private readonly ObservableCollection<MacroPlan> _plans;
    private readonly ObservableCollection<LogEntry> _logs = new();
    private MacroPlan? _plan;
    private MacroStep? _step;
    private MacroStep? _clip;
    private MacroPlan? _clipPlan;
    private MacroRunner? _runner;
    private IInputBackend? _backend;
    private bool _loading;
    private bool _planRightClickOnItem;
    private bool _stepRightClickOnItem;
    private List<Ch9329PortInfo> _ch9329Ports = new();
    // 跳转目标按“对象身份”跟随增删/排序：记住上次 RefreshIndices 时的顶层顺序，
    // 每次结构变更后把 JumpTarget(旧序号)→目标对象→新序号 重映射；目标被删/入组则清 0。
    private List<MacroStep> _jumpOrder = new();
    private MacroPlan? _jumpOrderPlan;
    // 撤销快照：方案级（名称 + 动作列表 + 循环次数 + 间隔），所有方案属性修改都可撤销。
    // AddedPlan 非空 → 这是一条“新增方案”的结构撤销项（撤销即移除该方案）；否则是当前方案内的动作编辑快照。
    // Condition 是方案级运行条件的快照（用一个空壳 MacroPlan 承载，仅取其 IRunCondition 部分）。
    // 方案设置对话框会一次性改动"循环 + 条件"，两者必须一起进撤销栈，否则撤销只回退一半。
    private sealed record UndoSnap(string Name, List<MacroStep> Steps, int LoopCount, int LoopDelayMs,
                                   MacroPlan? AddedPlan = null, MacroPlan? Condition = null);
    // 定长撤销栈：满了丢最旧一条（O(1)），不再每次超限 Take(50)+清空+逐条回推。
    private sealed class CappedStack<T>
    {
        private readonly LinkedList<T> _l = new();
        private readonly int _cap;
        public CappedStack(int cap) => _cap = cap;
        public int Count => _l.Count;
        public void Clear() => _l.Clear();
        public void Push(T v) { _l.AddLast(v); if (_l.Count > _cap) _l.RemoveFirst(); }
        public T Pop() { var v = _l.Last!.Value; _l.RemoveLast(); return v; }
    }
    private readonly CappedStack<UndoSnap> _undo = new(50);

    public static readonly DependencyProperty IsBatchModeProperty =
        DependencyProperty.Register(nameof(IsBatchMode), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool IsBatchMode { get => (bool)GetValue(IsBatchModeProperty); set => SetValue(IsBatchModeProperty, value); }

    private static bool _cutAllRegistered;
    // 输入框内 Ctrl+X：两种情况都自己处理，确保可靠剪切/清空（不依赖原生 Cut 命令是否触发）。
    // 有选区 → 剪切选区；无选区 → 剪切整框内容。
    private static void OnTextBoxCutAll(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.X || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (sender is not TextBox tb || tb.IsReadOnly || tb.Text.Length == 0) return;
        if (tb.SelectionLength > 0)
        {
            try { Clipboard.SetText(tb.SelectedText); } catch { }
            int start = tb.SelectionStart;
            tb.Text = tb.Text.Remove(start, tb.SelectionLength);
            tb.CaretIndex = Math.Min(start, tb.Text.Length);
        }
        else
        {
            try { Clipboard.SetText(tb.Text); } catch { }
            tb.Clear();
        }
        e.Handled = true;
    }

    public MainWindow(MacroDocument doc)
    {
        _doc = doc;
        _plans = new ObservableCollection<MacroPlan>(doc.Plans);
        _loading = true; // 防止 InitializeComponent 期间各 ComboBox 默认选中触发 SelectionChanged 改写设置/主题
        InitializeComponent();
        // 窗口几何记忆：主窗口与所有对话框共用 WindowMemory（对话框在 MakeDialog 里统一挂）。
        // 必须在 Show 之前挂，否则会看到窗口先按默认位置弹出再跳。
        WindowMemory.Init(_doc, PersistSettings);
        WindowMemory.Attach(this, "Main");
        // Ctrl+X 未选中文本时剪切整个输入框内容（原生仅剪切选区，无选区则无动作 → 无法一键清空）。
        if (!_cutAllRegistered)
        {
            _cutAllRegistered = true;
            EventManager.RegisterClassHandler(typeof(TextBox), PreviewKeyDownEvent, new KeyEventHandler(OnTextBoxCutAll));
        }
        PlansList.ItemsSource = _plans;
        LogList.ItemsSource = _logs;
        SnapshotAllPlans(); // 记录已加载方案的初始快照，作为"未保存"比较基准
        LoadSettingsToUi();
        // 打开 UI 之前：检测 CH9329（含桥片，未知则弹窗命名并持久化）、配置输出方式可用性、无设备则告警。
        RescanDevices(promptUnknown: true, alertIfNone: true);

        Loaded += (_, _) =>
        {
            ThemeManager.ApplyWindowTitleBar(this, ThemeManager.EffectiveDark);
            UpdateThemeToggle();
            SetNav(NavPlans, PagePlans);
            if (_plans.Count > 0) PlansList.SelectedIndex = 0; else ShowNoPlan();
            AddLog("Info", _plans.Count == 0 ? "就绪。请先新建方案。" : "已自动加载保存的方案。");
            AddLog("Info", App.IsRunningAsAdmin() ? "当前以管理员权限运行。" : "当前为普通权限。");
            OvVersionText.Text = "版本 " + Services.UpdateService.CurrentVersionText;
            ShowCurrentChangelog();
            ReportLastUpdateFailure();  // 上次就地更新若失败过，把原因摆出来
            _ = StartupUpdateCheck();   // 启动后静默查一次更新，有新版才弹窗提示
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 热键 F9/F10/F11 不再启动即全局注册——改到方案执行时才装（见 EnableHotkeys，由 RunPlan 调用），
        // 运行结束/停止即注销（OnRunFinished）。平时把这三个键还给系统，也不让低级键盘钩子常挂输入链。
    }

    // 概况页版本卡片：版本号下方直接列出本版更新点。本地调试版号不在 changelog.json 里时隐藏列表。
    private void ShowCurrentChangelog()
    {
        var entry = Services.Changelog.Current;
        if (entry == null || entry.Notes.Count == 0) { OvChangelogList.Visibility = Visibility.Collapsed; return; }
        OvChangelogDate.Text = entry.Date;
        OvChangelogList.ItemsSource = entry.Notes;
        OvChangelogList.Visibility = Visibility.Visible;
    }

    // ================= 导航 =================
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        UIElement? target = sender == NavOverview ? PageOverview :
            sender == NavConfig ? PageConfig :
            sender == NavPlans ? PagePlans :
            sender == NavRun ? PageRun : null;
        if (target != PagePlans && PagePlans.Visibility == Visibility.Visible && !EnsureCurrentPlanClean()) return;

        if (sender == NavOverview) { SetNav(NavOverview, PageOverview); RefreshAvailabilityUI(false); }
        else if (sender == NavConfig) { SetNav(NavConfig, PageConfig); RefreshAvailabilityUI(false); }
        else if (sender == NavPlans) SetNav(NavPlans, PagePlans);
        else if (sender == NavRun) SetNav(NavRun, PageRun);
    }

    private void SetNav(Button nav, UIElement page)
    {
        foreach (var b in new[] { NavOverview, NavConfig, NavPlans, NavRun }) b.Tag = b == nav ? "active" : null;
        PageOverview.Visibility = page == PageOverview ? Visibility.Visible : Visibility.Collapsed;
        PageConfig.Visibility = page == PageConfig ? Visibility.Visible : Visibility.Collapsed;
        PagePlans.Visibility = page == PagePlans ? Visibility.Visible : Visibility.Collapsed;
        PageRun.Visibility = page == PageRun ? Visibility.Visible : Visibility.Collapsed;
        AnimatePageIn(page);
    }

    // 页面切换轻动效：160ms 淡入 + 上移 8px 缓出。取代纯 Visibility 硬切，质感立刻不同。
    private void AnimatePageIn(UIElement page)
    {
        if (page.Visibility != Visibility.Visible) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        var tt = new TranslateTransform();
        page.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        // 在 浅/深 间切换（与下拉同步）
        bool toDark = !ThemeManager.EffectiveDark;
        _doc.Theme = toDark ? "Dark" : "Light";
        ApplyTheme();
        _loading = true; SelectByTag(ThemeCombo, _doc.Theme switch { "Light" => "浅色", "Dark" => "深色", _ => "跟随系统" }, byContent: true); _loading = false;
        PersistSettings();
    }

    private void ApplyTheme()
    {
        ThemeManager.Apply(_doc.Theme);
        ThemeManager.ApplyWindowTitleBar(this, ThemeManager.EffectiveDark);
        UpdateThemeToggle();
    }

    // 切换按钮图标随生效主题变化：亮=太阳(E706)，暗=月亮(E708)。
    private void UpdateThemeToggle()
    {
        if (ThemeToggleButton != null)
            ThemeToggleButton.Content = ThemeManager.EffectiveDark ? "" : "";
    }

    // ================= 配置 =================
    private void LoadSettingsToUi()
    {
        _loading = true;
        BackendCombo.SelectedIndex = string.Equals(_doc.Backend, "Native", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        SimTypeCombo.SelectedIndex = string.Equals(_doc.NativeKeyMode, "VirtualKey", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        BaudText.Text = _doc.BaudRate.ToString();
        ExecDelayText.Text = (_doc.ExecutionDelayMs / 1000.0).ToString();
        JitterCheck.IsChecked = _doc.TimingJitterEnabled;
        JitterText.Text = _doc.TimingJitterMs.ToString();
        JitterPanel.Visibility = _doc.TimingJitterEnabled ? Visibility.Visible : Visibility.Collapsed;
        AdminModeCheck.IsChecked = _doc.RunAsAdmin;
        ThemeCombo.SelectedIndex = _doc.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        UpdateBackendPanels(); // PortCombo 由 RescanDevices 填充
        UpdateDataDirText();
        _loading = false;
    }

    private void UpdateBackendPanels()
    {
        if (SerialPanel == null || NativePanel == null || BackendCombo == null) return; // InitializeComponent 期间控件未就绪
        bool serial = BackendCombo.SelectedIndex == 0;
        SerialPanel.Visibility = serial ? Visibility.Visible : Visibility.Hidden;
        NativePanel.Visibility = serial ? Visibility.Hidden : Visibility.Visible;
    }

    // 扫描 CH9329（含桥片），填充端口下拉；promptUnknown=对未知桥片弹窗命名并持久化；alertIfNone=无任何输出设备时告警。
    private bool _scanning;
    private async void RescanDevices(bool promptUnknown, bool alertIfNone)
    {
        // 运行期间 COM 被自己占着，Probe 必然打不开 → CH9329 会"消失"→ 自动切成 Native 并写进 _doc，
        // 下次就悄悄变软件模拟了。运行中直接不扫。
        if (_runner is { IsRunning: true }) { ShowToast("运行中，暂不重新检测设备"); return; }
        if (_scanning) return;   // 扫描进行中，忽略重复触发
        _scanning = true;
        try
        {
        var preferred = (PortCombo.SelectedItem as string) ?? _doc.Port;
        var bauds = new[] { _doc.BaudRate > 0 ? _doc.BaudRate : 9600, 9600, 115200 };
        var known = new Dictionary<string, string>(_doc.KnownBridges);   // 快照给后台线程，别在别的线程碰 UI 状态
        if (BridgeLabel != null) BridgeLabel.Text = "正在检测…";
        // 扫描（每口 open+60ms 探测）放后台线程，先把窗口显示出来，扫完再刷新下拉。
        _ch9329Ports = await System.Threading.Tasks.Task.Run(() => Ch9329Scanner.Scan(bauds, known));

        _loading = true;
        PortCombo.Items.Clear();
        foreach (var info in _ch9329Ports) PortCombo.Items.Add(info.Port);
        if (_ch9329Ports.Count > 0)
            PortCombo.SelectedItem = (!string.IsNullOrWhiteSpace(preferred) && PortCombo.Items.Contains(preferred)) ? preferred : _ch9329Ports[0].Port;
        _loading = false;

        if (_ch9329Ports.Count > 0)
        {
            _doc.Port = PortCombo.SelectedItem as string ?? _doc.Port;
            var sel = _ch9329Ports.FirstOrDefault(p => p.Port == _doc.Port) ?? _ch9329Ports[0];
            _doc.BaudRate = sel.Baud;
        }

        if (promptUnknown) PromptUnknownBridges();
        PersistSettings();

        var cur = _ch9329Ports.FirstOrDefault(p => p.Port == _doc.Port) ?? _ch9329Ports.FirstOrDefault();
        if (BaudText != null) BaudText.Text = (cur?.Baud ?? _doc.BaudRate).ToString();
        if (BridgeLabel != null) BridgeLabel.Text = cur != null ? $"桥片：{cur.BridgeName}" : "未检测到 CH9329";

        if (_ch9329Ports.Count == 1)
            AddLog("Info", $"已识别到 CH9329 设备：{_ch9329Ports[0].Port}（桥片 {_ch9329Ports[0].BridgeName}）。");
        else if (_ch9329Ports.Count > 1)
            AddLog("Info", $"识别到多个 CH9329 设备：{string.Join("，", _ch9329Ports.Select(p => p.Port))}，已选中 {PortCombo.SelectedItem}。");
        else
            AddLog("Info", "未识别到 CH9329 设备。");

        RefreshAvailabilityUI(alertIfNone);
        }
        finally { _scanning = false; }
    }

    // 对未知桥片逐一弹窗让用户命名，命名后写入 KnownBridges（持久化），并就地刷新 _ch9329Ports 的桥片名。
    private void PromptUnknownBridges()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var info in _ch9329Ports)
        {
            if (!info.BridgeKnown && !string.IsNullOrEmpty(info.VidPid) && seen.Add(info.VidPid))
            {
                var name = Prompt(this, "识别桥片", $"检测到 CH9329（{info.Port}），但桥片未知（{info.VidPid}）。请输入桥片名称：", "");
                if (!string.IsNullOrWhiteSpace(name)) { _doc.KnownBridges[info.VidPid] = name.Trim(); changed = true; }
            }
        }
        if (changed)
        {
            for (int i = 0; i < _ch9329Ports.Count; i++)
                if (_doc.KnownBridges.TryGetValue(_ch9329Ports[i].VidPid, out var nm))
                    _ch9329Ports[i] = _ch9329Ports[i] with { BridgeName = nm, BridgeKnown = true };
        }
    }

    // 当前选中口的标签："COMx · 桥片名"。
    private string SelectedPortLabel()
    {
        var sel = PortCombo.SelectedItem as string;
        var info = _ch9329Ports.FirstOrDefault(p => p.Port == sel) ?? _ch9329Ports.FirstOrDefault();
        return info != null ? $"{info.Port} · {info.BridgeName}" : (sel ?? "");
    }

    // 输出方式可用性 + 概况设备状态。无 CH9329 则隐藏其选项；本机键鼠不可用则禁用并提示；两者皆无则告警。
    private void RefreshAvailabilityUI(bool alertIfNone)
    {
        if (SerialBackendItem == null || NativeBackendItem == null || BackendCombo == null) return;
        bool serialOk = _ch9329Ports.Count > 0;
        bool nativeOk = NativeInputAvailable();

        SerialBackendItem.Visibility = serialOk ? Visibility.Visible : Visibility.Collapsed;
        NativeBackendItem.IsEnabled = nativeOk;

        // 当前选中的输出方式不可用 → 自动切到可用的另一个
        if (!serialOk && BackendCombo.SelectedItem == SerialBackendItem && nativeOk)
        {
            _loading = true; BackendCombo.SelectedItem = NativeBackendItem; _loading = false;
            _doc.Backend = "Native"; UpdateBackendPanels();
        }
        else if (!nativeOk && BackendCombo.SelectedItem == NativeBackendItem && serialOk)
        {
            _loading = true; BackendCombo.SelectedItem = SerialBackendItem; _loading = false;
            _doc.Backend = "Serial"; UpdateBackendPanels();
        }

        NoDeviceHint.Text = nativeOk ? "" : "「本机键鼠」不可用：未检测到键盘 / 鼠标。";
        NoDeviceHint.Visibility = nativeOk ? Visibility.Collapsed : Visibility.Visible;

        // 概况页设备状态
        OvSerialStatus.Visibility = serialOk ? Visibility.Visible : Visibility.Collapsed;
        if (serialOk) SetDeviceStatus(OvSerialStatus, true, $"CH9329 设备：可用（{SelectedPortLabel()}）");
        SetDeviceStatus(OvNativeStatus, nativeOk, nativeOk ? "本机键鼠：可用" : "本机键鼠：不可用（未检测到键盘 / 鼠标）");

        if (alertIfNone && !serialOk && !nativeOk)
            ThemedDialog.Show("未检测到可用的输出设备：未握手到 CH9329，也没有键盘 / 鼠标。", "无可用输出设备",
                MessageBoxButton.OK, MessageBoxImage.Exclamation);
    }

    private void BackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _doc.Backend = BackendCombo.SelectedItem == NativeBackendItem ? "Native" : "Serial";
        UpdateBackendPanels(); PersistSettings(); RefreshAvailabilityUI(false);
    }
    // 重新检测：重跑输出方式检查（扫描 CH9329 + 桥片、未知桥片弹窗命名、刷新可用性、无设备告警）。
    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
        => RescanDevices(promptUnknown: true, alertIfNone: true);
    private void PortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _doc.Port = PortCombo.SelectedItem as string ?? _doc.Port; PersistSettings();
    }
    private void Baud_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _doc.BaudRate = ParseInt(BaudText.Text, _doc.BaudRate); PersistSettings();
    }
    private void SimTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _doc.NativeKeyMode = SimTypeCombo.SelectedIndex == 1 ? "VirtualKey" : "ScanCode"; PersistSettings();
    }
    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _doc.Theme = ThemeCombo.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
        ApplyTheme(); PersistSettings();
    }
    private void ExecDelay_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (double.TryParse(ExecDelayText.Text.Trim(), out var sec)) _doc.ExecutionDelayMs = (int)(Math.Max(0, sec) * 1000);
        PersistSettings();
    }
    private void JitterEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _doc.TimingJitterEnabled = JitterCheck.IsChecked == true;
        JitterPanel.Visibility = _doc.TimingJitterEnabled ? Visibility.Visible : Visibility.Collapsed;
        PersistSettings();
    }
    private void Jitter_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (double.TryParse(JitterText.Text.Trim(), out var ms)) _doc.TimingJitterMs = Math.Clamp(ms, 0, 5000);
        JitterText.Text = _doc.TimingJitterMs.ToString();
        PersistSettings();
    }
    private static int StepDir(object sender) => (sender as FrameworkElement)?.Tag as string == "down" ? -1 : 1;
    private static string Fmt(double v) => v == Math.Floor(v) ? ((long)v).ToString() : v.ToString();
    private void ExecDelayStep_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        double.TryParse(ExecDelayText.Text.Trim(), out var sec);
        sec = Math.Clamp(Math.Round(sec + StepDir(sender), 3), 0, 9999);
        _doc.ExecutionDelayMs = (int)(sec * 1000);
        ExecDelayText.Text = Fmt(sec);
        PersistSettings();
    }
    private void JitterStep_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        double.TryParse(JitterText.Text.Trim(), out var ms);
        _doc.TimingJitterMs = Math.Clamp(ms + StepDir(sender), 0, 5000);
        JitterText.Text = _doc.TimingJitterMs.ToString();
        PersistSettings();
    }
    private void AdminMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool want = AdminModeCheck.IsChecked == true;
        _doc.RunAsAdmin = want; PersistSettings();
        bool isAdmin = App.IsRunningAsAdmin();
        if (want)
        {
            if (isAdmin)
            {
                ThemedDialog.Show("当前已是管理员身份运行，设置已保存。", "管理员模式", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (ThemedDialog.Show("已启用管理员模式。需要重启程序才能生效。\n是否立即以管理员身份重启？",
                         "管理员模式", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                if (App.RelaunchAsAdmin()) { Application.Current.Shutdown(); }
                else ThemedDialog.Show("提权被取消或失败，将在下次启动时再次尝试。", "管理员模式", MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
            else
            {
                AddLog("Info", "管理员模式已启用，将在下次启动时生效。");
            }
        }
        else if (isAdmin)
        {
            ThemedDialog.Show("已关闭管理员模式，将在下次启动时以普通权限运行。", "管理员模式", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ---- 数据文件夹：打开 / 更改位置 / 恢复默认（含一键迁移）----
    private void UpdateDataDirText()
    {
        if (DataDirText == null) return;
        DataDirText.Text = "当前：" + Storage.DataDir + (Storage.IsCustomDir ? "" : "（默认）");
        if (ResetDataDirButton != null) ResetDataDirButton.IsEnabled = Storage.IsCustomDir;
    }
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Storage.DataDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Storage.DataDir, UseShellExecute = true });
        }
        catch (Exception ex) { ThemedDialog.Show("无法打开数据文件夹：" + ex.Message, "提示"); }
    }
    private void ChangeDataDir_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureCurrentPlanClean()) return;   // 先把当前未保存方案存到旧位置，避免切换时丢失
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择数据保存文件夹" };
        try { if (Directory.Exists(Storage.DataDir)) dlg.InitialDirectory = Storage.DataDir; } catch { }
        if (dlg.ShowDialog(this) != true) return;
        var newDir = dlg.FolderName;
        if (string.IsNullOrWhiteSpace(newDir)) return;
        if (string.Equals(Path.GetFullPath(newDir), Path.GetFullPath(Storage.DataDir), StringComparison.OrdinalIgnoreCase))
        { ThemedDialog.Show("所选目录就是当前数据目录。", "提示"); return; }

        var r = ThemedDialog.Show($"将数据保存位置更改为：\n{newDir}\n\n是否把当前方案与日志一并迁移过去？\n（选“否”则不动该目录已有数据，直接加载它）", "更改数据位置", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return;
        if (r == MessageBoxResult.Yes && !Storage.MigrateTo(newDir, out var mErr))
        { ThemedDialog.Show("迁移失败：" + mErr, "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
        if (!Storage.SetDataDir(newDir, out var sErr))
        { ThemedDialog.Show("设置失败：" + sErr, "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
        // 注意：选“否”时不再向新目录写当前数据（不覆盖目标）。直接从新位置重新加载方案。
        ReloadPlansFromDataDir();
        UpdateDataDirText();
        ThemedDialog.Show("已更新数据路径并重新加载数据。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void ResetDataDir_Click(object sender, RoutedEventArgs e)
    {
        if (!Storage.IsCustomDir) return;
        if (!EnsureCurrentPlanClean()) return;
        var r = ThemedDialog.Show($"恢复默认数据位置：\n{Storage.DefaultDir}\n\n是否把当前方案与日志一并迁移回默认位置？\n（选“否”则不动默认目录已有数据，直接加载它）", "恢复默认", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return;
        if (r == MessageBoxResult.Yes && !Storage.MigrateTo(Storage.DefaultDir, out var mErr))
        { ThemedDialog.Show("迁移失败：" + mErr, "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
        Storage.SetDataDir(null, out _);
        ReloadPlansFromDataDir();
        UpdateDataDirText();
        ThemedDialog.Show("已更新数据路径并重新加载数据。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // 从（新）数据目录重新加载方案列表，刷新界面；只刷新方案，保留当前设置（配置与方案独立）。
    private void ReloadPlansFromDataDir()
    {
        var loaded = Storage.LoadDirect();   // 只读新位置 plans.json，不做参考版导入
        _plan = null;
        _undo.Clear(); UndoButton.IsEnabled = false;
        _plans.Clear();
        foreach (var p in loaded.Plans) _plans.Add(p);
        SnapshotAllPlans();                  // 以加载内容为已保存基准
        if (_plans.Count > 0) PlansList.SelectedIndex = 0; else ShowNoPlan();
        RefreshSaveState();
    }

    // 设备状态高亮：可用 → 实心圆点 + Success（绿）；不可用 → 空心圆点 + Muted。用 SetResourceReference 以随主题切换。
    private static void SetDeviceStatus(System.Windows.Controls.TextBlock? target, bool ok, string text)
    {
        if (target == null) return;
        target.Text = (ok ? "● " : "○ ") + text;
        target.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, ok ? "Success" : "Muted");
    }

    // 是否存在可用的本机键盘/鼠标（SM_MOUSEPRESENT=19 / 键盘类型）。
    private static bool NativeInputAvailable()
    {
        try { return GetSystemMetrics(19) != 0 || GetKeyboardType(0) != 0; }
        catch { return true; }
    }

    // ================= 方案 =================
    private bool _revertingSelection;
    private void PlansList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_revertingSelection) return;
        var newPlan = PlansList.SelectedItem as MacroPlan;
        // 切走前：若旧方案有未保存修改，提示保存/放弃/取消（取消则撤销本次切换）。
        if (_plan != null && newPlan != _plan && !EnsureCurrentPlanClean())
        {
            _revertingSelection = true;
            PlansList.SelectedItem = _plan;
            _revertingSelection = false;
            return;
        }
        _plan = newPlan;
        if (_plan == null) { ShowNoPlan(); return; }
        StepsPanel.Visibility = Visibility.Visible; NoPlanPanel.Visibility = Visibility.Collapsed;
        ExportCurrentButton.IsEnabled = true;
        StepsList.ItemsSource = _plan.Steps;
        _loading = true;
        _loading = false;
        RefreshIndices();
        RefreshSaveState();
        RefreshPlanSummary();
        _undo.Clear(); UndoButton.IsEnabled = false;
        AddLog("Info", "已切换到：" + _plan.Name);
    }

    private void ShowNoPlan()
    {
        _plan = null; StepsList.ItemsSource = null;
        StepsPanel.Visibility = Visibility.Collapsed; NoPlanPanel.Visibility = Visibility.Visible;
        ExportCurrentButton.IsEnabled = false;
        RefreshPlanSummary();
    }

    private void NewPlanIcon_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureCurrentPlanClean()) return;   // 先处理掉当前未保存方案，保证最多一个未保存
        var name = Prompt(this, "新建方案", "方案名称", $"方案 {_plans.Count + 1}");
        if (name == null) return;
        var p = new MacroPlan { Name = string.IsNullOrWhiteSpace(name) ? $"方案 {_plans.Count + 1}" : name };
        AddPlanUnsaved(p, -1);
    }

    // 新增方案：不落盘、标记为"未保存"（SavedSnapshot 留空 → 脏），由用户手动保存或退出时统一保存。
    private void AddPlanUnsaved(MacroPlan p, int index)
    {
        if (index < 0 || index > _plans.Count) _plans.Add(p); else _plans.Insert(index, p);
        PlansList.SelectedItem = p;    // 切换会清空 _undo，故结构撤销项要在其后再入栈
        p.SavedSnapshot = null;
        RecomputeDirty(p);             // → Dirty=true
        PlansList.Items.Refresh();
        SaveButton.IsEnabled = true;
        // 新增/粘贴方案可撤销：入一条结构撤销项，点“撤销”即移除刚加入的方案。
        _undo.Push(new UndoSnap("", new List<MacroStep>(), 0, 0, p));
        UndoButton.IsEnabled = true;
    }
    private void RenamePlanIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) return;
        var name = Prompt(this, "重命名方案", "方案名称", _plan.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name == _plan.Name) return;
        PushUndo();
        _plan.Name = name; PlansList.Items.Refresh(); MarkDirty();
    }
    private void DeletePlanIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) return;
        if (!EnsureCurrentPlanClean()) return;
        if (_plan == null) return; // 未保存的新方案选择“放弃”后已被移除
        if (ThemedDialog.Show($"确定删除“{_plan.Name}”吗？", "删除方案", MessageBoxButton.OKCancel, MessageBoxImage.Exclamation) != MessageBoxResult.OK) return;
        var del = _plan; _plan = null; // 先置空，避免切换选择时对已删方案弹"未保存"
        _plans.Remove(del);
        if (_plans.Count > 0) PlansList.SelectedIndex = 0; else ShowNoPlan();
        Save();   // 删除属结构改动，立即提交（此前已确保最多一个未保存方案）
    }
    // 方案设置：循环次数 / 间隔 / 运行条件三合一（运行条件与动作级共用同一套控件与逻辑）。
    private void PlanSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) return;
        PushUndo();   // 循环/条件改动可撤销（对话框取消时下面会把这次快照弹掉）
        if (ShowPlanSettingsDialog(_plan)) { RefreshPlanSummary(); MarkDirty(); }
        else if (_undo.Count > 0) { _undo.Pop(); UndoButton.IsEnabled = _undo.Count > 0; }
    }

    // 标题栏摘要：把收进对话框的设置用一行小字回显，免得点开才知道当前配置。
    private void RefreshPlanSummary()
    {
        if (PlanSummaryText == null) return;
        if (_plan == null) { PlanSummaryText.Text = ""; PlanSettingsButton.IsEnabled = false; return; }
        PlanSettingsButton.IsEnabled = true;
        int unit = Math.Clamp(_plan.LoopDelayUnit, 0, 3);
        string loops = _plan.LoopCount == 0 ? "无限循环" : $"循环 {_plan.LoopCount} 次";
        string delay = _plan.LoopDelayMs > 0 ? $" · 间隔 {FormatDelayValue(_plan.LoopDelayMs, unit)}{LoopUnitNames[unit]}" : "";
        string cond = _plan.HasRunCondition ? "  · 已设运行条件" : "";
        PlanSummaryText.Text = loops + delay + cond;
        // 有运行条件时整行用强调色，一眼看出这个方案不是无条件执行的。
        PlanSummaryText.Foreground = _plan.HasRunCondition ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
    }
    private void PlansList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) DeletePlanIcon_Click(sender, e);
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) CopyCurrentPlan();
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            int idx = _plan != null ? _plans.IndexOf(_plan) + 1 : _plans.Count;   // 粘到当前方案之后
            PastePlan(idx);
        }
    }
    // 循环间隔单位：0=毫秒 1=秒 2=分钟 3=小时。LoopDelayMs 始终以毫秒存储。
    private static readonly double[] LoopUnitFactors = { 1, 1000, 60000, 3600000 };
    private static readonly string[] LoopUnitNames = { "毫秒", "秒", "分钟", "小时" };
    private static double LoopUnitFactor(int idx) => LoopUnitFactors[Math.Clamp(idx, 0, LoopUnitFactors.Length - 1)];
    private static string FormatDelayValue(int ms, int unitIdx)
    {
        double v = ms / LoopUnitFactor(unitIdx);
        return v % 1.0 == 0 ? ((long)v).ToString() : v.ToString("0.###");
    }

    // ================= 动作 =================
    private void PushUndo()
    {
        if (_plan == null) return;
        var cond = new MacroPlan(); RunCondition.Copy(_plan, cond);
        _undo.Push(new UndoSnap(_plan.Name, _plan.Steps.Select(s => s.Clone()).ToList(), _plan.LoopCount, _plan.LoopDelayMs, null, cond));
        UndoButton.IsEnabled = _undo.Count > 0;
    }
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        var snap = _undo.Pop();
        if (snap.AddedPlan is { } added)
        {
            // 撤销“新增/粘贴方案”：移除该方案并选中相邻方案。置空 _plan 避免切换时对将删方案弹未保存。
            int i = _plans.IndexOf(added);
            if (i >= 0)
            {
                _plan = null;
                _plans.Remove(added);
                if (_plans.Count > 0) PlansList.SelectedIndex = Math.Min(i, _plans.Count - 1); else ShowNoPlan();
            }
            UndoButton.IsEnabled = _undo.Count > 0;   // 切换已清空 _undo，这里据实回填
            AddLog("Info", "已撤销：新增方案。");
            return;
        }
        if (_plan == null) return;
        _plan.Name = snap.Name;
        _plan.Steps.Clear();
        foreach (var s in snap.Steps) _plan.Steps.Add(s);
        _plan.LoopCount = snap.LoopCount;
        _plan.LoopDelayMs = snap.LoopDelayMs;
        if (snap.Condition != null) RunCondition.Copy(snap.Condition, _plan);
        RefreshPlanSummary();
        _jumpOrderPlan = null;   // 恢复的是快照克隆（新对象），其 JumpTarget 已对应恢复后的顺序，走基线不重映射
        RefreshIndices(); UndoButton.IsEnabled = _undo.Count > 0; MarkDirty();
        AddLog("Info", "已撤销上一步修改。");
    }

    private void OpenAddAction_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) { ThemedDialog.Show("请先新建/选择一个方案。", "提示"); return; }
        var s = ShowAddActionDialog();
        if (s != null)
        {
            PushUndo(); _plan.Steps.Add(s); RefreshIndices(); StepsList.SelectedItem = s; MarkDirty();
        }
    }
    private void StepsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 双击：命中的是组合子动作 → 编辑子动作；否则编辑该顶层动作/组合（参考标准逻辑）。
        if ((e.OriginalSource as FrameworkElement)?.DataContext is MacroStep s)
        {
            e.Handled = true;
            if (FindParentGroup(s) != null) { EditChild(s); return; }
            StepsList.SelectedItem = s;
            EditSelected();
        }
    }
    private void StepEdit_Click(object sender, RoutedEventArgs e) => EditSelected();
    // 编辑：组合用组合编辑对话框，叶子动作用动作编辑对话框；对话框返回新对象，整项替换。
    private void EditSelected()
    {
        if (_plan == null || _step == null) return;
        int idx = _plan.Steps.IndexOf(_step);
        if (idx < 0) return;
        var edited = _step.IsGroup ? ShowEditGroupDialog(_step) : ShowAddActionDialog(_step);
        if (edited != null)
        {
            if (SameStepContent(edited, _step)) return;
            PushUndo();
            // 编辑=整项替换成新对象；把基线里旧对象的身份过继给新对象，别让"指向被编辑步"的跳转丢失。
            int jo = _jumpOrder.IndexOf(_step);
            if (jo >= 0) _jumpOrder[jo] = edited;
            _plan.Steps[idx] = edited;
            RefreshIndices(); StepsList.SelectedIndex = idx; MarkDirty();
        }
    }

    private void StepsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { _step = StepsList.SelectedItem as MacroStep; SetFocusedStep(_step); }
    private void StepsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) StepDelete_Click(sender, e);
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) StepCopy_Click(sender, e);
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) StepPaste_Click(sender, e);
    }

    // 复制目标以“聚焦节点”为准：选中组合子节点时复制该子动作本身，选中父节点时复制整个组合。
    private void StepCopy_Click(object sender, RoutedEventArgs e)
    {
        var target = _focusedStep ?? _step;
        if (target != null) { _clip = target.Clone(); ShowToast("已复制动作"); }
    }
    private void StepPaste_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _clip == null) return;
        var target = _focusedStep ?? _step;
        var parentGroup = target != null ? FindParentGroup(target) : null;
        PushUndo();
        var pasted = _clip.Clone();
        pasted.JumpTarget = 0; pasted.JumpTimes = 0;   // 复制来的跳转序号在新位置无意义，清掉免得指错
        if (parentGroup != null && !_clip.IsGroup)
        {
            // 聚焦在组合内部的子动作：作为兄弟子动作粘到该子动作之后（组合不嵌套组合）。
            int at = parentGroup.Children.IndexOf(target!) + 1;
            parentGroup.Children.Insert(Math.Min(Math.Max(at, 0), parentGroup.Children.Count), pasted);
        }
        else
        {
            // 顶层粘贴：若聚焦点在组合内且剪贴板是组合，则贴到该组合之后（避免嵌套）。
            var anchor = parentGroup ?? target;
            int at = anchor != null ? _plan.Steps.IndexOf(anchor) + 1 : _plan.Steps.Count;
            _plan.Steps.Insert(Math.Min(Math.Max(at, 0), _plan.Steps.Count), pasted);
        }
        RefreshIndices(); MarkDirty();
    }
    private void StepUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void StepDown_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int d)
    {
        if (_plan == null || _step == null) return;
        int i = _plan.Steps.IndexOf(_step), j = i + d;
        if (i < 0 || j < 0 || j >= _plan.Steps.Count) return;
        PushUndo(); _plan.Steps.Move(i, j); RefreshIndices(); StepsList.SelectedItem = _step; MarkDirty();
    }
    private void StepDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) return;
        // 焦点在组合子动作上：只删该子动作，不解散组合。
        if (_focusedStep != null && FindParentGroup(_focusedStep) != null) { DeleteChild(_focusedStep); return; }
        if (_step == null) return;
        PushUndo(); _plan.Steps.Remove(_step); RefreshIndices(); MarkDirty();
    }
    private void ClearSteps_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _plan.Steps.Count == 0) return;
        if (ThemedDialog.Show("清空当前方案的全部动作？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        PushUndo(); _plan.Steps.Clear(); RefreshIndices(); MarkDirty();
    }
    // 递归收集全部组合(含嵌套子组合)
    private static IEnumerable<MacroStep> AllGroups(IEnumerable<MacroStep> steps)
    {
        foreach (var s in steps)
        {
            if (s.IsGroup)
            {
                yield return s;
                foreach (var g in AllGroups(s.Children)) yield return g;
            }
        }
    }
    // 一键折叠/展开：三态。展开=递归全部展开；折叠分两步——先只折最内层(不含子组合的组合)，再折全部。
    // 完全由当前展开状态推导下一步动作，手动点箭头改状态后仍自洽。
    private void ExpandAllToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) return;
        var groups = AllGroups(_plan.Steps).ToList();
        if (groups.Count == 0) return;

        if (groups.All(g => !g.IsExpanded))
        {
            // 已全部折叠 → 递归全部展开
            foreach (var g in groups) g.IsExpanded = true;
        }
        else
        {
            // 最内层组合 = 子动作里不含子组合的组合
            var innermost = groups.Where(g => !g.Children.Any(c => c.IsGroup)).ToList();
            if (innermost.Any(g => g.IsExpanded))
                foreach (var g in innermost) g.IsExpanded = false; // 第一步：只折最内层
            else
                foreach (var g in groups) g.IsExpanded = false;     // 第二步：折全部
        }

        bool anyCollapsed = groups.All(g => !g.IsExpanded);
        ExpandAllButton.ToolTip = anyCollapsed ? "全部展开" : "折叠(再点折叠更多)";
        // 全部折叠时显示"展开"图标(E70D)，否则显示"折叠"图标(E70E)
        ExpandAllButton.Content = !anyCollapsed ? "" : "";
    }
    private void GroupChevron_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MacroStep g && g.IsGroup) { g.IsExpanded = !g.IsExpanded; e.Handled = true; }
    }

    // ---- 批量 ----
    private void BatchToggle_Click(object sender, RoutedEventArgs e)
    {
        IsBatchMode = !IsBatchMode;
        BatchBar.Visibility = IsBatchMode ? Visibility.Visible : Visibility.Collapsed;
        if (!IsBatchMode && _plan != null) foreach (var s in _plan.Steps) s.IsChecked = false;
        UpdateBatchCount();
    }
    private void UpdateBatchCount() => BatchCountText.Text = $"已选 {(_plan?.Steps.Count(s => s.IsChecked) ?? 0)} 项";
    private List<MacroStep> Checked() => _plan?.Steps.Where(s => s.IsChecked).ToList() ?? new();
    private void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) return;
        var sel = Checked(); if (sel.Count == 0) return;
        PushUndo(); foreach (var s in sel) _plan.Steps.Remove(s);
        RefreshIndices(); UpdateBatchCount(); MarkDirty();
    }
    private void BatchCombine_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) return;
        var sel = Checked();
        if (sel.Count < 2) { ThemedDialog.Show("请至少勾选两个动作进行组合。", "无法组合"); return; }
        PushUndo();
        int at = _plan.Steps.IndexOf(sel[0]);
        var children = new ObservableCollection<MacroStep>();
        foreach (var s in sel) { s.IsChecked = false; children.Add(s); }   // 整体入组：被选中的组合作为嵌套子组合原样保留，不再打散合并
        foreach (var s in sel) _plan.Steps.Remove(s);
        var g = new MacroStep { Type = "Group", Children = children };
        _plan.Steps.Insert(Math.Min(at, _plan.Steps.Count), g);
        RefreshIndices(); BatchToggle_Click(this, e); StepsList.SelectedItem = g; MarkDirty();
    }

    private void RefreshIndices()
    {
        if (_plan == null) return;
        var steps = _plan.Steps;
        // 跳转目标重映射：仅当上次基线属于当前方案时做（换方案/撤销恢复走基线路径，保留既有序号）。
        if (ReferenceEquals(_jumpOrderPlan, _plan))
        {
            foreach (var s in steps)
            {
                if (s.JumpTarget < 1) continue;
                MacroStep? tgt = s.JumpTarget <= _jumpOrder.Count ? _jumpOrder[s.JumpTarget - 1] : null;
                int ni = tgt != null ? steps.IndexOf(tgt) : -1;
                s.JumpTarget = ni >= 0 ? ni + 1 : 0;   // 目标被删除 / 移入组合 → 取消跳转，避免指错或死循环
            }
        }
        for (int i = 0; i < steps.Count; i++) steps[i].DisplayIndex = i + 1;
        _jumpOrder = steps.ToList();      // 记下这次的顺序，作为下次重映射的基线
        _jumpOrderPlan = _plan;
    }

    // 右键"禁用/启用"：顶层动作用选中步 _step；组合子动作用 sender.DataContext。切换即标脏，徽标/变灰随属性通知即时更新。
    private void ToggleDisableSelected()
    {
        if (_step == null) return;
        PushUndo();
        _step.Disabled = !_step.Disabled;
        MarkDirty();
    }
    private void ChildToggleDisable_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MacroStep c) { PushUndo(); c.Disabled = !c.Disabled; MarkDirty(); }
    }

    // ================= 运行 =================
    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is { IsRunning: true })
        {
            if (_runner.IsPaused)
            {
                // 暂停中点“运行”不再无响应：让用户选择回运行页继续/停止暂停的方案。
                int c = ThemedDialog.ShowChoice(
                    $"方案「{_runDisplayName}」正处于暂停状态，需先处理后才能开始新的运行。",
                    "方案暂停中", "返回运行页面", "停止该方案", "取消");
                if (c == 0) SetNav(NavRun, PageRun);
                else if (c == 1) _runner.Stop();
            }
            else SetNav(NavRun, PageRun);   // 正在运行：切到运行页给出反馈，而非无响应
            return;
        }
        if (_plan == null || _plan.Steps.Count == 0) { ThemedDialog.Show("请先选择一个有动作的方案。", "提示"); return; }
        if (!EnsureCurrentPlanClean()) return;
        if (_plan == null || _plan.Steps.Count == 0) return;
        RunPlan(BuildRunPlan(_plan), _plan.Name);
    }

    // 运行页"双击回编辑"用：跑的是克隆副本，记住 clone→原始步骤 + 来源方案，双击时映射回真实对象去编辑。
    private MacroPlan? _runSourcePlan;
    private readonly Dictionary<MacroStep, MacroStep> _runOrigin = new();

    // 运行用的方案快照：整份克隆当前方案（步骤/循环/运行条件），让 runner 后台遍历的是独立副本——
    // 运行中即便编辑当前方案，也不会和 runner 争同一个 ObservableCollection 而抛"集合被修改"。
    private MacroPlan BuildRunPlan(MacroPlan src)
    {
        _runOrigin.Clear(); _runSourcePlan = src;
        var steps = new System.Collections.ObjectModel.ObservableCollection<MacroStep>();
        foreach (var s in src.Steps) { var c = s.Clone(); _runOrigin[c] = s; steps.Add(c); }
        return new MacroPlan
        {
            Name = src.Name, LoopCount = src.LoopCount, LoopDelayMs = src.LoopDelayMs,
            RunConditionType = src.RunConditionType, RunConditionInvert = src.RunConditionInvert,
            RunConditionStartMinute = src.RunConditionStartMinute, RunConditionEndMinute = src.RunConditionEndMinute,
            Steps = steps,
        };
    }

    // 运行指定方案（快照副本；"从此动作开始/单次"另构临时方案）。
    private bool _startingRun;
    private async void RunPlan(MacroPlan plan, string displayName)
    {
        if (_runner is { IsRunning: true } || _startingRun) return;
        _startingRun = true;
        try
        {
        var prev = _backend; _backend = null;
        var closing = _backendClosing;
        var backend = string.Equals(_doc.Backend, "Native", StringComparison.OrdinalIgnoreCase)
            ? (IInputBackend)new NativeInputDevice(_doc.NativeKeyMode) : new Ch9329Device(_doc.Port, _doc.BaudRate);
        // 释放上一后端、等上次串口释放完（偶发阻塞 2s）、开口/重试，全放后台线程，别卡 UI。
        bool ok = await System.Threading.Tasks.Task.Run(() =>
        {
            try { prev?.Dispose(); } catch { }
            if (closing is { IsCompleted: false }) { try { closing.Wait(2000); } catch { } }
            if (backend.Open()) return true;
            System.Threading.Thread.Sleep(300);   // 端口可能还没让干净，稍等重试一次
            return backend.Open();
        });
        if (_runner is { IsRunning: true }) { backend.Dispose(); return; }   // 等待期间已另起一次
        if (!ok)
        {
            backend.Dispose();
            AddLog("Error", $"输出设备打开失败：{backend.Describe}");
            SetNav(NavRun, PageRun); SetRunStatus("Error", "设备打开失败"); return;
        }
        _backend = backend;

        RunStepsList.ItemsSource = plan.Steps;
        _runTopAncestor.Clear();
        foreach (var top in plan.Steps) MapRunTree(top, top);   // 建"任意运行克隆→顶层行"映射，供自动滚动
        RunPlanNameText.Text = displayName;
        _planLoopText = ""; RunPlanLoopText.Text = "";   // 清上一轮的方案循环显示
        SetNav(NavRun, PageRun);
        string loopLabel = plan.LoopCount == 0 ? "无限循环" : (plan.LoopCount == 1 ? "执行 1 次" : $"循环 {plan.LoopCount} 次");
        AddLog("Info", $"▶ 开始运行：{displayName}（{_backend.Describe} · {loopLabel}）");

        _runDisplayName = displayName;
        // 运行器线程只"入队/置标志"（线程安全、不阻塞），UI 由定时器批量刷新——避免日志多时阻塞执行、影响计时精度。
        _runner = new MacroRunner(_backend);
        _runner.Log += (lvl, msg) => _logQueue.Enqueue(new LogMsg(LogOp.Row, LogTime(), lvl, msg, ""));
        _runner.ActBegin += body => _logQueue.Enqueue(new LogMsg(LogOp.Begin, LogTime(), "", body, ""));
        _runner.ActEnd += (status, kind) => _logQueue.Enqueue(new LogMsg(LogOp.End, "", status, kind, ""));
        _runner.StepStateChanged += (st, on) => _stepQueue.Enqueue((st, on));
        _runner.Progress += (pct, txt) => { _progPct = pct; _progText = txt; _progActive = true; };
        _runner.PlanLoopChanged += s => _planLoopText = s;
        _runner.PausedChanged += paused => _pausedSignal = paused ? 1 : 2;
        _runner.Finished += reason => _finishSignal = reason;

        PauseButton.IsEnabled = true; ResumeButton.IsEnabled = false; StopButton.IsEnabled = true;
        SetRunStatus("Running", $"运行中 · {displayName}");
        SendWindowToBottom();   // 下沉到最底层、不抢焦点，方便用户切到目标程序窗口
        StartRunUiTimer();
        EnableHotkeys();        // 仅在方案执行期间独占 F9/F10/F11 + 挂低级键盘钩子；OnRunFinished 里注销
        _runner.Start(plan, _doc.ExecutionDelayMs, _doc.TimingJitterEnabled ? _doc.TimingJitterMs : 0);
        }
        finally { _startingRun = false; }
    }

    // 运行时把本窗口压到 Z 序最底（不改大小/位置、不激活），让目标程序窗口浮到前面。
    // 结束后【不】主动恢复/抢前台（按用户要求停在后台）；但点击本体任意处应能把它提到前台——见 OnPreviewMouseDown。
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private static readonly IntPtr HWND_TOPMOST = new(-1);   // 供 Dialogs 的覆盖层用
    private void SendWindowToBottom()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, 0x13); // SWP_NOSIZE|SWP_NOMOVE|SWP_NOACTIVATE
    }
    // 运行结束后窗口停在 Z 序最底：点击本体任意处（内容区/标题栏都算）时，若它还不在前台就提到顶层并激活。
    // FluentWindow 下点内容区有时不会自动激活（只有标题栏会），这里兜底。仅非运行态处理，运行期保持下沉不打断。
    protected override void OnPreviewMouseDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (_runner is { IsRunning: true }) return;   // 运行期保持下沉、不打断
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        // 运行时本体被压到 Z 序最底；结束后点它：没焦点→稳健置前；已有焦点但仍被压在底（fg==self 却看不见，诊断日志实证）
        // → BringWindowToTop 抬到顶层。点内容区之所以"没反应"，正是只拿到焦点、Z 序没抬（点标题栏则系统会自动抬）。
        if (GetForegroundWindow() != h) Services.WindowActivator.ActivateHwnd(h);
        else BringWindowToTop(h);
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);

    // ---- 运行期 UI 解耦：队列 + 定时刷新 ----
    private enum LogOp { Row, Begin, End }
    private readonly record struct LogMsg(LogOp Op, string Time, string A, string B, string C); // Row:A=level,B=msg；Begin:B=body；End:A=status,B=kind
    private readonly System.Collections.Concurrent.ConcurrentQueue<LogMsg> _logQueue = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(MacroStep step, bool on)> _stepQueue = new();
    // 运行页扁平列表只有顶层行；把每个运行克隆（含嵌套子动作、监听动作）映射到它的【顶层祖先行】，供自动滚动定位。
    // （组合子动作不在列表里、且 DisplayIndex=0 → 执行子动作时滚到所属组合行，否则整段组合期间列表不滚、当前行漂出视口。）
    private readonly Dictionary<MacroStep, MacroStep> _runTopAncestor = new();
    private void MapRunTree(MacroStep node, MacroStep top)
    {
        _runTopAncestor[node] = top;
        foreach (var ch in node.Children) MapRunTree(ch, top);
        if (node.SuccessAction != null) MapRunTree(node.SuccessAction, top);
        if (node.CompleteAction != null) MapRunTree(node.CompleteAction, top);
        if (node.FailAction != null) MapRunTree(node.FailAction, top);
    }
    private volatile bool _progActive;
    private volatile int _progPct;
    private volatile string _progText = "";
    private volatile string _planLoopText = "";   // 方案级"第 N/总 轮"（PlanLoopChanged 置，FlushRunUi 显示在方案名后）
    private volatile int _pausedSignal;     // 0 无, 1 暂停, 2 运行
    private volatile string? _finishSignal; // Done/Stopped/Error
    private string _runDisplayName = "";
    private System.Windows.Threading.DispatcherTimer? _uiFlush;
    private static string LogTime() => DateTime.Now.ToString("HH:mm:ss.fff");

    private void StartRunUiTimer()
    {
        if (_uiFlush == null)
        {
            _uiFlush = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _uiFlush.Tick += (_, _) => FlushRunUi();
        }
        _uiFlush.Start();
    }

    private ScrollViewer? _logScroll;
    // 日志自动滚动：仅当用户已在（接近）底部时才跟随；上翻查看历史时暂停，滚回底部自动恢复。
    private bool LogAtBottom()
    {
        _logScroll ??= FindDescendantScrollViewer(LogList);
        if (_logScroll == null) return true;
        return _logScroll.ScrollableHeight - _logScroll.VerticalOffset < 24;
    }
    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var r = FindDescendantScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (r != null) return r;
        }
        return null;
    }

    private void FlushRunUi()
    {
        // 先快照结束信号再排空队列：runner 是"先入队结束日志、后置 finish"，观察到 fin 就保证结束日志已在队列里，
        // 随后的排空一定能捞到（否则可能排空后 runner 才入队，OnRunFinished 停表→末尾几行丢失）。
        var fin = _finishSignal;
        // 日志：入列表 + 攒一批一次落盘（不再每行 open/close 文件），滚动只做一次且用户上翻时暂停
        bool added = false;
        List<string>? pending = null;
        while (_logQueue.TryDequeue(out var m))
        {
            switch (m.Op)
            {
                case LogOp.Row:
                    _logs.Add(new LogEntry { Time = m.Time, Body = m.B, Level = m.A });
                    (pending ??= new()).Add($"{m.Time}  {m.B}");
                    added = true;
                    break;
                case LogOp.Begin:
                    _liveLog = new LogEntry { Time = m.Time, Body = m.B, Level = "Info", Status = "执行中", StatusKind = "Running" };
                    _logs.Add(_liveLog);
                    added = true;
                    break;
                case LogOp.End:
                    if (_liveLog != null)
                    {
                        _liveLog.Status = m.A; _liveLog.StatusKind = m.B;
                        (pending ??= new()).Add($"{_liveLog.Time}  {_liveLog.Body}  [{m.A}]");
                        _liveLog = null;
                    }
                    break;
            }
        }
        if (pending != null) Storage.AppendRunLog(string.Join(Environment.NewLine, pending));
        if (added)
        {
            bool atBottom = LogAtBottom();
            while (_logs.Count > 2000) _logs.RemoveAt(0);
            if (atBottom && LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        }
        // 当前动作高亮 + 自动滚动：滚动目标映射到顶层祖先行（组合子动作/监听动作不在扁平列表里），
        // 这样执行组合内部时也会滚到并保持该组合行可见（诊断日志证实：不映射时子动作 inList=False → 整段不滚）。
        MacroStep? scrollTo = null;
        while (_stepQueue.TryDequeue(out var s)) { s.step.IsExecuting = s.on; if (s.on) scrollTo = s.step; }
        if (scrollTo != null)
        {
            var top = _runTopAncestor.TryGetValue(scrollTo, out var t) ? t : scrollTo;
            if (RunStepsList.Items.Contains(top))
            {
                RunStepsList.ScrollIntoView(top);
                (RunStepsList.ItemContainerGenerator.ContainerFromItem(top) as FrameworkElement)?.BringIntoView();
            }
        }
        // 进度 / 状态栏
        if (RunPlanLoopText.Text != _planLoopText) RunPlanLoopText.Text = _planLoopText;   // 方案级循环（方案名后，与动作级进度分开）
        if (_progActive)
        {
            ProgressStrip.Visibility = Visibility.Visible;
            ProgressBar.Value = _progPct; ProgressPercentText.Text = _progPct + "%"; ProgressActionText.Text = _progText;
        }
        // 暂停/继续
        int p = _pausedSignal;
        if (p != 0)
        {
            _pausedSignal = 0;
            bool paused = p == 1;
            SetRunStatus(paused ? "Paused" : "Running", paused ? "已暂停" : $"运行中 · {_runDisplayName}");
            PauseButton.IsEnabled = !paused; ResumeButton.IsEnabled = paused;
        }
        // 结束（用本 tick 开头的快照；此时结束日志已随上面的排空落到界面/磁盘）
        if (fin != null) { _finishSignal = null; OnRunFinished(fin); }
    }

    // 上一次运行对输出设备的后台释放任务；串口 Close 偶发阻塞，异步释放避免卡住 UI 线程。
    private System.Threading.Tasks.Task? _backendClosing;
    private void OnRunFinished(string reason)
    {
        DisableHotkeys();       // 运行结束（完成/停止/出错）即释放 F9/F10/F11 与键盘钩子
        _uiFlush?.Stop();
        PauseButton.IsEnabled = ResumeButton.IsEnabled = StopButton.IsEnabled = false;
        _progActive = false;
        _planLoopText = ""; RunPlanLoopText.Text = "";
        ProgressStrip.Visibility = Visibility.Collapsed;
        SetRunStatus(reason == "Done" ? "Success" : reason, reason switch { "Done" => "运行完成", "Stopped" => "已停止", _ => "运行出错" });
        // 运行结束即释放输出设备：把 COM 口让给其它软件（如标准版）。
        // 串口 Close() 偶发阻塞——放后台线程释放，别在 UI 线程同步 Dispose，否则"结束后一段时间点不亮窗口"。
        // 记下释放任务：用户若立刻再运行，RunPlan 会先等它完成再开口，避免抢占同一 COM。
        if (_backend is { } b)
        {
            _backend = null;
            bool serial = b is Ch9329Device;
            _backendClosing = System.Threading.Tasks.Task.Run(() => { try { b.Dispose(); } catch { } });
            if (serial) AddLog("Info", "已释放串口（已让给其它软件）。");
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _runner?.Pause();
    private void Resume_Click(object sender, RoutedEventArgs e) => _runner?.Resume();
    private void Stop_Click(object sender, RoutedEventArgs e) => _runner?.Stop();
    private void BackToPlans_Click(object sender, RoutedEventArgs e) => SetNav(NavPlans, PagePlans);

    // 状态点配色（对齐参考 ApplyRunState）：运行/完成=绿，暂停=黄，停止/出错=红；
    // 状态文字颜色：停止/出错=红，其余=普通 Ink。
    private void SetRunStatus(string kind, string text)
    {
        RunStatusText.Text = text;
        string key = kind switch
        {
            "Running" => "Success", "Success" => "Success", "Done" => "Success",
            "Paused" => "Warning",
            "Stopped" => "Danger", "Error" => "Danger",
            _ => "Muted",
        };
        bool danger = kind is "Stopped" or "Error";
        RunStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
        RunStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, danger ? "Danger" : "Ink");
        if (RunStatusGlow != null && FindResource(key) is SolidColorBrush sb)
        {
            RunStatusGlow.Color = sb.Color;
            RunStatusGlow.BlurRadius = kind is "Running" or "Paused" ? 12 : 0;
        }
        if (kind == "Running") StartStatusPulse(); else StopStatusPulse();   // 运行中绿点呼吸
    }

    // 状态点呼吸特效：仅运行中，透明度 1.0↔0.25 往复（直接作用于元素，确保生效）。
    private bool _pulsing;
    private void StartStatusPulse()
    {
        if (RunStatusDot == null || _pulsing) return;
        _pulsing = true;
        var anim = new DoubleAnimation(1.0, 0.25, new Duration(TimeSpan.FromMilliseconds(850)))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        RunStatusDot.BeginAnimation(UIElement.OpacityProperty, anim);
    }
    private void StopStatusPulse()
    {
        if (RunStatusDot == null) return;
        _pulsing = false;
        RunStatusDot.BeginAnimation(UIElement.OpacityProperty, null); // 移除动画
        RunStatusDot.Opacity = 1.0;
    }

    // 普通日志行（无状态）。磁盘格式与界面一致：时间  正文。
    private void AddLog(string level, string msg)
    {
        var time = DateTime.Now.ToString("HH:mm:ss.fff");
        _logs.Add(new LogEntry { Time = time, Body = msg, Level = level });
        TrimAndScrollLog();
        Storage.AppendRunLog($"{time}  {msg}");
    }

    // 运行期动作日志的"执行中→成功/失败"实时行（由 FlushRunUi 在 UI 线程批量写入/更新）。
    private LogEntry? _liveLog;
    private void TrimAndScrollLog()
    {
        if (_logs.Count > 2000) _logs.RemoveAt(0);
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }
    private void LogClear_Click(object sender, RoutedEventArgs e) { _logs.Clear(); _liveLog = null; }

    // 底部居中 Toast：承载"已保存 / 已复制"等瞬时反馈，1.9s 后自动淡出（不写进运行日志）。
    private System.Windows.Threading.DispatcherTimer? _toastTimer;
    private void ShowToast(string msg)
    {
        if (Toast == null) return;
        ToastText.Text = msg;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        if (_toastTimer == null)
        {
            _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1900) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer!.Stop();
                var fade = new DoubleAnimation(Toast.Opacity, 0, TimeSpan.FromMilliseconds(260));
                fade.Completed += (_, _) => { if (Toast.Opacity == 0) Toast.Visibility = Visibility.Collapsed; };
                Toast.BeginAnimation(OpacityProperty, fade);
            };
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ================= 保存 / 导入导出 =================
    // MaxDepth 256：嵌套组合 + 递归监听会超过默认 64 层，否则判脏/克隆序列化会抛异常。
    private static readonly JsonSerializerOptions _planJsonOpts = new() { WriteIndented = false, MaxDepth = 256 };
    private static string SerializePlan(MacroPlan p) => JsonSerializer.Serialize(p, _planJsonOpts);
    private static string SerializeStep(MacroStep s) => JsonSerializer.Serialize(s, _planJsonOpts);

    // 判断两个动作"内容是否相同"，用于"编辑后未改动就不标脏"。
    // 忽略纯显示提示：HoldUnit/DurationUnit（仅用于重新编辑时还原单位，不影响执行/展示；
    // 默认 -1 会被编辑对话框解析成具体单位下标，否则未改也会误判为脏）与 Note 的 null/空串差异。
    private static bool SameStepContent(MacroStep a, MacroStep b) =>
        SerializeStep(NormalizeForCompare(a.Clone())) == SerializeStep(NormalizeForCompare(b.Clone()));

    private static MacroStep NormalizeForCompare(MacroStep s)
    {
        s.HoldUnit = -1;
        s.DurationUnit = -1;
        s.Note = string.IsNullOrEmpty(s.Note) ? "" : s.Note;
        if (s.SuccessAction != null) NormalizeForCompare(s.SuccessAction);
        if (s.CompleteAction != null) NormalizeForCompare(s.CompleteAction);
        if (s.FailAction != null) NormalizeForCompare(s.FailAction);
        foreach (var c in s.Children) NormalizeForCompare(c);
        return s;
    }

    // 上次保存/加载时的方案顺序（按对象身份）。判断"文档是否有未保存改动"用它 + 各方案 Dirty 标志，
    // 不再每次编辑都全量序列化所有方案（含 base64 截图）做字符串比较。
    private List<MacroPlan> _savedPlanIds = new();
    private bool IsDocDirty()
    {
        if (_plan != null) RecomputeDirty(_plan);   // 只重算当前方案；其它方案离开编辑上下文时已保证 clean
        if (_plans.Count != _savedPlanIds.Count) return true;
        for (int i = 0; i < _plans.Count; i++)
            if (!ReferenceEquals(_plans[i], _savedPlanIds[i])) return true;   // 方案被增删/排序
        foreach (var p in _plans) if (p.Dirty || p.SavedSnapshot == null) return true;
        return false;
    }
    private void RefreshSaveState() => SaveButton.IsEnabled = IsDocDirty();

    // 按内容比较计算单个方案"是否有未保存修改"（用于行内"未保存"徽标）。
    private void RecomputeDirty(MacroPlan? plan)
    {
        if (plan == null) return;
        plan.Dirty = plan.SavedSnapshot == null || SerializePlan(plan) != plan.SavedSnapshot;
    }
    // 保存后/加载后：记录每个方案快照 + 整表快照，并清除脏标记。
    private void SnapshotAllPlans()
    {
        foreach (var p in _plans) { p.SavedSnapshot = SerializePlan(p); p.Dirty = false; }
        _savedPlanIds = _plans.ToList();
        PlansList.Items.Refresh();
    }

    // 仅持久化"设置"（主题/串口/桥片/数据目录等），不提交未保存的方案编辑：
    // 写盘时 Plans 用上次保存的快照，避免设置变更顺带把未保存的方案偷偷存了。
    // 只写 settings.json（不含方案），改一个下拉框/开关不再把含截图的整份方案重写一遍。
    private void PersistSettings() => Storage.SaveSettings(_doc);

    // 确保"当前方案"无未保存修改：有则弹框保存/放弃/取消。返回 false 表示用户取消（调用方应中止操作）。
    // 任何离开当前方案编辑上下文的操作（切页、切方案、运行、关闭、新建/导入/排序/删除等）都应先调用它。
    private bool EnsureCurrentPlanClean()
    {
        if (_plan == null) return true;
        RecomputeDirty(_plan);
        if (!_plan.Dirty) return true;
        var r = ThemedDialog.Show("当前方案有未保存的修改，是否保存？", "未保存", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) Save();
        else DiscardPlanChanges(_plan);
        return true;
    }

    // 放弃某方案的未保存修改：从快照还原其内容。
    private void DiscardPlanChanges(MacroPlan plan)
    {
        if (plan.SavedSnapshot == null)
        {
            _plans.Remove(plan); // 从未保存过的新方案，放弃 = 直接移除
            if (_plan == plan)
            {
                _plan = null;
                _undo.Clear(); UndoButton.IsEnabled = false;
                if (_plans.Count > 0) PlansList.SelectedIndex = Math.Min(PlansList.SelectedIndex, _plans.Count - 1);
                else ShowNoPlan();
            }
            RefreshSaveState();
            PlansList.Items.Refresh();
            return;
        }
        plan.Dirty = false;
        var saved = JsonSerializer.Deserialize<MacroPlan>(plan.SavedSnapshot);
        if (saved == null) return;
        plan.Name = saved.Name; plan.LoopCount = saved.LoopCount; plan.LoopDelayMs = saved.LoopDelayMs;
        plan.Steps.Clear();
        foreach (var s in saved.Steps) plan.Steps.Add(s);
        _undo.Clear(); UndoButton.IsEnabled = false;
        _jumpOrderPlan = null;   // 同 Undo：填入的是反序列化的新对象，跳转序号已对应恢复后的顺序，走基线不重映射（否则全被清 0）
        RefreshIndices();
        RefreshSaveState();
        PlansList.Items.Refresh();
    }

    private void MarkDirty()
    {
        if (_plan == null) return;
        RecomputeDirty(_plan);
        PlansList.Items.Refresh();
        RefreshSaveState();   // 保存按钮按"整个文档是否有改动"判断
    }
    private void Save_Click(object sender, RoutedEventArgs e) => Save();
    // 方案保存：提交内存中的全部方案到磁盘，更新快照、清脏。
    private void Save()
    {
        _doc.Plans = _plans.ToList();
        foreach (var p in _plans) ImageStore.Externalize(p);   // 内联 base64 图片就地外置成 file:hash（含方案级条件），plans.json 只留引用
        if (!Storage.Save(_doc))
        {
            // 写盘失败不再静默假装"已保存"：保留脏标记、提示用户，避免误以为存上了。
            AddLog("Error", "保存失败：无法写入数据文件（磁盘只读 / 目录被占用 / 权限不足？）。");
            ThemedDialog.Show("保存失败：无法写入数据文件。\n请检查数据目录是否可写、磁盘是否已满。", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Exclamation);
            return;
        }
        SnapshotAllPlans(); // 更新全部方案快照并清脏
        SaveButton.IsEnabled = false;
        _undo.Clear(); UndoButton.IsEnabled = false; // 保存后清空撤销历史，回撤按钮置灰
        SweepOrphanImages();   // 清掉不再被任何方案引用的外置截图（孤儿图片）
        ShowToast("已保存");
    }
    // 清理孤儿图片：保存是唯一让"引用集合"落定的时刻，在这里做正合适。
    // 引用集合 = 全部方案（含方案级条件）+ 方案/动作剪贴板；撤销栈刚在保存时清空，无需考虑。
    // 方案运行期间跳过——运行的是旧快照，其中的图片可能已不在当前方案里，删了会让运行中的条件失效。
    private void SweepOrphanImages()
    {
        if (_runner is { IsRunning: true }) return;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _plans) ImageStore.CollectHashes(p, used);
        if (_clipPlan != null) ImageStore.CollectHashes(_clipPlan, used);
        if (_clip != null) ImageStore.CollectHashes(new[] { _clip }, used);
        int n = ImageStore.Sweep(used);
        if (n > 0) AddLog("Info", $"已清理 {n} 张不再使用的条件截图。");
    }

    // 方案列表标题栏的导出：整个方案列表（存成方案数组）。
    private void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (_plans.Count == 0) { ThemedDialog.Show("当前没有可导出的方案。", "导出方案"); return; }
        ExportPlans(_plans.ToList(), single: false);
    }

    // 方案编辑区底栏的导出：只导当前这一个方案（存成单个方案对象）。
    private void ExportCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) { ThemedDialog.Show("请先选择一个方案。", "导出方案"); return; }
        ExportPlans(new List<MacroPlan> { _plan }, single: true);
    }

    // single=true 存单个方案对象；否则存方案数组。均自包含（图片引用→内联 base64）。
    private void ExportPlans(List<MacroPlan> list, bool single)
    {
        if (list.Count == 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "方案文件 (*.json)|*.json",
            // 列表导出与实际存储文件同名 plans.json；单方案导出为 plans_<方案名>.json
            FileName = (single ? "plans_" + PlanFileStem(list[0].Name) : "plans") + ".json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = true, MaxDepth = 256 };
            object payload = single ? CloneForExport(list[0]) : list.Select(CloneForExport).ToList();
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(payload, opts));
            AddLog("Info", $"已导出 {(single ? 1 : list.Count)} 个方案：{dlg.FileName}");
            ShowToast($"已导出 {(single ? 1 : list.Count)} 个方案");
        }
        catch (Exception ex) { ThemedDialog.Show("导出失败：" + ex.Message, "MacroPilot"); }
    }

    // 方案名 → 可直接做文件名的词干：先剔除非法字符，再按分隔符切词后拼接（不留空格）。
    // 纯 ASCII（英文方案名）转成首字母大写的驼峰；含中文等非 ASCII 时保留原文，只去掉分隔符。
    private static string PlanFileStem(string? name)
    {
        var s = (name ?? "").Trim();
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
        var parts = s.Split(new[] { ' ', '\t', '_', '-', '.', '　' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "plan";
        bool ascii = s.All(ch => ch < 128);
        var stem = ascii
            ? string.Concat(parts.Select(w => char.ToUpperInvariant(w[0]) + w[1..]))
            : string.Concat(parts);
        return stem.Length == 0 ? "plan" : stem;
    }

    // 自包含克隆：JSON 往返（保留全部字段、MaxDepth 放大）+ 图片引用解析回内联 base64，不动内存里的方案。
    private static MacroPlan CloneForExport(MacroPlan p)
    {
        var opts = new JsonSerializerOptions { MaxDepth = 256 };
        var c = JsonSerializer.Deserialize<MacroPlan>(JsonSerializer.Serialize(p, opts), opts)!;
        ImageStore.Inline(c);   // 方案级条件 + 全部动作一起内联，导出文件完全自包含
        return c;
    }

    // 校验并读出一个文件里的方案（支持"单个对象"或"方案数组"）；失败时 error 给出第一处格式错误的具体位置，不生成空方案。
    private static bool TryReadPlans(string path, out List<MacroPlan> plans, out string error)
    {
        plans = new List<MacroPlan>(); error = "";
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { error = "读取文件失败：" + ex.Message; return false; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 }); }
        catch (JsonException jx) { error = $"不是有效的 JSON：{jx.Message}（第 {(jx.LineNumber ?? 0) + 1} 行，第 {(jx.BytePositionInLine ?? 0) + 1} 列）"; return false; }

        static string Kind(JsonValueKind k) => k switch
        {
            JsonValueKind.Object => "对象", JsonValueKind.Array => "数组", JsonValueKind.String => "字符串",
            JsonValueKind.Number => "数字", JsonValueKind.True or JsonValueKind.False => "布尔", JsonValueKind.Null => "null", _ => k.ToString(),
        };
        var readOpts = new JsonSerializerOptions { MaxDepth = 256 };
        using (doc)
        {
            var root = doc.RootElement;
            bool isArray = root.ValueKind == JsonValueKind.Array;
            List<JsonElement> items;
            if (isArray) items = root.EnumerateArray().ToList();
            else if (root.ValueKind == JsonValueKind.Object) items = new List<JsonElement> { root };
            else { error = $"顶层是{Kind(root.ValueKind)}，既不是方案对象也不是方案数组。"; return false; }

            for (int i = 0; i < items.Count; i++)
            {
                var el = items[i];
                string where = isArray ? $"第 {i + 1} 个方案" : "方案";
                if (el.ValueKind != JsonValueKind.Object) { error = $"{where}不是对象（而是{Kind(el.ValueKind)}）。"; return false; }
                if (!el.TryGetProperty("Steps", out var s)) { error = $"{where}缺少「Steps」字段。"; return false; }
                if (s.ValueKind != JsonValueKind.Array) { error = $"{where}的「Steps」不是数组（而是{Kind(s.ValueKind)}）。"; return false; }
                try { if (el.Deserialize<MacroPlan>(readOpts) is { } p) plans.Add(p); }
                catch (JsonException jx) { error = $"{where}解析失败：{jx.Message}" + (string.IsNullOrEmpty(jx.Path) ? "" : $"（位置 {jx.Path}）"); return false; }
            }
            if (plans.Count == 0) { error = "文件里没有任何方案。"; return false; }
            return true;
        }
    }

    // 批量加入未保存方案：只在最后切换一次选中项，避免逐个切换时对上一个未保存的导入方案反复弹"保存？"。
    private void AddPlansUnsaved(List<MacroPlan> list)
    {
        foreach (var p in list) { _plans.Add(p); p.SavedSnapshot = null; RecomputeDirty(p); }
        PlansList.SelectedItem = list[^1];   // 单次切换（切走前的旧方案在 Import 入口已确保 clean）
        PlansList.Items.Refresh();
        SaveButton.IsEnabled = true;
        foreach (var p in list) _undo.Push(new UndoSnap("", new List<MacroStep>(), 0, 0, p));   // 每个可撤销（切换会清 _undo，故放选中之后）
        UndoButton.IsEnabled = true;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureCurrentPlanClean()) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "方案文件 (*.json)|*.json", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        var imported = new List<MacroPlan>();
        var errors = new List<string>();
        foreach (var file in dlg.FileNames)
        {
            if (TryReadPlans(file, out var got, out var err)) imported.AddRange(got);
            else errors.Add($"「{System.IO.Path.GetFileName(file)}」：{err}");   // 带上第一处格式错误的具体位置
        }
        if (imported.Count > 0)
        {
            foreach (var p in imported) ImageStore.Externalize(p);   // 内联 base64 图片（含方案级条件）落到本地 images\ 并转引用
            AddPlansUnsaved(imported);
            AddLog("Info", $"已导入 {imported.Count} 个方案（未保存）。");
            ShowToast($"已导入 {imported.Count} 个方案");
        }
        if (errors.Count > 0)
            ThemedDialog.Show("以下文件格式有误，已跳过：\n\n" + string.Join("\n\n", errors), "导入警告",
                MessageBoxButton.OK, MessageBoxImage.Exclamation);
        else if (imported.Count == 0)
            ThemedDialog.Show("未导入任何方案。", "导入", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ================= 运行期热键 F9/F10/F11（仅方案执行期间注册，平时不占用） =================
    private const int WM_HOTKEY = 0x0312;
    private bool _hotkeysOn;
    // 装热键：方案开始执行时调用（RunPlan）。幂等——已装则跳过。
    private void EnableHotkeys()
    {
        if (_hotkeysOn) return;
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        var src = HwndSource.FromHwnd(h);
        src?.AddHook(HotkeyHook);
        const uint MOD_NOREPEAT = 0x4000; // 长按不重复触发
        RegisterHotKey(h, 1, MOD_NOREPEAT, 0x78); // F9 暂停
        RegisterHotKey(h, 2, MOD_NOREPEAT, 0x79); // F10 继续
        RegisterHotKey(h, 3, MOD_NOREPEAT, 0x7A); // F11 停止
        InstallKeyboardHook();
        _hotkeysOn = true;
    }
    // 卸热键：运行结束/停止（OnRunFinished）、关窗（OnClosed）时调用。幂等。
    private void DisableHotkeys()
    {
        if (!_hotkeysOn) return;
        if (_keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero)
        {
            HwndSource.FromHwnd(h)?.RemoveHook(HotkeyHook);
            for (int i = 1; i <= 3; i++) UnregisterHotKey(h, i);
        }
        _hotkeysOn = false;
    }
    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == 1) _runner?.Pause();
            else if (id == 2) _runner?.Resume();
            else if (id == 3) _runner?.Stop();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---- 低级键盘钩子：全屏/独占游戏内 RegisterHotKey 常无效，钩子在系统输入线程级捕获，可兜底 ----
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelKeyboardProc? _keyboardProc;   // 必须持有引用防 GC
    private IntPtr _keyboardHook;
    private void InstallKeyboardHook()
    {
        try
        {
            _keyboardProc = KeyboardHookCallback;
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            using var mod = proc.MainModule;
            _keyboardHook = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, _keyboardProc, GetModuleHandle(mod?.ModuleName), 0);
            if (_keyboardHook == IntPtr.Zero) AddLog("Warning", "键盘钩子安装失败，全屏游戏内热键可能不可用。");
        }
        catch (Exception ex) { AddLog("Warning", "键盘钩子安装异常：" + ex.Message); }
    }
    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == 0x0100 /*WM_KEYDOWN*/ || msg == 0x0104 /*WM_SYSKEYDOWN*/)
            {
                int vk = Marshal.ReadInt32(lParam);                       // KBDLLHOOKSTRUCT.vkCode
                int flags = Marshal.ReadInt32(lParam + 8);                // .flags
                if ((flags & 0x10 /*LLKHF_INJECTED*/) == 0)               // 忽略模拟注入的按键，只响应真实物理键
                {
                    switch (vk)
                    {
                        case 0x78: Dispatcher.BeginInvoke(new Action(() => _runner?.Pause())); break;  // F9
                        case 0x79: Dispatcher.BeginInvoke(new Action(() => _runner?.Resume())); break; // F10
                        case 0x7A: Dispatcher.BeginInvoke(new Action(() => _runner?.Stop())); break;   // F11
                    }
                }
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ================= 鼠标轨迹录制（拟人化调优：采集真人手感数据） =================
    private readonly Services.MouseTraceRecorder _traceRec = new();
    private void TraceRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_traceRec.IsRecording)
        {
            _traceRec.Stop();
            _traceRec.Progress -= OnTraceProgress;
            TraceRecordButton.Content = "开始录制";
            TraceRecordButton.Style = (Style)FindResource("PrimaryButton");
            var name = _traceRec.CurrentFile != null ? System.IO.Path.GetFileName(_traceRec.CurrentFile) : "";
            TraceStatusText.Text = $"已保存：{name}";
            return;
        }
        _traceRec.Progress += OnTraceProgress;
        if (_traceRec.Start(Services.Storage.TraceDir, out var err))
        {
            TraceRecordButton.Content = "停止录制";
            TraceRecordButton.Style = (Style)FindResource("DangerButton");
            TraceStatusText.Text = "录制中…（正常操作鼠标）";
            TraceStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            _traceRec.Progress -= OnTraceProgress;
            TraceStatusText.Text = "无法开始录制：" + err;
            TraceStatusText.Visibility = Visibility.Visible;
        }
    }
    private void OnTraceProgress(int count, double seconds) =>
        TraceStatusText.Text = $"录制中…{count} 个采样点 · {seconds:F1} 秒（正常操作鼠标，完成后点“停止录制”）";
    private void TraceOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Services.Storage.TraceDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Services.Storage.TraceDir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ================= 在线更新（启动后每 30s 自动查 → 状态栏提醒 → 可关闭 / 直接更新） =================
    private bool _updateBusy;
    private Services.UpdateInfo? _pendingUpdate;   // 当前查到的新版本
    private string? _dismissedTag;                 // 用户关掉提醒的版本：轮询不再提示它（更新的版本或手动检查仍提示）
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    private async System.Threading.Tasks.Task StartupUpdateCheck()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(1500);
            await CheckForUpdate(silent: true);
            // 启动后每 30s 自动查一次。靠 ETag 条件请求拿 304（GitHub 不计入未鉴权的 60 次/小时配额），
            // 否则 30s 一次 =120/小时必被限流。
            _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _updateTimer.Tick += async (_, _) => await CheckForUpdate(silent: true);
            _updateTimer.Start();
        }
        catch { }
    }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdate(silent: false);

    private async System.Threading.Tasks.Task CheckForUpdate(bool silent)
    {
        if (_updateBusy) return;
        try
        {
            if (!silent) { CheckUpdateButton.IsEnabled = false; UpdateStatusText.Text = "正在检查…"; }
            var info = await Services.UpdateService.CheckLatestAsync();
            if (info == null) { if (!silent) UpdateStatusText.Text = "无法获取更新信息（请检查网络）"; return; }
            if (!Services.UpdateService.IsNewer(info))
            {
                _pendingUpdate = null;
                UpdateBar.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = silent ? "" : $"已是最新版本（{Services.UpdateService.CurrentVersionText}）";
                return;
            }
            _pendingUpdate = info;
            UpdateStatusText.Text = $"发现新版本 v{info.VersionText}";
            if (silent && _dismissedTag == info.VersionText) return;   // 这个版本用户关过提醒 → 轮询不再打扰
            UpdateBarText.Text = $"发现新版本 v{info.VersionText}（当前 {Services.UpdateService.CurrentVersionText}）";
            UpdateBarText.ToolTip = string.IsNullOrWhiteSpace(info.Notes) ? null
                : (info.Notes!.Length > 500 ? info.Notes![..500] + "…" : info.Notes);
            UpdateBar.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { if (!silent) UpdateStatusText.Text = "检查更新失败：" + ex.Message; }
        finally { if (!silent && !_updateBusy) CheckUpdateButton.IsEnabled = true; }
    }

    // 状态栏提醒条：立即更新 / 关闭提醒
    private async void UpdateBarGo_Click(object sender, RoutedEventArgs e)
    {
        var info = _pendingUpdate;
        if (info == null) return;
        UpdateBar.Visibility = Visibility.Collapsed;
        await DownloadAndRun(info);
    }
    private void UpdateBarDismiss_Click(object sender, RoutedEventArgs e)
    {
        _dismissedTag = _pendingUpdate?.VersionText;   // 记住该版本，轮询不再提示；出更新版本时会重新提醒
        UpdateBar.Visibility = Visibility.Collapsed;
    }

    // 更新中：关窗不再弹"未保存"询问（下面已提前处理过），否则 Shutdown 会被模态框卡住、
    // 本体不退出 → 助手等不到解锁 → "backup rename failed: dir still in use"。
    private bool _updatingNow;

    private async System.Threading.Tasks.Task DownloadAndRun(Services.UpdateInfo info)
    {
        // 更新会重启本体，必须先把"未保存的方案"和"正在运行的方案"处理干净，
        // 否则退出流程会被拦下（这正是 v0.1.3 之前点更新后进程不退、更新失败的原因）。
        if (_runner is { IsRunning: true })
        {
            if (ThemedDialog.Show("方案正在运行，更新需要重启程序。是否停止运行并继续更新？", "更新",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _runner?.Stop();
        }
        if (!EnsureCurrentPlanClean()) return;   // 用户选"取消"→ 放弃本次更新

        _updateBusy = true;
        CheckUpdateButton.IsEnabled = false;
        var ui = new UpdateProgressUi(this, info.VersionText);
        try
        {
            ui.SetStep(0, "正在下载更新包…", "");
            var prog = new Progress<double>(p =>
            {
                ui.SetProgress(p);
                UpdateStatusText.Text = p < 0 ? "正在下载更新…" : $"正在下载更新… {p * 100:0}%";
            });
            var file = await Services.UpdateService.DownloadAsync(info, prog);
            if (ui.Canceled) { ui.Close(); UpdateStatusText.Text = "已取消更新"; return; }

            ui.SetStep(1, "正在校验完整性…", "核对 SHA-256");
            ui.Indeterminate();
            await System.Threading.Tasks.Task.Delay(150);   // 让这一步在界面上可见（校验在下载时已完成）

            ui.SetStep(2, "正在退出并覆盖安装…", "程序即将关闭，安装完成后会自动重启");
            UpdateStatusText.Text = "下载完成，正在静默更新并自动重启…";
            await System.Threading.Tasks.Task.Delay(400);   // 给用户看清这句话的时间
            _updatingNow = true;   // 让 OnClosing 直接放行，别再弹任何模态框
            Services.UpdateService.ApplyAndExit(file);   // zip→就地解压覆盖(备份/回滚/保留unins)；exe→静默安装器兜底
        }
        catch (Exception ex)
        {
            _updateBusy = false;
            CheckUpdateButton.IsEnabled = true;
            UpdateStatusText.Text = "更新失败：" + ex.Message;
            ui.Fail(ex.Message + "\n\n可前往发布页手动下载安装。");
        }
    }

    // ================= 窗口快捷键 / 关闭 =================
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { Save(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z && PagePlans.Visibility == Visibility.Visible) { Undo_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape && PageRun.Visibility == Visibility.Visible) { SetNav(NavPlans, PagePlans); e.Handled = true; }   // 运行界面 ESC 直接回方案界面（运行继续在后台）
    }

    // 退出前：若有未保存的方案修改（增删改/排序/循环间隔等），弹框确认保存/放弃/取消。
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 更新流程已在 DownloadAndRun 里问过未保存方案，这里必须无条件放行：
        // 任何模态询问都会挂住 Shutdown，导致进程不退、安装目录被占、更新失败。
        if (!_updatingNow && !EnsureCurrentPlanClean()) { e.Cancel = true; return; }
        base.OnClosing(e);   // 窗口几何由 WindowMemory 在 Closing 时统一保存
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _runner?.Stop();
            _uiFlush?.Stop();
            _updateTimer?.Stop();   // 停掉 30s 更新轮询
            _traceRec.Stop();   // 兜底：录制中直接关窗也卸鼠标钩子、把剩余样本落盘
            DisableHotkeys();   // 兜底：运行中直接关窗也不泄漏热键/键盘钩子
            // 串口 Close() 偶发阻塞，放后台线程释放（后台线程不会阻止进程退出），
            // 避免卡住 UI 线程导致进程迟迟不退、单实例锁不释放。
            var backend = _backend; _backend = null;
            if (backend != null) System.Threading.Tasks.Task.Run(() => { try { backend.Dispose(); } catch { } });
        }
        catch { }
        base.OnClosed(e);
    }

    // ================= 右键菜单：方案 =================
    private void PlansList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _planRightClickOnItem = item != null;
        if (item != null) item.IsSelected = true;
    }
    private void PlansContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        cm.Items.Clear();
        bool onItem = _planRightClickOnItem && _plan != null;
        bool canPaste = _clipPlan != null;
        if (onItem)
        {
            int idx = _plans.IndexOf(_plan!);
            cm.Items.Add(MakeMenuItem("在上面新建方案", () => InsertNewPlan(idx)));
            cm.Items.Add(MakeMenuItem("在下面新建方案", () => InsertNewPlan(idx + 1)));
            cm.Items.Add(new Separator());
            cm.Items.Add(MakeMenuItem("重命名", () => RenamePlanIcon_Click(this, EmptyArgs)));
            cm.Items.Add(MakeMenuItem("复制", CopyCurrentPlan, gesture: "Ctrl+C"));
            cm.Items.Add(MakeMenuItem("在上面粘贴方案", () => PastePlan(idx), canPaste));
            cm.Items.Add(MakeMenuItem("在下面粘贴方案", () => PastePlan(idx + 1), canPaste));
            cm.Items.Add(new Separator());
            cm.Items.Add(MakeMenuItem("删除", () => DeletePlanIcon_Click(this, EmptyArgs), gesture: "Delete"));
        }
        else
        {
            cm.Items.Add(MakeMenuItem("新建方案", () => InsertNewPlan(_plans.Count)));
            cm.Items.Add(MakeMenuItem("粘贴方案", () => PastePlan(_plans.Count), canPaste, gesture: "Ctrl+V"));
        }
    }
    private void InsertNewPlan(int index)
    {
        if (!EnsureCurrentPlanClean()) return;
        var name = Prompt(this, "新建方案", "方案名称", $"方案 {_plans.Count + 1}");
        if (name == null) return;
        if (string.IsNullOrWhiteSpace(name)) name = $"方案 {_plans.Count + 1}";
        index = Math.Clamp(index, 0, _plans.Count);
        var p = new MacroPlan { Name = name.Trim() };
        AddPlanUnsaved(p, index);
    }
    private void PastePlan(int index)
    {
        if (_clipPlan == null) return;
        if (!EnsureCurrentPlanClean()) return;
        index = Math.Clamp(index, 0, _plans.Count);
        var p = ClonePlan(_clipPlan, _clipPlan.Name + " - 副本");
        AddPlanUnsaved(p, index);
    }
    private void CopyCurrentPlan()
    {
        if (_plan == null) return;
        _clipPlan = ClonePlan(_plan, _plan.Name);
        ShowToast($"已复制方案：{_plan.Name}");
    }
    private static MacroPlan ClonePlan(MacroPlan src, string name)
    {
        var p = new MacroPlan { Name = name, LoopCount = src.LoopCount, LoopDelayMs = src.LoopDelayMs };
        foreach (var s in src.Steps) p.Steps.Add(s.Clone());
        return p;
    }

    // ================= 右键菜单：动作 =================
    private void StepsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _stepRightClickOnItem = item != null;
        if (item != null && !IsBatchMode) item.IsSelected = true;
    }
    private void StepsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        cm.Items.Clear();
        bool onItem = _stepRightClickOnItem && StepsList.SelectedItem is MacroStep;
        bool running = _runner is { IsRunning: true };
        bool canPaste = _clip != null;
        if (onItem)
        {
            var step = StepsList.SelectedItem as MacroStep;
            cm.Items.Add(MakeMenuItem("从此动作开始执行", () => RunFromHere_Click(this, EmptyArgs), !running));
            cm.Items.Add(MakeMenuItem("单次执行（测试）", () => RunSingleStep_Click(this, EmptyArgs), !running));
            cm.Items.Add(new Separator());
            cm.Items.Add(MakeMenuItem("编辑", EditSelected));
            cm.Items.Add(MakeMenuItem(step!.Disabled ? "启用此项" : "禁用此项", ToggleDisableSelected));
            if (step is { IsGroup: true })
            {
                cm.Items.Add(MakeMenuItem("向组合添加动作", AddActionToGroup));
                cm.Items.Add(MakeMenuItem("拆分组合", Ungroup));
            }
            cm.Items.Add(new Separator());
            cm.Items.Add(MakeMenuItem("在前面新增动作", () => AddStepRelative(0)));
            cm.Items.Add(MakeMenuItem("在后面新增动作", () => AddStepRelative(1)));
            cm.Items.Add(new Separator());
            cm.Items.Add(MakeMenuItem("复制", () => StepCopy_Click(this, EmptyArgs), gesture: "Ctrl+C"));
            cm.Items.Add(MakeMenuItem("粘贴", () => StepPaste_Click(this, EmptyArgs), canPaste, gesture: "Ctrl+V"));
            cm.Items.Add(new Separator());
            cm.Items.Add(MakeMenuItem("上移", () => Move(-1)));
            cm.Items.Add(MakeMenuItem("下移", () => Move(1)));
            cm.Items.Add(MakeMenuItem("置顶", () => MoveSelectedToEdge(false)));
            cm.Items.Add(MakeMenuItem("置底", () => MoveSelectedToEdge(true)));
            cm.Items.Add(MakeMenuItem("移到指定序号之前…", () => MoveSelectedToNumber(false)));
            cm.Items.Add(MakeMenuItem("移到指定序号之后…", () => MoveSelectedToNumber(true)));
            cm.Items.Add(new Separator());
            cm.Items.Add(MakeMenuItem("删除", () => StepDelete_Click(this, EmptyArgs), gesture: "Delete"));
        }
        else
        {
            cm.Items.Add(MakeMenuItem("新建动作", () => OpenAddAction_Click(this, EmptyArgs), _plan != null));
            cm.Items.Add(MakeMenuItem("粘贴动作", () => StepPaste_Click(this, EmptyArgs), _plan != null && canPaste, gesture: "Ctrl+V"));
        }
    }

    private void RunFromHere_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) return;
        int i = StepsList.SelectedIndex;
        if (!EnsureCurrentPlanClean()) return;
        if (_plan == null) return;
        if (i < 0 || i >= _plan.Steps.Count) return;
        var sub = new MacroPlan { Name = $"{_plan.Name}（从第 {i + 1} 步）", LoopCount = 1 };
        _runOrigin.Clear(); _runSourcePlan = _plan;
        // 克隆（不动原方案对象）并重映射跳转：原 1 起序号 T 落到子列表 = T-i；指向起点之前的步（T≤i）无处可跳，清 0。
        foreach (var st in _plan.Steps.Skip(i))
        {
            var c = st.Clone();
            if (c.JumpTarget > i) c.JumpTarget -= i;
            else { c.JumpTarget = 0; c.JumpTimes = 0; }
            _runOrigin[c] = st;
            sub.Steps.Add(c);
        }
        RunPlan(sub, sub.Name);
    }
    private void RunSingleStep_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _step == null) return;
        int i = StepsList.SelectedIndex;
        if (!EnsureCurrentPlanClean()) return;
        if (_plan == null || i < 0 || i >= _plan.Steps.Count) return;
        var sub = new MacroPlan { Name = $"单次测试：第 {i + 1} 步", LoopCount = 1 };
        _runOrigin.Clear(); _runSourcePlan = _plan;
        var c = _plan.Steps[i].Clone();
        c.JumpTarget = 0; c.JumpTimes = 0;   // 单步测试无处可跳（也别动原对象）
        _runOrigin[c] = _plan.Steps[i];
        sub.Steps.Add(c);
        RunPlan(sub, sub.Name);
    }

    private void MoveSelectedToEdge(bool toEnd)
    {
        if (_plan == null || _step == null) return;
        int i = _plan.Steps.IndexOf(_step); if (i < 0) return;
        int target = toEnd ? _plan.Steps.Count - 1 : 0;
        if (i == target) return;
        PushUndo(); _plan.Steps.Move(i, target); RefreshIndices(); StepsList.SelectedItem = _step; MarkDirty();
    }
    private void MoveSelectedToNumber(bool after)
    {
        if (_plan == null || _step == null) return;
        int i = _plan.Steps.IndexOf(_step); if (i < 0) return;
        int count = _plan.Steps.Count;
        var txt = Prompt(this, after ? "移到指定序号之后" : "移到指定序号之前", $"请输入目标序号（1-{count}）：", (i + 1).ToString());
        if (string.IsNullOrWhiteSpace(txt) || !int.TryParse(txt.Trim(), out var r)) return;
        int t = Math.Clamp(r - 1, 0, count - 1);
        int dest = after ? t + 1 : t;
        if (i < dest) dest--;
        dest = Math.Clamp(dest, 0, count - 1);
        if (i == dest) return;
        PushUndo(); _plan.Steps.Move(i, dest); RefreshIndices(); StepsList.SelectedItem = _step; MarkDirty();
    }

    private void AddActionToGroup()
    {
        if (_step is not { IsGroup: true }) return;
        var g = _step!;
        var ns = ShowAddActionDialog();
        if (ns != null) { PushUndo(); g.Children.Add(ns); RefreshIndices(); MarkDirty(); }
    }
    private void Ungroup()
    {
        if (_plan == null || _step is not { IsGroup: true }) return;
        var g = _step!;
        int i = _plan.Steps.IndexOf(g); if (i < 0) return;
        PushUndo();
        _plan.Steps.RemoveAt(i);
        var kids = g.Children.ToList();
        // 子动作提升回顶层：其原 JumpTarget 是"在组合内/入组前"的旧序号，对当前顶层无意义，清掉。
        foreach (var c in kids) { c.JumpTarget = 0; c.JumpTimes = 0; }
        for (int k = 0; k < kids.Count; k++) _plan.Steps.Insert(i + k, kids[k]);
        RefreshIndices();
        if (i < _plan.Steps.Count) StepsList.SelectedIndex = i;
        MarkDirty();
    }

    // ================= 右键菜单：组合子动作 =================
    private void ChildEdit_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is MacroStep c) EditChild(c); }
    private void ChildAddBefore_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is MacroStep c) AddChildRelative(c, 0); }
    private void ChildAddAfter_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is MacroStep c) AddChildRelative(c, 1); }
    private void ChildRemove_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is MacroStep c) RemoveChildFromGroup(c); }
    private void ChildDelete_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is MacroStep c) DeleteChild(c); }
    // 删除组合内某个子动作：只从该组合移除；组合被删空时一并移除（不把剩余子动作解散回顶层）。
    private void DeleteChild(MacroStep child)
    {
        if (_plan == null) return;
        var g = FindParentGroup(child); if (g == null) return;
        PushUndo();
        g.Children.Remove(child);
        if (g.Children.Count == 0) _plan.Steps.Remove(g);
        SetFocusedStep(null);
        RefreshIndices(); MarkDirty();
    }
    private void ChildCopy_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is MacroStep c) { SetFocusedStep(c); _clip = c.Clone(); ShowToast("已复制动作"); } }
    private void ChildPaste_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is MacroStep c) { SetFocusedStep(c); StepPaste_Click(this, EmptyArgs); } }

    private void EditChild(MacroStep child)
    {
        var g = FindParentGroup(child); if (g == null) return;
        int idx = g.Children.IndexOf(child); if (idx < 0) return;
        var edited = child.IsGroup ? ShowEditGroupDialog(child) : ShowAddActionDialog(child); // 子动作可以是嵌套组合
        if (edited != null)
        {
            if (SameStepContent(edited, child)) return;
            PushUndo(); g.Children[idx] = edited; MarkDirty();
        }
    }
    // 顶层动作：在选中步骤之前(offset=0)/之后(offset=1)插入一个新动作。
    private void AddStepRelative(int offset)
    {
        if (_plan == null || _step == null) return;
        int i = _plan.Steps.IndexOf(_step); if (i < 0) return;
        var ns = ShowAddActionDialog();
        if (ns != null) { PushUndo(); _plan.Steps.Insert(Math.Clamp(i + offset, 0, _plan.Steps.Count), ns); RefreshIndices(); MarkDirty(); }
    }
    private void AddChildRelative(MacroStep child, int offset)
    {
        var g = FindParentGroup(child); if (g == null) return;
        int idx = g.Children.IndexOf(child); if (idx < 0) return;
        var ns = ShowAddActionDialog();
        if (ns != null) { PushUndo(); g.Children.Insert(Math.Clamp(idx + offset, 0, g.Children.Count), ns); MarkDirty(); }
    }
    private void RemoveChildFromGroup(MacroStep child)
    {
        if (_plan == null) return;
        var g = FindParentGroup(child); if (g == null) return;
        PushUndo();
        int gi = _plan.Steps.IndexOf(g);
        g.Children.Remove(child);
        child.JumpTarget = 0; child.JumpTimes = 0;   // 移出到顶层：旧跳转序号无意义，清掉
        _plan.Steps.RemoveAt(gi);
        int at = gi;
        if (g.Children.Count >= 2) _plan.Steps.Insert(at++, g);
        else { foreach (var c in g.Children.ToList()) { c.JumpTarget = 0; c.JumpTimes = 0; _plan.Steps.Insert(at++, c); } g.Children.Clear(); }
        _plan.Steps.Insert(at, child);
        RefreshIndices(); StepsList.SelectedIndex = at; MarkDirty();
    }
    private MacroStep? FindParentGroup(MacroStep child)
    {
        if (_plan == null) return null;
        return FindParentIn(_plan.Steps, child);
    }
    // 递归查 child 的父组合（支持任意嵌套深度）：先看本层各组合的直接子，再往下钻。
    private static MacroStep? FindParentIn(System.Collections.Generic.IEnumerable<MacroStep> steps, MacroStep child)
    {
        foreach (var s in steps)
            if (s.IsGroup)
            {
                if (s.Children.Contains(child)) return s;
                var deeper = FindParentIn(s.Children, child);
                if (deeper != null) return deeper;
            }
        return null;
    }

    // ================= 右键菜单：日志 + 运行页双击跳回 =================
    private void LogList_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C) { CopySelectedLog(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A) { LogList.SelectAll(); e.Handled = true; }
    }
    private void LogCopy_Click(object sender, RoutedEventArgs e) => CopySelectedLog();
    private void LogSelectAll_Click(object sender, RoutedEventArgs e) => LogList.SelectAll();
    private void LogOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Storage.LogDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Storage.LogDir, UseShellExecute = true });
        }
        catch (Exception ex) { ThemedDialog.Show("无法打开日志文件夹：" + ex.Message, "提示"); }
    }
    private void CopySelectedLog()
    {
        var src = LogList.SelectedItems.Count > 0 ? LogList.SelectedItems.Cast<LogEntry>() : _logs.AsEnumerable();
        var text = string.Join(Environment.NewLine, src.Select(le =>
            string.IsNullOrEmpty(le.Status) ? $"{le.Time}  {le.Body}" : $"{le.Time}  {le.Body}  [{le.Status}]"));
        if (!string.IsNullOrEmpty(text)) { try { Clipboard.SetText(text); } catch { } }
    }
    private void RunStepsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_runner is { IsRunning: true }) return;
        // 运行页展示的是克隆副本 → 先映射回真实步骤与来源方案，再跳到方案页打开其编辑窗口。
        if (RunStepsList.SelectedItem is not MacroStep clone) return;
        if (!_runOrigin.TryGetValue(clone, out var step) || _runSourcePlan == null || !_plans.Contains(_runSourcePlan)) return;
        if (_runSourcePlan != _plan && !EnsureCurrentPlanClean()) return;
        SetNav(NavPlans, PagePlans);
        PlansList.SelectedItem = _runSourcePlan;
        StepsList.SelectedItem = step;
        StepsList.ScrollIntoView(step);
        EditSelected();
    }

    // ================= 拖拽排序 =================
    private Point _dragStartPoint;
    private MacroPlan? _planDragItem;
    private bool _planDragging;
    private MacroStep? _dragItem;
    private ListBoxItem? _dragContainer;
    private bool _dragArmed, _dragging;
    private System.Windows.Threading.DispatcherTimer? _longPressTimer;
    private System.Windows.Threading.DispatcherTimer? _dragScrollTimer;
    private int _dragScrollDir;
    private InsertionAdorner? _insertionAdorner;
    private MacroStep? _focusedStep;

    // ---- 方案拖拽（移动超过阈值即触发）----
    private void PlansList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _planDragItem = item?.DataContext as MacroPlan;
        _planDragging = false;
        if (item == null) PlansList.SelectedIndex = -1;
    }
    private void PlansList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_planDragItem == null || e.LeftButton != MouseButtonState.Pressed || _planDragging) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _planDragging = true;
        try { DragDrop.DoDragDrop(PlansList, _planDragItem, DragDropEffects.Move); }
        finally { RemoveInsertionAdorner(); _planDragItem = null; _planDragging = false; }
    }
    private void PlansList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(MacroPlan)) is MacroPlan)
        {
            ShowPlanInsertionAdorner(GetPlanDropIndex(e.GetPosition(PlansList)));
            e.Effects = DragDropEffects.Move; e.Handled = true;
        }
    }
    private void PlansList_Drop(object sender, DragEventArgs e)
    {
        RemoveInsertionAdorner();
        if (e.Data.GetData(typeof(MacroPlan)) is not MacroPlan item) return;
        int from = _plans.IndexOf(item);
        if (from < 0) return;
        int drop = GetPlanDropIndex(e.GetPosition(PlansList));
        if (drop != from && drop != from + 1)
        {
            int to = Math.Clamp(drop > from ? drop - 1 : drop, 0, _plans.Count - 1);
            MovePlan(from, to);
        }
    }
    private void MovePlan(int from, int to)
    {
        if (from == to || from < 0 || from >= _plans.Count) return;
        var item = _plans[from];
        if (!EnsureCurrentPlanClean()) return;   // 先处理当前未保存方案，避免排序时把它一起提交
        from = _plans.IndexOf(item);
        if (from < 0 || from == to) return;
        to = Math.Clamp(to, 0, _plans.Count - 1);
        var cur = _plan;
        _plans.Move(from, to);
        PlansList.SelectedItem = cur ?? item;
        Save();   // 排序属结构改动，立即提交，保证排序后无未保存残留
        AddLog("Info", "已调整方案顺序。");
    }

    // ---- 动作拖拽（长按 350ms 触发，对应"长按可拖拽排序"）----
    private void StepsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsBatchMode) return;
        CancelDrag();
        _dragStartPoint = e.GetPosition(null);
        _dragContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _dragItem = _dragContainer?.DataContext as MacroStep;
        if (_dragItem == null) { if (_dragContainer == null) StepsList.SelectedIndex = -1; return; }
        _longPressTimer ??= CreateLongPressTimer();
        _longPressTimer.Stop(); _longPressTimer.Start();
    }
    private void StepsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        if (!_dragArmed)
        {
            var p = e.GetPosition(null);
            if (Math.Abs(p.X - _dragStartPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(p.Y - _dragStartPoint.Y) >= SystemParameters.MinimumVerticalDragDistance)
                CancelDrag(); // 长按尚未触发就移动 → 视作普通滚动/选择，取消拖拽
        }
        else if (!_dragging)
        {
            _dragging = true;
            try { DragDrop.DoDragDrop(StepsList, _dragItem, DragDropEffects.Move); }
            finally { StopDragScroll(); RemoveInsertionAdorner(); ResetDragVisual(); CancelDrag(); }
        }
    }
    private void StepsList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(MacroStep)) is MacroStep)
        {
            ShowInsertionAdorner(GetDropIndex(e.GetPosition(StepsList)));
            double y = e.GetPosition(StepsList).Y;
            if (y < 30) StartDragScroll(-1);
            else if (y > StepsList.ActualHeight - 30) StartDragScroll(1);
            else StopDragScroll();
            e.Effects = DragDropEffects.Move; e.Handled = true;
        }
    }
    private void StepsList_Drop(object sender, DragEventArgs e)
    {
        RemoveInsertionAdorner();
        if (_plan == null || e.Data.GetData(typeof(MacroStep)) is not MacroStep step) return;
        int from = _plan.Steps.IndexOf(step);
        if (from < 0) return;
        int to = GetDropIndex(e.GetPosition(StepsList));
        if (to != from && to != from + 1)
        {
            PushUndo();
            _plan.Steps.RemoveAt(from);
            if (to > from) to--;
            to = Math.Clamp(to, 0, _plan.Steps.Count);
            _plan.Steps.Insert(to, step);
            RefreshIndices(); StepsList.SelectedIndex = to; MarkDirty();
        }
    }
    private void StepHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MacroStep s) { SetFocusedStep(s); if (s.IsGroup) s.IsExpanded = true; }
    }
    private void Child_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 子动作点击后，父行 ListBoxItem 的 SelectionChanged 会把焦点重置到组合；延后一拍确保焦点落在子动作本身。
        if ((sender as FrameworkElement)?.DataContext is MacroStep s)
            Dispatcher.BeginInvoke(new Action(() => SetFocusedStep(s)), System.Windows.Threading.DispatcherPriority.Input);
    }
    private void BatchCheck_Changed(object sender, RoutedEventArgs e) => UpdateBatchCount();

    private System.Windows.Threading.DispatcherTimer CreateLongPressTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        t.Tick += (_, _) => ArmDrag();
        return t;
    }
    private void ArmDrag()
    {
        _longPressTimer?.Stop();
        if (_dragItem == null || _dragContainer == null || Mouse.LeftButton != MouseButtonState.Pressed) { CancelDrag(); return; }
        _dragArmed = true;
        _dragContainer.RenderTransformOrigin = new Point(0.5, 0.5);
        _dragContainer.RenderTransform = new ScaleTransform(1.04, 1.04);
        _dragContainer.Opacity = 0.85;
    }
    private void CancelDrag()
    {
        _longPressTimer?.Stop();
        _dragArmed = false; _dragging = false; _dragItem = null; _dragContainer = null;
    }
    private void ResetDragVisual()
    {
        if (_dragContainer != null) { _dragContainer.RenderTransform = null; _dragContainer.Opacity = 1.0; }
    }
    private void SetFocusedStep(MacroStep? step)
    {
        if (_focusedStep == step) return;
        if (_focusedStep != null) _focusedStep.IsFocused = false;
        _focusedStep = step;
        if (_focusedStep != null) _focusedStep.IsFocused = true;
    }
    private int GetDropIndex(Point pos) => DropIndex(StepsList, pos);
    private int GetPlanDropIndex(Point pos) => DropIndex(PlansList, pos);
    private static int DropIndex(ListBox list, Point pos)
    {
        for (int i = 0; i < list.Items.Count; i++)
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem lbi)
            {
                var p = lbi.TranslatePoint(new Point(0, 0), list);
                if (pos.Y < p.Y + lbi.ActualHeight / 2.0) return i;
            }
        return list.Items.Count;
    }
    private void ShowInsertionAdorner(int index) => ShowAdorner(StepsList, index);
    private void ShowPlanInsertionAdorner(int index) => ShowAdorner(PlansList, index);
    private void ShowAdorner(ListBox list, int index)
    {
        RemoveInsertionAdorner();
        if (list.Items.Count == 0) return;
        bool below = index >= list.Items.Count;
        int idx = below ? list.Items.Count - 1 : index;
        if (list.ItemContainerGenerator.ContainerFromIndex(idx) is ListBoxItem lbi)
        {
            var layer = AdornerLayer.GetAdornerLayer(lbi);
            if (layer != null) { _insertionAdorner = new InsertionAdorner(lbi, below); layer.Add(_insertionAdorner); }
        }
    }
    private void RemoveInsertionAdorner()
    {
        if (_insertionAdorner != null)
        {
            AdornerLayer.GetAdornerLayer(_insertionAdorner.AdornedElement)?.Remove(_insertionAdorner);
            _insertionAdorner = null;
        }
    }
    private void StartDragScroll(int dir)
    {
        _dragScrollDir = dir;
        if (_dragScrollTimer == null)
        {
            _dragScrollTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _dragScrollTimer.Tick += (_, _) =>
            {
                var sv = GetStepsScrollViewer();
                if (sv != null && _dragScrollDir != 0) sv.ScrollToVerticalOffset(sv.VerticalOffset + _dragScrollDir);
            };
        }
        _dragScrollTimer.Start();
    }
    private void StopDragScroll() { _dragScrollDir = 0; _dragScrollTimer?.Stop(); }
    private ScrollViewer? GetStepsScrollViewer() => FindDescendant<ScrollViewer>(StepsList);
    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) return null;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = FindDescendant<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    // 拖拽时的插入指示线（Accent 色横线 + 两端三角），覆盖在目标项上/下沿。
    private sealed class InsertionAdorner : Adorner
    {
        private static readonly Pen LinePen = CreatePen();
        private readonly bool _below;
        public InsertionAdorner(UIElement el, bool below) : base(el) { _below = below; IsHitTestVisible = false; }
        protected override void OnRender(DrawingContext dc)
        {
            double w = AdornedElement.RenderSize.Width;
            double y = _below ? AdornedElement.RenderSize.Height : 0.0;
            dc.DrawLine(LinePen, new Point(0, y), new Point(w, y));
            dc.DrawGeometry(LinePen.Brush, null, Marker(0, y, 1));
            dc.DrawGeometry(LinePen.Brush, null, Marker(w, y, -1));
        }
        private static Pen CreatePen()
        {
            var pen = new Pen((Application.Current?.Resources["Accent"] as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(15, 118, 110)), 2.0);
            pen.Freeze(); return pen;
        }
        private static Geometry Marker(double x, double y, int dir)
        {
            var f = new PathFigure { StartPoint = new Point(x, y - 5), IsClosed = true };
            f.Segments.Add(new LineSegment(new Point(x, y + 5), false));
            f.Segments.Add(new LineSegment(new Point(x + dir * 5, y), false));
            var g = new PathGeometry(); g.Figures.Add(f); g.Freeze(); return g;
        }
    }

    // ================= 小工具 =================
    private static readonly RoutedEventArgs EmptyArgs = new();
    private static MenuItem MakeMenuItem(string header, Action act, bool enabled = true, string gesture = "")
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (!string.IsNullOrEmpty(gesture)) mi.InputGestureText = gesture; // 快捷键右对齐显示，不再拼进 Header
        mi.Click += (_, _) => act();
        return mi;
    }
    private static T? FindAncestor<T>(DependencyObject? cur) where T : DependencyObject
    {
        while (cur != null && cur is not T) cur = VisualTreeHelper.GetParent(cur);
        return cur as T;
    }
    private static int ParseInt(string? s, int def) => int.TryParse((s ?? "").Trim(), out var v) ? v : def;
    private static double ParseDouble(string? s, double def) => double.TryParse((s ?? "").Trim(), out var v) ? v : def;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static string? TagOf(Selector cb) => (cb.SelectedItem as ComboBoxItem)?.Tag as string;
    private static void SelectByTag(Selector cb, string val, bool byContent = false)
    {
        foreach (var it in cb.Items)
            if (it is ComboBoxItem ci && ((byContent ? ci.Content as string : ci.Tag as string) == val)) { cb.SelectedItem = ci; return; }
    }

    /// <summary>简易输入对话框（新建/重命名方案用），代码构建，无独立 XAML。</summary>
    private static string? Prompt(Window owner, string title, string label, string initial)
    {
        // owner 可能尚未显示（启动时在主窗口 Show 之前弹桥片命名框）——此时不设 Owner，居中于屏幕。
        bool ownerShown = owner.IsLoaded || owner.IsVisible;
        var win = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = ownerShown ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = ownerShown ? owner : null,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI"),
        };
        // 用 SetResourceReference 绑定主题资源（随主题变化），与参考一致。
        win.SetResourceReference(Control.BackgroundProperty, "Bg");
        win.SetResourceReference(Control.ForegroundProperty, "Ink");
        win.SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBar(win, ThemeManager.EffectiveDark);

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Grid.SetRow(lbl, 0); grid.Children.Add(lbl);
        var tb = new TextBox { Text = initial, Height = 34, MinWidth = 340 };
        Grid.SetRow(tb, 1); grid.Children.Add(tb);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        Grid.SetRow(row, 2); grid.Children.Add(row);
        string? result = null;
        // 按钮不设显式 Style：套用 App.xaml 隐式主题按钮（与参考一致，避免蓝色默认按钮）。
        var ok = new Button { Content = "确定", Width = 86, Height = 34, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", Width = 86, Height = 34, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        ok.Click += (_, _) => { result = tb.Text; win.DialogResult = true; };
        row.Children.Add(ok); row.Children.Add(cancel);
        win.Content = grid;
        win.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return win.ShowDialog() == true ? result : null;
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern int GetKeyboardType(int nTypeFlag);
}
