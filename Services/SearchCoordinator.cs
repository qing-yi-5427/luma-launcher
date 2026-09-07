using LumaLauncher.Models;
using LumaLauncher.Services.Providers;

namespace LumaLauncher.Services;

public sealed class SearchCoordinator : IDisposable
{
    private readonly ApplicationProvider _apps = new();
    private readonly EverythingFileProvider _files = new();
    private readonly BuiltInToolsProvider _builtIns = new();
    private readonly WindowSwitcherProvider _windows = new();
    private readonly SystemCommandsProvider _system = new();
    private readonly BrowserBookmarkProvider _bookmarks = new();
    private readonly ClipboardHistoryService _clipboard = new();
    private readonly IconService _icons = new();
    private readonly UsageStore _usage = new();
    private readonly PreviewService _preview = new();
    private readonly List<ILumaProvider> _providers;
    private IReadOnlyDictionary<string, string> _aliases = new Dictionary<string, string>();
    private string _resultSort = ResultRanker.Smart;
    private bool _recordHistory = true;

    public SearchCoordinator()
    {
        _providers = [_builtIns, _system, _apps, _windows, _files, _bookmarks];
    }

    internal SearchCoordinator(Func<string, int, CancellationToken, string, string, Task<EverythingSearchResponse>> queryFiles)
        : this()
    {
        // Test seam: wrap the injected file query behind the Everything provider surface.
        _files.TestQuery = queryFiles;
    }

    internal SearchCoordinator(Func<string, int, CancellationToken, string, Task<EverythingSearchResponse>> queryFiles)
        : this((query, limit, token, filter, _) => queryFiles(query, limit, token, filter))
    {
    }

    public bool IsInitialized => _apps.IsReady;
    public UsageStore Usage => _usage;
    public PreviewService Preview => _preview;

    public Task InitializeAsync(CancellationToken token = default) =>
        _apps.InitializeAsync(token);

    public bool Configure(AppSettings settings)
    {
        UiStrings.SetCulture(settings.Language);
        _files.Configure(settings.EverythingPathMode, settings.EverythingPath, settings.EverythingLifecycle);
        _files.PreferWindowsIndex = settings.PreferWindowsIndex;
        _builtIns.Configure(settings);
        _windows.Enabled = settings.EnableWindowSwitcher;
        _system.Enabled = settings.EnableSystemCommands;
        _bookmarks.Enabled = settings.EnableBookmarks;
        _clipboard.Enabled = settings.EnableClipboardHistory;
        _aliases = ParseAliases(settings.Aliases);
        _resultSort = ResultRanker.Normalize(settings.ResultSort);
        _recordHistory = settings.RecordHistory;
        return _apps.ConfigureCustomFolders(settings.AppFolders);
    }

    public Task<bool> EnsureEverythingRunningAsync(CancellationToken token = default) =>
        _files.EnsureRunningAsync(token);

    public async Task ReloadAppsAsync(CancellationToken token = default) =>
        await _apps.ReloadAsync(token).ConfigureAwait(false);

    public async Task<SearchBatch> SearchAsync(string query, int maximumResults, CancellationToken token,
        string filter = "All", Action<SearchBatch>? publishApplications = null)
    {
        token.ThrowIfCancellationRequested();
        var sortMode = _resultSort;
        var trimmed = query.Trim();

        // Built-ins and system commands short-circuit.
        var builtInTask = _builtIns.QueryAsync(BuildContext(trimmed, maximumResults, filter, sortMode), token);
        var builtInResults = await builtInTask.ConfigureAwait(false);
        if (_builtIns.ShouldShortCircuit(trimmed, builtInResults))
            return new SearchBatch(builtInResults.Take(maximumResults).ToList(), "Luma 内建工具", true);

        if (_clipboard.Enabled &&
            (trimmed.Equals("clip", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("clip ", StringComparison.OrdinalIgnoreCase)))
        {
            var clips = _clipboard.Search(trimmed, Math.Min(maximumResults, 20));
            if (clips.Count > 0)
                return new SearchBatch(clips, "剪贴板历史", true);
            return new SearchBatch([], "剪贴板历史为空（需在设置中开启）", true);
        }

        var systemResults = await _system.QueryAsync(BuildContext(trimmed, maximumResults, filter, sortMode), token)
            .ConfigureAwait(false);
        if (systemResults.Count > 0 && systemResults.Any(r => r.Score > 600))
        {
            // Strong system-command match (e.g. exact "lock") still merges below;
            // keep going so files/apps can compete unless the match is exact-title.
            if (systemResults.Any(r => r.Title.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                var exact = systemResults.Where(r => r.Title.Equals(trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
                return new SearchBatch(exact, "系统命令", true);
            }
        }

        var preparedQuery = FuzzyMatcher.Prepare(trimmed);
        var context = BuildContext(trimmed, maximumResults, filter, sortMode);

        var knownTask = Task.Run<IReadOnlyList<LauncherResult>>(() =>
            !ResultRanker.IsProviderSort(sortMode) && !LooksLikeEverythingSyntax(trimmed)
                ? _usage.Search(trimmed, filter, token) : [], token);

        var appTask = _apps.QueryAsync(context, token);
        var windowTask = _windows.QueryAsync(context, token);
        var bookmarkTask = _bookmarks.QueryAsync(context, token);
        var fileTask = _files.QueryAsync(context, token);

        var apps = await appTask.ConfigureAwait(false);
        if (!fileTask.IsCompleted && apps.Count > 0)
            publishApplications?.Invoke(new SearchBatch(ResultRanker.Rank(apps, sortMode, maximumResults, _usage.GetBoost),
                UiStrings.Get("AppsReady"), false));

        var files = await fileTask.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var known = await knownTask.ConfigureAwait(false);
        var windows = await windowTask.ConfigureAwait(false);
        var bookmarks = await bookmarkTask.ConfigureAwait(false);
        if (filter == "All")
            builtInResults = await builtInTask.ConfigureAwait(false);

        var all = new List<LauncherResult>(
            apps.Count + files.Count + known.Count + windows.Count + bookmarks.Count + builtInResults.Count + systemResults.Count);
        all.AddRange(apps);
        all.AddRange(known);
        if (filter == "All")
        {
            all.AddRange(builtInResults);
            all.AddRange(systemResults);
            all.AddRange(windows);
            all.AddRange(bookmarks);
        }
        all.AddRange(files);

        // Cross-provider ranking: score already includes usage boost; Smart mode
        // no longer permanently pins Everything files above applications.
        var unique = all
            .GroupBy(result => GroupKey(result), StringComparer.OrdinalIgnoreCase)
            .Select(group => ResultRanker.Rank(group, sortMode, 1, _usage.GetBoost)[0])
            .ToList();
        var ranked = ResultRanker.Rank(unique, sortMode, maximumResults, _usage.GetBoost);

        var source = filter switch
        {
            "Application" => "应用",
            "File" or "Folder" when _files.PreferWindowsIndex => UiStrings.Get("WindowsIndex"),
            _ => files.Count > 0 && !_files.PreferWindowsIndex ? "综合搜索" : "应用与工具"
        };
        var hasMore = unique.Count > maximumResults;
        return new SearchBatch(ranked, source, true, hasMore, files.Count > 0 ? files.Count : null);
    }

    public Task<SearchBatch> GetRecommendationsAsync(int maximumResults, CancellationToken token)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var recent = _usage.GetRecent(maximumResults).ToList();
            token.ThrowIfCancellationRequested();
            var status = recent.Count == 0 ? UiStrings.Get("InputHint") : UiStrings.Get("RecentUsage");
            return new SearchBatch(recent, status, true);
        }, token);
    }

    public void RecordLaunch(LauncherResult result) { if (_recordHistory) _usage.Record(result); }
    public void ClearHistory() => _usage.ClearHistory();
    public bool ToggleFavorite(LauncherResult result) => _usage.ToggleFavorite(result);
    public bool IsFavorite(LauncherResult result) => _usage.IsFavorite(result.Target);
    public void RemoveFromHistory(LauncherResult result) => _usage.Remove(result.Target);
    public void ShutdownEverything() => _files.ShutdownClient();
    public void ReloadBookmarks() => _bookmarks.Reload();

    public Task<System.Windows.Media.ImageSource?> LoadIconAsync(LauncherResult result, CancellationToken token) =>
        _icons.GetAsync(result.Target, token);

    public void TrimCaches()
    {
        _icons.Trim();
        _preview.TrimCache();
    }

    private ProviderContext BuildContext(string query, int maximumResults, string filter, string sortMode) => new()
    {
        Query = query,
        Prepared = FuzzyMatcher.Prepare(query),
        MaximumResults = maximumResults,
        Filter = filter,
        SortMode = sortMode,
        Usage = _usage,
        Aliases = _aliases
    };

    private static string GroupKey(LauncherResult result) => result.Kind switch
    {
        LauncherResultKind.Application => $"app::{result.Title}",
        LauncherResultKind.Window => result.Target,
        LauncherResultKind.System => result.Target,
        _ => result.Target
    };

    private static bool LooksLikeEverythingSyntax(string query) =>
        query.IndexOfAny([':', '*', '?', '|', '!', '<', '>', '"']) >= 0;

    private static IReadOnlyDictionary<string, string> ParseAliases(string value)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
                continue;
            var alias = FuzzyMatcher.Prepare(line[..separator]).Normalized;
            var target = line[(separator + 1)..].Trim();
            if (alias.Length > 0 && target.Length > 0)
                aliases[alias] = target;
        }
        return aliases;
    }

    public void Dispose()
    {
        _icons.Trim(0);
        _files.ShutdownClient();
        _apps.Dispose();
        _clipboard.Dispose();
    }
}
