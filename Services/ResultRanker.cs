using LumaLauncher.Models;

namespace LumaLauncher.Services;

internal static class ResultRanker
{
    internal const string Smart = "Smart";
    internal const string Relevance = "Relevance";
    internal const string Usage = "Usage";
    internal const string Name = "Name";
    internal const string NameDescending = "NameDescending";
    internal const string SizeAscending = "SizeAscending";
    internal const string SizeDescending = "SizeDescending";
    internal const string ModifiedNewest = "ModifiedNewest";
    internal const string ModifiedOldest = "ModifiedOldest";

    internal static readonly (string Mode, string Label)[] Options =
    [
        (Smart, "智能"), (Relevance, "相关性"), (Usage, "常用与收藏"),
        (Name, "名称 A–Z"), (NameDescending, "名称 Z–A"),
        (SizeDescending, "大小 · 大到小"), (SizeAscending, "大小 · 小到大"),
        (ModifiedNewest, "修改 · 最新优先"), (ModifiedOldest, "修改 · 最早优先")
    ];

    internal static string Normalize(string? mode) => Options.Any(option => option.Mode == mode) ? mode! : Smart;
    internal static string Label(string? mode) => Options.First(option => option.Mode == Normalize(mode)).Label;
    internal static bool IsProviderSort(string mode) => Normalize(mode) is Name or NameDescending or SizeAscending or SizeDescending or ModifiedNewest or ModifiedOldest;
    internal static bool IsSizeSort(string mode) => mode is SizeAscending or SizeDescending;
    internal static bool NeedsWideCandidateSet(string mode) => Normalize(mode) != Smart;

    // SDK sort constants. The provider applies these BEFORE SetMax, not after hydration.
    internal static uint EverythingSort(string mode) => Normalize(mode) switch
    {
        NameDescending => 2, SizeAscending => 5, SizeDescending => 6,
        ModifiedOldest => 13, ModifiedNewest => 14, _ => 1
    };

    internal static IReadOnlyList<LauncherResult> Rank(IEnumerable<LauncherResult> results, string mode,
        int maximumResults, Func<string, double> getUsageBoost)
    {
        var candidates = results.ToList();
        IOrderedEnumerable<LauncherResult> ordered = Normalize(mode) switch
        {
            Relevance => candidates.OrderByDescending(result => RelevanceOf(result, getUsageBoost)),
            Usage => candidates.OrderByDescending(result => result.IsFavorite)
                .ThenByDescending(result => getUsageBoost(result.Target))
                .ThenByDescending(result => RelevanceOf(result, getUsageBoost)),
            Name => candidates.OrderBy(result => result.ProviderOrder.HasValue ? 0 : 1)
                .ThenBy(result => result.ProviderOrder)
                .ThenBy(result => result.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(result => result.Kind),
            NameDescending => candidates.OrderBy(result => result.ProviderOrder.HasValue ? 0 : 1)
                .ThenBy(result => result.ProviderOrder)
                .ThenByDescending(result => result.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(result => result.Kind),
            SizeAscending or SizeDescending or ModifiedNewest or ModifiedOldest => candidates
                .OrderBy(result => result.ProviderOrder.HasValue ? 0 : 1).ThenBy(result => result.ProviderOrder)
                .ThenBy(result => result.Title, StringComparer.CurrentCultureIgnoreCase),
            // Smart mode uses combined Score (already includes usage). A small intent bias
            // promotes calculator/system/window results without rewriting file-vs-app order.
            _ => candidates
                .OrderByDescending(result => result.Score + KindBias(result.Kind))
        };
        return ordered.ThenBy(result => result.Title.Length)
            .ThenBy(result => result.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(result => result.Target, StringComparer.OrdinalIgnoreCase).Take(maximumResults).ToList();
    }

    private static double RelevanceOf(LauncherResult result, Func<string, double> getUsageBoost) =>
        result.Score - getUsageBoost(result.Target);

    private static double KindBias(LauncherResultKind kind) => kind switch
    {
        LauncherResultKind.Calculation => 80,
        LauncherResultKind.System => 60,
        LauncherResultKind.Window => 40,
        _ => 0
    };
}
