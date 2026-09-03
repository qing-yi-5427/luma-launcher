using LumaLauncher.Models;

namespace LumaLauncher.Services;

internal static class ResultRanker
{
    internal const string Smart = "Smart";
    internal const string Relevance = "Relevance";
    internal const string Usage = "Usage";
    internal const string Name = "Name";

    internal static string Normalize(string? mode) => mode switch
    {
        Relevance => Relevance,
        Usage => Usage,
        Name => Name,
        _ => Smart
    };

    internal static bool NeedsWideCandidateSet(string mode) =>
        Normalize(mode) != Smart;

    internal static IReadOnlyList<LauncherResult> Rank(
        IEnumerable<LauncherResult> results,
        string mode,
        int maximumResults,
        Func<string, double> getUsageBoost)
    {
        var candidates = results.ToList();
        IOrderedEnumerable<LauncherResult> ordered = Normalize(mode) switch
        {
            Relevance => candidates
                .OrderByDescending(result => RelevanceOf(result, getUsageBoost)),
            Usage => candidates
                .OrderByDescending(result => result.IsFavorite)
                .ThenByDescending(result => getUsageBoost(result.Target))
                .ThenByDescending(result => RelevanceOf(result, getUsageBoost)),
            Name => candidates
                .OrderBy(result => result.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(result => result.Kind),
            _ => candidates
                .OrderByDescending(result => result.Score)
        };

        return ordered
            .ThenBy(result => result.Title.Length)
            .ThenBy(result => result.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(maximumResults)
            .ToList();
    }

    private static double RelevanceOf(LauncherResult result, Func<string, double> getUsageBoost) =>
        result.Score - getUsageBoost(result.Target);
}
