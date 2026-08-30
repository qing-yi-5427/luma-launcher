using System.Text.Json;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaLauncher");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public void Save(AppSettings settings)
    {
        AtomicFileService.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
        Current = settings.Copy();
    }

    private AppSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(AtomicFileService.ReadAllText(_path)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception exception)
        {
            AtomicFileService.PreserveCorruptFile(_path);
            DiagnosticsService.Log("settings-load", exception);
            return new AppSettings();
        }
    }
}
