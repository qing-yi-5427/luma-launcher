using System.Text.Json;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

public sealed class UsageStore
{
    private sealed class UsageEntry
    {
        public string Target { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public LauncherResultKind Kind { get; set; }
        public int Count { get; set; }
        public DateTime LastUsedUtc { get; set; }
        public bool Favorite { get; set; }
        public string Arguments { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = string.Empty;
        public string CopyText { get; set; } = string.Empty;
    }

    private readonly object _sync = new();
    private readonly string _path;
    private Dictionary<string, UsageEntry> _entries;

    public UsageStore()
    {
        var directory = AppDataPaths.DirectoryPath;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "usage.json");
        _entries = Load();
    }

    public double GetBoost(string target)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(target, out var entry))
                return 0;
            var recencyDays = Math.Max(0, (DateTime.UtcNow - entry.LastUsedUtc).TotalDays);
            return Math.Log2(entry.Count + 1) * 42 + Math.Max(0, 36 - recencyDays * 3);
        }
    }

    public IReadOnlyList<LauncherResult> GetRecent(int limit)
    {
        UsageEntry[] recent;
        lock (_sync)
        {
            recent = _entries.Values
                .OrderByDescending(entry => entry.Favorite)
                .ThenByDescending(entry => entry.LastUsedUtc)
                .ToArray();
        }

        var results = new List<LauncherResult>(limit);
        foreach (var entry in recent)
        {
            if (!IsTargetAvailable(entry))
                continue;
            results.Add(new LauncherResult
            {
                Title = entry.Title,
                Subtitle = entry.Subtitle,
                Target = entry.Target,
                Kind = entry.Kind,
                Score = (entry.Favorite ? 2000 : 1000) + GetBoost(entry.Target),
                Arguments = entry.Arguments,
                WorkingDirectory = entry.WorkingDirectory,
                CopyText = entry.CopyText,
                IsFavorite = entry.Favorite
            });
            if (results.Count == limit)
                break;
        }
        return results;
    }

    public void Record(LauncherResult result)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(result.Target, out var entry))
            {
                entry = new UsageEntry { Target = result.Target };
                _entries[result.Target] = entry;
            }
            entry.Title = result.Title;
            entry.Subtitle = result.Subtitle;
            entry.Kind = result.Kind;
            entry.Arguments = result.Arguments;
            entry.WorkingDirectory = result.WorkingDirectory;
            entry.CopyText = result.CopyText;
            entry.Count++;
            entry.LastUsedUtc = DateTime.UtcNow;
            try { Save(); }
            catch (Exception exception) { DiagnosticsService.Log("usage-save", exception); }
        }
    }

    public IReadOnlyList<LauncherResult> Search(string query, string filter, CancellationToken token)
    {
        int count;
        lock (_sync) count = _entries.Count;
        var prepared = FuzzyMatcher.Prepare(query);
        var matches = new List<LauncherResult>();
        foreach (var item in GetRecent(Math.Max(1, count)))
        {
            token.ThrowIfCancellationRequested();
            if (item.Kind is not (LauncherResultKind.File or LauncherResultKind.Folder) ||
                filter == "Application" || filter == "File" && item.Kind != LauncherResultKind.File ||
                filter == "Folder" && item.Kind != LauncherResultKind.Folder) continue;
            var score = FuzzyMatcher.Score(prepared, FuzzyMatcher.PrepareCandidate(item.Title),
                FuzzyMatcher.PrepareCandidate(item.Subtitle));
            if (double.IsNegativeInfinity(score)) continue;
            matches.Add(new LauncherResult { Title = item.Title, Subtitle = item.Subtitle, Target = item.Target,
                Kind = item.Kind, Score = score + GetBoost(item.Target), IsFavorite = item.IsFavorite });
        }
        return matches;
    }

    public void ClearHistory()
    {
        lock (_sync)
        {
            _entries = _entries.Where(pair => pair.Value.Favorite)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _entries.Values) { entry.Count = 0; entry.LastUsedUtc = DateTime.MinValue; }
            Save();
        }
    }

    public bool ToggleFavorite(LauncherResult result)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(result.Target, out var entry))
            {
                entry = new UsageEntry { Target = result.Target, Title = result.Title, Subtitle = result.Subtitle, Kind = result.Kind };
                _entries[result.Target] = entry;
            }
            entry.Title = result.Title;
            entry.Subtitle = result.Subtitle;
            entry.Kind = result.Kind;
            entry.Arguments = result.Arguments;
            entry.WorkingDirectory = result.WorkingDirectory;
            entry.CopyText = result.CopyText;
            entry.Favorite = !entry.Favorite;
            try { Save(); }
            catch (Exception exception) { DiagnosticsService.Log("usage-favorite", exception); }
            return entry.Favorite;
        }
    }

    public bool IsFavorite(string target)
    {
        lock (_sync)
            return _entries.TryGetValue(target, out var entry) && entry.Favorite;
    }

    public void Remove(string target)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(target, out var entry))
                return;
            if (entry.Favorite)
            {
                entry.Count = 0;
                entry.LastUsedUtc = DateTime.MinValue;
            }
            else
            {
                _entries.Remove(target);
            }
            try { Save(); }
            catch (Exception exception) { DiagnosticsService.Log("usage-remove", exception); }
        }
    }

    private Dictionary<string, UsageEntry> Load()
    {
        try
        {
            return File.Exists(_path)
                ? new Dictionary<string, UsageEntry>(JsonSerializer.Deserialize<Dictionary<string, UsageEntry>>(AtomicFileService.ReadAllText(_path)) ?? NewDictionary(), StringComparer.OrdinalIgnoreCase)
                : NewDictionary();
        }
        catch (Exception exception)
        {
            AtomicFileService.PreserveCorruptFile(_path);
            DiagnosticsService.Log("usage-load", exception);
            return NewDictionary();
        }
    }

    private void Save()
    {
        AtomicFileService.WriteAllText(_path, JsonSerializer.Serialize(_entries));
    }

    private static Dictionary<string, UsageEntry> NewDictionary() => new(StringComparer.OrdinalIgnoreCase);
    private static bool IsTargetAvailable(UsageEntry entry) =>
        entry.Kind is LauncherResultKind.Web or LauncherResultKind.Command or LauncherResultKind.Calculation ||
        entry.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
        File.Exists(entry.Target) || Directory.Exists(entry.Target);
}
