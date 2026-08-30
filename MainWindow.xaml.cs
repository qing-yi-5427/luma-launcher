using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher;

public sealed partial class MainWindow : Window
{
    private const double CompactHeight = 94;
    private const double ExpandedHeight = 522;
    private readonly ObservableCollection<LauncherResult> _results = [];
    private readonly SearchCoordinator _search = new();
    private readonly SettingsStore _settings;
    private readonly HotkeyService _hotkey = new();
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _idleMaintenanceCancellation;
    private HwndSource? _source;
    private HotkeyRegistration? _registration;
    private long _searchGeneration;
    private bool _allowClose;
    private bool _contextMenuOpen;
    private DateTimeOffset _ignoreDeactivateUntil;

    internal Func<int, IntPtr, IntPtr, bool>? TrayMessageHandler { get; set; }

    public MainWindow(SettingsStore settings)
    {
        _settings = settings;
        InitializeComponent();
        ResultsList.ItemsSource = _results;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<HotkeyRegistration>? HotkeyRegistrationChanged;

    public HotkeyRegistration InitializeLauncher()
    {
        new WindowInteropHelper(this).EnsureHandle();
        _ = InitializeSearchAsync();
        _registration ??= new HotkeyRegistration(_settings.Current.Hotkey, "未注册", true, 0);
        return _registration;
    }

    public void ApplySettings()
    {
        ThemeService.Apply(_settings.Current.Theme);
        _search.ConfigureEverything(_settings.Current);
        _ = EnsureEverythingRunningAsync();
        if (_source is null)
            return;
        _registration = _hotkey.Register(_source.Handle, _settings.Current.Hotkey);
        HotkeyText.Text = _registration.Active.Replace("+", "  ").ToUpperInvariant();
        HotkeyRegistrationChanged?.Invoke(_registration);
    }

    public async Task ReloadAppsAsync()
    {
        try
        {
            StatusText.Text = "正在重建应用索引…";
            await _search.ReloadAppsAsync();
            StatusText.Text = "应用索引已更新";
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("app-index-reload", exception);
            StatusText.Text = "应用索引更新失败";
        }
    }

    private async Task InitializeSearchAsync()
    {
        try { await _search.InitializeAsync(); }
        catch (Exception exception) { DiagnosticsService.Log("search-initialize", exception); }
    }

    private async Task EnsureEverythingRunningAsync()
    {
        try { await _search.EnsureEverythingRunningAsync(); }
        catch (Exception exception) { DiagnosticsService.Log("everything-initialize", exception); }
    }

    public void ToggleLauncher()
    {
        if (IsVisible && IsActive)
            HideLauncher();
        else
            ShowLauncher();
    }

    public void ShowLauncher()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowLauncher);
            return;
        }

        _idleMaintenanceCancellation?.Cancel();
        Show();
        _ignoreDeactivateUntil = DateTimeOffset.UtcNow.AddMilliseconds(900);
        WindowState = WindowState.Normal;
        UpdateLayout();
        PositionOnCursorMonitor();
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
        AnimateShow();
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
            _ = ShowRecommendationsAsync();
    }

    public void HideLauncher()
    {
        _searchCancellation?.Cancel();
        Hide();
        ScheduleIdleTrim();
    }

    internal void ScheduleIdleTrim()
    {
        _idleMaintenanceCancellation?.Cancel();
        _idleMaintenanceCancellation?.Dispose();
        _idleMaintenanceCancellation = new CancellationTokenSource();
        _ = TrimWorkingSetWhenIdleAsync(_idleMaintenanceCancellation.Token);
    }

    private async Task TrimWorkingSetWhenIdleAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(5000, token);
            if (IsVisible || token.IsCancellationRequested)
                return;
            _search.TrimCaches();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            NativeMethods.EmptyWorkingSet(process.Handle);
        }
        catch (OperationCanceledException) { }
    }

    public void OpenSettings() => SettingsRequested?.Invoke();
    public void RequestExit() => ExitRequested?.Invoke();

    public void ShutdownEverything() => _search.ShutdownEverything();

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WindowProcedure);
        ApplyDwmStyling(handle);
        ApplySettings();
    }

    private IntPtr WindowProcedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (TrayMessageHandler?.Invoke(message, wParam, lParam) == true)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (_hotkey.IsHotkeyMessage(message, wParam))
        {
            ToggleLauncher();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        var generation = Interlocked.Increment(ref _searchGeneration);

        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            await ShowRecommendationsAsync();
            return;
        }

        SetExpanded(0, showBody: true);
        _results.Clear();
        EmptyText.Text = "正在搜索…";
        EmptyText.Visibility = Visibility.Visible;
        ResultsList.Visibility = Visibility.Collapsed;
        StatusText.Text = "Everything + 应用";

        try
        {
            await Task.Delay(65, token);
            var query = SearchBox.Text.Trim();
            var batch = await _search.SearchAsync(query, 8, token);
            if (generation != _searchGeneration || !query.Equals(SearchBox.Text.Trim(), StringComparison.Ordinal))
                return;
            ApplyBatch(batch, "没有找到匹配项");
            SetExpanded(batch.Results.Count, showBody: true);
            _ = LoadIconsAsync(batch.Results, generation, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (generation == _searchGeneration)
        {
            DiagnosticsService.Log("search", exception);
            ApplyBatch(new SearchBatch([], "搜索暂时不可用", false), "搜索暂时不可用，请稍后重试");
            SetExpanded(0, showBody: true);
        }
    }

    private async Task ShowRecommendationsAsync()
    {
        var generation = Interlocked.Increment(ref _searchGeneration);
        var token = _searchCancellation?.Token ?? CancellationToken.None;
        try
        {
            var batch = await _search.GetRecommendationsAsync(8, token);
            if (generation != _searchGeneration || SearchBox.Text.Length != 0)
                return;
            ApplyBatch(batch, "输入应用、文件名或 Everything 语法");
            SetExpanded(batch.Results.Count, batch.Results.Count > 0);
            _ = LoadIconsAsync(batch.Results, generation, token);
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyBatch(SearchBatch batch, string emptyMessage)
    {
        _results.Clear();
        foreach (var result in batch.Results)
            _results.Add(result);
        ResultsList.SelectedIndex = _results.Count > 0 ? 0 : -1;
        EmptyText.Text = emptyMessage;
        EmptyText.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = _results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = batch.StatusText;
    }

    private async Task LoadIconsAsync(IReadOnlyList<LauncherResult> results, long generation, CancellationToken token)
    {
        if (results.Count == 0)
            return;
        try
        {
            var icons = await _search.LoadIconsAsync(results, token);
            if (generation != _searchGeneration || token.IsCancellationRequested)
                return;
            for (var index = 0; index < results.Count; index++)
                results[index].Icon = icons[index];
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { DiagnosticsService.Log("icon-hydration", exception); }
    }

    private void SetExpanded(int resultCount, bool showBody)
    {
        ResultsRow.Height = new GridLength(showBody ? 1 : 0, showBody ? GridUnitType.Star : GridUnitType.Pixel);
        FooterRow.Height = new GridLength(showBody ? 38 : 0);
        ResultsHost.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;
        Footer.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;
        var resultArea = resultCount > 0 ? Math.Min(8, resultCount) * 48 + 8 : 92;
        var targetHeight = showBody ? Math.Min(ExpandedHeight, CompactHeight + resultArea + 38) : CompactHeight;
        if (!SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(HeightProperty, null);
            Height = targetHeight;
            return;
        }
        var animation = new DoubleAnimation(targetHeight, TimeSpan.FromMilliseconds(135))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateShow()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            WindowTranslate.Y = 0;
            return;
        }
        Opacity = 0.94;
        WindowTranslate.Y = -5;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(115)));
        WindowTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void PositionOnCursorMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.TryGetCursorWorkArea(out var area))
            return;
        if (!NativeMethods.GetWindowRect(handle, out var rectangle))
            return;
        var windowWidth = rectangle.Right - rectangle.Left;
        var areaWidth = area.Right - area.Left;
        var areaHeight = area.Bottom - area.Top;
        var x = area.Left + (areaWidth - windowWidth) / 2;
        var y = area.Top + Math.Max(42, (int)(areaHeight * 0.13));
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    private static void ApplyDwmStyling(IntPtr handle)
    {
        var rounded = 2;
        NativeMethods.DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        var backdrop = 2;
        NativeMethods.DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideLauncher();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Down or Key.Up && _results.Count > 0)
        {
            var delta = e.Key == Key.Down ? 1 : -1;
            var next = (ResultsList.SelectedIndex + delta + _results.Count) % _results.Count;
            ResultsList.SelectedIndex = next;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            var modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Shift))
                OpenSelected(runAsAdministrator: true);
            else if (modifiers.HasFlag(ModifierKeys.Control))
                RevealSelected();
            else
                OpenSelected(runAsAdministrator: false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && ResultsList.SelectedItem is LauncherResult copyResult)
        {
            ResultExecutionService.CopyPath(copyResult);
            StatusText.Text = "已复制路径";
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right || e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ShowActions();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.OemComma && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OpenSettings();
            e.Handled = true;
        }
    }

    private void OpenSelected(bool runAsAdministrator)
    {
        if (ResultsList.SelectedItem is not LauncherResult selected)
            return;
        if (ResultExecutionService.Open(selected, runAsAdministrator))
        {
            _search.RecordLaunch(selected);
            HideLauncher();
        }
    }

    private void RevealSelected()
    {
        if (ResultsList.SelectedItem is not LauncherResult selected)
            return;
        ResultExecutionService.Reveal(selected);
        HideLauncher();
    }

    private void ShowActions()
    {
        if (ResultsList.SelectedItem is not LauncherResult selected)
            return;

        var menu = new System.Windows.Controls.ContextMenu();
        AddMenuItem(menu, "打开", () => OpenSelected(false));
        AddMenuItem(menu, "在文件资源管理器中显示", RevealSelected);
        AddMenuItem(menu, "复制路径", () => ResultExecutionService.CopyPath(selected));
        if (selected.CanRunAsAdministrator)
            AddMenuItem(menu, "以管理员身份运行", () => OpenSelected(true));
        menu.Closed += (_, _) => _contextMenuOpen = false;
        _contextMenuOpen = true;
        menu.PlacementTarget = ResultsList;
        menu.IsOpen = true;
    }

    private static void AddMenuItem(System.Windows.Controls.ContextMenu menu, string title, Action action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = title };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected(false);

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is LauncherResult selected)
            StatusText.Text = selected.Target;
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (IsVisible && !_contextMenuOpen && DateTimeOffset.UtcNow >= _ignoreDeactivateUntil)
            HideLauncher();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1 || IsInteractiveElement(e.OriginalSource as DependencyObject))
            return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private bool IsInteractiveElement(DependencyObject? element)
    {
        for (var current = element; current is not null && current != this; current = GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.TextBoxBase or
                System.Windows.Controls.Primitives.ButtonBase or
                System.Windows.Controls.Primitives.Selector or
                System.Windows.Controls.Primitives.ScrollBar or
                System.Windows.Controls.ScrollViewer)
                return true;
        }
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        try { return System.Windows.Media.VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element); }
        catch (InvalidOperationException) { return LogicalTreeHelper.GetParent(element); }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        HideLauncher();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _idleMaintenanceCancellation?.Cancel();
        _idleMaintenanceCancellation?.Dispose();
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _search.Dispose();
        _hotkey.Unregister();
        _source?.RemoveHook(WindowProcedure);
    }
}
