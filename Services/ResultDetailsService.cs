using System.Globalization;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

internal sealed record ResultDetails(
    string Kind,
    string Location,
    string Size,
    string Modified,
    string Description);

internal static class ResultDetailsService
{
    internal static Task<ResultDetails> LoadAsync(LauncherResult result, CancellationToken token) =>
        Task.Run(() => Load(result, token), token);

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var scaled = (double)value;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value} {units[unit]}"
            : $"{scaled:0.#} {units[unit]}";
    }

    private static ResultDetails Load(LauncherResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (result.Kind == LauncherResultKind.Folder && Directory.Exists(result.Target))
            {
                var directory = new DirectoryInfo(result.Target);
                return new ResultDetails(
                    "文件夹",
                    directory.FullName,
                    "—",
                    FormatDate(directory.LastWriteTime),
                    "文件夹结果。可直接打开、定位、复制路径，或切换当前文件对话框。" );
            }

            if (result.IsFileSystemItem && File.Exists(result.Target))
            {
                var file = new FileInfo(result.Target);
                var kind = result.Kind == LauncherResultKind.Application
                    ? "应用"
                    : string.IsNullOrEmpty(file.Extension) ? "文件" : $"{file.Extension.TrimStart('.').ToUpperInvariant()} 文件";
                return new ResultDetails(
                    kind,
                    file.FullName,
                    FormatSize(file.Length),
                    FormatDate(file.LastWriteTime),
                    result.Kind == LauncherResultKind.Application
                        ? "应用程序结果。可直接启动、定位程序文件或以管理员身份运行。"
                        : "文件结果。可打开、定位、复制路径或使用更多文件操作。" );
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            DiagnosticsService.Log("result-details", exception);
        }

        token.ThrowIfCancellationRequested();
        return result.Kind switch
        {
            LauncherResultKind.Application => Basic("应用", result, "应用程序结果。"),
            LauncherResultKind.Folder => Basic("文件夹", result, "文件夹当前不可访问，仍可尝试打开或复制路径。"),
            LauncherResultKind.File => Basic(FileKind(result.Target), result, "文件当前不可访问，仍可尝试打开或复制路径。"),
            LauncherResultKind.Calculation => new ResultDetails("计算结果", result.CopyText, "—", "—", "打开此结果会将计算结果复制到剪贴板。"),
            LauncherResultKind.Web => Basic("网页", result, "将在默认浏览器中打开此地址。"),
            LauncherResultKind.Command => Basic("自定义命令", result, "执行已配置的自定义命令。"),
            _ => Basic("结果", result, string.Empty)
        };
    }

    private static ResultDetails Basic(string kind, LauncherResult result, string description) =>
        new(kind, result.Target, "—", "—", description);

    private static string FileKind(string path)
    {
        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? "文件" : $"{extension.TrimStart('.').ToUpperInvariant()} 文件";
    }

    private static string FormatDate(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}
