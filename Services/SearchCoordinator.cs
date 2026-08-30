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
        _initializeTask ??= _apps.ReloadAsync(token);

    public void ConfigureEverything(AppSettings settings) =>
        _everything.Configure(settings.EverythingPathMode, settings.EverythingPath);

    public Task<bool> EnsureEverythingRunningAsync(CancellationToken token = default) =>
        _everything.EnsureRunningAsync(token);

    public async Task ReloadAppsAsync(CancellationToken token = default) =>
        await _apps.ReloadAsync(token).ConfigureAwait(false);

    public async Task<SearchBatch> SearchAsync(string query, int maximumResults, CancellationToken token)
    {
        if (_initializeTask is not null)
        {
            try { await _initializeTask.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        var appTask = Task.Run(() => _apps.Search(query, maximumResults, _usage), token);
        var fileTask = _everything.SearchAsync(query, Math.Max(maximumResults * 5, 40), token);
        await Task.WhenAll(appTask, fileTask).ConfigureAwait(false);

        var all = new List<LauncherResult>(appTask.Result.Count + fileTask.Result.Results.Count);
        all.AddRange(appTask.Result);
        foreach (var file in fileTask.Result.Results)
        {
            var match = FuzzyMatcher.Score(query, file.Title, file.Subtitle);
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

        await PopulateIconsAsync(ranked, token).ConfigureAwait(false);
        var source = fileTask.Result.Available ? "Everything + 应用" : $"仅应用 · {fileTask.Result.StatusText}";
        return new SearchBatch(ranked, $"{ranked.Count} 个结果 · {source}", fileTask.Result.Available);
    }

    public async Task<SearchBatch> GetRecommendationsAsync(int maximumResults, CancellationToken token)
    {
        var recent = _usage.GetRecent(maximumResults).ToList();
        await PopulateIconsAsync(recent, token).ConfigureAwait(false);
        var status = recent.Count == 0 ? "输入应用、文件名或 Everything 语法" : "最近使用";
        return new SearchBatch(recent, status, true);
    }

    public void RecordLaunch(LauncherResult result) => _usage.Record(result);

    public void ShutdownEverything() => _everything.ShutdownClient();

    private async Task PopulateIconsAsync(IReadOnlyList<LauncherResult> results, CancellationToken token)
    {
        var tasks = results.Select(async result => result.Icon = await _icons.GetAsync(result.Target, token).ConfigureAwait(false));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public void Dispose() => _everything.Dispose();
}
