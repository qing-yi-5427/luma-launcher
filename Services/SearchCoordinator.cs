using LumaLauncher.Models;

namespace LumaLauncher.Services;

public sealed class SearchCoordinator : IDisposable
{
    private readonly AppIndexService _apps = new();
    private readonly EverythingSearchService _everything = new();
    private readonly IconService _icons = new();
    private readonly UsageStore _usage = new();
    private Task? _initializeTask;

    public Task InitializeAsync(CancellationToken token = default) =>
        _initializeTask ??= _apps.InitializeAsync(token);

    public void ConfigureEverything(AppSettings settings) =>
        _everything.Configure(settings.EverythingPathMode, settings.EverythingPath);

    public Task<bool> EnsureEverythingRunningAsync(CancellationToken token = default) =>
        _everything.EnsureRunningAsync(token);

    public async Task ReloadAppsAsync(CancellationToken token = default) =>
        await _apps.ReloadAsync(token).ConfigureAwait(false);

    public async Task<SearchBatch> SearchAsync(string query, int maximumResults, CancellationToken token)
    {
        if (_initializeTask is not null && !_apps.IsReady)
        {
            try { await _initializeTask.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        var preparedQuery = FuzzyMatcher.Prepare(query);
        var appTask = Task.Run(() => _apps.Search(preparedQuery, maximumResults, _usage), token);
        var fileTask = _everything.SearchAsync(query, Math.Max(maximumResults * 5, 40), token);
        await Task.WhenAll(appTask, fileTask).ConfigureAwait(false);

        var all = new List<LauncherResult>(appTask.Result.Count + fileTask.Result.Results.Count);
        all.AddRange(appTask.Result);
        var everythingSyntax = LooksLikeEverythingSyntax(query);
        for (var index = 0; index < fileTask.Result.Results.Count; index++)
        {
            var file = fileTask.Result.Results[index];
            var match = everythingSyntax
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
                Score = match + _usage.GetBoost(file.Target)
            });
        }

        var ranked = all
            .GroupBy(result => result.Kind == LauncherResultKind.Application
                ? $"app::{result.Title}"
                : result.Target, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(result => result.Score).First())
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Title.Length)
            .ThenBy(result => result.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(maximumResults)
            .ToList();

        var source = fileTask.Result.Available ? "Everything + 应用" : $"仅应用 · {fileTask.Result.StatusText}";
        return new SearchBatch(ranked, $"{ranked.Count} 个结果 · {source}", fileTask.Result.Available);
    }

    public Task<SearchBatch> GetRecommendationsAsync(int maximumResults, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var recent = _usage.GetRecent(maximumResults).ToList();
        var status = recent.Count == 0 ? "输入应用、文件名或 Everything 语法" : "最近使用";
        return Task.FromResult(new SearchBatch(recent, status, true));
    }

    public void RecordLaunch(LauncherResult result) => _usage.Record(result);

    public void ShutdownEverything() => _everything.ShutdownClient();

    public async Task<IReadOnlyList<System.Windows.Media.ImageSource?>> LoadIconsAsync(
        IReadOnlyList<LauncherResult> results, CancellationToken token)
    {
        var tasks = results.Select(result => _icons.GetAsync(result.Target, token));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public void TrimCaches() => _icons.Trim();

    private static bool LooksLikeEverythingSyntax(string query) =>
        query.IndexOfAny([':', '*', '?', '|', '!', '<', '>', '"']) >= 0;

    public void Dispose()
    {
        _icons.Trim(0);
        _everything.Dispose();
    }
}
