namespace LumaLauncher.Services;

/// <summary>Lightweight UI string table (zh-CN default, en-US optional).</summary>
public static class UiStrings
{
    private static readonly Dictionary<string, string> Zh = new()
    {
        ["SearchHint"] = "搜索应用、文件，= 计算，? 网页，> Shell，win 窗口",
        ["HistoryTitle"] = "搜索历史",
        ["HistoryEmpty"] = "还没有搜索历史",
        ["ClearHistory"] = "清空历史",
        ["GameModeOn"] = "游戏模式：热键已暂停",
        ["GameModeOff"] = "游戏模式：已关闭",
        ["WindowsIndex"] = "Windows 索引",
        ["WindowsIndexFallback"] = "Everything 不可用 · 已回退 Windows 索引",
        ["Preview"] = "预览",
        ["Window"] = "窗口",
        ["System"] = "系统",
        ["Bookmark"] = "书签",
        ["SearchError"] = "搜索暂时不可用，请稍后重试",
        ["NoResults"] = "没有找到匹配项",
        ["Searching"] = "正在搜索…",
        ["AppsReady"] = "应用已就绪 · 文件搜索中…",
        ["RecentUsage"] = "最近使用",
        ["InputHint"] = "输入应用、文件名或 Everything 语法"
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["SearchHint"] = "Search apps & files · = calc · ? web · > shell · win windows",
        ["HistoryTitle"] = "Search history",
        ["HistoryEmpty"] = "No search history yet",
        ["ClearHistory"] = "Clear history",
        ["GameModeOn"] = "Game mode: hotkey paused",
        ["GameModeOff"] = "Game mode: off",
        ["WindowsIndex"] = "Windows Index",
        ["WindowsIndexFallback"] = "Everything unavailable · using Windows Search",
        ["Preview"] = "Preview",
        ["Window"] = "Window",
        ["System"] = "System",
        ["Bookmark"] = "Bookmark",
        ["SearchError"] = "Search is temporarily unavailable",
        ["NoResults"] = "No matches",
        ["Searching"] = "Searching…",
        ["AppsReady"] = "Apps ready · searching files…",
        ["RecentUsage"] = "Recent",
        ["InputHint"] = "Type an app, file name, or Everything syntax"
    };

    public static string Culture { get; private set; } = "zh-CN";

    public static void SetCulture(string culture)
    {
        Culture = culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
    }

    public static string Get(string key)
    {
        var table = Culture == "en-US" ? En : Zh;
        return table.TryGetValue(key, out var value) ? value : key;
    }
}
