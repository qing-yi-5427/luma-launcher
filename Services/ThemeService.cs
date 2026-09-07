using System.Runtime.InteropServices;
using System.Windows.Media;

namespace LumaLauncher.Services;

public static class ThemeService
{
    public const string Auto = "Auto";
    public const string InkTeal = "InkTeal";
    public const string Dusk = "Dusk";
    public const string Paper = "Paper";
    public const string Sky = "Sky";

    private static string _requestedTheme = Auto;
    private static string _dayTheme = Paper;
    private static string _nightTheme = InkTeal;

    public static void StartFollowingSystem() => Microsoft.Win32.SystemEvents.UserPreferenceChanged += PreferenceChanged;
    public static void StopFollowingSystem() => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= PreferenceChanged;

    private static void PreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        var app = System.Windows.Application.Current;
        if (app is not null && !app.Dispatcher.HasShutdownStarted)
            app.Dispatcher.BeginInvoke(() => Apply(_requestedTheme));
    }

    private sealed record Palette(
        string Window, string Panel, string Hover, string Selected,
        string Text, string Muted, string Faint, string Stroke,
        string Accent, string AccentSoft);

    private static void ApplyPalette(Palette palette, bool isDark)
    {
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
        Set("SurfaceSubtleBrush", palette.Hover);
        Set("SurfaceElevatedBrush", palette.Selected);
        Set("DangerBrush", isDark ? "#FFF07178" : "#FFB42318");
        Set("SuccessBrush", isDark ? "#FF7FD99A" : "#FF0F7B3D");
    }

    // ── 夜间 01 · 墨青 InkTeal ──────────────────────────────────────────
    private static readonly Palette InkTealPalette = new(
        Window: "#F00D1218",
        Panel: "#FF141B23",
        Hover: "#FF1C2632",
        Selected: "#FF243444",
        Text: "#FFE6EEF5",
        Muted: "#FF8A9AAB",
        Faint: "#FF5A6B7C",
        Stroke: "#FF2A3644",
        Accent: "#FF3FD9B8",
        AccentSoft: "#333FD9B8");

    // ── 夜间 02 · 赭暮 Dusk ─────────────────────────────────────────────
    private static readonly Palette DuskPalette = new(
        Window: "#F0121012",
        Panel: "#FF1A1618",
        Hover: "#FF252023",
        Selected: "#FF32282C",
        Text: "#FFF1EBE8",
        Muted: "#FFA89895",
        Faint: "#FF6E605E",
        Stroke: "#FF3A3034",
        Accent: "#FFE07A6B",
        AccentSoft: "#33E07A6B");

    // ── 日间 01 · 素笺 Paper ────────────────────────────────────────────
    private static readonly Palette PaperPalette = new(
        Window: "#F6F4EFE8",
        Panel: "#FFFBF9F5",
        Hover: "#FFEFE9DF",
        Selected: "#FFE4DCD0",
        Text: "#FF1C1B19",
        Muted: "#FF6B6560",
        Faint: "#FF8A8480",
        Stroke: "#FFD8D2C8",
        Accent: "#FFC45C26",
        AccentSoft: "#22C45C26");

    // ── 日间 02 · 晴空 Sky ──────────────────────────────────────────────
    private static readonly Palette SkyPalette = new(
        Window: "#F4EFF4F9",
        Panel: "#FFFAFCFE",
        Hover: "#FFE4EBF2",
        Selected: "#FFD8E4F0",
        Text: "#FF0F172A",
        Muted: "#FF5B6B7C",
        Faint: "#FF7C8B9A",
        Stroke: "#FFD0DAE4",
        Accent: "#FF0B7BC4",
        AccentSoft: "#220B7BC4");

    private static readonly IReadOnlyDictionary<string, Palette> Palettes =
        new Dictionary<string, Palette>(StringComparer.OrdinalIgnoreCase)
        {
            [InkTeal] = InkTealPalette,
            [Dusk] = DuskPalette,
            [Paper] = PaperPalette,
            [Sky] = SkyPalette
        };

    private static readonly HashSet<string> DarkIds = new(StringComparer.OrdinalIgnoreCase) { InkTeal, Dusk };

    public static readonly (string Id, string Label, bool IsDark)[] Catalog =
    [
        (InkTeal, "墨青", true),
        (Dusk, "赭暮", true),
        (Paper, "素笺", false),
        (Sky, "晴空", false)
    ];

    public static bool IsDarkTheme(string themeId)
    {
        var id = NormalizeId(themeId);
        return DarkIds.Contains(id);
    }

    public static void ConfigureAutoPair(string dayTheme, string nightTheme)
    {
        if (Palettes.ContainsKey(dayTheme)) _dayTheme = dayTheme;
        if (Palettes.ContainsKey(nightTheme)) _nightTheme = nightTheme;
    }

    public static string ResolveEffectiveTheme(string requestedTheme)
    {
        if (IsAuto(requestedTheme))
            return SystemPrefersLight() ? _dayTheme : _nightTheme;
        return NormalizeId(requestedTheme);
    }

    /// <summary>Maps stored theme ids (including legacy) to the Settings combo tags.</summary>
    public static string ResolveRequestedForUi(string theme) => NormalizeId(theme);

    public static void Apply(string requestedTheme)
    {
        _requestedTheme = NormalizeId(requestedTheme);

        if (System.Windows.SystemParameters.HighContrast)
        {
            foreach (var key in new[] { "WindowBrush", "PanelBrush", "PanelHoverBrush" })
                System.Windows.Application.Current.Resources[key] = System.Windows.SystemColors.WindowBrush;
            foreach (var key in new[] { "TextBrush", "MutedTextBrush", "FaintTextBrush", "StrokeBrush", "AccentBrush" })
                System.Windows.Application.Current.Resources[key] = System.Windows.SystemColors.WindowTextBrush;
            foreach (var key in new[] { "PanelSelectedBrush", "SurfaceSubtleBrush", "SurfaceElevatedBrush" })
                System.Windows.Application.Current.Resources[key] = System.Windows.SystemColors.ControlBrush;
            System.Windows.Application.Current.Resources["AccentSoftBrush"] = System.Windows.SystemColors.ControlBrush;
            System.Windows.Application.Current.Resources["DangerBrush"] = System.Windows.SystemColors.WindowTextBrush;
            System.Windows.Application.Current.Resources["SuccessBrush"] = System.Windows.SystemColors.WindowTextBrush;
            return;
        }

        var effective = ResolveEffectiveTheme(_requestedTheme);
        var palette = Palettes.GetValueOrDefault(effective, InkTealPalette);
        ApplyPalette(palette, DarkIds.Contains(effective));
    }

    private static bool IsAuto(string theme) =>
        theme.Equals("Auto", StringComparison.OrdinalIgnoreCase) ||
        theme.Equals("System", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeId(string theme)
    {
        if (IsAuto(theme))
            return Auto;
        return theme switch
        {
            "Dark" or "Win11Graphite" => InkTeal,
            "Light" or "Win11Mist" => Paper,
            "Win11Blue" => Sky,
            "Win11Sage" => Paper,
            _ when Palettes.ContainsKey(theme) => theme,
            _ => Auto
        };
    }

    private static bool SystemPrefersLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
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
