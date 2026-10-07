using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

/// <summary>Measures the real shown WPF input/results/glass path without global keyboard injection.</summary>
internal static class InputLatencyTests
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { ProbeOnStaThread(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Shown-window input latency probe failed.", failure);
    }

    private static void ProbeOnStaThread()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var app = new App();
        app.InitializeComponent();
        ThemeService.Apply(ThemeService.AppleDark);
        var store = new SettingsStore();
        store.Save(new AppSettings { Theme = ThemeService.AppleDark, EverythingLifecycle = "Connect",
            EnableWindowSwitcher = false, EnableBookmarks = false, EnableClipboardHistory = false });
        var icon = new DrawingImage();
        icon.Freeze();
        var search = new SearchCoordinator((_, _, _, _, _) =>
            Task.FromResult(new EverythingSearchResponse([], true, "isolated")));
        search.TestApplicationQuery = (context, _) => Task.FromResult<IReadOnlyList<LauncherResult>>(
            Enumerable.Range(0, 64).Select(index => new LauncherResult
            {
                Title = $"Probe application {index:00}", Subtitle = context.Query,
                Target = $"probe-application-{index:00}", Kind = LauncherResultKind.Application,
                Score = 2000 - index, Icon = icon
            }).ToArray());
        var work = SystemParameters.WorkArea;
        var window = new MainWindow(store, true, search)
        {
            ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = work.Left + 30, Top = work.Top + 30
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            var glass = typeof(MainWindow).GetField("_glass", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window) as WindowsGlassService;
            if (glass is null || !glass.IsAcrylicActive)
                throw new InvalidOperationException("Shown launcher did not initialize its Windows glass host.");
            var input = (TextBox)window.FindName("SearchBox");
            var list = (ListBox)window.FindName("ResultsList");
            var root = (FrameworkElement)window.FindName("LauncherRoot");
            input.Text = "perfprobe";
            Until(() => list.Items.Count == 8 && !Pending(window), TimeSpan.FromSeconds(4));
            Pump(TimeSpan.FromMilliseconds(450));
            AssertProgressBounds(window);
            MeasureScenario(dispatcher, window, glass, input, list, root, "default", 8);
            typeof(MainWindow).GetMethod("EnterFullResultsMode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            Until(() => list.Items.Count == 64 && !Pending(window), TimeSpan.FromSeconds(4));
            Pump(TimeSpan.FromMilliseconds(450));
            AssertProgressBounds(window);
            MeasureScenario(dispatcher, window, glass, input, list, root, "full", 64);
        }
        finally
        {
            window.CloseForExit();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

    }

    private static void MeasureScenario(Dispatcher dispatcher, MainWindow window, WindowsGlassService glass,
        TextBox input, ListBox list, FrameworkElement root, string scenario, int visibleResults)
    {
        int layouts = 0;
        EventHandler onLayout = (_, _) => layouts++;
        root.LayoutUpdated += onLayout;
        var syncTimes = new List<double>();
        var heartbeatGaps = new List<double>();
        var watch = Stopwatch.StartNew();
        var lastBeat = watch.Elapsed.TotalMilliseconds;
        int scansBefore = glass.RegionScanCount;
        int updatesBefore = glass.ShapeUpdateCount;
        var heartbeat = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        { Interval = TimeSpan.FromMilliseconds(10) };
        heartbeat.Tick += (_, _) =>
        {
            var now = watch.Elapsed.TotalMilliseconds;
            heartbeatGaps.Add(now - lastBeat);
            lastBeat = now;
        };
        var inputTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        { Interval = TimeSpan.FromMilliseconds(45) };
        var frame = new DispatcherFrame();
        int edits = 0;
        Exception? tickFailure = null;
        inputTimer.Tick += (_, _) =>
        {
            try
            {
                var start = Stopwatch.GetTimestamp();
                input.Text = ++edits % 2 == 0 ? "perfprobe" : "perfprobex";
                syncTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            catch (Exception exception) { tickFailure = exception; }
            if (edits < 80 && tickFailure is null) return;
            inputTimer.Stop();
            frame.Continue = false;
        };
        try
        {
            heartbeat.Start();
            inputTimer.Start();
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            inputTimer.Stop();
            heartbeat.Stop();
            root.LayoutUpdated -= onLayout;
        }
        if (tickFailure is not null) throw tickFailure;
        Until(() => !Pending(window) && list.Items.Count == visibleResults, TimeSpan.FromSeconds(4));
        double syncP95 = Percentile(syncTimes, .95);
        double heartbeatP95 = Percentile(heartbeatGaps, .95);
        double heartbeatMax = heartbeatGaps.Max();
        int scans = glass.RegionScanCount - scansBefore;
        int updates = glass.ShapeUpdateCount - updatesBefore;
        Console.WriteLine($"INPUT scenario={scenario} shown=true acrylic=true results={visibleResults} edits={edits} " +
            $"textchanged_sync_p95_ms={syncP95:F2} heartbeat_p95_gap_ms={heartbeatP95:F2} " +
            $"heartbeat_max_gap_ms={heartbeatMax:F2} layout_events={layouts} " +
            $"glass_scans={scans} glass_shape_updates={updates}");
        if (syncP95 >= 16 || heartbeatP95 >= 45 || heartbeatMax >= 120)
            throw new InvalidOperationException($"{scenario} input dispatcher remains visibly stalled; inspect metrics.");
    }

    private static void AssertProgressBounds(MainWindow window)
    {
        var host = (FrameworkElement)window.FindName("ProgressHost");
        var pulse = (FrameworkElement)window.FindName("ProgressPulse");
        var surface = (FrameworkElement)window.FindName("ResultsSurface");
        var transform = (TranslateTransform)window.FindName("ProgressPulseTransform");
        var originalVisibility = host.Visibility;
        var originalX = transform.X;
        try
        {
            host.Visibility = Visibility.Visible;
            window.UpdateLayout();
            var hostBounds = host.TransformToAncestor(window)
                .TransformBounds(new Rect(0, 0, host.ActualWidth, host.ActualHeight));
            var surfaceBounds = surface.TransformToAncestor(window)
                .TransformBounds(new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
            if (hostBounds.Left < surfaceBounds.Left || hostBounds.Right > surfaceBounds.Right || !host.ClipToBounds)
                throw new InvalidOperationException("Progress animation is not clipped inside the results surface.");
            transform.X = -pulse.ActualWidth;
            if (!host.ClipToBounds) throw new InvalidOperationException("Left overflow of progress pulse is not clipped.");
            transform.X = host.ActualWidth + pulse.ActualWidth;
            if (!host.ClipToBounds) throw new InvalidOperationException("Right overflow of progress pulse is not clipped.");
        }
        finally
        {
            transform.X = originalX;
            host.Visibility = originalVisibility;
        }
    }

    private static bool Pending(MainWindow window) =>
        (bool)typeof(MainWindow).GetField("_searchPending", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static double Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0) throw new InvalidOperationException("Latency probe collected no samples.");
        values.Sort();
        return values[(int)Math.Ceiling(values.Count * fraction) - 1];
    }

    private static void Until(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException("Shown-window latency fixture did not settle.");
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
