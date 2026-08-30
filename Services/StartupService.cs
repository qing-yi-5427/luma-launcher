using Microsoft.Win32;

namespace LumaLauncher.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LumaLauncher";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --silent");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
