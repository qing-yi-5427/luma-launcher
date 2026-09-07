using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace LumaLauncher.Services;

internal static class UpdateService
{
    internal const string ReleasesUrl = "https://github.com/qing-yi-5427/luma-launcher/releases";
    internal const string Repository = "qing-yi-5427/luma-launcher";
    internal static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    internal sealed record UpdateInfo(string Tag, string Notes, string? AssetUrl, string? Sha256, bool HasNewer);

    internal static async Task<UpdateInfo> QueryAsync(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Luma/{CurrentVersion}");
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
        string? assetUrl = null;
        string? sha256 = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name.Equals("Luma.exe", StringComparison.OrdinalIgnoreCase) && url is not null)
                    assetUrl = url;
                if (name.Equals("Luma.exe.sha256", StringComparison.OrdinalIgnoreCase) && url is not null)
                {
                    // Prefer companion hash file when present.
                    try
                    {
                        using var hashClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                        var hashText = await hashClient.GetStringAsync(url, token);
                        sha256 = hashText.Trim().Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        DiagnosticsService.Log("update-hash-fetch", exception);
                    }
                }
            }
        }

        var hasNewer = Version.TryParse(tag.TrimStart('v', 'V'), out var latest) && latest > Version.Parse(CurrentVersion);
        return new UpdateInfo(tag, notes, assetUrl, sha256, hasNewer);
    }

    internal static async Task<string> CheckAsync(CancellationToken token)
    {
        var info = await QueryAsync(token).ConfigureAwait(false);
        if (!Version.TryParse(info.Tag.TrimStart('v', 'V'), out var latest))
            return $"无法识别发布版本 {info.Tag}，请打开下载页核对。";
        if (info.HasNewer)
            return $"发现新版本 {info.Tag}。可点击“下载并校验”安装；设置和收藏会保留。";
        return $"当前版本 {CurrentVersion} 已是最新正式版。";
    }

    /// <summary>
    /// Downloads Luma.exe only with a valid companion SHA-256, verifies it, and stages
    /// a replacement script. Never silently overwrites a running executable or elevates.
    /// </summary>
    internal static async Task<string> DownloadAndStageAsync(CancellationToken token)
    {
        var info = await QueryAsync(token).ConfigureAwait(false);
        if (!info.HasNewer || info.AssetUrl is null)
            return "没有可下载的新版本，或发布包缺少 Luma.exe。";
        if (!IsValidSha256(info.Sha256))
            return "发布包缺少有效的 Luma.exe.sha256 或校验文件下载失败，已停止自动下载与替换。请重试或前往发布页核对。";

        var directory = Path.Combine(AppDataPaths.DirectoryPath, "updates");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"Luma-{info.Tag}.exe");
        var temp = target + ".partial";

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Luma/{CurrentVersion}");
        using var response = await client.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using (var stream = await response.Content.ReadAsStreamAsync(token))
        await using (var file = File.Create(temp))
            await stream.CopyToAsync(file, token);

        var actualHash = await ComputeSha256Async(temp, token);
        if (!actualHash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            return $"哈希校验失败（期望 {info.Sha256![..12]}…，实际 {actualHash[..12]}…）。已取消替换。";
        }

        File.Move(temp, target, true);
        var script = Path.Combine(directory, "apply-update.cmd");
        var exePath = Environment.ProcessPath ?? "Luma.exe";
        await File.WriteAllTextAsync(script, $"""
            @echo off
            timeout /t 2 /nobreak >nul
            copy /y "{target}" "{exePath}"
            if errorlevel 1 (
              echo Failed to replace Luma.exe
              pause
              exit /b 1
            )
            start "" "{exePath}"
            del "%~f0"
            """, token);

        return $"已下载并校验 {info.Tag}（SHA-256 {actualHash[..16]}…）。请退出 Luma，然后运行：{script}";
    }

    internal static bool IsValidSha256(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, token);
        return Convert.ToHexString(hash);
    }
}
