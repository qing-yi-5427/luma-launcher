using LumaLauncher.Models;

namespace LumaLauncher.Services;

public sealed class SearchCoordinator : IDisposable
{
    private readonly AppIndexService _apps = new();
    private readonly EverythingSearchService _everything = new();
    private readonly IconService _icons = new();
    private readonly UsageStore _usage = new();
    private readonly BuiltInSearchService _builtIns = new();
    private IReadOnlyDictionary<string, string> _aliases = new Dictionary<string, string>();
    private Task? _initializeTask;
    private string _resultSort = ResultRanker.Smart;
    private bool _recordHistory = true;

    private readonly Func<string, int, CancellationToken, string, string, Task<EverythingSearchResponse>> _queryFiles;

    public SearchCoordinator() => _queryFiles = (query, limit, token, filter, sort) => _everything.SearchAsync(query, limit, token, filter, sort);

    internal SearchCoordinator(Func<string, int, CancellationToken, string, Task<EverythingSearchResponse>> queryFiles)
        : this((query, limit, token, filter, _) => queryFiles(query, limit, token, filter)) { }
    internal SearchCoordinator(Func<string, int, CancellationToken, string, string, Task<EverythingSearchResponse>> queryFiles) => _queryFiles = queryFiles;

    public bool IsInitialized => _apps.IsReady;

    public Task InitializeAsync(CancellationToken token = default) =>
        _initializeTask ??= _apps.InitializeAsync(token);

    public bool Configure(AppSettings settings)
    {
        _everything.Configure(settings.EverythingPathMode, settings.EverythingPath, settings.EverythingLifecycle);
        _builtIns.Configure(settings);
        _aliases = ParseAliases(settings.Aliases);
        _resultSort = ResultRanker.Normalize(settings.ResultSort);
        _recordHistory = settings.RecordHistory;
        return _apps.ConfigureCustomFolders(settings.AppFolders);
    }

    public Task<bool> EnsureEverythingRunningAsync(CancellationToken token = default) =>
        _everything.EnsureRunningAsync(token);

    public async Task ReloadAppsAsync(CancellationToken token = default) =>
        await _apps.ReloadAsync(token).ConfigureAwait(false);

    public async Task<SearchBatch> SearchAsync(string query, int maximumResults, CancellationToken token,
        string filter = "All", Action<SearchBatch>? publishApplications = null)
    {
        token.ThrowIfCancellationRequested();
        var sortMode = _resultSort; // One immutable choice for this entire asynchronous request.
        var builtInResults = filter == "All" ? _builtIns.Search(query) : [];
        if (builtInResults.Count > 0 &&
            (builtInResults.Any(result => result.Kind != LauncherResultKind.Web) ||
             query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             query.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
             query.StartsWith("? ") || query.StartsWith("web ") || query.StartsWith("g ")))
            return new SearchBatch(builtInResults.Take(maximumResults).ToList(), "Luma 内建工具", true);

        if (_initializeTask is not null && !_apps.IsReady)
        {
            try { await _initializeTask.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        var preparedQuery = FuzzyMatcher.Prepare(query);
        var knownTask = Task.Run<IReadOnlyList<LauncherResult>>(() =>
            !ResultRanker.IsProviderSort(sortMode) && !LooksLikeEverythingSyntax(query)
                ? _usage.Search(query, filter, token) : [], token);
        var appTask = Task.Run<IReadOnlyList<LauncherResult>>(() => filter is "File" or "Folder"
            ? [] : _apps.Search(preparedQuery, Math.Max(maximumResults, _apps.Count), _usage, _aliases), token);
        var fileCandidateLimit = maximumResults <= 64
            ? Math.Max(maximumResults * 5, 40)
            : Math.Max(maximumResults * 2, 512);
        if (ResultRanker.IsProviderSort(sortMode)) fileCandidateLimit = maximumResults;
        var fileTask = filter == "Application"
            ? Task.FromResult(new EverythingSearchResponse([], true, "仅应用"))
            : _queryFiles(query, fileCandidateLimit, token, filter, sortMode);
        await appTask.ConfigureAwait(false);
        if (!fileTask.IsCompleted && appTask.Result.Count > 0)
            publishApplications?.Invoke(new SearchBatch(ResultRanker.Rank(appTask.Result, sortMode, maximumResults, _usage.GetBoost),
                "应用已就绪 · 文件搜索中…", false));
        await fileTask.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var all = new List<LauncherResult>(appTask.Result.Count + fileTask.Result.Results.Count);
        all.AddRange(appTask.Result);
        all.AddRange(await knownTask.ConfigureAwait(false));
        token.ThrowIfCancellationRequested();
        if (filter == "All") all.AddRange(builtInResults);
        var everythingSyntax = LooksLikeEverythingSyntax(query);
        for (var index = 0; index < fileTask.Result.Results.Count; index++)
        {
            var file = fileTask.Result.Results[index];
            var match = everythingSyntax || ResultRanker.IsProviderSort(sortMode)
                ? 120 - index
                : FuzzyMatcher.Score(preparedQuery,
                    FuzzyMatcher.PrepareCandidate(file.Title),
                    FuzzyMatcher.PrepareCandidate(file.Subtitle));
            if (double.IsNegativeInfinity(match))
                continue;
            all.Add(new LauncherResult
            {
                Title = file.Title,
                Subtitle = file.Subtitle,
                Target = file.Target,
                Kind = file.Kind,
                ProviderOrder = ResultRanker.IsProviderSort(sortMode) ? index : null,
                IndexedSize = file.IndexedSize,
                IndexedModifiedFileTime = file.IndexedModifiedFileTime,
                Score = match + _usage.GetBoost(file.Target),
                IsFavorite = _usage.IsFavorite(file.Target)
            });
        }

        var unique = all
            .GroupBy(result => result.Kind == LauncherResultKind.Application
                ? $"app::{result.Title}"
                : result.Target, StringComparer.OrdinalIgnoreCase)
            .Select(group => ResultRanker.Rank(group, sortMode, 1, _usage.GetBoost)[0])
            .ToList();
        var ranked = ResultRanker.Rank(unique, sortMode, maximumResults, _usage.GetBoost);

        var source = filter == "Application" ? "应用" : fileTask.Result.Available ? "Everything + 应用" : $"仅应用 · {fileTask.Result.StatusText}";
        var hasMore = unique.Count > maximumResults || fileTask.Result.HasMore;
        return new SearchBatch(ranked, source, fileTask.Result.Available, hasMore,
            fileTask.Result.Available && filter != "Application" ? fileTask.Result.TotalMatches : null);
    }

    public Task<SearchBatch> GetRecommendationsAsync(int maximumResults, CancellationToken token)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var recent = _usage.GetRecent(maximumResults).ToList();
            token.ThrowIfCancellationRequested();
            var status = recent.Count == 0 ? "输入应用、文件名或 Everything 语法" : "最近使用";
            return new SearchBatch(recent, status, true);
        }, token);
    }

    public void RecordLaunch(LauncherResult result) { if (_recordHistory) _usage.Record(result); }
    public void ClearHistory() => _usage.ClearHistory();

    public bool ToggleFavorite(LauncherResult result) => _usage.ToggleFavorite(result);

    public bool IsFavorite(LauncherResult result) => _usage.IsFavorite(result.Target);

    public void RemoveFromHistory(LauncherResult result) => _usage.Remove(result.Target);

    public void ShutdownEverything() => _everything.ShutdownClient();

    public Task<System.Windows.Media.ImageSource?> LoadIconAsync(LauncherResult result, CancellationToken token) =>
        _icons.GetAsync(result.Target, token);

    public void TrimCaches() => _icons.Trim();

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
        _everything.Dispose();
    }
}
