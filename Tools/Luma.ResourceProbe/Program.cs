using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LumaLauncher;
using LumaLauncher.Models;
using LumaLauncher.Services;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var assembly = typeof(MainWindow).Assembly;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var data = Directory.CreateTempSubdirectory("Luma.ResourceProbe.").FullName;
        assembly.GetType("LumaLauncher.Services.AppDataPaths")!.GetProperty("DirectoryPath", flags)!.SetValue(null, data);
        typeof(App).GetProperty("IsTestHost", flags)!.SetValue(null, true);
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var task = RunAsync(args.FirstOrDefault() ?? "sample");
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string label)
    {
        var store = new SettingsStore();
        // Deserialize so the same probe binary/source works against main and mimo.
        store.Save(JsonSerializer.Deserialize<AppSettings>("""
            {"EverythingLifecycle":"Connect","RecordHistory":false,"RecordQueryHistory":false,
             "EnableWindowSwitcher":false,"EnableBookmarks":false,"EnablePreview":false,
             "EnableGameMode":false,"EnableClipboardHistory":false,"ShowOnboarding":false,"Theme":"Paper"}
            """)!);
        var window = new MainWindow(store, true);
        var search = (SearchCoordinator)typeof(MainWindow).GetField("_search", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        search.Configure(store.Current);
        await search.InitializeAsync();
        var input = (TextBox)window.FindName("SearchBox");
        var results = (ListBox)window.FindName("ResultsList");
        var completed = typeof(MainWindow).GetField("_completedQuery", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var samples = new List<object>();
        using var process = Process.GetCurrentProcess();
        async Task Sample(string stage)
        {
            var cpu = process.TotalProcessorTime.TotalMilliseconds;
            await Task.Delay(3000);
            process.Refresh();
            samples.Add(new { stage, workingSetMiB = Math.Round(process.WorkingSet64 / 1048576d, 2),
                privateMiB = Math.Round(process.PrivateMemorySize64 / 1048576d, 2),
                gcHeapAfterLastCollectionMiB = Math.Round(GC.GetGCMemoryInfo().HeapSizeBytes / 1048576d, 2), handles = process.HandleCount,
                threads = process.Threads.Count, idleCpuMsOver3s = Math.Round(process.TotalProcessorTime.TotalMilliseconds - cpu, 2) });
        }
        await Task.Delay(5000);
        await Sample("initialized-hidden");
        var latencies = new List<double>();
        try
        {
            // Offscreen logical wake/search/hide cycles, with real app index and
            // Everything Connect-only. No native Show/Activate or shell execution.
            for (var block = 0; block < 3; block++)
            {
                for (var cycle = 0; cycle < 20; cycle++)
                {
                    var query = new[] { "notepad", "windows", "program", "system" }[cycle % 4];
                    var timer = Stopwatch.StartNew();
                    input.Text = query;
                    while (!Equals(completed.GetValue(window), query))
                    {
                        if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Query failed: " + query);
                        await Task.Delay(2);
                    }
                    latencies.Add(timer.Elapsed.TotalMilliseconds);
                    var root = (FrameworkElement)window.Content;
                    root.Measure(new Size(700, 600));
                    root.Arrange(new Rect(0, 0, 700, 600));
                    root.UpdateLayout();
                    await Task.WhenAll(results.Items.Cast<LauncherResult>().Take(8).Select(async item =>
                        item.Icon = await search.LoadIconAsync(item, CancellationToken.None)));
                    window.HideLauncher();
                }
                await Task.Delay(6000);
                await Sample($"after-{(block + 1) * 20}-cycles");
            }
            // Distinguish delayed native/dispatcher cleanup from steady growth.
            await Task.Delay(30000);
            await Sample("settled-30s");
            latencies.Sort();
            Console.WriteLine(JsonSerializer.Serialize(new { label, runtime = Environment.Version.ToString(),
                frameworkDependent = true, visibleWindow = false, forcedGc = false, cycles = 60,
                inputToFinalP95Ms = Math.Round(latencies[(int)Math.Ceiling(latencies.Count * .95) - 1], 2), samples },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.CloseForExit(); }
    }
}
