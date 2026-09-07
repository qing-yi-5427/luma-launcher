using System.Text;
using System.Text.Json;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

/// <summary>Recent query strings used for the history panel and input autocomplete.</summary>
public sealed class QueryHistoryStore
{
    private const int MaxEntries = 200;
    private readonly object _sync = new();
    private readonly string _path;
    private List<string> _entries;

    public QueryHistoryStore()
    {
        var directory = AppDataPaths.DirectoryPath;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "queries.json");
        _entries = Load();
    }

    public IReadOnlyList<string> Recent(int limit)
    {
        lock (_sync)
            return _entries.Take(Math.Max(0, limit)).ToList();
    }

    public IReadOnlyList<string> Suggest(string prefix, int limit)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return [];
        lock (_sync)
        {
            return _entries
                .Where(entry => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                !entry.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                .Take(Math.Max(0, limit))
                .ToList();
        }
    }

    public void Record(string? query)
    {
        query = query?.Trim();
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return;
        lock (_sync)
        {
            _entries.RemoveAll(entry => entry.Equals(query, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, query);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            try { Save(); }
            catch (Exception exception) { DiagnosticsService.Log("query-history-save", exception); }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            try { Save(); }
            catch (Exception exception) { DiagnosticsService.Log("query-history-clear", exception); }
        }
    }

    private List<string> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return [];
            var entries = JsonSerializer.Deserialize<List<string>>(AtomicFileService.ReadAllText(_path)) ?? [];
            return entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Select(entry => entry.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntries)
                .ToList();
        }
        catch (Exception exception)
        {
            AtomicFileService.PreserveCorruptFile(_path);
            DiagnosticsService.Log("query-history-load", exception);
            return [];
        }
    }

    private void Save()
    {
        AtomicFileService.WriteAllText(_path, JsonSerializer.Serialize(_entries));
    }
}
