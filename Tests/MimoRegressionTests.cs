using System.Reflection;
using System.Threading;
using System.Windows.Interop;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

internal static class MimoRegressionTests
{
    internal static async Task RunAsync()
    {
        using var search = new SearchCoordinator((query, limit, token, filter, sort) =>
            Task.FromResult(new EverythingSearchResponse(Enumerable.Range(0, Math.Min(limit, 1600))
                .Select(i => new LauncherResult { Title = $"item-{i}.txt", Target = $"C:\\fake\\item-{i}.txt",
                    Subtitle = "fake", Kind = LauncherResultKind.File, Score = 0 }).ToArray(), true, "Everything", 1600, limit < 1600)));
        foreach (var (mode, _) in ResultRanker.Options.Where(x => ResultRanker.IsProviderSort(x.Mode)))
        {
            search.Configure(new AppSettings { ResultSort = mode, EverythingLifecycle = "Connect", EnableBookmarks = false, EnableWindowSwitcher = false });
            foreach (var limit in new[] { 64, 512, 1024, 2048 })
            {
                var batch = await search.SearchAsync("item", limit, CancellationToken.None, "File");
                Check(batch.FileMatchCount == 1600 && batch.HasMore == (limit < 1600), "Lost provider pagination: " + mode);
            }
        }
        using var unavailable = new SearchCoordinator((_, _, _, _, _) => Task.FromResult(new EverythingSearchResponse([], false, "Everything timeout")),
            (_, _, _, _, _) => Task.FromResult(new EverythingSearchResponse([], false, "Index offline")));
        unavailable.Configure(new AppSettings { EnableBookmarks = false, EnableWindowSwitcher = false, EverythingLifecycle = "Connect" });
        var failed = await unavailable.SearchAsync("item", 64, CancellationToken.None, "File");
        Check(!failed.EverythingAvailable && failed.FileMatchCount is null && failed.StatusText.Contains("timeout") && failed.StatusText.Contains("offline"), "Failed providers marked completed");
        using var empty = new SearchCoordinator((_, _, _, _, _) => Task.FromResult(new EverythingSearchResponse([], true, "Everything")),
            (_, _, _, _, _) => throw new Exception("An empty successful query must not fall back"));
        Check((await empty.SearchAsync("item", 64, CancellationToken.None, "File")).EverythingAvailable, "Empty result is not failure");

        using var clipboard = new ClipboardHistoryService(); // No listener; synthetic text only.
        var entries = (List<string>)typeof(ClipboardHistoryService).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clipboard)!;
        entries.AddRange(["apple", "banana", new string('x', 90) + "orange"]);
        Check(clipboard.Search("CLIP apple", 20).Single().Title == "apple", "Clipboard prefix not stripped");
        Check(clipboard.Search("clip orange", 20).Count == 1, "Clipboard must search beyond the preview");
        Check(clipboard.Search("clip", 20).Count == 3 && clipboard.Search("clip missing", 20).Count == 0, "Clipboard matching failed");
        foreach (var invalid in new string?[] { null, "", "bad", new string('z', 64), new string('a', 63) })
            Check(!UpdateService.IsValidSha256(invalid), "Invalid update hash accepted");
        Check(UpdateService.IsValidSha256(new string('a', 64)), "Valid hash rejected");
        foreach (var (mode, order) in new[] { ("Name", "System.ItemNameDisplay ASC"), ("NameDescending", "System.ItemNameDisplay DESC"),
            ("SizeAscending", "System.Size ASC"), ("SizeDescending", "System.Size DESC"),
            ("ModifiedNewest", "System.DateModified DESC"), ("ModifiedOldest", "System.DateModified ASC") })
        {
            var sql = WindowsIndexSearchService.BuildQuery("O'Brien", 1024, "Folder", mode);
            Check(sql.Contains("TOP 1025") && sql.Contains("ORDER BY " + order) && sql.Contains("O''Brien") && sql.Contains("System.ItemType = 'Directory'"), "Index query regression: " + mode);
        }
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var hotkey = new HotkeyService();
            try
            {
                using var window = new HwndSource(new HwndSourceParameters("Luma regression probe") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
                var registration = hotkey.Register(window.Handle, "Ctrl+Alt+Shift+F24");
                Check(registration.Active != "未注册", "Could not register test hotkey");
                Check(!HotkeyService.TryProbe(registration.Active, out _), "Expected real duplicate registration to fail");
                Check(HotkeyService.TryProbeForSettings(registration.Active, registration.Active, out _), "Own active hotkey blocks settings");
                Check(!HotkeyService.TryProbeForSettings(registration.Active, null, out _), "Unowned conflict ignored");
            }
            catch (Exception e) { failure = e; }
            finally { hotkey.Unregister(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
        if (Environment.GetCommandLineArgs().Contains("--integration"))
        {
            var index = new WindowsIndexSearchService();
            foreach (var (mode, _) in ResultRanker.Options.Where(x => ResultRanker.IsProviderSort(x.Mode)))
            {
                var first = await index.SearchAsync("txt", 16, "File", CancellationToken.None, mode);
                var more = await index.SearchAsync("txt", 64, "File", CancellationToken.None, mode);
                Check(first.Available && more.Available, "Windows Index query failed: " + mode);
                Check(first.Results.Select(x => x.Target).SequenceEqual(more.Results.Take(first.Results.Count).Select(x => x.Target)), "Windows Index prefix order changed: " + mode);
                if (more.Results.Count > first.Results.Count) Check(first.HasMore, "Windows Index lost lookahead");
                Console.WriteLine($"PASS live Windows Index {mode}: {first.Results.Count}/{more.Results.Count}, hasMore={first.HasMore}");
            }
        }
        Console.WriteLine("PASS mimo regressions: hotkey ownership, pagination, failure states, clipboard, hashes and index sorting");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
