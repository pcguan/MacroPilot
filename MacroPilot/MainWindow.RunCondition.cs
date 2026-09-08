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
        public readonly ComboBox LogicCombo = new();                       // 与 / 或 / 自定义表达式
        public readonly System.Windows.Controls.TextBox Expr = new();      // 自定义表达式内容（LogicCombo 选第 3 项时生效）
        public readonly System.Collections.Generic.List<ConditionItem> Items = new();   // 条件列表（编辑期副本）
        public readonly CheckBox Retry = new();                            // 条件不满足时重复检查
        public readonly System.Windows.Controls.TextBox RetryInterval = new(), RetryMax = new(), RetryTimeout = new();
        public readonly ComboBox RetryIntervalUnit = new(), RetryTimeoutUnit = new();   // 0毫秒 1秒 2分钟 3小时
        public StackPanel Panel = null!;
        /// <summary>条件列表变更后刷新列表 UI（由 BuildRunConditionPanel 赋值）。</summary>
        public Action RefreshItems = () => { };
    }

    /// <summary>建一套完整的运行条件控件并回填 source（source 为 null 即新建）。</summary>
    private RunConditionEditor BuildRunConditionEditor(IRunCondition? source)
    {
        var ed = new RunConditionEditor();
        ed.Panel = BuildRunConditionPanel(ed);
        if (source != null) LoadRunCondition(ed, source);
        return ed;
    }

    // 回填必须把条件列表也读进来：动作对话框是"先 BuildRunConditionEditor(null) 建面板、
    // 事后再 LoadRunCondition 回填"，若只在构造路径读列表，编辑既有动作时条件会显示为空。
    private static void LoadRunCondition(RunConditionEditor ed, IRunCondition src)
    {
        RunCondition.Normalize(src);                       // 历史存档的单条字段并入列表
        ed.Items.Clear();
        foreach (var it in src.RunConditions) ed.Items.Add(it.Clone());   // 副本：取消编辑时不影响原对象
        ed.RefreshItems();
        ed.Enabled.IsChecked = RunCondition.Has(src);
        if (CondExpr.IsExpr(src.RunConditionLogic)) { ed.LogicCombo.SelectedIndex = 2; ed.Expr.Text = CondExpr.Get(src.RunConditionLogic); }
        else ed.LogicCombo.SelectedIndex = string.Equals(src.RunConditionLogic, "Or", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ed.Retry.IsChecked = src.RunConditionRetry;
        // 间隔 0 是合法值（=立刻重判），不能像以前那样把 0 当"没设置"顶成 1000
        int iv = Math.Max(0, src.RunConditionRetryIntervalMs);
        int ivU = Math.Clamp(src.RunConditionRetryIntervalUnit, 0, 3);
        ed.RetryIntervalUnit.SelectedIndex = ivU;
        ed.RetryInterval.Text = FormatDelayValue(iv, ivU);
        ed.RetryMax.Text = Math.Max(0, src.RunConditionRetryMax).ToString();
        int to = Math.Max(0, src.RunConditionRetryTimeoutMs);
        int toU = Math.Clamp(src.RunConditionRetryTimeoutUnit, 0, 3);
        ed.RetryTimeoutUnit.SelectedIndex = toU;
        ed.RetryTimeout.Text = FormatDelayValue(to, toU);
    }

    /// <summary>把编辑结果写回 dst。输入不合法时抛 InvalidOperationException，由调用方统一提示。</summary>
    private static void ApplyRunCondition(RunConditionEditor ed, IRunCondition dst)
    {
        RunCondition.Clear(dst);
        if (ed.Enabled.IsChecked != true) return;

        var valid = ed.Items.FindAll(i => i.IsValid);
        if (valid.Count == 0) throw new InvalidOperationException("运行条件已启用，请至少添加一条有效的条件。");
        foreach (var it in valid) dst.RunConditions.Add(it.Clone());
        dst.RunConditionLogic = ResolveLogic(ed.LogicCombo, ed.Expr, ed.Items);
        ApplyRetry(ed, dst);
    }

    // 重复检查三项（两种条件类型共用；放在 RunCondition.Clear 之后写，否则会被清掉）。
    private static void ApplyRetry(RunConditionEditor ed, IRunCondition dst)
    {
        dst.RunConditionRetry = ed.Retry.IsChecked == true;
        int ivU = Math.Clamp(ed.RetryIntervalUnit.SelectedIndex, 0, 3);
        int toU = Math.Clamp(ed.RetryTimeoutUnit.SelectedIndex, 0, 3);
        // 0 是合法值：间隔 0=立刻重判、时限 0=不限时长。别再把 0 兜成默认值。
        dst.RunConditionRetryIntervalMs = (int)Math.Round(Math.Max(0, ParseDouble(ed.RetryInterval.Text, 0)) * LoopUnitFactor(ivU));
        dst.RunConditionRetryIntervalUnit = ivU;
        dst.RunConditionRetryMax = Math.Max(0, ParseInt(ed.RetryMax.Text, 0));
        dst.RunConditionRetryTimeoutMs = (int)Math.Round(Math.Max(0, ParseDouble(ed.RetryTimeout.Text, 0)) * LoopUnitFactor(toU));
        dst.RunConditionRetryTimeoutUnit = toU;
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

        // ---- 失败处理 ----
        var pauseChk = new CheckBox { Content = "动作执行失败时立即暂停（F9 继续）", IsChecked = plan.PauseOnFail };
        sp.Children.Add(GroupCard("失败处理",
            pauseChk,
            new TextBlock
            {
                Text = "动作失败通常意味着画面或状态与预期不符，自动暂停可避免后续误操作。\n若动作自身设置了「运行失败后」监听，则它的失败交由监听处理，不触发暂停。",
                Foreground = (System.Windows.Media.Brush)FindResource("Muted"),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            }));

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

                // 条件是否有变：整体序列化后比较，别再手写字段列表——历史上每加一个字段都得记得补一行，
                // 漏了就"改了不标脏、不落盘"。条件已升级成列表，逐字段比更不现实。
                bool pause = pauseChk.IsChecked == true;
                changed = loops != plan.LoopCount || delayMs != plan.LoopDelayMs || u != plan.LoopDelayUnit
                          || pause != plan.PauseOnFail
                          || RunCondition.Snapshot(probe) != RunCondition.Snapshot(plan);

                plan.LoopCount = loops; plan.LoopDelayMs = delayMs; plan.LoopDelayUnit = u;
                plan.PauseOnFail = pause;
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
