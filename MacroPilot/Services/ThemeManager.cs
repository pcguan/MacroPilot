using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MacroPilot.Services;

/// <summary>明暗主题 + 暖色品牌调色板（与参考版一致）。Accent 取系统强调色。</summary>
public static class ThemeManager
{
    public enum Mode { System, Light, Dark }

    public static bool EffectiveDark { get; private set; }

    private static readonly Dictionary<string, (Color Light, Color Dark)> Palette = new()
    {
        // 暗色改为"暖色深色版的同一产品"：微暖深底(#201D1A 系) + 去饱和暖驼强调色(#C8A87E)，
        // 而不是纯灰 #1E1E1E/#A8A8A8（那会让选中/悬停灰上灰、暖色身份丢失）。
        ["Bg"]         = (Hex("#F3EFE7"), Hex("#201D1A")),
        ["Panel"]      = (Hex("#FFFDF8"), Hex("#2A2623")),
        ["Ink"]        = (Hex("#1F2933"), Hex("#ECE7DF")),
        ["Muted"]      = (Hex("#5A6472"), Hex("#B3A99B")),
        // 说明性辅助文字：比 Muted 略亮/略深，保证暗色下对比度(无障碍)又与主标题分层。
        ["Subtext"]    = (Hex("#6B7280"), Hex("#CBC2B4")),
        // 浅色 = 暖米底配深米驼；深色 = 暖深底配去饱和暖驼（选中/悬停/强调都带暖色身份）。
        ["Accent"]     = (Hex("#8A7860"), Hex("#C8A87E")),
        ["AccentDark"] = (Hex("#6F604C"), Hex("#B0916A")),
        ["Line"]       = (Hex("#E7DED0"), Hex("#3B352E")),
        ["ButtonBg"]   = (Hex("#E8E0D4"), Hex("#38322C")),
        ["ButtonHover"]= (Hex("#DCD2C2"), Hex("#48413A")),
        ["Field"]      = (Hex("#FFFFFF"), Hex("#2F2A25")),
        ["LogBg"]      = (Hex("#FFF8EE"), Hex("#1C1916")),
        ["Highlight"]  = (Hex("#FFE3A8"), Hex("#544420")),
        ["Success"]    = (Hex("#15803D"), Hex("#6FCF97")),
        ["Danger"]     = (Hex("#B91C1C"), Hex("#F08A8A")),
        ["Warning"]    = (Hex("#B45309"), Hex("#E0B15A")),
        ["SuccessBg"]  = (Hex("#DCF3E3"), Hex("#2A4A33")),
        ["DangerBg"]   = (Hex("#FBE0E0"), Hex("#4A2A2A")),
        ["WarningBg"]  = (Hex("#FCEFD6"), Hex("#4A3C1E")),
    };

    public static Mode Parse(string? v) => v switch { "Light" => Mode.Light, "Dark" => Mode.Dark, _ => Mode.System };

    public static bool IsEffectiveDark(Mode m) => m switch { Mode.System => IsSystemDark(), Mode.Dark => true, _ => false };

    public static void Apply(string? mode) => Apply(Parse(mode));

    public static void Apply(Mode mode)
    {
        bool dark = EffectiveDark = IsEffectiveDark(mode);
        // 同步 WPF-UI 自身主题，否则 FluentWindow 会跟随系统主题渲染（导致内容区与系统不一致）
        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.None, updateAccent: false);
        }
        catch { }
        var res = Application.Current?.Resources;
        if (res == null) return;
        foreach (var kv in Palette)
            SetBrush(res, kv.Key, dark ? kv.Value.Dark : kv.Value.Light);

        // Accent 取调色板里与主题同系的中性强调色（不读系统强调色，避免蓝色等异色相突兀），
        // 派生 OnAccent/Hover/Selected/AccentBg——半透明叠加后即浅色下的米色系、深色下的灰色系。
        var accent = dark ? Palette["Accent"].Dark : Palette["Accent"].Light;
        SetBrush(res, "OnAccent", Luminance(accent) < 0.6 ? Colors.White : Hex("#1F2933"));
        SetBrush(res, "Hover", Color.FromArgb((byte)(dark ? 40 : 28), accent.R, accent.G, accent.B));
        SetBrush(res, "Selected", Color.FromArgb((byte)(dark ? 72 : 56), accent.R, accent.G, accent.B));
        SetBrush(res, "AccentBg", Color.FromArgb((byte)(dark ? 56 : 36), accent.R, accent.G, accent.B));
    }

    public static void ApplyWindowTitleBar(Window window, bool dark)
    {
        var h = new WindowInteropHelper(window).Handle;
        if (h == IntPtr.Zero) return;
        int v = dark ? 1 : 0;
        if (DwmSetWindowAttribute(h, 20, ref v, 4) != 0)
            DwmSetWindowAttribute(h, 19, ref v, 4);
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int i) return i == 0;
        }
        catch { }
        return false;
    }

    private static Color? GetSystemAccent()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int n)
            {
                uint u = (uint)n;
                return Color.FromRgb((byte)(u & 0xFF), (byte)((u >> 8) & 0xFF), (byte)((u >> 16) & 0xFF));
            }
        }
        catch { }
        return null;
    }

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
    private static Color Darken(Color c, double a) => Color.FromRgb((byte)(c.R * (1 - a)), (byte)(c.G * (1 - a)), (byte)(c.B * (1 - a)));
    private static void SetBrush(ResourceDictionary res, string key, Color c) { var b = new SolidColorBrush(c); b.Freeze(); res[key] = b; }
    private static Color Hex(string v) => (Color)ColorConverter.ConvertFromString(v);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
}
