using LumaLauncher.Models;

namespace LumaLauncher.Services;

/// <summary>
/// Search orchestration extracted from MainWindow so the window only owns presentation.
/// </summary>
public sealed class LauncherController
{
    private readonly SearchCoordinator _search;
    private readonly QueryHistoryStore _queryHistory = new();
    private readonly SettingsStore _settings;
    private readonly GameModeService _gameMode = new();

    public LauncherController(SearchCoordinator search, SettingsStore settings)
    {
        _search = search;
        _settings = settings;
        _gameMode.Enabled = settings.Current.EnableGameMode;
    }

    public SearchCoordinator Search => _search;
    public QueryHistoryStore QueryHistory => _queryHistory;
    public GameModeService GameMode => _gameMode;

    public void ApplySettings(AppSettings settings)
    {
        _search.Configure(settings);
        _gameMode.Enabled = settings.EnableGameMode;
    }

    public void RecordCompletedQuery(string query)
    {
        if (_settings.Current.RecordQueryHistory)
            _queryHistory.Record(query);
    }

    public void ClearQueryHistory() => _queryHistory.Clear();

    public Task<SearchBatch> SearchAsync(string query, int maximumResults, CancellationToken token,
        string filter, Action<SearchBatch>? publishApplications = null) =>
        _search.SearchAsync(query, maximumResults, token, filter, publishApplications);

    public Task<SearchBatch> GetRecommendationsAsync(int maximumResults, CancellationToken token) =>
        _search.GetRecommendationsAsync(maximumResults, token);

    public Task<PreviewService.PreviewInfo?> LoadPreviewAsync(LauncherResult result, CancellationToken token)
    {
        if (!_settings.Current.EnablePreview)
            return Task.FromResult<PreviewService.PreviewInfo?>(null);
        return _search.Preview.LoadAsync(result, token);
    }
}
