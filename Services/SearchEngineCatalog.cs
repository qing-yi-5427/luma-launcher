namespace LumaLauncher.Services;

public sealed record SearchEngine(string Keyword, string Name, string UrlTemplate)
{
    public bool MatchesPrefix(string query, out string terms)
    {
        terms = string.Empty;
        if (!query.StartsWith(Keyword + " ", StringComparison.OrdinalIgnoreCase) &&
            !query.Equals(Keyword, StringComparison.OrdinalIgnoreCase))
            return false;
        terms = query.Length > Keyword.Length ? query[(Keyword.Length + 1)..].Trim() : string.Empty;
        return true;
    }
}

public static class SearchEngineCatalog
{
    public const string DefaultTemplate = "https://www.bing.com/search?q={query}";

    public static readonly SearchEngine[] BuiltIn =
    [
        new("g", "Google", "https://www.google.com/search?q={query}"),
        new("bing", "Bing", "https://www.bing.com/search?q={query}"),
        new("bd", "百度", "https://www.baidu.com/s?wd={query}"),
        new("gh", "GitHub", "https://github.com/search?q={query}"),
        new("so", "Stack Overflow", "https://stackoverflow.com/search?q={query}"),
        new("wiki", "Wikipedia", "https://en.wikipedia.org/w/index.php?search={query}"),
        new("bili", "哔哩哔哩", "https://search.bilibili.com/all?keyword={query}")
    ];

    public static IReadOnlyList<SearchEngine> Parse(string? custom, string defaultUrl)
    {
        var engines = new List<SearchEngine>();
        if (!string.IsNullOrWhiteSpace(custom))
        {
            foreach (var line in custom.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith('#'))
                    continue;
                var parts = line.Split('|');
                if (parts.Length < 3)
                    continue;
                var keyword = parts[0].Trim();
                var name = parts[1].Trim();
                var url = parts[2].Trim();
                if (keyword.Length == 0 || !url.Contains("{query}", StringComparison.OrdinalIgnoreCase))
                    continue;
                engines.Add(new SearchEngine(keyword, name.Length == 0 ? keyword : name, url));
            }
        }

        if (engines.Count == 0)
            engines.AddRange(BuiltIn);

        var fallback = string.IsNullOrWhiteSpace(defaultUrl) || !defaultUrl.Contains("{query}", StringComparison.OrdinalIgnoreCase)
            ? DefaultTemplate
            : defaultUrl;
        if (!engines.Any(e => e.UrlTemplate.Equals(fallback, StringComparison.OrdinalIgnoreCase)))
            engines.Insert(0, new SearchEngine(string.Empty, "默认", fallback));
        return engines;
    }

    public static string Resolve(string terms, IReadOnlyList<SearchEngine> engines, string defaultUrl)
    {
        foreach (var engine in engines)
        {
            if (engine.Keyword.Length == 0)
                continue;
            if (engine.MatchesPrefix(terms, out var rest) && rest.Length > 0)
                return engine.UrlTemplate.Replace("{query}", Uri.EscapeDataString(rest), StringComparison.OrdinalIgnoreCase);
        }

        var template = string.IsNullOrWhiteSpace(defaultUrl) || !defaultUrl.Contains("{query}", StringComparison.OrdinalIgnoreCase)
            ? DefaultTemplate
            : defaultUrl;
        return template.Replace("{query}", Uri.EscapeDataString(terms), StringComparison.OrdinalIgnoreCase);
    }
}
