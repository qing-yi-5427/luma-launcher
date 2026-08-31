using LumaLauncher.Services;
using LumaLauncher.Models;
using System.Diagnostics;

var exact = FuzzyMatcher.Score("notepad", "Notepad", string.Empty);
var fuzzy = FuzzyMatcher.Score("ntpd", "Notepad", string.Empty);
var miss = FuzzyMatcher.Score("zzzzzz", "Notepad", string.Empty);
Require(exact > fuzzy, "Exact match should outrank fuzzy match.");
Require(!double.IsNegativeInfinity(fuzzy), "Subsequence match should be accepted.");
Require(double.IsNegativeInfinity(miss), "Unrelated text should not match.");
var prepared = FuzzyMatcher.Prepare("ntpd");
Require(FuzzyMatcher.Score(prepared, FuzzyMatcher.PrepareCandidate("Notepad"), string.Empty) == fuzzy,
    "Prepared fuzzy matching should preserve ranking behavior.");
Require(!double.IsNegativeInfinity(FuzzyMatcher.Score("wx", "微信", string.Empty)),
    "Chinese application names should match their Pinyin initials.");

using (var builtIns = new SearchCoordinator())
{
    builtIns.Configure(new AppSettings
    {
        CustomCommands = "note|新建记事|notepad.exe|{query}|"
    });
    var calculation = await builtIns.SearchAsync("= (12 + 8) * 3", 8, CancellationToken.None);
    Require(calculation.Results.FirstOrDefault()?.Kind == LauncherResultKind.Calculation &&
            calculation.Results[0].CopyText == "60", "Calculator queries should return a copyable result.");
    var website = await builtIns.SearchAsync("example.com", 8, CancellationToken.None);
    Require(website.Results.FirstOrDefault()?.Kind == LauncherResultKind.Web,
        "Domain names should return a web result.");
    var command = await builtIns.SearchAsync("note roadmap", 8, CancellationToken.None);
    Require(command.Results.FirstOrDefault()?.Kind == LauncherResultKind.Command &&
            command.Results[0].Arguments == "roadmap", "Custom command placeholders should receive query arguments.");
}

var apps = new AppIndexService();
await apps.InitializeAsync();
Require(apps.Count > 0, "Application index should not be empty.");
var indexTimer = Stopwatch.StartNew();
await apps.ReloadAsync();
indexTimer.Stop();
var cachedApps = new AppIndexService();
await cachedApps.InitializeAsync();
Require(cachedApps.IsReady && cachedApps.Count == apps.Count, "Application cache should be reusable.");

using var everything = new EverythingSearchService();
var detectedEverything = EverythingSearchService.FindExecutable();
Require(detectedEverything is not null, "Everything executable should be detected automatically.");
Require(EverythingSearchService.FindExecutable("Manual", @"Z:\missing\Everything.exe") is null,
    "An invalid manual Everything path should be rejected.");
everything.Configure("Auto", string.Empty);
Require(await everything.EnsureRunningAsync(), "Everything should be available during launcher startup.");
var everythingResult = await everything.SearchAsync("Windows", 5, CancellationToken.None);
Require(everythingResult.Available, $"Everything IPC should be available: {everythingResult.StatusText}");
Require(everythingResult.Results.Count > 0, "Everything should return at least one Windows result.");

using var coordinator = new SearchCoordinator();
await coordinator.InitializeAsync();
var combined = await coordinator.SearchAsync("Windows", 8, CancellationToken.None);
Require(combined.Results.Count > 0, "Combined search should return results.");
var syntax = await coordinator.SearchAsync("ext:exe", 8, CancellationToken.None);
Require(syntax.EverythingAvailable && syntax.Results.Count > 0, "Everything syntax should not be removed by fuzzy filtering.");
var expanded = await coordinator.SearchAsync("exe", 64, CancellationToken.None);
Require(expanded.Results.Count > 8, "Expanded searches should provide enough results for paging and filters.");

var latencies = new List<long>();
foreach (var query in new[] { "win", "windows", "note", "exe", "program", "system" })
{
    var timer = Stopwatch.StartNew();
    var result = await coordinator.SearchAsync(query, 8, CancellationToken.None);
    timer.Stop();
    Require(result.Results.Count > 0, $"Warm query '{query}' should return results.");
    latencies.Add(timer.ElapsedMilliseconds);
}
latencies.Sort();
var p95 = latencies[(int)Math.Ceiling(latencies.Count * 0.95) - 1];
Require(p95 < 1000, $"Warm search p95 should stay below 1000ms, actual {p95}ms.");

if (args.Contains("--lifecycle", StringComparer.OrdinalIgnoreCase))
{
    everything.ShutdownClient();
    await Task.Delay(700);
    using var restartedEverything = new EverythingSearchService();
    restartedEverything.Configure("Auto", string.Empty);
    Require(await restartedEverything.EnsureRunningAsync(), "Everything should restart after a lifecycle shutdown.");
}

// Regression: tray right-click menu must build and lay out (see TrayMenuTests doc comment).
LumaLauncher.Tests.TrayMenuTests.Run();

Console.WriteLine($"PASS apps={apps.Count} index_ms={indexTimer.ElapsedMilliseconds} everything={everythingResult.Results.Count} combined={combined.Results.Count} search_p95_ms={p95}");
return;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
