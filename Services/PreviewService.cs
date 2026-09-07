using System.Diagnostics;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

/// <summary>Shell thumbnail / metadata preview for selected filesystem results.</summary>
public sealed class PreviewService
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff"];

    public sealed record PreviewInfo(
        string? ThumbnailPath,
        string KindLabel,
        string SizeText,
        string ModifiedText,
        string Description,
        bool CanPreviewImage);

    public Task<PreviewInfo?> LoadAsync(LauncherResult result, CancellationToken token)
    {
        return Task.Run(() =>
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
        }, token);
    }

    private static string? TryCreateThumbnail(string path, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            // Prefer WPF decode with width cap to avoid loading huge bitmaps.
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri(path, UriKind.Absolute),
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None);
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                return null;
            var scale = Math.Min(1.0, 240.0 / Math.Max(frame.PixelWidth, frame.PixelHeight));
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                new Uri(path, UriKind.Absolute),
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnDemand);
            var transform = new System.Windows.Media.ScaleTransform(scale, scale);
            var thumb = new System.Windows.Media.Imaging.TransformedBitmap(decoder.Frames[0], transform);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(thumb));
            var cache = Path.Combine(AppDataPaths.DirectoryPath, "preview-cache");
            Directory.CreateDirectory(cache);
            var output = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".png");
            using var stream = File.Create(output);
            encoder.Save(stream);
            token.ThrowIfCancellationRequested();
            return output;
        }
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

    public void TrimCache()
    {
        try
        {
            var cache = Path.Combine(AppDataPaths.DirectoryPath, "preview-cache");
            if (!Directory.Exists(cache))
                return;
            var cutoff = DateTime.UtcNow.AddHours(-6);
            foreach (var file in Directory.EnumerateFiles(cache))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("preview-cache-trim", exception);
        }
    }
}
