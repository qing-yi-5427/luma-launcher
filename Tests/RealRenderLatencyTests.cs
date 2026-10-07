using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

/// <summary>Visible, native-glass baseline with the actual application and file providers.</summary>
internal static class RealRenderLatencyTests
{
    private static readonly (string Text, int NextDelayMs)[] Edits =
    [
        ("s", 110), ("ss", 110), ("sss", 110), ("ssss", 260),
        ("sss", 110), ("ss", 110), ("s", 260),
        ("c", 90), ("ch", 90), ("chr", 90), ("chro", 90),
        ("chrom", 90), ("chrome", 320), ("chrom", 90),
        ("chro", 90), ("chrome", 300)
    ];

    internal static void Run(bool minimalProbes = false)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { ProbeOnStaThread(minimalProbes); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Real render latency probe failed.", failure);
    }

    private static void ProbeOnStaThread(bool minimalProbes)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var app = new App();
        app.InitializeComponent();
        // Same defaults as the interactive preview, with the configured
        // Everything instance connected and all test data under temp AppData.
        var store = new SettingsStore();
        store.Save(new AppSettings
        {
            EverythingLifecycle = "Connect", StartWithWindows = false,
            RecordHistory = false, RecordQueryHistory = false, EnableClipboardHistory = false
        });
        ThemeService.Apply(store.Current.Theme);
        var search = new SearchCoordinator();
        search.Configure(store.Current);
        using var initializeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var initialize = search.InitializeAsync(initializeTimeout.Token);
        Until(() => initialize.IsCompleted, TimeSpan.FromSeconds(21));
        initialize.GetAwaiter().GetResult();
        Console.WriteLine($"RENDER provider_index_ready=true indexed_apps={GetIndexedAppCount(search)} " +
            $"theme={store.Current.Theme} effective_theme={ThemeService.ResolveEffectiveTheme(store.Current.Theme)} " +
            $"render_tier={RenderCapability.Tier >> 16} language={store.Current.Language} " +
            $"window_switcher={store.Current.EnableWindowSwitcher} everything={store.Current.EverythingLifecycle} " +
            $"mode={(minimalProbes ? "minimal" : "full")}");

        var work = SystemParameters.WorkArea;
        var window = new MainWindow(store, true, search)
        {
            ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = work.Left + 40, Top = work.Top + 40
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(window);
            Console.WriteLine($"RENDER dpi_x={dpi.DpiScaleX:F2} dpi_y={dpi.DpiScaleY:F2} " +
                $"high_contrast={SystemParameters.HighContrast}");
            var glass = typeof(MainWindow).GetField("_glass", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window) as WindowsGlassService;
            if (glass is null || !glass.IsAcrylicActive)
                throw new InvalidOperationException("Real render probe did not attach native glass.");
            var input = (TextBox)window.FindName("SearchBox");
            var resultsList = (ListBox)window.FindName("ResultsList");
            var resultsHost = (FrameworkElement)window.FindName("ResultsHost");
            var root = (FrameworkElement)window.FindName("LauncherRoot");
            var visibleResults = (ObservableCollection<LauncherResult>)typeof(MainWindow)
                .GetField("_results", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Pump(TimeSpan.FromMilliseconds(180));
            Measure("cold", dispatcher, window, input, root, resultsHost, resultsList, visibleResults, glass, minimalProbes);
            Pump(TimeSpan.FromMilliseconds(400));
            Measure("warm", dispatcher, window, input, root, resultsHost, resultsList, visibleResults, glass, minimalProbes);
        }
        finally
        {
            window.CloseForExit();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static int GetIndexedAppCount(SearchCoordinator search)
    {
        // AppIndexService.Count is internal to the coordinator; this is only
        // diagnostic and does not enumerate or open an application.
        var apps = typeof(SearchCoordinator).GetField("_apps", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(search)!;
        return (int)apps.GetType().GetProperty("Count")!.GetValue(apps)!;
    }

    private static void Measure(string phase, Dispatcher dispatcher, MainWindow window, TextBox input,
        FrameworkElement root, FrameworkElement resultsHost, ListBox resultsList,
        ObservableCollection<LauncherResult> visibleResults, WindowsGlassService glass, bool minimalProbes)
    {
        int layouts = 0, windowResizes = 0, resets = 0, adds = 0, hostOpacityDrops = 0, listOpacityDrops = 0;
        var syncTimes = new List<double>();
        var frameGaps = new List<double>();
        var inputToRendering = new List<double>();
        var heartbeatGaps = new List<double>();
        var postedInputQueueDelays = new List<double>();
        var dispatcherTimes = new Dictionary<DispatcherPriority, List<double>>();
        var startedOperations = new Dictionary<DispatcherOperation, long>();
        var pendingInput = new List<double>();
        var watch = Stopwatch.StartNew();
        int postProbeRunning = 1, postProbePending = 0;
        var postProbe = new Thread(() =>
        {
            // One outstanding input-priority callback: this measures the real
            // dispatcher queue without WM_TIMER coalescing or probe buildup.
            while (Volatile.Read(ref postProbeRunning) != 0)
            {
                if (Interlocked.CompareExchange(ref postProbePending, 1, 0) == 0)
                {
                    double postedAt = watch.Elapsed.TotalMilliseconds;
                    dispatcher.BeginInvoke(() =>
                    {
                        if (Volatile.Read(ref postProbeRunning) != 0)
                            postedInputQueueDelays.Add(watch.Elapsed.TotalMilliseconds - postedAt);
                        Interlocked.Exchange(ref postProbePending, 0);
                    }, DispatcherPriority.Input);
                }
                Thread.Sleep(10);
            }
        }) { IsBackground = true, Name = "Luma input queue probe" };
        double lastFrame = 0, lastBeat = 0;
        double lastHostOpacity = resultsHost.Opacity, lastListOpacity = resultsList.Opacity;
        int scansBefore = glass.RegionScanCount, shapesBefore = glass.ShapeUpdateCount;
        EventHandler onLayout = (_, _) => layouts++;
        SizeChangedEventHandler onWindowSize = (_, _) => windowResizes++;
        NotifyCollectionChangedEventHandler onCollection = (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset) resets++;
            if (args.Action == NotifyCollectionChangedAction.Add) adds += args.NewItems?.Count ?? 0;
        };
        EventHandler onRendering = (_, _) =>
        {
            double now = watch.Elapsed.TotalMilliseconds;
            if (lastFrame > 0 && now - lastFrame > 1) frameGaps.Add(now - lastFrame);
            lastFrame = now;
            foreach (double at in pendingInput) inputToRendering.Add(now - at);
            pendingInput.Clear();
            double hostOpacity = resultsHost.Opacity, listOpacity = resultsList.Opacity;
            if (hostOpacity < lastHostOpacity - .2) hostOpacityDrops++;
            if (listOpacity < lastListOpacity - .2) listOpacityDrops++;
            lastHostOpacity = hostOpacity;
            lastListOpacity = listOpacity;
        };
        DispatcherHookEventHandler onOperationStarted = (_, args) =>
            startedOperations[args.Operation] = Stopwatch.GetTimestamp();
        DispatcherHookEventHandler onOperationCompleted = (_, args) =>
        {
            if (!startedOperations.Remove(args.Operation, out var started)) return;
            var priority = args.Operation.Priority;
            if (!dispatcherTimes.TryGetValue(priority, out var times))
                dispatcherTimes[priority] = times = [];
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        };
        var heartbeat = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        { Interval = TimeSpan.FromMilliseconds(10) };
        heartbeat.Tick += (_, _) =>
        {
            double now = watch.Elapsed.TotalMilliseconds;
            if (lastBeat > 0) heartbeatGaps.Add(now - lastBeat);
            lastBeat = now;
        };
        var inputTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        { Interval = TimeSpan.FromMilliseconds(90) };
        var frame = new DispatcherFrame();
        int edit = 0;
        Exception? tickFailure = null;
        inputTimer.Tick += (_, _) =>
        {
            try
            {
                var step = Edits[edit++];
                double at = watch.Elapsed.TotalMilliseconds;
                pendingInput.Add(at);
                long started = Stopwatch.GetTimestamp();
                input.Text = step.Text;
                syncTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                inputTimer.Interval = TimeSpan.FromMilliseconds(step.NextDelayMs);
            }
            catch (Exception exception) { tickFailure = exception; }
            if (edit < Edits.Length && tickFailure is null) return;
            inputTimer.Stop();
            var tail = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            { Interval = TimeSpan.FromMilliseconds(700) };
            tail.Tick += (_, _) => { tail.Stop(); frame.Continue = false; };
            tail.Start();
        };
        root.LayoutUpdated += onLayout;
        window.SizeChanged += onWindowSize;
        visibleResults.CollectionChanged += onCollection;
        if (!minimalProbes)
        {
            CompositionTarget.Rendering += onRendering;
            dispatcher.Hooks.OperationStarted += onOperationStarted;
            dispatcher.Hooks.OperationCompleted += onOperationCompleted;
        }
        try
        {
            if (!minimalProbes) heartbeat.Start();
            postProbe.Start();
            inputTimer.Start();
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            inputTimer.Stop();
            heartbeat.Stop();
            Volatile.Write(ref postProbeRunning, 0);
            if (postProbe.IsAlive) postProbe.Join(TimeSpan.FromMilliseconds(100));
            if (!minimalProbes)
            {
                CompositionTarget.Rendering -= onRendering;
                dispatcher.Hooks.OperationStarted -= onOperationStarted;
                dispatcher.Hooks.OperationCompleted -= onOperationCompleted;
            }
            root.LayoutUpdated -= onLayout;
            window.SizeChanged -= onWindowSize;
            visibleResults.CollectionChanged -= onCollection;
        }
        if (tickFailure is not null) throw tickFailure;
        var allResults = (List<LauncherResult>)typeof(MainWindow)
            .GetField("_allResults", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Console.WriteLine($"RENDER phase={phase} mode={(minimalProbes ? "minimal" : "full")} " +
            $"edits={edit} visible={visibleResults.Count} all={allResults.Count} " +
            $"textchanged_sync_p95_ms={P95(syncTimes):F2} " +
            $"render_callback_p95_gap_ms={P95(frameGaps):F2} render_callback_max_gap_ms={Max(frameGaps):F2} " +
            $"input_to_next_render_callback_p95_ms={P95(inputToRendering):F2} " +
            $"heartbeat_p95_gap_ms={P95(heartbeatGaps):F2} heartbeat_max_gap_ms={Max(heartbeatGaps):F2} " +
            $"posted_input_samples={postedInputQueueDelays.Count} " +
            $"posted_input_queue_p95_ms={P95(postedInputQueueDelays):F2} " +
            $"posted_input_queue_max_ms={Max(postedInputQueueDelays):F2} " +
            $"result_resets={resets} result_adds={adds} host_opacity_drops={hostOpacityDrops} " +
            $"list_opacity_drops={listOpacityDrops} layout_events={layouts} window_resizes={windowResizes} " +
            $"glass_scans={glass.RegionScanCount - scansBefore} glass_shape_updates={glass.ShapeUpdateCount - shapesBefore}");
        if (minimalProbes)
        {
            if (windowResizes > 64)
                throw new InvalidOperationException($"Native window resized {windowResizes} times during {phase} input.");
            if (P95(postedInputQueueDelays) > 100)
                throw new InvalidOperationException($"Input queue P95 exceeded 100 ms during {phase} input.");
        }
        foreach (var (priority, times) in dispatcherTimes.OrderByDescending(item => item.Value.Sum()))
            Console.WriteLine($"RENDER dispatch phase={phase} priority={priority} count={times.Count} " +
                $"total_ms={times.Sum():F1} p95_ms={P95(times):F2} max_ms={Max(times):F2}");
    }

    private static double P95(List<double> values)
    {
        if (values.Count == 0) return double.NaN;
        values.Sort();
        return values[(int)Math.Ceiling(values.Count * .95) - 1];
    }

    private static double Max(List<double> values) => values.Count == 0 ? double.NaN : values.Max();

    private static void Until(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException("Real provider initialization did not finish.");
            Pump(TimeSpan.FromMilliseconds(10));
        }
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
