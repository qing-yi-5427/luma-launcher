using System.Text.Json;
using System.IO;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

internal static class ReleaseRegressionTests
{
    internal static void Run()
    {
        var builtIns = new BuiltInSearchService();
        foreach (var name in new[] { "report.pdf", "notepad.exe", "README.md", "photo.jpg", "settings.json" })
            Check(builtIns.Search(name).Count == 0, $"Filename hijacked: {name}");
        Check(builtIns.Search("example.com").Any(item => item.Kind == LauncherResultKind.Web), "Domain suggestion missing");
        Check(builtIns.Search("https://example.com/report.pdf").Any(item => item.Kind == LauncherResultKind.Web), "Explicit URL missing");
        Check(QuickSwitchService.NativeInputSize == 40, "x64 INPUT ABI must be 40 bytes");

        var repaired = JsonSerializer.Deserialize<AppSettings>("{\"Theme\":null,\"Aliases\":null,\"Hotkey\":\"bad\",\"ResultSort\":null,\"WebSearchUrl\":null}")!.Normalize();
        Check(repaired.Theme == "System" && repaired.Hotkey == "Alt+Space" && repaired.Aliases == "", "Legacy settings normalization");
        Check(repaired.WebSearchUrl.StartsWith("https://") && repaired.ResultSort == "Smart", "Invalid search settings normalization");
        try { new AppSettings { SchemaVersion = 999 }.Normalize(); throw new Exception("Future schema accepted"); }
        catch (InvalidDataException) { }

        var settingsPath = Path.Combine(AppDataPaths.DirectoryPath, "settings.json");
        const string futureJson = "{\"SchemaVersion\":999,\"FutureField\":\"keep me\"}";
        File.WriteAllText(settingsPath, futureJson);
        var futureStore = new SettingsStore();
        Check(futureStore.CompatibilityWarning is not null, "Future config must warn");
        try { futureStore.Save(new AppSettings()); throw new Exception("Future config overwritten"); }
        catch (InvalidOperationException) { }
        Check(File.ReadAllText(settingsPath) == futureJson, "Future config not preserved");
        File.Delete(settingsPath); // Isolated test directory only.

        var store = new SettingsStore();
        store.Save(new AppSettings { RecordHistory = false, Theme = "Win11Mist" });
        Check(!new SettingsStore().Current.RecordHistory && new SettingsStore().Current.Theme == "Win11Mist", "Settings roundtrip");
        foreach (var (mode, _) in ResultRanker.Options)
        {
            store.Save(new AppSettings { ResultSort = mode });
            Check(new SettingsStore().Current.ResultSort == mode, $"Sort roundtrip: {mode}");
        }
        Check(new AppSettings { ResultSort = "invalid" }.Normalize().ResultSort == ResultRanker.Smart, "Invalid sort accepted");
        Check(ResultRanker.Options.Select(option => ResultRanker.EverythingSort(option.Mode))
            .SequenceEqual(new uint[] { 1, 1, 1, 1, 2, 6, 5, 14, 13 }), "SDK sort mapping");
        var usage = new UsageStore();
        var favorite = new LauncherResult { Title = "Website", Target = "https://example.com", Subtitle = "test", Kind = LauncherResultKind.Web, Score = 1 };
        usage.Record(favorite);
        usage.ToggleFavorite(favorite);
        usage.ClearHistory();
        Check(usage.IsFavorite(favorite.Target) && usage.GetRecent(8).Count == 1, "Clearing history removed favorite");
        var reloaded = new UsageStore();
        Check(reloaded.IsFavorite(favorite.Target.ToUpperInvariant()), "History lost case-insensitive identity after reload");
        using var coordinator = new SearchCoordinator();
        coordinator.Configure(new AppSettings { RecordHistory = false, EverythingLifecycle = "Connect" });
        coordinator.RecordLaunch(new LauncherResult { Title = "Private", Target = "https://private.test", Subtitle = "", Kind = LauncherResultKind.Web, Score = 1 });
        Check(!new UsageStore().GetRecent(20).Any(item => item.Target == "https://private.test"), "History pause ignored");
    }

    internal static async Task RunSearchAsync()
    {
        var calls = new List<(string Query, int Limit, string Filter)>();
        using var coordinator = new SearchCoordinator((query, limit, token, filter) =>
        {
            token.ThrowIfCancellationRequested();
            calls.Add((query, limit, filter));
            var items = Enumerable.Range(0, Math.Min(limit, 1600)).Select(i => new LauncherResult
            {
                Title = $"report.pdf {i:D4}", Target = $@"C:\isolated\{i}.pdf", Subtitle = "isolated",
                Kind = filter == "Folder" ? LauncherResultKind.Folder : LauncherResultKind.File, Score = 1
            }).ToArray();
            return Task.FromResult(new EverythingSearchResponse(items, true, "fake", 1600, limit < 1600));
        });
        coordinator.Configure(new AppSettings { EverythingLifecycle = "Connect" });
        foreach (var query in new[] { "report.pdf", "notepad.exe", "README.md", "example.com", "http.config" })
        {
            await coordinator.SearchAsync(query, 64, CancellationToken.None);
            Check(calls.Last().Query == query, $"Local provider bypassed: {query}");
        }
        var initial = await coordinator.SearchAsync("report.pdf", 64, CancellationToken.None, "File");
        var expanded = await coordinator.SearchAsync("report.pdf", 512, CancellationToken.None, "File");
        var more = await coordinator.SearchAsync("report.pdf", 1024, CancellationToken.None, "File");
        Check(initial.Results.Count == 64 && expanded.Results.Count == 512 && more.Results.Count == 1024 && more.HasMore,
            "Progressive results capped or falsely complete");
        Check(calls.Last().Filter == "File" && more.FileMatchCount == 1600, "Provider filter/count lost");
        var favoritePath = Path.Combine(AppDataPaths.DirectoryPath, "report.pdf favorite.pdf");
        File.WriteAllText(favoritePath, "test");
        coordinator.ToggleFavorite(new LauncherResult { Title = "report.pdf favorite.pdf", Target = favoritePath,
            Subtitle = AppDataPaths.DirectoryPath, Kind = LauncherResultKind.File, Score = 1 });
        coordinator.Configure(new AppSettings { ResultSort = "Usage", EverythingLifecycle = "Connect" });
        var withFavorite = await coordinator.SearchAsync("report.pdf", 64, CancellationToken.None, "File");
        Check(withFavorite.Results.Any(item => item.Target == favoritePath && item.IsFavorite), "Favorite outside provider prefix lost");
        await coordinator.SearchAsync("https://example.com", 64, CancellationToken.None, "Folder");
        Check(calls.Last().Filter == "Folder", "Explicit URL bypassed type filter");
        var before = calls.Count;
        await coordinator.SearchAsync("report.pdf", 64, CancellationToken.None, "Application");
        Check(calls.Count == before, "Application filter queried file provider");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await coordinator.SearchAsync("=1+2", 64, cancellation.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        foreach (var (mode, _) in ResultRanker.Options)
        {
            using var sorted = new SearchCoordinator((query, limit, token, filter, sort) =>
            {
                Check(sort == mode, "Coordinator lost sort mode");
                if (ResultRanker.IsProviderSort(mode)) Check(limit is 64 or 512 or 1024, "Explicit sort overfetch");
                return Task.FromResult(new EverythingSearchResponse(Enumerable.Range(0, limit).Select(i =>
                    new LauncherResult { Title = $"provider-{limit - i}", Target = $"fake-{i}", Subtitle = "", Kind = LauncherResultKind.File, Score = 0 }).ToArray(), true, "fake", 2000, true));
            });
            sorted.Configure(new AppSettings { ResultSort = mode, EverythingLifecycle = "Connect" });
            foreach (var limit in new[] { 64, 512, 1024 })
            foreach (var filter in new[] { "File", "Folder", "All" })
            {
                var batch = await sorted.SearchAsync("ext:pdf", limit, CancellationToken.None, filter);
                if (ResultRanker.IsProviderSort(mode))
                    Check(batch.Results.Select(item => item.Target).SequenceEqual(Enumerable.Range(0, limit).Select(i => $"fake-{i}")), "Provider order changed after recall");
            }
        }
        var unknown = new[] { new LauncherResult { Title = "Zulu", Target = "app", Subtitle = "", Kind = LauncherResultKind.Application, Score = 999 },
            new LauncherResult { Title = "Alpha", Target = "file", Subtitle = "", Kind = LauncherResultKind.File, Score = 0, ProviderOrder = 0 } };
        foreach (var mode in new[] { ResultRanker.SizeAscending, ResultRanker.SizeDescending, ResultRanker.ModifiedNewest, ResultRanker.ModifiedOldest })
            Check(ResultRanker.Rank(unknown, mode, 2, _ => 0)[0].Target == "file", "Unknown metadata must follow provider results in both directions");
        Console.WriteLine("PASS provider routing, all 9 sort modes, 64/512/1024 loading, persistence, totals and cancellation");
    }

    private static void Check(bool passed, string message)
    { if (!passed) throw new InvalidOperationException(message); }
}
