using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MacroPilot.Services;

/// <summary>
/// 主题化消息框，替代系统 MessageBox：用应用的 Panel/Ink 配色、主题标题栏与按钮样式，
/// 与参考标准外观一致。签名兼容 System.Windows.MessageBox.Show。
/// </summary>
public static class ThemedDialog
{
    /// <summary>自定义按钮文案的选择框：返回被点按钮的下标；按 Esc/关闭返回最后一个（约定为“取消”）的下标。</summary>
    public static int ShowChoice(string message, string title, params string[] labels)
    {
        var res = Application.Current?.Resources;
        Brush bg = res?["Panel"] as Brush ?? Brushes.White;
        Brush fg = res?["Ink"] as Brush ?? Brushes.Black;
        var owner = Application.Current?.MainWindow;

        var win = new Window
        {
            Title = title,
            Owner = owner != null && owner.IsVisible ? owner : null,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = (owner != null && owner.IsVisible) ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, MinWidth = 340,
            Background = bg, Foreground = fg,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI"),
        };
        win.SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBar(win, ThemeManager.EffectiveDark);

        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 0, 0, 20) };
        Grid.SetRow(text, 0); grid.Children.Add(text);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(bar, 1); grid.Children.Add(bar);

        int chosen = labels.Length - 1;   // Esc/关闭 → 最后一个（取消）
        for (int i = 0; i < labels.Length; i++)
        {
            int idx = i;
            var btn = new Button
            {
                Content = labels[i], MinWidth = 96, Height = 34, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 0, 12, 0),
                IsDefault = i == 0, IsCancel = i == labels.Length - 1,
            };
            btn.Click += (_, _) => { chosen = idx; win.DialogResult = true; };
            bar.Children.Add(btn);
        }

        win.Content = grid;
        win.ShowDialog();
        return chosen;
    }

    public static MessageBoxResult Show(string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        var owner = Application.Current?.MainWindow;
        var res = Application.Current?.Resources;
        Brush bg = res?["Panel"] as Brush ?? Brushes.White;
        Brush fg = res?["Ink"] as Brush ?? Brushes.Black;

        var win = new Window
        {
            Title = title,
            Owner = owner != null && owner.IsVisible ? owner : null,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = (owner != null && owner.IsVisible) ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            MinWidth = 320,
            Background = bg,
            Foreground = fg,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI"),
        };
        win.SourceInitialized += (_, _) => ThemeManager.ApplyWindowTitleBar(win, ThemeManager.EffectiveDark);

        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 0, 0, 20) };
        Grid.SetRow(text, 0);
        grid.Children.Add(text);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);

        var result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;

        void Add(string content, MessageBoxResult value, bool isDefault = false, bool isCancel = false)
        {
            // 不显式设 Style：让 App.xaml 的隐式 {x:Type Button} 主题样式自动套用（与参考一致；
            // 显式设 null 反而会屏蔽隐式样式、退回 WPF 默认按钮的蓝色 IsDefault 高亮）。
            var btn = new Button
            {
                Content = content, Width = 86, Height = 34, Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault, IsCancel = isCancel,
            };
            btn.Click += (_, _) => { result = value; win.DialogResult = true; };
            bar.Children.Add(btn);
        }

        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                Add("确定", MessageBoxResult.OK, isDefault: true);
                Add("取消", MessageBoxResult.Cancel, isCancel: true);
                break;
            case MessageBoxButton.YesNo:
                Add("是", MessageBoxResult.Yes, isDefault: true);
                Add("否", MessageBoxResult.No, isCancel: true);
                break;
            case MessageBoxButton.YesNoCancel:
                Add("是", MessageBoxResult.Yes, isDefault: true);
                Add("否", MessageBoxResult.No);
                Add("取消", MessageBoxResult.Cancel, isCancel: true);
                break;
            default:
                Add("确定", MessageBoxResult.OK, isDefault: true, isCancel: true);
                break;
        }

        win.Content = grid;
        win.ShowDialog();
        return result;
    }
}
