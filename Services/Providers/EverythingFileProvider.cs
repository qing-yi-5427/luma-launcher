using LumaLauncher.Models;

namespace LumaLauncher.Services.Providers;

public sealed class EverythingFileProvider : ILumaProvider
{
    private readonly EverythingSearchService _everything = new();
    private readonly WindowsIndexSearchService _windowsIndex = new();
    private bool _preferWindowsIndex;
    private Func<string, int, CancellationToken, string, string, Task<EverythingSearchResponse>>? _testQuery;

    public string Id => "files";
    public int Priority => 20;
    public bool CanHandle(string filter) => filter is not "Application";

    internal Func<string, int, CancellationToken, string, string, Task<EverythingSearchResponse>>? TestQuery
    {
        get => _testQuery;
        set => _testQuery = value;
    }

    public void Configure(string pathMode, string configuredPath, string lifecycle) =>
        _everything.Configure(pathMode, configuredPath, lifecycle);

    public Task<bool> EnsureRunningAsync(CancellationToken token = default) =>
        _everything.EnsureRunningAsync(token);

    public void ShutdownClient() => _everything.ShutdownClient();

    public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;

    public bool PreferWindowsIndex
    {
        get => _preferWindowsIndex;
        set => _preferWindowsIndex = value;
    }

    public async Task<IReadOnlyList<LauncherResult>> QueryAsync(ProviderContext context, CancellationToken token)
    {
        if (context.Filter == "Application")
            return [];

        var candidateLimit = context.MaximumResults <= 64
            ? Math.Max(context.MaximumResults * 5, 40)
            : Math.Max(context.MaximumResults * 2, 512);
        if (ResultRanker.IsProviderSort(context.SortMode))
            candidateLimit = context.MaximumResults;

        if (!_preferWindowsIndex)
        {
            var response = _testQuery is not null
                ? await _testQuery(context.Query, candidateLimit, token, context.Filter, context.SortMode).ConfigureAwait(false)
                : await _everything.SearchAsync(context.Query, candidateLimit, token, context.Filter, context.SortMode)
                    .ConfigureAwait(false);
            if (response.Available && response.Results.Count > 0)
                return Hydrate(response.Results, context);

            // Fallback when Everything is missing or returned nothing usable.
            if (!response.Available || response.Results.Count == 0)
            {
                var fallback = await _windowsIndex.SearchAsync(context.Query, candidateLimit, context.Filter, token)
                    .ConfigureAwait(false);
                if (fallback.Available && fallback.Results.Count > 0)
                    return Hydrate(fallback.Results, context);
            }
        }
        else
        {
            var fallback = await _windowsIndex.SearchAsync(context.Query, candidateLimit, context.Filter, token)
                .ConfigureAwait(false);
            if (fallback.Available)
                return Hydrate(fallback.Results, context);
        }

        return [];
    }

    private static IReadOnlyList<LauncherResult> Hydrate(IReadOnlyList<LauncherResult> files, ProviderContext context)
    {
        var everythingSyntax = LooksLikeEverythingSyntax(context.Query);
        var providerSort = ResultRanker.IsProviderSort(context.SortMode);
        var hydrated = new List<LauncherResult>(files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var match = everythingSyntax || providerSort
                ? 120 - index
                : FuzzyMatcher.Score(context.Prepared,
                    FuzzyMatcher.PrepareCandidate(file.Title),
                    FuzzyMatcher.PrepareCandidate(file.Subtitle));
            if (double.IsNegativeInfinity(match))
                continue;
            hydrated.Add(new LauncherResult
            {
                Title = file.Title,
                Subtitle = file.Subtitle,
                Target = file.Target,
                Kind = file.Kind,
                ProviderOrder = providerSort ? index : null,
                IndexedSize = file.IndexedSize,
                IndexedModifiedFileTime = file.IndexedModifiedFileTime,
                Score = match + context.Usage.GetBoost(file.Target),
                IsFavorite = context.Usage.IsFavorite(file.Target)
            });
        }
        return hydrated;
    }

    private static bool LooksLikeEverythingSyntax(string query) =>
        query.IndexOfAny([':', '*', '?', '|', '!', '<', '>', '"']) >= 0;
}
