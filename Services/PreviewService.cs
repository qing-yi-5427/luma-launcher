using System.Windows.Media.Imaging;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

/// <summary>Shell thumbnail / metadata preview for selected filesystem results.</summary>
public sealed class PreviewService
{
    private readonly SemaphoreSlim _decodeSlot = new(1, 1);
    private readonly object _cacheSync = new();
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff"];

    public sealed record PreviewInfo(
        string? ThumbnailPath,
        string KindLabel,
        string SizeText,
        string ModifiedText,
        string Description,
        bool CanPreviewImage);

    public async Task<PreviewInfo?> LoadAsync(LauncherResult result, CancellationToken token)
    {
        await _decodeSlot.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!result.IsFileSystemItem || !File.Exists(result.Target))
                {
                    if (result.Kind == LauncherResultKind.Web)
                        return new PreviewInfo(null, "网页", string.Empty, string.Empty, result.Target, false);
                    return null;
                }

                var info = new FileInfo(result.Target);
                var extension = info.Extension.ToLowerInvariant();
                var isImage = ImageExtensions.Contains(extension);
                string? thumb = null;
                if (isImage)
                    thumb = TryCreateThumbnail(result.Target, token);

                var description = result.Kind switch
                {
                    LauncherResultKind.Application => "应用程序",
                    LauncherResultKind.Folder => "文件夹",
                    _ => DescribeExtension(extension)
                };
                return new PreviewInfo(
                    thumb,
                    result.SourceLabel,
                    FormatSize(info.Exists ? info.Length : result.IndexedSize),
                    info.Exists ? info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") : string.Empty,
                    description,
                    isImage);
            }, token).ConfigureAwait(false);
        }
        finally { _decodeSlot.Release(); }
    }

    private string? TryCreateThumbnail(string path, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            if (file.Length > 50L * 1024 * 1024) return null;
            var identity = Path.GetFullPath(path).ToUpperInvariant() + "|" + file.LastWriteTimeUtc.Ticks + "|" + file.Length;
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
            var cache = Path.Combine(AppDataPaths.DirectoryPath, "preview-cache");
            Directory.CreateDirectory(cache);
            var output = Path.Combine(cache, key + ".png");
            lock (_cacheSync)
            {
                if (File.Exists(output))
                {
                    File.SetLastWriteTimeUtc(output, DateTime.UtcNow);
                    return output;
                }
            }
            // Read dimensions without retaining a URI cache entry or a file handle.
            using var source = File.OpenRead(path);
            var frame = BitmapFrame.Create(source, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > 40_000_000)
                return null;
            token.ThrowIfCancellationRequested();
            source.Position = 0;
            var thumb = new BitmapImage();
            thumb.BeginInit();
            thumb.CacheOption = BitmapCacheOption.OnLoad;
            if (frame.PixelWidth >= frame.PixelHeight) thumb.DecodePixelWidth = Math.Min(240, frame.PixelWidth);
            else thumb.DecodePixelHeight = Math.Min(240, frame.PixelHeight);
            thumb.StreamSource = source;
            thumb.EndInit();
            thumb.Freeze();
            token.ThrowIfCancellationRequested();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(thumb));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            token.ThrowIfCancellationRequested();
            // A restart during the write must not leave a corrupt cache hit.
            var pending = output + ".tmp";
            lock (_cacheSync)
            {
                try
                {
                    File.WriteAllBytes(pending, stream.ToArray());
                    token.ThrowIfCancellationRequested();
                    File.Move(pending, output, true);
                    TrimCache();
                }
                finally { if (File.Exists(pending)) File.Delete(pending); }
            }
            return output;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            DiagnosticsService.Log("preview-thumbnail", exception);
            return null;
        }
    }

    private static string FormatSize(long? size)
    {
        if (size is null or < 0)
            return string.Empty;
        double value = size.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private static string DescribeExtension(string extension) => extension switch
    {
        ".txt" or ".md" => "文本文件",
        ".pdf" => "PDF 文档",
        ".doc" or ".docx" => "Word 文档",
        ".xls" or ".xlsx" => "Excel 表格",
        ".ppt" or ".pptx" => "PowerPoint 演示",
        ".zip" or ".7z" or ".rar" => "压缩包",
        ".cs" or ".ts" or ".js" or ".py" or ".cpp" or ".h" => "源代码",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => "图片",
        ".mp3" or ".wav" or ".flac" => "音频",
        ".mp4" or ".mkv" or ".mov" => "视频",
        _ => "文件"
    };

    private const int MaxCacheFiles = 200;
    private const long MaxCacheBytes = 40L * 1024 * 1024;

    public void TrimCache()
    {
        lock (_cacheSync) TrimCacheCore();
    }

    private static void TrimCacheCore()
    {
        try
        {
            var cache = Path.Combine(AppDataPaths.DirectoryPath, "preview-cache");
            if (!Directory.Exists(cache))
                return;
            var files = Directory.EnumerateFiles(cache)
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            var cutoff = DateTime.UtcNow.AddHours(-6);
            long total = 0;
            var index = 0;
            foreach (var file in files)
            {
                index++;
                var tooOld = file.LastWriteTimeUtc < cutoff;
                var tooMany = index > MaxCacheFiles;
                if (tooOld || tooMany || total + file.Length > MaxCacheBytes || file.Extension == ".tmp")
                {
                    try { file.Delete(); }
                    catch { }
                }
                else total += file.Length;
            }
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("preview-cache-trim", exception);
        }
    }
}
