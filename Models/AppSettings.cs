namespace LumaLauncher.Models;

public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Alt+Space";
    public string Theme { get; set; } = "System";
    public bool StartWithWindows { get; set; }
    public string EverythingPathMode { get; set; } = "Auto";
    public string EverythingPath { get; set; } = string.Empty;

    public AppSettings Copy() => new()
    {
        Hotkey = Hotkey,
        Theme = Theme,
        StartWithWindows = StartWithWindows,
        EverythingPathMode = EverythingPathMode,
        EverythingPath = EverythingPath
    };
}
