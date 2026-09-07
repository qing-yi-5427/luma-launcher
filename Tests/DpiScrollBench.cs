using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

/// <summary>
/// Measures layout/render cost of the results list at multiple DPI scales and
/// simulates scrolling under virtualization. Invoked via `--dpi-scroll`.
/// </summary>
internal static class DpiScrollBench
{
    private const int ResultCount = 512;
    private const int ScrollSteps = 40;

    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunOnSta();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("DPI/scroll bench failed: " + failure, failure);
    }

    private static void RunOnSta()
    {
        var app = new App();
        app.InitializeComponent();

        Console.WriteLine($"PROCESS_DPI_AWARENESS={DescribeProcessDpiAwareness()}");
        Console.WriteLine($"SYSTEM_DPI={GetDpiForSystem()}");

        var scales = new[] { 1.0, 1.25, 1.5, 2.0 };
        var rows = new List<string>(scales.Length + 2);
        rows.Add("scale,measure_first_ms,p95_measure_ms,render_first_ms,p95_render_ms,scroll_p95_ms,scroll_avg_ms");

        foreach (var scale in scales)
        {
            var metrics = MeasureAtScale(scale);
            rows.Add($"{scale:0.##},{metrics.MeasureFirst:F2},{metrics.MeasureP95:F2},{metrics.RenderFirst:F2},{metrics.RenderP95:F2},{metrics.ScrollP95:F2},{metrics.ScrollAvg:F2}");
            Console.WriteLine($"DPI {scale * 100:0}%  measure_first={metrics.MeasureFirst:F2}ms p95={metrics.MeasureP95:F2}ms  render_first={metrics.RenderFirst:F2}ms p95={metrics.RenderP95:F2}ms  scroll_p95={metrics.ScrollP95:F2}ms avg={metrics.ScrollAvg:F2}ms");
        }

        var output = Path.Combine(AppContext.BaseDirectory, "dpi-scroll-report.csv");
        File.WriteAllLines(output, rows);
        Console.WriteLine($"PASS dpi/scroll bench -> {output}");
    }

    private sealed record Metrics(
        double MeasureFirst, double MeasureP95,
        double RenderFirst, double RenderP95,
        double ScrollP95, double ScrollAvg);

    private static Metrics MeasureAtScale(double scale)
    {
        var settings = new SettingsStore();
        var main = new MainWindow(settings, previewMode: true)
        {
            Width = 700,
            Height = 520,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        try
        {
            ThemeService.Apply("InkTeal");
            // Force results chrome visible without invoking search providers.
            ((RowDefinition)main.FindName("ResultsRow")!).Height = new GridLength(1, GridUnitType.Star);
            ((RowDefinition)main.FindName("FooterRow")!).Height = new GridLength(36);
            ((FrameworkElement)main.FindName("ResultsHost")!).Visibility = Visibility.Visible;
            ((FrameworkElement)main.FindName("Footer")!).Visibility = Visibility.Visible;
            ((FrameworkElement)main.FindName("EmptyState")!).Visibility = Visibility.Collapsed;

            var list = (ListBox)main.FindName("ResultsList")!;
            list.ItemsSource = CreateResults(ResultCount);
            list.SelectedIndex = 0;

            // Offscreen measure/arrange at the requested DPI (96 * scale).
            var dpi = 96.0 * scale;
            var measureTimes = new List<double>(20);
            var renderTimes = new List<double>(20);

            for (var i = 0; i < 20; i++)
            {
                var sw = Stopwatch.StartNew();
                list.Measure(new Size(680, 420));
                list.Arrange(new Rect(0, 0, 680, 420));
                list.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                sw.Stop();
                measureTimes.Add(sw.Elapsed.TotalMilliseconds);

                sw.Restart();
                var bitmap = new RenderTargetBitmap(700, 520, dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(main);
                sw.Stop();
                renderTimes.Add(sw.Elapsed.TotalMilliseconds);
            }

            // Scrolling: drive ScrollViewer offset through a page of virtualized rows.
            var scrollViewer = FindScrollViewer(list);
            var scrollTimes = new List<double>(ScrollSteps);
            if (scrollViewer is not null)
            {
                scrollViewer.ScrollToHome();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                for (var i = 0; i < ScrollSteps; i++)
                {
                    var sw = Stopwatch.StartNew();
                    scrollViewer.LineDown();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    list.UpdateLayout();
                    sw.Stop();
                    scrollTimes.Add(sw.Elapsed.TotalMilliseconds);
                }
            }

            return new Metrics(
                measureTimes[0],
                Percentile(measureTimes, 0.95),
                renderTimes[0],
                Percentile(renderTimes, 0.95),
                Percentile(scrollTimes, 0.95),
                scrollTimes.Count == 0 ? 0 : scrollTimes.Average());
        }
        finally
        {
            main.CloseForExit();
            main.Close();
        }
    }

    private static LauncherResult[] CreateResults(int count) =>
        Enumerable.Range(1, count).Select(i => new LauncherResult
        {
            Title = $"report-{i:D4}-quarterly-summary.pdf",
            Subtitle = $@"C:\Users\demo\Documents\Archive\2026\Q{(i % 4) + 1}\report-{i:D4}.pdf",
            Target = $@"C:\Users\demo\Documents\Archive\report-{i:D4}.pdf",
            Kind = i % 7 == 0 ? LauncherResultKind.Application : LauncherResultKind.File,
            Score = count - i
        }).ToArray();

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
            return viewer;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindScrollViewer(child);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static uint GetDpiForSystem()
    {
        try { return GetDpiForSystemNative(); }
        catch { return 96; }
    }

    private static string DescribeProcessDpiAwareness()
    {
        try
        {
            var handle = Process.GetCurrentProcess().Handle;
            if (GetProcessDpiAwareness(handle, out var awareness) == 0)
                return awareness.ToString();
        }
        catch { }
        return "unknown";
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystemNative();

    [DllImport("shcore.dll")]
    private static extern int GetProcessDpiAwareness(IntPtr handle, out int awareness);
}
