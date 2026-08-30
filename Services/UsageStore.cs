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
    }

    private readonly object _sync = new();
    private readonly string _path;
    private Dictionary<string, UsageEntry> _entries;

    public UsageStore()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaLauncher");
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
        lock (_sync)
        {
            return _entries.Values
                .Where(entry => IsTargetAvailable(entry.Target))
                .OrderByDescending(entry => entry.LastUsedUtc)
                .Take(limit)
                .Select(entry => new LauncherResult
                {
                    Title = entry.Title,
                    Subtitle = entry.Subtitle,
                    Target = entry.Target,
                    Kind = entry.Kind,
                    Score = 1000 + GetBoost(entry.Target)
                })
                .ToList();
        }
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
            entry.Count++;
            entry.LastUsedUtc = DateTime.UtcNow;
            Save();
        }
    }

    private Dictionary<string, UsageEntry> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, UsageEntry>>(File.ReadAllText(_path)) ?? NewDictionary()
                : NewDictionary();
        }
        catch
        {
            return NewDictionary();
        }
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_entries));
        File.Move(temporary, _path, true);
    }

    private static Dictionary<string, UsageEntry> NewDictionary() => new(StringComparer.OrdinalIgnoreCase);
    private static bool IsTargetAvailable(string target) => target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || File.Exists(target) || Directory.Exists(target);
}
