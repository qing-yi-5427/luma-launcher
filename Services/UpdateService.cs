using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace LumaLauncher.Services;

internal static class UpdateService
{
    internal const string ReleasesUrl = "https://github.com/qing-yi-5427/luma-launcher/releases";
    internal static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    internal static async Task<string> CheckAsync(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Luma/{CurrentVersion}");
        using var response = await client.GetAsync("https://api.github.com/repos/qing-yi-5427/luma-launcher/releases/latest", token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var tag = document.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            return $"无法识别发布版本 {tag}，请打开下载页核对。";
        return latest > Version.Parse(CurrentVersion)
            ? $"发现新版本 {tag}。打开下载页，退出 Luma 后替换 EXE；设置和收藏会保留。"
            : $"当前版本 {CurrentVersion} 已是最新正式版。";
    }
}
