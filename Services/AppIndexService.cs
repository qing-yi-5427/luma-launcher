using Microsoft.Win32;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

public sealed class AppIndexService
{
    private sealed record AppEntry(string Title, string Target, string Subtitle);
    private IReadOnlyList<AppEntry> _entries = [];

    public bool IsReady { get; private set; }
    public int Count => _entries.Count;

    public async Task ReloadAsync(CancellationToken token = default)
    {
        var entries = await Task.Run(() => BuildIndex(token), token).ConfigureAwait(false);
        _entries = entries;
        IsReady = true;
    }

    public IReadOnlyList<LauncherResult> Search(string query, int maximumResults, UsageStore usage)
    {
        return _entries
            .Select(entry => (Entry: entry, Match: FuzzyMatcher.Score(query, entry.Title, entry.Subtitle)))
            .Where(item => !double.IsNegativeInfinity(item.Match))
            .Select(item => new LauncherResult
            {
                Title = item.Entry.Title,
                Subtitle = item.Entry.Subtitle,
                Target = item.Entry.Target,
                Kind = LauncherResultKind.Application,
                Score = item.Match + 180 + usage.GetBoost(item.Entry.Target)
            })
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Title.Length)
            .Take(maximumResults)
            .ToList();
    }

    private static IReadOnlyList<AppEntry> BuildIndex(CancellationToken token)
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
        return entries.Values.OrderBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void AddFolder(Dictionary<string, AppEntry> entries, string folder, string source, bool recursive, CancellationToken token, bool executableAliasesOnly = false)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        try
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in Directory.EnumerateFiles(folder, "*", option))
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
                entries.TryAdd(file, new AppEntry(title, file, source));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static void AddRegistryApps(Dictionary<string, AppEntry> entries, RegistryHive hive, RegistryView view, CancellationToken token)
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
                entries.TryAdd(path, new AppEntry(title, path, "已安装应用"));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }
}
