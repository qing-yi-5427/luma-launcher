using System.Threading;
using System.Windows.Threading;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

internal static class PreviewHarness
{
    internal static void Render()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var output = System.IO.Path.Combine(AppContext.BaseDirectory, "renders");
                System.IO.Directory.CreateDirectory(output);
                foreach (var theme in new[] { "Dark", "Light" })
                {
                    ThemeService.Apply(theme);
                    var settings = new SettingsWindow(new AppSettings { Theme = theme, EverythingLifecycle = "Connect" });
                    Save((System.Windows.FrameworkElement)settings.Content, 540, 760, 1, $"settings-{theme}.png");
                    Save((System.Windows.FrameworkElement)settings.Content, 400, 360, 2, $"settings-{theme}-200pct.png");
                    var main = new MainWindow(new SettingsStore(), true);
                    main.ChangeSort(ResultRanker.SizeDescending);
                    ThemeService.Apply(theme);
                    var menu = main.CreateSortMenu();
                    Save(menu, 240, 350, 1, $"sort-menu-{theme}.png");
                    var trayMenu = TrayIconService.BuildMenu(() => { }, () => { }, () => Task.CompletedTask, () => { }, "Alt+Space", out _);
                    Save(trayMenu, 240, 250, 2, $"tray-menu-{theme}.png");
                    ((System.Windows.Controls.Grid)main.FindName("ResultsHost")).Visibility = System.Windows.Visibility.Visible;
                    ((System.Windows.Controls.RowDefinition)main.FindName("ResultsRow")).Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
                    ((System.Windows.Controls.RowDefinition)main.FindName("FooterRow")).Height = new System.Windows.GridLength(38);
                    ((System.Windows.FrameworkElement)main.FindName("Footer")).Visibility = System.Windows.Visibility.Visible;
                    ((System.Windows.FrameworkElement)main.FindName("EmptyText")).Visibility = System.Windows.Visibility.Collapsed;
                    var list = (System.Windows.Controls.ListBox)main.FindName("ResultsList");
                    // Exercise the real SearchBox binding without scheduling any provider work.
                    var searchBox = (System.Windows.Controls.TextBox)main.FindName("SearchBox");
                    var handler = (System.Windows.Controls.TextChangedEventHandler)Delegate.CreateDelegate(
                        typeof(System.Windows.Controls.TextChangedEventHandler), main,
                        typeof(MainWindow).GetMethod("SearchBox_TextChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!);
                    searchBox.TextChanged -= handler;
                    searchBox.Text = "ddd";
                    ((System.Windows.FrameworkElement)main.FindName("SearchHint")).Visibility = System.Windows.Visibility.Collapsed;
                    list.ItemsSource = Enumerable.Range(1, 40).Select(i => new LauncherResult
                    {
                        Title = i == 1 ? "03baddd-DDD.txt" : i == 2 ? "ddd-notes-ddd.md" : i == 3 ? "d-d-d (no false highlight).txt" : $"report-{i:D3}.pdf",
                        Subtitle = @"C:\Documents\ddd\Archive-DDD", Target = @"C:\Documents\ddd\Archive-DDD\03baddd-DDD.txt",
                        Kind = LauncherResultKind.File, Score = 1
                    }).ToArray();
                    list.SelectedIndex = 0; // quick mode: no asynchronous metadata reads
                    SearchHighlight.SetText((System.Windows.Controls.TextBlock)main.FindName("DetailLocationText"), ((LauncherResult)list.SelectedItem).Target);
                    ((System.Windows.Controls.TextBlock)main.FindName("StatusText")).Text = "已加载 512 项 · 文件匹配 ≥ 1600 · 可继续加载";
                    ((System.Windows.FrameworkElement)main.FindName("MoreButton")).Visibility = System.Windows.Visibility.Visible;
                    ((System.Windows.FrameworkElement)main.FindName("LoadMoreButton")).Visibility = System.Windows.Visibility.Visible;
                    Save((System.Windows.FrameworkElement)main.Content, 700, 600, 1, $"main-quick-{theme}.png");
                    ((System.Windows.Controls.ColumnDefinition)main.FindName("DetailsDividerColumn")).Width = new System.Windows.GridLength(21);
                    ((System.Windows.Controls.ColumnDefinition)main.FindName("DetailsPaneColumn")).Width = new System.Windows.GridLength(370);
                    ((System.Windows.FrameworkElement)main.FindName("DetailsDivider")).Visibility = System.Windows.Visibility.Visible;
                    ((System.Windows.FrameworkElement)main.FindName("DetailsPane")).Visibility = System.Windows.Visibility.Visible;
                    Save((System.Windows.FrameworkElement)main.Content, 1040, 680, 1, $"main-{theme}.png");
                    Save((System.Windows.FrameworkElement)main.Content, 540, 360, 2, $"main-{theme}-200pct.png");
                }
                Console.WriteLine($"PASS nonactivating renders: {output}");
                void Save(System.Windows.FrameworkElement root, double width, double height, double scale, string name)
                {
                    root.Measure(new System.Windows.Size(width, height));
                    root.Arrange(new System.Windows.Rect(0, 0, width, height));
                    root.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    root.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = System.IO.File.Create(System.IO.Path.Combine(output, name));
                    encoder.Save(stream);
                }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    internal static void Run()
    {
        var thread = new Thread(() =>
        {
            var app = new App();
            app.InitializeComponent();
            Console.WriteLine("preview: application initialized");
            var store = new SettingsStore();
            store.Save(new AppSettings { EverythingLifecycle = "Connect", RecordHistory = false });
            var main = new MainWindow(store, previewMode: true) { Title = "Luma · 验证预览", ShowInTaskbar = true };
            Console.WriteLine("preview: main window created");
            main.SettingsRequested += () =>
            {
                var settings = new SettingsWindow(store.Current.Copy());
                settings.SettingsSaved += value => { store.Save(value); main.ApplySettings(); };
                settings.Show();
            };
            main.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            main.InitializeLauncher();
            Console.WriteLine("preview: launcher initialized");
            main.ShowLauncher();
            Console.WriteLine("preview: launcher shown");
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
