namespace LumaLauncher.Models;

public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Alt+Space";
    public string Theme { get; set; } = "System";
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

    public AppSettings Copy() => new()
    {
        Hotkey = Hotkey,
        Theme = Theme,
        StartWithWindows = StartWithWindows,
        EverythingPathMode = EverythingPathMode,
        EverythingPath = EverythingPath,
        EverythingLifecycle = EverythingLifecycle,
        EnableQuickSwitch = EnableQuickSwitch,
        Aliases = Aliases,
        AppFolders = AppFolders,
        CustomCommands = CustomCommands,
        WebSearchUrl = WebSearchUrl,
        ResultSort = ResultSort
    };
}
