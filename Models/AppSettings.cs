namespace LumaLauncher.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool RecordHistory { get; set; } = true;
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
        SchemaVersion = SchemaVersion,
        RecordHistory = RecordHistory,
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

    public AppSettings Normalize()
    {
        if (SchemaVersion > 1) throw new InvalidDataException("此配置来自更新版本的 Luma，请先升级程序。");
        SchemaVersion = 1;
        if (!new[] { "Alt+Space", "Ctrl+Space", "Ctrl+Alt+Space", "Ctrl+Shift+Space" }.Contains(Hotkey)) Hotkey = "Alt+Space";
        if (!new[] { "System", "Light", "Dark", "Win11Blue", "Win11Graphite", "Win11Mist", "Win11Sage" }.Contains(Theme)) Theme = "System";
        EverythingPathMode = EverythingPathMode == "Manual" ? "Manual" : "Auto";
        EverythingLifecycle = EverythingLifecycle == "Connect" ? "Connect" : "Managed";
        EverythingPath ??= string.Empty;
        Aliases ??= string.Empty;
        AppFolders ??= string.Empty;
        CustomCommands ??= string.Empty;
        ResultSort = Services.ResultRanker.Normalize(ResultSort);
        if (string.IsNullOrWhiteSpace(WebSearchUrl) || !WebSearchUrl.Contains("{query}") ||
            !Uri.TryCreate(WebSearchUrl.Replace("{query}", "test"), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) WebSearchUrl = "https://www.bing.com/search?q={query}";
        return this;
    }
}
