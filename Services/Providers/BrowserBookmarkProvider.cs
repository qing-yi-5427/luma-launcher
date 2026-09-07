using LumaLauncher.Models;

namespace LumaLauncher.Services.Providers;

public sealed class BrowserBookmarkProvider : ILumaProvider
{
    private readonly BrowserBookmarkService _service = new();
    private bool _enabled = true;

    public string Id => "bookmarks";
    public int Priority => 25;
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public bool CanHandle(string filter) => _enabled && filter == "All";

    public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;

    public Task<IReadOnlyList<LauncherResult>> QueryAsync(ProviderContext context, CancellationToken token)
    {
        if (!_enabled || context.Filter != "All" || context.Query.Length < 2)
            return Task.FromResult<IReadOnlyList<LauncherResult>>([]);
        return Task.Run(() => _service.Search(context.Query, Math.Min(context.MaximumResults, 12), context.Usage), token);
    }

    public void Reload() => _service.Reload();
}
