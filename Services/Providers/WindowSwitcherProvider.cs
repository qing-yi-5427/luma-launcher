using LumaLauncher.Models;

namespace LumaLauncher.Services.Providers;

public sealed class WindowSwitcherProvider : ILumaProvider
{
    private readonly WindowSwitcherService _service = new();
    private bool _enabled;

    public string Id => "windows";
    public int Priority => 15;
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public bool CanHandle(string filter) => _enabled && filter is "All" or "Application";

    public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;

    public Task<IReadOnlyList<LauncherResult>> QueryAsync(ProviderContext context, CancellationToken token)
    {
        if (!_enabled || context.Filter is "File" or "Folder")
            return Task.FromResult<IReadOnlyList<LauncherResult>>([]);
        if (context.Query.Length < 1)
            return Task.FromResult<IReadOnlyList<LauncherResult>>([]);

        return Task.Run(() => _service.Search(context.Query, Math.Min(context.MaximumResults, 16), context.Usage), token);
    }

    public static bool Activate(LauncherResult result) => WindowSwitcherService.Activate(result);
}
