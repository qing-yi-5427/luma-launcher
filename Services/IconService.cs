using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LumaLauncher.Services;

public sealed class IconService
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const int MaximumCacheEntries = 192;

    private sealed record CacheEntry(ImageSource? Image, LinkedListNode<string> Node);

    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<ImageSource?>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recency = new();

    public async Task<ImageSource?> GetAsync(string target, CancellationToken token)
    {
        Task<ImageSource?> loadTask;
        lock (_sync)
        {
            if (_cache.TryGetValue(target, out var cached))
            {
                _recency.Remove(cached.Node);
                _recency.AddFirst(cached.Node);
                return cached.Image;
            }

            if (!_inflight.TryGetValue(target, out loadTask!))
            {
                loadTask = LoadAndCacheAsync(target);
                _inflight[target] = loadTask;
            }
        }

        return await loadTask.WaitAsync(token).ConfigureAwait(false);
    }

    public void Trim(int entriesToKeep = 64)
    {
        entriesToKeep = Math.Clamp(entriesToKeep, 0, MaximumCacheEntries);
        lock (_sync)
        {
            while (_cache.Count > entriesToKeep && _recency.Last is { } last)
            {
                _cache.Remove(last.Value);
                _recency.RemoveLast();
            }
        }
    }

    internal int CachedCount
    {
        get { lock (_sync) return _cache.Count; }
    }

    private async Task<ImageSource?> LoadAndCacheAsync(string target)
    {
        ImageSource? icon = null;
        try
        {
            icon = await Task.Run(() => GetIcon(target)).ConfigureAwait(false);
            return icon;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DiagnosticsService.Log("icon", exception);
            return null;
        }
        finally
        {
            lock (_sync)
            {
                _inflight.Remove(target);
                var node = _recency.AddFirst(target);
                _cache[target] = new CacheEntry(icon, node);
                while (_cache.Count > MaximumCacheEntries && _recency.Last is { } last)
                {
                    _cache.Remove(last.Value);
                    _recency.RemoveLast();
                }
            }
        }
    }

    private static ImageSource? GetIcon(string target)
    {
        var info = new ShellFileInfo();
        var result = SHGetFileInfo(target, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiSmallIcon);
        if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24));
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DestroyIcon(info.Icon);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        internal IntPtr Icon;
        internal int IconIndex;
        internal uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] internal string TypeName;
    }
}
