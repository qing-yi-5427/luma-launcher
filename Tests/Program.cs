using LumaLauncher.Services;

var exact = FuzzyMatcher.Score("notepad", "Notepad", string.Empty);
var fuzzy = FuzzyMatcher.Score("ntpd", "Notepad", string.Empty);
var miss = FuzzyMatcher.Score("zzzzzz", "Notepad", string.Empty);
Require(exact > fuzzy, "Exact match should outrank fuzzy match.");
Require(!double.IsNegativeInfinity(fuzzy), "Subsequence match should be accepted.");
Require(double.IsNegativeInfinity(miss), "Unrelated text should not match.");

var apps = new AppIndexService();
await apps.ReloadAsync();
Require(apps.Count > 0, "Application index should not be empty.");

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

if (args.Contains("--lifecycle", StringComparer.OrdinalIgnoreCase))
{
    everything.ShutdownClient();
    await Task.Delay(700);
    using var restartedEverything = new EverythingSearchService();
    restartedEverything.Configure("Auto", string.Empty);
    Require(await restartedEverything.EnsureRunningAsync(), "Everything should restart after a lifecycle shutdown.");
}

Console.WriteLine($"PASS apps={apps.Count} everything={everythingResult.Results.Count} combined={combined.Results.Count}");
return;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
