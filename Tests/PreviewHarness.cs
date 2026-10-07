using System.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

internal static class PreviewHarness
{
    private const string PreviewMutexName = @"Local\LumaLauncher.ApplePreview.Primary";
    private const string PreviewActivationEventName = @"Local\LumaLauncher.ApplePreview.Activate";

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
                    var renderStore = new SettingsStore();
                    var renderSettings = renderStore.Current.Copy();
                    renderSettings.Theme = theme;
                    renderSettings.Language = "zh-CN";
                    renderStore.Save(renderSettings);
                    var main = new MainWindow(renderStore, true);
                    main.ChangeSort(ResultRanker.Smart);
                    ThemeService.Apply(theme);
                    var idleRoot = (System.Windows.FrameworkElement)main.Content;
                    var idleSearchBox = (System.Windows.Controls.TextBox)main.FindName("SearchBox");
                    var idleHotkey = (System.Windows.FrameworkElement)main.FindName("HotkeyText");
                    var idleHint = (System.Windows.Controls.TextBlock)main.FindName("SearchHint");
                    if (idleHint.Text != "搜索")
                        throw new InvalidOperationException("Chinese idle hint was replaced with the old long syntax explanation.");
                    Save(idleRoot, 660, 82, 1, $"main-idle-{theme}.png");
                    Save(idleRoot, 660, 82, 1.5, $"main-idle-{theme}-150pct.png");
                    if (idleSearchBox.ActualWidth < 150 || idleHotkey.Visibility != System.Windows.Visibility.Visible)
                        throw new InvalidOperationException("Normal-width search field or hotkey hint is unavailable.");
                    var inputBounds = idleSearchBox.TransformToAncestor(idleRoot)
                        .TransformBounds(new System.Windows.Rect(0, 0, idleSearchBox.ActualWidth, idleSearchBox.ActualHeight));
                    var hotkeyBounds = idleHotkey.TransformToAncestor(idleRoot)
                        .TransformBounds(new System.Windows.Rect(0, 0, idleHotkey.ActualWidth, idleHotkey.ActualHeight));
                    if (inputBounds.Right > hotkeyBounds.Left)
                        throw new InvalidOperationException("Search input overlaps the hotkey hint.");
                    Save(idleRoot, 400, 82, 2, $"main-idle-{theme}-200pct.png");
                    if (idleSearchBox.ActualWidth < 150 || idleHotkey.Visibility != System.Windows.Visibility.Collapsed)
                        throw new InvalidOperationException("Narrow search must retain input space and hide its secondary hint.");
                    Save(idleRoot, 340, 82, 2, $"main-idle-{theme}-340dip-200pct.png");
                    if (idleSearchBox.ActualWidth < 120 || idleHotkey.Visibility != System.Windows.Visibility.Collapsed)
                        throw new InvalidOperationException("Minimum-width search must keep a usable input field.");
                    if (theme == ThemeService.AppleLight)
                    {
                        renderSettings = renderStore.Current.Copy();
                        renderSettings.Language = "en-US";
                        renderStore.Save(renderSettings);
                        main.ApplySettings();
                        if (idleHint.Text != "Search")
                            throw new InvalidOperationException("English idle hint was replaced with the old long syntax explanation.");
                        Save(idleRoot, 660, 82, 1, "main-idle-en-AppleLight.png");
                        Save(idleRoot, 400, 82, 2, "main-idle-en-AppleLight-200pct.png");
                        renderSettings.Language = "zh-CN";
                        renderStore.Save(renderSettings);
                        main.ApplySettings();
                    }
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

    internal static void VerifyHotkeyLifecycle()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                var app = new App();
                app.InitializeComponent();
                var store = new SettingsStore();
                store.Save(new AppSettings
                {
                    Hotkey = "Ctrl+Alt+Shift+F24", EverythingLifecycle = "Connect",
                    StartWithWindows = false, RecordHistory = false, RecordQueryHistory = false,
                    EnableClipboardHistory = false
                });
                window = new MainWindow(store, previewMode: false)
                {
                    ShowActivated = false, ShowInTaskbar = false,
                    Left = -32000, Top = -32000
                };
                var registration = window.InitializeLauncher();
                if (registration.Active != store.Current.Hotkey || HotkeyService.TryProbe(registration.Active, out _))
                    throw new InvalidOperationException("The isolated preview did not own its test hotkey.");
                window.Show();
                if (!window.IsVisible) throw new InvalidOperationException("Preview did not show.");
                window.HideLauncher();
                if (window.IsVisible || HotkeyService.TryProbe(registration.Active, out _))
                    throw new InvalidOperationException("Hiding the preview released its hotkey or left it visible.");
                window.Show();
                if (!window.IsVisible) throw new InvalidOperationException("Hidden preview could not show again.");
                window.CloseForExit();
                if (!HotkeyService.TryProbe(registration.Active, out _))
                    throw new InvalidOperationException("Closing the preview did not release its hotkey.");
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                try { window?.CloseForExit(); }
                catch (Exception cleanupException) { failure ??= cleanupException; }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Preview hotkey lifecycle failed.", failure);
        Console.WriteLine("PASS isolated preview registration survives hide/show and releases on exit");
    }

    internal static void Run(bool simulateFailure = false)
    {
        using var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, PreviewActivationEventName);
        using var previewMutex = new Mutex(true, PreviewMutexName, out var isPrimary);
        if (!isPrimary)
        {
            activationEvent.Set();
            Console.WriteLine("preview: existing preview activated");
            return;
        }
        try
        {
            RunPrimary(activationEvent, simulateFailure);
        }
        finally { previewMutex.ReleaseMutex(); }
    }

    private static void RunPrimary(EventWaitHandle activationEvent, bool simulateFailure)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Dispatcher? dispatcher = null;
            MainWindow? main = null;
            SettingsWindow? settingsWindow = null;
            TrayIconService? trayIcon = null;
            RegisteredWaitHandle? activationWait = null;
            var activationStopped = 0;
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.UnhandledException += (_, e) =>
                {
                    failure ??= e.Exception;
                    e.Handled = true;
                    try { main?.CloseForExit(); }
                    catch (Exception cleanupException) { failure ??= cleanupException; }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                if (simulateFailure)
                    throw new InvalidOperationException("Deliberate preview worker failure.");
                var app = new App();
                app.InitializeComponent();
                Console.WriteLine("preview: application initialized");
                var store = new SettingsStore();
                store.Save(new AppSettings
                {
                    EverythingLifecycle = "Connect", StartWithWindows = false,
                    RecordHistory = false, RecordQueryHistory = false, EnableClipboardHistory = false
                });
                var launcher = new MainWindow(store, previewMode: false)
                {
                    Title = "Luma · Apple 预览", ShowInTaskbar = true
                };
                main = launcher;
                Console.WriteLine("preview: main window created");
                void ExitPreview()
                {
                    settingsWindow?.Close();
                    launcher.CloseForExit();
                }
                launcher.SettingsRequested += () =>
                {
                    launcher.HideLauncher();
                    if (settingsWindow is not null) { settingsWindow.Activate(); return; }
                    settingsWindow = new SettingsWindow(store.Current.Copy(), launcher.ActiveHotkey);
                    settingsWindow.SettingsSaved += value =>
                    {
                        value.EverythingLifecycle = "Connect";
                        value.StartWithWindows = false;
                        value.RecordHistory = false;
                        value.RecordQueryHistory = false;
                        value.EnableClipboardHistory = false;
                        store.Save(value);
                        launcher.ApplySettings();
                    };
                    settingsWindow.Closed += (_, _) => settingsWindow = null;
                    settingsWindow.Show();
                    settingsWindow.Activate();
                };
                launcher.ExitRequested += ExitPreview;
                launcher.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key == Key.Q && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                    {
                        e.Handled = true;
                        ExitPreview();
                    }
                };
                launcher.Closed += (_, _) =>
                {
                    Interlocked.Exchange(ref activationStopped, 1);
                    activationWait?.Unregister(null);
                    trayIcon?.Dispose();
                    launcher.TrayMessageHandler = null;
                    settingsWindow?.Close();
                    if (!dispatcher.HasShutdownStarted)
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                var registration = launcher.InitializeLauncher();
                Console.WriteLine($"preview: requested hotkey={registration.Requested}; active hotkey={registration.Active}; error={registration.ErrorCode}");
                trayIcon = new TrayIconService(new WindowInteropHelper(launcher).Handle,
                    launcher.ToggleLauncher, launcher.OpenSettings, launcher.ReloadAppsAsync,
                    ExitPreview, registration.Active);
                launcher.TrayMessageHandler = trayIcon.HandleMessage;
                launcher.HotkeyRegistrationChanged += updated => trayIcon?.UpdateHotkey(updated.Active);
                activationWait = ThreadPool.RegisterWaitForSingleObject(activationEvent, (_, timedOut) =>
                {
                    if (timedOut || Volatile.Read(ref activationStopped) != 0) return;
                    try
                    {
                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (Volatile.Read(ref activationStopped) == 0)
                                launcher.ShowLauncher();
                        }), DispatcherPriority.Background);
                    }
                    catch (Exception exception) { Console.Error.WriteLine($"preview activation: {exception}"); }
                }, null, Timeout.Infinite, executeOnlyOnce: false);
                launcher.ShowLauncher();
                Console.WriteLine("preview: launcher shown");
                Console.WriteLine("preview: Esc hides; hotkey or tray icon restores; tray menu Exit or Ctrl+Shift+Q exits.");
                if (registration.UsedFallback)
                {
                    Console.Error.WriteLine($"Preview cannot use {registration.Requested}; active={registration.Active}, Win32 error={registration.ErrorCode}.");
                    System.Windows.MessageBox.Show(
                        $"{registration.Requested} 当前被占用。预览快捷键：{registration.Active}。\n正式版不会被关闭；也可点击托盘图标唤出预览。",
                        "Luma · 预览快捷键", System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                Interlocked.Exchange(ref activationStopped, 1);
                activationWait?.Unregister(null);
                try { settingsWindow?.Close(); }
                catch (Exception cleanupException) { failure ??= cleanupException; }
                try { main?.CloseForExit(); }
                catch (Exception cleanupException) { failure ??= cleanupException; }
                try { trayIcon?.Dispose(); }
                catch (Exception cleanupException) { failure ??= cleanupException; }
                try
                {
                    if (dispatcher is { HasShutdownStarted: false })
                        dispatcher.InvokeShutdown();
                }
                catch (Exception cleanupException) { failure ??= cleanupException; }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Preview failed.", failure);
    }
}
