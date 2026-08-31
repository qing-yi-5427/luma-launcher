using Microsoft.Win32;
using System.Windows.Media;

namespace LumaLauncher.Services;

public static class ThemeService
{
    private sealed record Palette(
        string Window, string Panel, string Hover, string Selected,
        string Text, string Muted, string Faint, string Stroke,
        string Accent, string AccentSoft);

    private static readonly Palette LumaDark = new(
        "#F3121519", "#FF1A1E23", "#FF22272D", "#FF282C2D",
        "#FFF4F1EA", "#FF969A9E", "#FF656A70", "#FF30353B",
        "#FFE9A84C", "#2EE9A84C");

    private static readonly Palette LumaLight = new(
        "#F7F2F0EA", "#FFFAF8F3", "#FFF0ECE4", "#FFE9E3D8",
        "#FF1B1D20", "#FF656A70", "#FF8A8E92", "#FFDAD4C9",
        "#FFB86D1D", "#24B86D1D");

    private static readonly IReadOnlyDictionary<string, Palette> Palettes =
        new Dictionary<string, Palette>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dark"] = LumaDark,
            ["Light"] = LumaLight,
            ["Win11Blue"] = new(
                "#F20C1623", "#FF111F2E", "#FF192C40", "#FF20384E",
                "#FFF5F9FC", "#FFA9B7C5", "#FF728396", "#FF2D4358",
                "#FF60CDFF", "#3260CDFF"),
            ["Win11Graphite"] = new(
                "#F218191B", "#FF202225", "#FF292C30", "#FF33373C",
                "#FFF7F7F7", "#FFB2B6BC", "#FF777D85", "#FF3A3E44",
                "#FFA8B3C5", "#30A8B3C5"),
            ["Win11Mist"] = new(
                "#F4F3F7FB", "#FFF9FBFD", "#FFEAF1F8", "#FFDDEAF6",
                "#FF18212B", "#FF536273", "#FF7C8996", "#FFD2DCE6",
                "#FF0067C0", "#240067C0"),
            ["Win11Sage"] = new(
                "#F3F1F6F2", "#FFF8FBF8", "#FFE7F1EB", "#FFD8EADF",
                "#FF17221C", "#FF52665B", "#FF7A8C82", "#FFCFDDD5",
                "#FF0F7B6C", "#260F7B6C")
        };

    public static void Apply(string requestedTheme)
    {
        var palette = requestedTheme.Equals("System", StringComparison.OrdinalIgnoreCase)
            ? SystemPrefersLight() ? LumaLight : LumaDark
            : Palettes.GetValueOrDefault(requestedTheme, LumaDark);

        Set("WindowBrush", palette.Window);
        Set("PanelBrush", palette.Panel);
        Set("PanelHoverBrush", palette.Hover);
        Set("PanelSelectedBrush", palette.Selected);
        Set("TextBrush", palette.Text);
        Set("MutedTextBrush", palette.Muted);
        Set("FaintTextBrush", palette.Faint);
        Set("StrokeBrush", palette.Stroke);
        Set("AccentBrush", palette.Accent);
        Set("AccentSoftBrush", palette.AccentSoft);
    }

    private static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Set(string key, string color)
    {
        var parsed = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
        var brush = new SolidColorBrush(parsed);
        brush.Freeze();
        System.Windows.Application.Current.Resources[key] = brush;
    }
}
