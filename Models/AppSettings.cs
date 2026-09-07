namespace LumaLauncher.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public bool RecordHistory { get; set; } = true;
    public string Hotkey { get; set; } = "Alt+Space";
    public string Theme { get; set; } = "Auto";
    public string DayTheme { get; set; } = "Paper";
    public string NightTheme { get; set; } = "InkTeal";
    public bool StartWithWindows { get; set; }
    public string EverythingPathMode { get; set; } = "Auto";
    public string EverythingPath { get; set; } = string.Empty;
    public string EverythingLifecycle { get; set; } = "Managed";
    public bool EnableQuickSwitch { get; set; } = true;
    public string Aliases { get; set; } = string.Empty;
    public string AppFolders { get; set; } = string.Empty;
    public string CustomCommands { get; set; } = string.Empty;
    public string WebSearchUrl { get; set; } = "https://www.bing.com/search?q={query}";
    public string ResultSort { get; set; } = "Smart";

    // Schema v2
    public string SearchEngines { get; set; } = string.Empty;
    public bool EnableWindowSwitcher { get; set; } = true;
    public bool EnableSystemCommands { get; set; } = true;
    public bool EnableBookmarks { get; set; }
    public bool EnableGameMode { get; set; }
    public bool EnablePreview { get; set; }
    public bool PreferWindowsIndex { get; set; }
    public string Language { get; set; } = "zh-CN";
    public bool RecordQueryHistory { get; set; } = true;
    public string Density { get; set; } = "Comfortable";
    public bool EnableClipboardHistory { get; set; }
    public bool ShowOnboarding { get; set; } = true;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool RememberWindowPosition { get; set; }

    public AppSettings Copy() => new()
    {
        SchemaVersion = SchemaVersion,
        RecordHistory = RecordHistory,
        Hotkey = Hotkey,
        Theme = Theme,
        DayTheme = DayTheme,
        NightTheme = NightTheme,
        StartWithWindows = StartWithWindows,
        EverythingPathMode = EverythingPathMode,
        EverythingPath = EverythingPath,
        EverythingLifecycle = EverythingLifecycle,
        EnableQuickSwitch = EnableQuickSwitch,
        Aliases = Aliases,
        AppFolders = AppFolders,
        CustomCommands = CustomCommands,
        WebSearchUrl = WebSearchUrl,
        ResultSort = ResultSort,
        SearchEngines = SearchEngines,
        EnableWindowSwitcher = EnableWindowSwitcher,
        EnableSystemCommands = EnableSystemCommands,
        EnableBookmarks = EnableBookmarks,
        EnableGameMode = EnableGameMode,
        EnablePreview = EnablePreview,
        PreferWindowsIndex = PreferWindowsIndex,
        Language = Language,
        RecordQueryHistory = RecordQueryHistory,
        Density = Density,
        EnableClipboardHistory = EnableClipboardHistory,
        ShowOnboarding = ShowOnboarding,
        WindowLeft = WindowLeft,
        WindowTop = WindowTop,
        RememberWindowPosition = RememberWindowPosition
    };

    public AppSettings Normalize()
    {
        if (SchemaVersion > 2) throw new InvalidDataException("此配置来自更新版本的 Luma，请先升级程序。");
        if (SchemaVersion < 2)
        {
            // v1 files load with defaults for the new fields.
            SchemaVersion = 2;
        }

        if (string.IsNullOrWhiteSpace(Hotkey) || !Services.HotkeyGesture.TryParse(Hotkey, out _))
            Hotkey = "Alt+Space";

        // Theme ids: Auto / System + the four curated palettes (legacy ids remapped by ThemeService).
        var knownThemes = new[] { "Auto", "System", "InkTeal", "Dusk", "Paper", "Sky",
            "Dark", "Light", "Win11Blue", "Win11Graphite", "Win11Mist", "Win11Sage" };
        if (!knownThemes.Contains(Theme)) Theme = "Auto";
        if (DayTheme is not ("Paper" or "Sky")) DayTheme = "Paper";
        if (NightTheme is not ("InkTeal" or "Dusk")) NightTheme = "InkTeal";
        Density = Density == "Compact" ? "Compact" : "Comfortable";

        EverythingPathMode = EverythingPathMode == "Manual" ? "Manual" : "Auto";
        EverythingLifecycle = EverythingLifecycle == "Connect" ? "Connect" : "Managed";
        EverythingPath ??= string.Empty;
        Aliases ??= string.Empty;
        AppFolders ??= string.Empty;
        CustomCommands ??= string.Empty;
        SearchEngines ??= string.Empty;
        Language = Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
        ResultSort = Services.ResultRanker.Normalize(ResultSort);
        if (string.IsNullOrWhiteSpace(WebSearchUrl) || !WebSearchUrl.Contains("{query}") ||
            !Uri.TryCreate(WebSearchUrl.Replace("{query}", "test"), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) WebSearchUrl = "https://www.bing.com/search?q={query}";
        return this;
    }
}
