using LumaLauncher.Models;
using LumaLauncher.Services.Providers;

namespace LumaLauncher.Services.Providers;

public sealed class ApplicationProvider : ILumaProvider
{
    private readonly AppIndexService _apps = new();
    private Task? _initializeTask;

    public string Id => "apps";
    public int Priority => 10;
    public bool CanHandle(string filter) => filter is "All" or "Application";

    public bool IsReady => _apps.IsReady;
    public int Count => _apps.Count;

    public Task InitializeAsync(CancellationToken token) => _initializeTask ??= _apps.InitializeAsync(token);

    public bool ConfigureCustomFolders(string value) => _apps.ConfigureCustomFolders(value);

    public Task ReloadAsync(CancellationToken token) => _apps.ReloadAsync(token);

    public async Task<IReadOnlyList<LauncherResult>> QueryAsync(ProviderContext context, CancellationToken token)
    {
        if (context.Filter is "File" or "Folder")
            return [];
        if (_initializeTask is not null && !_apps.IsReady)
        {
            try { await _initializeTask.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return _apps.Search(context.Prepared, Math.Max(context.MaximumResults, _apps.Count), context.Usage, context.Aliases);
    }
}
