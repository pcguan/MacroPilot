using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using MacroPilot.Models;
using Xunit;
using Xunit.Abstractions;

namespace MacroPilot.Tests;

/// <summary>
/// Measures real production list templates and WPF layout, without constructing MainWindow,
/// creating Application, showing a desktop window, or touching hardware/user storage.
/// This checks layout/virtualization, not presentation latency of the whole desktop application.
/// </summary>
[Collection(PerformanceProbeCollection.Name)]
public sealed class WpfLogLayoutProbeTests
{
    private readonly ITestOutputHelper _output;
    public WpfLogLayoutProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ProductionListsVirtualizeLargeSourcesAndBoundIncrementalLayout()
    {
        var done = new TaskCompletionSource<LayoutReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            LayoutReport? report = null;
            Exception? failure = null;
            try { report = MeasureProductionLists(); }
            catch (Exception ex) { failure = ex; }
            finally
            {
                // There is no Application singleton or global dispatcher loop to shut down.
                // Dispose the dispatcher created only for this thread after detaching controls.
                try { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
                catch (Exception ex) { failure ??= ex; }
                if (failure != null) done.TrySetException(failure);
                else done.TrySetResult(report!);
            }
        }) { IsBackground = true, Name = "MacroPilot WPF layout probe" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        LayoutReport result;
        try { result = await done.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        finally
        {
            Assert.True(await Task.Run(() => thread.Join(TimeSpan.FromSeconds(2))), "The probe must release its STA thread.");
        }

        _output.WriteLine($"production_xaml_ms={result.XamlMs:F2}; initial_layout_ms={result.InitialMs:F2}; " +
            $"steps=10000; generated_step_containers={result.InitialSteps}; " +
            $"logs=2000; generated_log_containers={result.InitialLogs}");
        _output.WriteLine($"incremental_64_row_batch_ms=[{string.Join(", ", result.BatchMs.Select(ms => ms.ToString("F2")))}]; " +
            $"tail_step_containers={result.FinalSteps}; tail_log_containers={result.FinalLogs}; " +
            "fixed_viewport=1080x560; hidden_measure_arrange_only=true");

        // Structural bounds are portable; timing numbers are evidence, not machine-speed promises.
        Assert.InRange(result.InitialSteps, 1, 500);
        Assert.InRange(result.InitialLogs, 1, 300);
        Assert.InRange(result.FinalSteps, 1, 500);
        Assert.InRange(result.FinalLogs, 1, 300);
        Assert.True(result.StepTailRealized, "ScrollIntoView must realize the final step with virtualization enabled.");
        Assert.True(result.LogTailRealized, "ScrollIntoView must realize the newest log with virtualization enabled.");
    }

    private static LayoutReport MeasureProductionLists()
    {
        // Ensure the XAML URI mapping used by the production merged dictionaries is loaded.
        _ = typeof(Wpf.Ui.Controls.FluentWindow).Assembly;
        var parse = Stopwatch.StartNew();
        var grid = LoadProductionLists();
        double xamlMs = parse.Elapsed.TotalMilliseconds;
        var stepsList = (ListBox)grid.FindName("RunStepsList");
        var logList = (ListBox)grid.FindName("LogList");
        var steps = new ObservableCollection<MacroStep>(Enumerable.Range(1, 10000).Select(index =>
            new MacroStep { Type = "Wait", DurationMs = 3000, DisplayIndex = index, Note = "动作 " + index }));
        var logs = new ObservableCollection<LogEntry>(Enumerable.Range(1, 2000).Select(NewLog));

        try
        {
            var initial = Stopwatch.StartNew();
            stepsList.ItemsSource = steps;
            logList.ItemsSource = logs;
            Layout(grid);
            double initialMs = initial.Elapsed.TotalMilliseconds;
            int initialSteps = GeneratedCount(stepsList);
            int initialLogs = GeneratedCount(logList);
            var samples = new List<double>();
            for (int batch = 0; batch < 8; batch++)
            {
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < 64; i++)
                {
                    logs.RemoveAt(0);
                    logs.Add(NewLog(2001 + batch * 64 + i));
                }
                steps[^1].IsExecuting = true;
                stepsList.ScrollIntoView(steps[^1]);
                logList.ScrollIntoView(logs[^1]);
                Layout(grid);
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            return new LayoutReport(xamlMs, initialMs, initialSteps, initialLogs,
                GeneratedCount(stepsList), GeneratedCount(logList), samples.ToArray(),
                stepsList.ItemContainerGenerator.ContainerFromIndex(steps.Count - 1) != null,
                logList.ItemContainerGenerator.ContainerFromIndex(logs.Count - 1) != null);
        }
        finally
        {
            stepsList.ItemsSource = null;
            logList.ItemsSource = null;
            grid.Children.Clear();
            grid.Resources.Clear();
        }
    }

    private static LogEntry NewLog(int index) => new()
    {
        Id = index, Time = "12:34:56.789", Level = index % 11 == 0 ? "Warning" : "Info",
        Body = $"动作 {index}：等待 3 秒后执行下一项；嵌套组合与监听的日志需要正确换行，同时保留实际执行状态。",
        Status = "执行成功", StatusKind = "Success",
    };

    private static Grid LoadProductionLists()
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string fixtures = Path.Combine(AppContext.BaseDirectory, "LayoutFixtures");
        var app = XDocument.Load(Path.Combine(fixtures, "App.xaml"));
        var main = XDocument.Load(Path.Combine(fixtures, "MainWindow.xaml"));
        var resources = new XElement(app.Descendants(p + "ResourceDictionary").First());
        XElement Extract(string name, int column)
        {
            var element = new XElement(main.Descendants(p + "ListBox").Single(node => (string?)node.Attribute("Name") == name));
            // Keep production properties, resources and templates. Only remove behavior hooks
            // whose handlers belong to MainWindow, and menus unrelated to list layout.
            element.Elements(p + "ListBox.ContextMenu").Remove();
            element.Attribute("MouseDoubleClick")?.Remove();
            element.Attribute("KeyDown")?.Remove();
            element.SetAttributeValue("Grid.Column", column);
            return element;
        }
        var root = new XElement(p + "Grid",
            new XAttribute("xmlns", p.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "ui", "http://schemas.lepo.co/wpfui/2022/xaml"),
            new XAttribute("UseLayoutRounding", "True"),
            new XElement(p + "Grid.Resources", resources),
            new XElement(p + "Grid.ColumnDefinitions",
                new XElement(p + "ColumnDefinition", new XAttribute("Width", "430")),
                new XElement(p + "ColumnDefinition", new XAttribute("Width", "650"))),
            Extract("RunStepsList", 0), Extract("LogList", 1));
        using var reader = root.CreateReader();
        return (Grid)XamlReader.Load(reader);
    }

    private static void Layout(Grid grid)
    {
        grid.Measure(new Size(1080, 560));
        grid.Arrange(new Rect(0, 0, 1080, 560));
        grid.UpdateLayout();
        // Bindings, ScrollIntoView and virtualized container generation may enqueue dispatcher
        // work. Pump only this isolated dispatcher, then settle the fixed-size layout again.
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        grid.UpdateLayout();
    }

    private static int GeneratedCount(ListBox list)
    {
        int count = 0;
        for (int i = 0; i < list.Items.Count; i++)
            if (list.ItemContainerGenerator.ContainerFromIndex(i) != null) count++;
        return count;
    }

    private sealed record LayoutReport(double XamlMs, double InitialMs, int InitialSteps, int InitialLogs,
        int FinalSteps, int FinalLogs, double[] BatchMs, bool StepTailRealized, bool LogTailRealized);
}
