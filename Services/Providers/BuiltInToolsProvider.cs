using LumaLauncher.Models;

namespace LumaLauncher.Services.Providers;

public sealed class BuiltInToolsProvider : ILumaProvider
{
    private readonly BuiltInSearchService _builtIns = new();
    private SearchEngine[] _engines = SearchEngineCatalog.BuiltIn;
    private string _defaultUrl = SearchEngineCatalog.DefaultTemplate;

    public string Id => "builtins";
    public int Priority => 5;
    public bool CanHandle(string filter) => filter == "All";

    public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;

    public void Configure(AppSettings settings)
    {
        _builtIns.Configure(settings);
        _defaultUrl = settings.WebSearchUrl;
        _engines = SearchEngineCatalog
            .Parse(settings.SearchEngines, settings.WebSearchUrl)
            .Where(e => e.Keyword.Length > 0)
            .ToArray();
    }

    public Task<IReadOnlyList<LauncherResult>> QueryAsync(ProviderContext context, CancellationToken token)
    {
        if (context.Filter != "All")
            return Task.FromResult<IReadOnlyList<LauncherResult>>([]);

        var query = context.Query.Trim();
        var results = new List<LauncherResult>(_builtIns.Search(query));

        // Multi-engine web search: "g foo" / "bd bar" when not already claimed as an explicit URL.
        if (!query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !query.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            query.Contains(' ') &&
            !results.Any(r => r.Kind is LauncherResultKind.Calculation or LauncherResultKind.Command))
        {
            foreach (var engine in _engines)
            {
                if (!engine.MatchesPrefix(query, out var terms) || terms.Length == 0)
                    continue;
                var url = engine.UrlTemplate.Replace("{query}", Uri.EscapeDataString(terms), StringComparison.OrdinalIgnoreCase);
                results.Insert(0, new LauncherResult
                {
                    Title = $"用 {engine.Name} 搜索：{terms}",
                    Subtitle = url,
                    Target = url,
                    Kind = LauncherResultKind.Web,
                    Score = 2300
                });
                break;
            }
        }

        // History panel query mode: "h " or "history "
        if (query.StartsWith("h ", StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith("history ", StringComparison.OrdinalIgnoreCase))
        {
            // Handled by SearchCoordinator; keep provider quiet.
        }

        return Task.FromResult<IReadOnlyList<LauncherResult>>(results);
    }

    public bool ShouldShortCircuit(string query, IReadOnlyList<LauncherResult> builtInResults)
    {
        if (builtInResults.Count == 0)
            return false;
        if (builtInResults.Any(result => result.Kind != LauncherResultKind.Web))
            return true;
        return query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               query.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               query.StartsWith("? ") || query.StartsWith("web ") || query.StartsWith("g ");
    }
}
