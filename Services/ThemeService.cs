using Microsoft.Win32;
using System.Windows.Media;

namespace LumaLauncher.Services;

public static class ThemeService
{
    public static void Apply(string requestedTheme)
    {
        var light = requestedTheme.Equals("Light", StringComparison.OrdinalIgnoreCase) ||
                    requestedTheme.Equals("System", StringComparison.OrdinalIgnoreCase) && SystemPrefersLight();

        Set("WindowBrush", light ? "#F7F2F0EA" : "#F3121519");
        Set("PanelBrush", light ? "#FFFAF8F3" : "#FF1A1E23");
        Set("PanelHoverBrush", light ? "#FFF0ECE4" : "#FF22272D");
        Set("PanelSelectedBrush", light ? "#FFE9E3D8" : "#FF282C2D");
        Set("TextBrush", light ? "#FF1B1D20" : "#FFF4F1EA");
        Set("MutedTextBrush", light ? "#FF656A70" : "#FF969A9E");
        Set("FaintTextBrush", light ? "#FF8A8E92" : "#FF656A70");
        Set("StrokeBrush", light ? "#FFDAD4C9" : "#FF30353B");
        Set("AccentBrush", light ? "#FFB86D1D" : "#FFE9A84C");
        Set("AccentSoftBrush", light ? "#24B86D1D" : "#2EE9A84C");
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
        System.Windows.Application.Current.Resources[key] = new SolidColorBrush(parsed);
    }
}
