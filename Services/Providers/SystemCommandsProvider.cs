using LumaLauncher.Models;

namespace LumaLauncher.Services.Providers;

public sealed class SystemCommandsProvider : ILumaProvider
{
    private readonly SystemCommandsService _service = new();
    private bool _enabled = true;

    public string Id => "system";
    public int Priority => 8;
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
        return Task.FromResult<IReadOnlyList<LauncherResult>>(_service.Search(context.Query, 6));
    }

    public static bool Execute(LauncherResult result) => SystemCommandsService.Execute(result);
}
