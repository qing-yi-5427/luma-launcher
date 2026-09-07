using System.Text.Json;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

public sealed class BrowserBookmarkService
{
    private sealed record BookmarkEntry(string Title, string Url, string Browser);

    private readonly object _sync = new();
    private BookmarkEntry[] _entries = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public IReadOnlyList<LauncherResult> Search(string query, int limit, UsageStore usage, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        EnsureLoaded();
        token.ThrowIfCancellationRequested();
        BookmarkEntry[] snapshot;
        lock (_sync) snapshot = _entries;
        if (snapshot.Length == 0)
            return [];

        var prepared = FuzzyMatcher.Prepare(query);
        var matches = new List<LauncherResult>();
        foreach (var entry in snapshot)
        {
            token.ThrowIfCancellationRequested();
            var score = FuzzyMatcher.Score(prepared,
                FuzzyMatcher.PrepareCandidate(entry.Title),
                FuzzyMatcher.PrepareCandidate($"{entry.Browser} {entry.Url}"));
            if (double.IsNegativeInfinity(score))
                continue;
            matches.Add(new LauncherResult
            {
                Title = string.IsNullOrWhiteSpace(entry.Title) ? entry.Url : entry.Title,
                Subtitle = $"{entry.Browser} · {entry.Url}",
                Target = entry.Url,
                Kind = LauncherResultKind.Web,
                Score = score + 150,
                IsFavorite = usage.IsFavorite(entry.Url)
            });
        }

        return matches
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title.Length)
            .Take(limit)
            .ToList();
    }

    public void Reload()
    {
        lock (_sync)
        {
            _loadedAt = DateTimeOffset.MinValue;
        }
        EnsureLoaded();
    }

    private void EnsureLoaded()
    {
        lock (_sync)
        {
            if (DateTimeOffset.UtcNow - _loadedAt < CacheTtl)
                return;
            _entries = LoadAll();
            _loadedAt = DateTimeOffset.UtcNow;
        }
    }

    private static BookmarkEntry[] LoadAll()
    {
        var results = new List<BookmarkEntry>();
        results.AddRange(LoadChromium("Chrome", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Google\Chrome\User Data\Default\Bookmarks")));
        results.AddRange(LoadChromium("Edge", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Edge\User Data\Default\Bookmarks")));
        results.AddRange(LoadChromium("Brave", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"BraveSoftware\Brave-Browser\User Data\Default\Bookmarks")));
        results.AddRange(LoadFirefox());
        return results.DistinctBy(b => b.Url + "|" + b.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<BookmarkEntry> LoadChromium(string browser, string path)
    {
        if (!File.Exists(path))
            yield break;
        BookmarkEntry[] loaded;
        try
        {
            using var document = JsonDocument.Parse(AtomicFileService.ReadAllText(path));
            var list = new List<BookmarkEntry>();
            WalkChromium(document.RootElement, list);
            loaded = list.ToArray();
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("bookmarks-" + browser.ToLowerInvariant(), exception);
            yield break;
        }
        foreach (var entry in loaded)
            yield return entry with { Browser = browser };
    }

    private static void WalkChromium(JsonElement element, List<BookmarkEntry> results)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                WalkChromium(child, results);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
            return;

        if (element.TryGetProperty("type", out var type) && type.GetString() == "url")
        {
            var name = element.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var url = element.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                results.Add(new BookmarkEntry(name, url, string.Empty));
            return;
        }

        if (element.TryGetProperty("children", out var children))
            WalkChromium(children, results);
    }

    private static IEnumerable<BookmarkEntry> LoadFirefox()
    {
        var profilesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla\\Firefox\\Profiles");
        if (!Directory.Exists(profilesRoot))
            yield break;

        foreach (var profile in Directory.EnumerateDirectories(profilesRoot))
        {
            var places = Path.Combine(profile, "places.sqlite");
            if (!File.Exists(places))
                continue;
            // SQLite is not a dependency; Firefox bookmarks stay optional until a native reader is added.
            yield break;
        }
    }
}
