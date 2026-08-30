using System.Text.Json;
using Microsoft.Win32;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

public sealed class AppIndexService
{
    private const int CacheVersion = 1;
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = false };

    private sealed class AppEntry
    {
        public string Title { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string NormalizedTitle { get; set; } = string.Empty;
        public string NormalizedSubtitle { get; set; } = string.Empty;
    }

    private sealed class AppIndexCache
    {
        public int Version { get; set; }
        public AppEntry[] Entries { get; set; } = [];
    }

    private readonly string _cachePath;
    private AppEntry[] _entries;
    private int _ready;

    public AppIndexService()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaLauncher");
        Directory.CreateDirectory(directory);
        _cachePath = Path.Combine(directory, "apps.json");
        _entries = [];
    }

    public bool IsReady => Volatile.Read(ref _ready) != 0;
    public int Count => Volatile.Read(ref _entries).Length;

    public async Task InitializeAsync(CancellationToken token = default)
    {
        var cached = await Task.Run(LoadCache, token).ConfigureAwait(false);
        if (cached.Length > 0)
        {
            Volatile.Write(ref _entries, cached);
            Volatile.Write(ref _ready, 1);
        }
        await ReloadAsync(token).ConfigureAwait(false);
    }

    public async Task ReloadAsync(CancellationToken token = default)
    {
        var entries = await Task.Run(() => BuildIndex(token), token).ConfigureAwait(false);
        Volatile.Write(ref _entries, entries);
        Volatile.Write(ref _ready, 1);
        await Task.Run(() => SaveCache(entries), CancellationToken.None).ConfigureAwait(false);
    }

    public IReadOnlyList<LauncherResult> Search(FuzzyMatcher.PreparedQuery query, int maximumResults, UsageStore usage)
    {
        var matches = new List<LauncherResult>(Math.Min(32, Count));
        foreach (var entry in Volatile.Read(ref _entries))
        {
            var match = FuzzyMatcher.Score(query, entry.NormalizedTitle, entry.NormalizedSubtitle);
            if (double.IsNegativeInfinity(match))
                continue;
            matches.Add(new LauncherResult
            {
                Title = entry.Title,
                Subtitle = entry.Subtitle,
                Target = entry.Target,
                Kind = LauncherResultKind.Application,
                Score = match + 180 + usage.GetBoost(entry.Target)
            });
        }

        matches.Sort(static (left, right) =>
        {
            var score = right.Score.CompareTo(left.Score);
            return score != 0 ? score : left.Title.Length.CompareTo(right.Title.Length);
        });
        if (matches.Count > maximumResults)
            matches.RemoveRange(maximumResults, matches.Count - maximumResults);
        return matches;
    }

    private static AppEntry[] BuildIndex(CancellationToken token)
    {
        var entries = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        AddFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "开始菜单", recursive: true, token);
        AddFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "开始菜单", recursive: true, token);
        AddFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "桌面", recursive: false, token);
        AddFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "公共桌面", recursive: false, token);

        var aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        AddFolder(entries, aliases, "应用别名", recursive: false, token, executableAliasesOnly: true);

        AddRegistryApps(entries, RegistryHive.CurrentUser, RegistryView.Default, token);
        AddRegistryApps(entries, RegistryHive.LocalMachine, RegistryView.Registry64, token);
        AddRegistryApps(entries, RegistryHive.LocalMachine, RegistryView.Registry32, token);
        return entries.Values.OrderBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static void AddFolder(Dictionary<string, AppEntry> entries, string folder, string source, bool recursive,
        CancellationToken token, bool executableAliasesOnly = false)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.System
            };
            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                token.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(file);
                var supported = executableAliasesOnly
                    ? extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    : extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                      extension.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
                      extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase) ||
                      extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
                if (!supported)
                    continue;

                var title = Path.GetFileNameWithoutExtension(file);
                if (title.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || title.Contains("卸载", StringComparison.OrdinalIgnoreCase))
                    continue;
                entries.TryAdd(file, CreateEntry(title, file, source));
            }
        }
        catch (IOException exception) { DiagnosticsService.Log("app-index", exception); }
    }

    private static void AddRegistryApps(Dictionary<string, AppEntry> entries, RegistryHive hive, RegistryView view,
        CancellationToken token)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (appPaths is null)
                return;

            foreach (var name in appPaths.GetSubKeyNames())
            {
                token.ThrowIfCancellationRequested();
                using var appKey = appPaths.OpenSubKey(name);
                if (appKey?.GetValue(null) is not string rawPath)
                    continue;
                var path = Environment.ExpandEnvironmentVariables(rawPath.Trim('"'));
                if (!File.Exists(path))
                    continue;
                var title = Path.GetFileNameWithoutExtension(name);
                entries.TryAdd(path, CreateEntry(title, path, "已安装应用"));
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            DiagnosticsService.Log("app-index", exception);
        }
    }

    private static AppEntry CreateEntry(string title, string target, string subtitle) => new()
    {
        Title = title,
        Target = target,
        Subtitle = subtitle,
        NormalizedTitle = FuzzyMatcher.PrepareCandidate(title),
        NormalizedSubtitle = FuzzyMatcher.PrepareCandidate(subtitle)
    };

    private AppEntry[] LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return [];
            var cache = JsonSerializer.Deserialize<AppIndexCache>(AtomicFileService.ReadAllText(_cachePath), CacheJsonOptions);
            if (cache?.Version != CacheVersion)
                return [];
            return cache.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Title) && !string.IsNullOrWhiteSpace(entry.Target))
                .ToArray();
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("app-cache-load", exception);
            return [];
        }
    }

    private void SaveCache(AppEntry[] entries)
    {
        try
        {
            var json = JsonSerializer.Serialize(new AppIndexCache { Version = CacheVersion, Entries = entries }, CacheJsonOptions);
            AtomicFileService.WriteAllText(_cachePath, json);
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("app-cache-save", exception);
        }
    }
}
