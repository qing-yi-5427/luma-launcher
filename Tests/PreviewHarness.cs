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
                foreach (var theme in new[] { ThemeService.AppleLight, ThemeService.AppleDark })
                {
                    ThemeService.Apply(theme);
                    var settings = new SettingsWindow(new AppSettings { Theme = theme, EverythingLifecycle = "Connect" });
                    Save((System.Windows.FrameworkElement)settings.Content, 740, 680, 1, $"settings-{theme}.png");
                    Save((System.Windows.FrameworkElement)settings.Content, 400, 360, 2, $"settings-{theme}-200pct.png");
                    ((System.Windows.Controls.ListBox)settings.FindName("SettingsNav")).SelectedIndex = 1;
                    Save((System.Windows.FrameworkElement)settings.Content, 740, 680, 1, $"settings-appearance-{theme}.png");
                    if (theme == ThemeService.AppleLight)
                    {
                        var nav = (System.Windows.Controls.ListBox)settings.FindName("SettingsNav");
                        foreach (var section in new[] { (2, "search"), (3, "sources"), (4, "features"), (5, "privacy"), (6, "about") })
                        {
                            nav.SelectedIndex = section.Item1;
                            Save((System.Windows.FrameworkElement)settings.Content, 740, 680, 1, $"settings-{section.Item2}-{theme}.png");
                        }
                    }
                    var main = new MainWindow(new SettingsStore(), true);
                    main.ChangeSort(ResultRanker.Smart);
                    ThemeService.Apply(theme);
                    var menu = main.CreateSortMenu();
                    Save(menu, 240, 350, 1, $"sort-menu-{theme}.png");
                    var trayMenu = TrayIconService.BuildMenu(() => { }, () => { }, () => Task.CompletedTask, () => { }, "Alt+Space", out _);
                    Save(trayMenu, 240, 250, 2, $"tray-menu-{theme}.png");
                    ((System.Windows.Controls.Grid)main.FindName("ResultsHost")).Visibility = System.Windows.Visibility.Visible;
                    ((System.Windows.FrameworkElement)main.FindName("ResultsSurface")).Visibility = System.Windows.Visibility.Visible;
                    ((System.Windows.Controls.RowDefinition)main.FindName("ResultsRow")).Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
                    ((System.Windows.Controls.RowDefinition)main.FindName("FooterRow")).Height = new System.Windows.GridLength(38);
                    ((System.Windows.FrameworkElement)main.FindName("Footer")).Visibility = System.Windows.Visibility.Visible;
                    ((System.Windows.FrameworkElement)main.FindName("EmptyState")).Visibility = System.Windows.Visibility.Collapsed;
                    var list = (System.Windows.Controls.ListBox)main.FindName("ResultsList");
                    // Exercise the real SearchBox binding without scheduling any provider work.
                    var searchBox = (System.Windows.Controls.TextBox)main.FindName("SearchBox");
                    var handler = (System.Windows.Controls.TextChangedEventHandler)Delegate.CreateDelegate(
                        typeof(System.Windows.Controls.TextChangedEventHandler), main,
                        typeof(MainWindow).GetMethod("SearchBox_TextChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!);
                    searchBox.TextChanged -= handler;
                    searchBox.Text = "design";
                    ((System.Windows.FrameworkElement)main.FindName("SearchHint")).Visibility = System.Windows.Visibility.Collapsed;
                    var sample = new (string Title, string Subtitle, LauncherResultKind Kind)[]
                    {
                        ("Luma Design", "应用 · 快速打开", LauncherResultKind.Application),
                        ("Design system notes", @"文档 · C:\Work\Projects\Luma", LauncherResultKind.File),
                        ("Design assets", @"文件夹 · C:\Work\Projects\Luma", LauncherResultKind.Folder),
                        ("Apple Human Interface Guidelines", "网页书签 · developer.apple.com", LauncherResultKind.Bookmark),
                        ("Design review", "窗口 · Microsoft Edge", LauncherResultKind.Window),
                        ("Color palette.fig", @"文件 · C:\Work\Projects\Luma", LauncherResultKind.File),
                        ("Open Settings", "系统命令 · 偏好设置", LauncherResultKind.System),
                        ("Design handoff.pdf", @"文件 · C:\Work\Projects\Luma", LauncherResultKind.File)
                    };
                    list.ItemsSource = sample.Select((entry, i) => new LauncherResult
                    {
                        Title = entry.Title, Subtitle = entry.Subtitle,
                        Target = i == 0 ? @"C:\Program Files\Luma\Luma.exe" : @"C:\Work\Projects\Luma\" + entry.Title,
                        Kind = entry.Kind, Score = 100 - i, IsFavorite = i == 0,
                        Icon = i == 0 ? new System.Windows.Media.Imaging.BitmapImage(
                            new Uri("pack://application:,,,/Luma;component/Assets/Luma.ico")) : null
                    }).ToArray();
                    ((System.Windows.Controls.TextBlock)main.FindName("ResultCountText")).Text = "8 项";
                    list.SelectedIndex = 0; // quick mode: no asynchronous metadata reads
                    SearchHighlight.SetText((System.Windows.Controls.TextBlock)main.FindName("DetailLocationText"), ((LauncherResult)list.SelectedItem).Target);
                    ((System.Windows.Controls.TextBlock)main.FindName("StatusText")).Text = "8 个结果 · 按 Enter 打开";
                    Save((System.Windows.FrameworkElement)main.Content, 660, 610, 1, $"main-quick-{theme}.png");
                    var titleBlock = FindResultTitle((System.Windows.DependencyObject)main.Content);
                    if (titleBlock is null || !SearchHighlight.GetSelected(titleBlock) ||
                        titleBlock.Inlines.OfType<System.Windows.Documents.Run>().Any(run =>
                            run.Foreground is not System.Windows.Media.SolidColorBrush brush ||
                            brush.Color != System.Windows.Media.Colors.White))
                        throw new InvalidOperationException("Selected result text must remain readable on the blue highlight.");
                    ((System.Windows.Controls.ColumnDefinition)main.FindName("DetailsDividerColumn")).Width = new System.Windows.GridLength(21);
                    ((System.Windows.Controls.ColumnDefinition)main.FindName("DetailsPaneColumn")).Width = new System.Windows.GridLength(370);
                    ((System.Windows.FrameworkElement)main.FindName("DetailsDivider")).Visibility = System.Windows.Visibility.Visible;
                    ((System.Windows.FrameworkElement)main.FindName("DetailsPane")).Visibility = System.Windows.Visibility.Visible;
                    Save((System.Windows.FrameworkElement)main.Content, 960, 680, 1, $"main-{theme}.png");
                    ((System.Windows.Controls.ColumnDefinition)main.FindName("DetailsDividerColumn")).Width = new System.Windows.GridLength(0);
                    ((System.Windows.Controls.ColumnDefinition)main.FindName("DetailsPaneColumn")).Width = new System.Windows.GridLength(0);
                    ((System.Windows.FrameworkElement)main.FindName("DetailsDivider")).Visibility = System.Windows.Visibility.Collapsed;
                    ((System.Windows.FrameworkElement)main.FindName("DetailsPane")).Visibility = System.Windows.Visibility.Collapsed;
                    Save((System.Windows.FrameworkElement)main.Content, 540, 360, 2, $"main-{theme}-200pct.png");
                    if (theme == ThemeService.AppleLight)
                        MotionTests.VerifyWindow(main);
                    else
                        main.CloseForExit();
                    settings.Close();
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
                static System.Windows.Controls.TextBlock? FindResultTitle(System.Windows.DependencyObject root)
                {
                    if (root is System.Windows.Controls.TextBlock block && SearchHighlight.GetText(block) == "Luma Design")
                        return block;
                    for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                    {
                        var found = FindResultTitle(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
                        if (found is not null) return found;
                    }
                    return null;
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
            main.IsVisibleChanged += (_, _) =>
            {
                if (!main.IsVisible)
                    Dispatcher.CurrentDispatcher.BeginInvoke(() => main.CloseForExit(), DispatcherPriority.Background);
            };
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
