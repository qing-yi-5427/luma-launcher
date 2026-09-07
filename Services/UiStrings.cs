namespace LumaLauncher.Services;

/// <summary>Lightweight UI string table (zh-CN default, en-US optional).</summary>
public static class UiStrings
{
    private static readonly Dictionary<string, string> Zh = new()
    {
        ["SearchHint"] = "搜索应用、文件，= 计算，? 网页，win 窗口",
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
        ["InputHint"] = "输入应用、文件名或 Everything 语法",
        ["HelpTitle"] = "快捷键",
        ["CopiedPath"] = "已复制路径",
        ["CopiedResult"] = "已复制结果",
        ["HotkeyBusy"] = "快捷键可能已被占用，已使用回退组合",
        ["FirstRunTip"] = "按 Alt+Space 唤醒。建议安装 Everything 以启用文件搜索。",
        ["PortableOn"] = "便携模式：数据保存在程序目录",
        ["OnboardingBody"] = "1. 安装 Everything（普通版）以启用文件搜索\n2. 默认热键 Alt+Space，可在设置中录制\n3. 计算用 =，网页用 ?，窗口用 win 前缀"
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["SearchHint"] = "Search apps & files · = calc · ? web · win windows",
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
        ["InputHint"] = "Type an app, file name, or Everything syntax",
        ["HelpTitle"] = "Shortcuts",
        ["CopiedPath"] = "Path copied",
        ["CopiedResult"] = "Result copied",
        ["HotkeyBusy"] = "Hotkey may be in use; fell back to another combo",
        ["FirstRunTip"] = "Press Alt+Space to summon Luma. Install Everything for file search.",
        ["PortableOn"] = "Portable mode: data stored next to the executable",
        ["OnboardingBody"] = "1. Install Everything (regular) for file search\n2. Default hotkey Alt+Space — re-record in Settings\n3. Use = for calc, ? for web, win for windows"
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
