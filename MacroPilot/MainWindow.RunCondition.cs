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
        public readonly ComboBox LogicCombo = new();                       // 与 / 或
        public readonly System.Collections.Generic.List<ConditionItem> Items = new();   // 条件列表（编辑期副本）
        public readonly CheckBox Retry = new();                            // 条件不满足时重复检查
        public readonly System.Windows.Controls.TextBox RetryInterval = new(), RetryMax = new();
        public StackPanel Panel = null!;
    }

    /// <summary>建一套完整的运行条件控件并回填 source（source 为 null 即新建）。</summary>
    private RunConditionEditor BuildRunConditionEditor(IRunCondition? source)
    {
        var ed = new RunConditionEditor();
        // 先把源对象上的条件读进编辑期副本，再建面板——面板要按条数决定"与/或"是否显示。
        if (source != null)
        {
            RunCondition.Normalize(source);                                // 历史存档的单条字段并入列表
            foreach (var it in source.RunConditions) ed.Items.Add(it.Clone());   // 副本：取消时不影响原对象
        }
        ed.Panel = BuildRunConditionPanel(ed.Enabled, ed.Items, ed.LogicCombo, ed.Retry, ed.RetryInterval, ed.RetryMax);
        if (source != null) LoadRunCondition(ed, source);
        return ed;
    }

    private static void LoadRunCondition(RunConditionEditor ed, IRunCondition src)
    {
        RunCondition.Normalize(src);
        ed.Enabled.IsChecked = RunCondition.Has(src);
        ed.LogicCombo.SelectedIndex = string.Equals(src.RunConditionLogic, "Or", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ed.Retry.IsChecked = src.RunConditionRetry;
        ed.RetryInterval.Text = (src.RunConditionRetryIntervalMs <= 0 ? 1000 : src.RunConditionRetryIntervalMs).ToString();
        ed.RetryMax.Text = Math.Max(0, src.RunConditionRetryMax).ToString();
    }

    /// <summary>把编辑结果写回 dst。输入不合法时抛 InvalidOperationException，由调用方统一提示。</summary>
    private static void ApplyRunCondition(RunConditionEditor ed, IRunCondition dst)
    {
        RunCondition.Clear(dst);
        if (ed.Enabled.IsChecked != true) return;

        var valid = ed.Items.FindAll(i => i.IsValid);
        if (valid.Count == 0) throw new InvalidOperationException("运行条件已启用，请至少添加一条有效的条件。");
        foreach (var it in valid) dst.RunConditions.Add(it.Clone());
        dst.RunConditionLogic = (ed.LogicCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "And";
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

                // 条件是否有变：整体序列化后比较，别再手写字段列表——历史上每加一个字段都得记得补一行，
                // 漏了就"改了不标脏、不落盘"。条件已升级成列表，逐字段比更不现实。
                changed = loops != plan.LoopCount || delayMs != plan.LoopDelayMs || u != plan.LoopDelayUnit
                          || RunCondition.Snapshot(probe) != RunCondition.Snapshot(plan);

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
