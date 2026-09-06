using System.Text.Json;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore()
    {
        var directory = AppDataPaths.DirectoryPath;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        Current = Load();
    }

    public AppSettings Current { get; private set; }
    public string? CompatibilityWarning { get; private set; }

    public void Save(AppSettings settings)
    {
        if (CompatibilityWarning is not null) throw new InvalidOperationException(CompatibilityWarning);
        settings = settings.Copy().Normalize();
        AtomicFileService.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
        Current = settings.Copy();
    }

    private AppSettings Load()
    {
        try
        {
            var settings = File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(AtomicFileService.ReadAllText(_path)) ?? new AppSettings()
                : new AppSettings();
            if (settings.SchemaVersion > 1)
            {
                CompatibilityWarning = "配置来自更新版本的 Luma。当前使用临时默认值，禁止保存以保护原配置；请升级程序。";
                return new AppSettings();
            }
            return settings.Normalize();
        }
        catch (Exception exception)
        {
            AtomicFileService.PreserveCorruptFile(_path);
            DiagnosticsService.Log("settings-load", exception);
            return new AppSettings();
        }
    }
}
