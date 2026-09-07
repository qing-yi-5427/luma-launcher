using LumaLauncher.Models;

namespace LumaLauncher.Services.Providers;

public sealed class ProviderContext
{
    public required string Query { get; init; }
    public required FuzzyMatcher.PreparedQuery Prepared { get; init; }
    public required int MaximumResults { get; init; }
    public required string Filter { get; init; }
    public required string SortMode { get; init; }
    public required UsageStore Usage { get; init; }
    public required IReadOnlyDictionary<string, string> Aliases { get; init; }
    public AppSettings? Settings { get; init; }
}

public interface ILumaProvider
{
    string Id { get; }
    /// <summary>Lower values are queried earlier. Used only for fan-out order, not ranking.</summary>
    int Priority { get; }
    bool CanHandle(string filter);
    Task InitializeAsync(CancellationToken token);
    Task<IReadOnlyList<LauncherResult>> QueryAsync(ProviderContext context, CancellationToken token);
}
