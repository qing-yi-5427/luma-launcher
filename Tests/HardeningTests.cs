using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LumaLauncher.Models;
using LumaLauncher.Services;
using LumaLauncher.Services.Providers;

namespace LumaLauncher.Tests;

internal static class HardeningTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    internal static async Task RunAsync()
    {
        await VerifyApplicationWorkerAsync();
        await VerifyOptionalIsolationAsync();
        await VerifyPreviewsAsync();
        await VerifyIconQueueAsync();
        var index = new WindowsIndexSearchService();
        var indexSlot = (SemaphoreSlim)typeof(WindowsIndexSearchService).GetField("_querySlot", Private)!.GetValue(index)!;
        await indexSlot.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var queued = index.SearchAsync("synthetic", 8, "File", cancellation.Token);
            cancellation.Cancel();
            await MustCancel(queued); // No COM call is permitted while the slot is held.
        }
        finally { indexSlot.Release(); }
        var defaults = new AppSettings();
        Check(!defaults.EnableBookmarks && !defaults.EnablePreview && !defaults.EnableClipboardHistory,
            "New users must opt into optional expensive features");
        var existing = JsonSerializer.Deserialize<AppSettings>("{\"EnableBookmarks\":true,\"EnablePreview\":true}")!.Normalize().Copy();
        Check(existing.EnableBookmarks && existing.EnablePreview, "Existing opt-ins were lost");

        var bookmarks = new BrowserBookmarkService();
        var loadedAt = DateTimeOffset.UtcNow;
        typeof(BrowserBookmarkService).GetField("_loadedAt", Private)!.SetValue(bookmarks, loadedAt);
        Check(bookmarks.Search("test", 8, new UsageStore()).Count == 0, "Empty cache must remain empty during TTL");
        Check((DateTimeOffset)typeof(BrowserBookmarkService).GetField("_loadedAt", Private)!.GetValue(bookmarks)! == loadedAt,
            "Empty bookmark cache was reloaded");
        Console.WriteLine("PASS hardening: background apps, cancellation, optional isolation, preview cache/bounds and opt-in defaults");
    }

    private static async Task VerifyIconQueueAsync()
    {
        using var release = new ManualResetEventSlim();
        var icons = new IconService { TestLoadIcon = _ => { release.Wait(); return null; } };
        var loads = new List<Task<System.Windows.Media.ImageSource?>>();
        using var cancellation = new CancellationTokenSource();
        try
        {
            for (var i = 0; i < 300; i++) loads.Add(icons.GetAsync("synthetic-icon-" + i, cancellation.Token));
            Check(icons.PendingCount == IconService.MaximumPendingLoads, "Shell icon queue unbounded");
            cancellation.Cancel();
            foreach (var task in loads)
            {
                try { await task; }
                catch (OperationCanceledException) { }
            }
        }
        finally { release.Set(); }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (icons.PendingCount > 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(icons.PendingCount == 0 && icons.CachedCount <= IconService.MaximumPendingLoads, "Icon loads did not drain");
    }

    private static async Task VerifyApplicationWorkerAsync()
    {
        using var provider = new ApplicationProvider();
        var entryType = typeof(AppIndexService).GetNestedType("AppEntry", BindingFlags.NonPublic)!;
        var entries = Array.CreateInstance(entryType, 1);
        var entry = Activator.CreateInstance(entryType)!;
        foreach (var (name, value) in new[] { ("Title", "Notepad"), ("Target", "notepad.exe"),
                     ("NormalizedTitle", FuzzyMatcher.PrepareCandidate("Notepad")) })
            entryType.GetProperty(name)!.SetValue(entry, value);
        entries.SetValue(entry, 0);
        typeof(AppIndexService).GetField("_entries", Private)!.SetValue(provider.Index, entries);
        var usage = new UsageStore();
        var context = new ProviderContext { Query = "note", Prepared = FuzzyMatcher.Prepare("note"),
            MaximumResults = 8, Filter = "Application", SortMode = "Smart", Usage = usage,
            Aliases = new Dictionary<string, string>() };
        var returned = new TaskCompletionSource<Task<IReadOnlyList<LauncherResult>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            try { returned.SetResult(provider.QueryAsync(context, CancellationToken.None)); }
            catch (Exception e) { returned.SetException(e); }
        }) { IsBackground = true };
        // Hold the usage lock so scoring cannot finish. QueryAsync must still
        // return to its caller, rather than doing the scan on that thread.
        lock (typeof(UsageStore).GetField("_sync", Private)!.GetValue(usage)!)
        {
            caller.Start();
            Check(returned.Task.Wait(TimeSpan.FromSeconds(3)), "Application scoring blocked its caller");
        }
        var results = await (await returned.Task).WaitAsync(TimeSpan.FromSeconds(3));
        Check(results.Count == 1, "Synthetic application not returned");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await MustCancel(provider.QueryAsync(context, cancelled.Token));
        try
        {
            provider.Index.Search(context.Prepared, 8, usage, context.Aliases, cancelled.Token);
            throw new InvalidOperationException("Application scan ignored cancellation");
        }
        catch (OperationCanceledException) { }
    }

    private static async Task VerifyOptionalIsolationAsync()
    {
        using var coordinator = new SearchCoordinator((_, _, _, _, _) => Task.FromResult(
            new EverythingSearchResponse([new LauncherResult { Title = "needle", Target = @"C:\fake\needle.txt",
                Kind = LauncherResultKind.File, Subtitle = "synthetic", Score = 1 }], true, "fake", 1, false)));
        coordinator.Configure(new AppSettings { EnableWindowSwitcher = false, EverythingLifecycle = "Connect" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        coordinator.TestOptionalQuery = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            release.Wait(); // Simulate uncancellable synchronous/native work.
            return Task.FromResult<IReadOnlyList<LauncherResult>>([]);
        };
        var partial = new TaskCompletionSource<SearchBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var search = coordinator.SearchAsync("needle", 64, CancellationToken.None, "All", b => partial.TrySetResult(b));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check((await partial.Task.WaitAsync(TimeSpan.FromSeconds(3))).Results.Any(r => r.Title == "needle"),
                "Core results waited for optional provider");
            Check((await search.WaitAsync(TimeSpan.FromSeconds(3))).Results.Any(r => r.Title == "needle"),
                "Timed-out optional provider lost core results");
            for (var i = 0; i < 12; i++)
                await coordinator.SearchAsync("needle", 64, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Check(calls == 1, "Repeated queries queued more blocked optional work");
        }
        finally { release.Set(); }
        var slot = (SemaphoreSlim)typeof(SearchCoordinator).GetField("_bookmarkSlot", Private)!.GetValue(coordinator)!;
        Check(await slot.WaitAsync(TimeSpan.FromSeconds(3)), "Optional slot not released");
        slot.Release();
        coordinator.TestOptionalQuery = (_, _) => throw new IOException("synthetic optional failure");
        Check((await coordinator.SearchAsync("needle", 64, CancellationToken.None)).Results.Count > 0,
            "Optional failure escaped into core search");
        coordinator.TestOptionalQuery = (_, _) => Task.FromResult<IReadOnlyList<LauncherResult>>(
            [new LauncherResult { Title = "needle bookmark", Target = "https://needle.test", Kind = LauncherResultKind.Web, Subtitle = "synthetic", Score = 1 }]);
        Check((await coordinator.SearchAsync("needle", 64, CancellationToken.None)).Results.Any(r => r.Title == "needle bookmark"),
            "Fast optional results not merged");
        var cancellationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.TestOptionalQuery = async (_, token) =>
        {
            cancellationStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancellationObserved.TrySetResult(); }
            return [];
        };
        using var cancellation = new CancellationTokenSource();
        var cancelledSearch = coordinator.SearchAsync("needle", 64, cancellation.Token);
        await cancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await MustCancel(cancelledSearch);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task VerifyPreviewsAsync()
    {
        var preview = new PreviewService();
        var path = Path.Combine(AppDataPaths.DirectoryPath, "landscape.png");
        WriteImage(path, 800, 400);
        var result = new LauncherResult { Title = "landscape", Target = path, Kind = LauncherResultKind.File, Subtitle = "synthetic", Score = 1 };
        var first = await preview.LoadAsync(result, CancellationToken.None);
        Check(first?.ThumbnailPath is not null, "Thumbnail not generated");
        using (var stream = File.OpenRead(first!.ThumbnailPath!))
        {
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Check(image.PixelWidth == 240 && image.PixelHeight == 120, "Decode dimensions not bounded");
        }
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => preview.LoadAsync(result, CancellationToken.None)));
        Check(concurrent.All(p => p?.ThumbnailPath == first.ThumbnailPath), "Cache not reused");
        Check(Directory.GetFiles(Path.GetDirectoryName(first.ThumbnailPath!)!, "*.png").Length == 1, "Duplicate cache files");
        WriteImage(path, 200, 600);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        var changed = await preview.LoadAsync(result, CancellationToken.None);
        Check(changed?.ThumbnailPath is not null && changed.ThumbnailPath != first.ThumbnailPath, "Modified image reused stale cache");
        using (var stream = File.OpenRead(changed!.ThumbnailPath!))
        {
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Check(image.PixelWidth == 80 && image.PixelHeight == 240, "Portrait decode not bounded");
        }
        var slot = (SemaphoreSlim)typeof(PreviewService).GetField("_decodeSlot", Private)!.GetValue(preview)!;
        await slot.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var waiting = preview.LoadAsync(result, cancellation.Token);
            cancellation.Cancel();
            await MustCancel(waiting);
        }
        finally { slot.Release(); }
        File.Delete(path); // Decoder must not keep the input locked.
        Check(await preview.LoadAsync(result, CancellationToken.None) is null, "Missing file preview must be empty");
        File.WriteAllText(path, "not an image");
        Check((await preview.LoadAsync(result, CancellationToken.None))?.ThumbnailPath is null, "Corrupt input accepted");
        using (var oversized = File.Create(path)) oversized.SetLength(51L * 1024 * 1024);
        Check((await preview.LoadAsync(result, CancellationToken.None))?.ThumbnailPath is null, "Oversized image accepted");
        File.Delete(path);
        var cache = Path.Combine(AppDataPaths.DirectoryPath, "preview-cache");
        for (var i = 0; i < 210; i++) File.WriteAllText(Path.Combine(cache, $"synthetic-{i}.png"), "cache");
        var old = Path.Combine(cache, "old.png");
        File.WriteAllText(old, "old");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-7));
        preview.TrimCache();
        Check(Directory.GetFiles(cache).Length <= 200 && !File.Exists(old), "Cache count/age not bounded");
        var hugeCache = Path.Combine(cache, "huge.png");
        using (var stream = File.Create(hugeCache)) stream.SetLength(41L * 1024 * 1024);
        preview.TrimCache();
        Check(Directory.GetFiles(cache).Sum(f => new FileInfo(f).Length) <= 40L * 1024 * 1024, "Cache byte limit ignored");
    }

    private static void WriteImage(string path, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, new byte[width * height * 3], width * 3);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task MustCancel(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Cancellation ignored");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
