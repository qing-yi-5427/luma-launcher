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
    private const double ExpandedHeight = 560;
    private const int PageSize = 8;
    private const int SearchResultLimit = 64;
    private readonly ObservableCollection<LauncherResult> _results = [];
    private readonly List<LauncherResult> _allResults = [];
    private readonly SearchCoordinator _search = new();
    private readonly QuickSwitchService _quickSwitch = new();
    private readonly SettingsStore _settings;
    private readonly HotkeyService _hotkey = new();
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _idleMaintenanceCancellation;
    private HwndSource? _source;
    private HotkeyRegistration? _registration;
    private long _searchGeneration;
    private bool _allowClose;
    private bool _contextMenuOpen;
    private string _activeFilter = "All";
    private string _batchStatus = string.Empty;
    private int _pageIndex;
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
        var foldersChanged = _search.Configure(_settings.Current);
        _quickSwitch.Enabled = _settings.Current.EnableQuickSwitch;
        _ = EnsureEverythingRunningAsync();
        if (foldersChanged && _search.IsInitialized)
            _ = ReloadAppsAsync();
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

        _quickSwitch.CaptureForegroundDialog();
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
        QuickSwitchHint.Visibility = _quickSwitch.HasTarget ? Visibility.Visible : Visibility.Collapsed;
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
        _allResults.Clear();
        _results.Clear();
        MoreButton.Visibility = Visibility.Collapsed;
        EmptyText.Text = "正在搜索…";
        EmptyText.Visibility = Visibility.Visible;
        ResultsList.Visibility = Visibility.Collapsed;
        StatusText.Text = "Everything + 应用";

        try
        {
            await Task.Delay(65, token);
            var query = SearchBox.Text.Trim();
            var batch = await _search.SearchAsync(query, SearchResultLimit, token);
            if (generation != _searchGeneration || !query.Equals(SearchBox.Text.Trim(), StringComparison.Ordinal))
                return;
            ApplyBatch(batch, "没有找到匹配项");
            _ = LoadIconsAsync(_results.ToList(), generation, token);
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
            var batch = await _search.GetRecommendationsAsync(SearchResultLimit, token);
            if (generation != _searchGeneration || SearchBox.Text.Length != 0)
                return;
            ApplyBatch(batch, "输入应用、文件名或 Everything 语法");
            _ = LoadIconsAsync(_results.ToList(), generation, token);
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyBatch(SearchBatch batch, string emptyMessage)
    {
        _allResults.Clear();
        _allResults.AddRange(batch.Results);
        _batchStatus = batch.StatusText;
        _pageIndex = 0;
        ApplyCurrentPage(emptyMessage);
    }

    private void ApplyCurrentPage(string emptyMessage = "这个分类没有结果")
    {
        var filtered = _allResults.Where(MatchesActiveFilter).ToList();
        var pageCount = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)PageSize));
        _pageIndex = Math.Clamp(_pageIndex, 0, pageCount - 1);

        _results.Clear();
        foreach (var result in filtered.Skip(_pageIndex * PageSize).Take(PageSize))
            _results.Add(result);
        ResultsList.SelectedIndex = _results.Count > 0 ? 0 : -1;
        EmptyText.Text = emptyMessage;
        EmptyText.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = _results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = filtered.Count == 0 ? _batchStatus : $"{filtered.Count} 个结果 · {_batchStatus}";
        MoreButton.Visibility = filtered.Count > PageSize ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Content = pageCount > 1 ? $"{_pageIndex + 1}/{pageCount}  更多" : "更多";
        UpdateFilterButtons();
        SetExpanded(_results.Count, _allResults.Count > 0 || SearchBox.Text.Length > 0);
    }

    private bool MatchesActiveFilter(LauncherResult result) => _activeFilter switch
    {
        "Application" => result.Kind == LauncherResultKind.Application,
        "File" => result.Kind == LauncherResultKind.File,
        "Folder" => result.Kind == LauncherResultKind.Folder,
        _ => true
    };

    private void UpdateFilterButtons()
    {
        foreach (var button in new[] { AllFilterButton, AppFilterButton, FileFilterButton, FolderFilterButton })
        {
            var selected = string.Equals(button.Tag as string, _activeFilter, StringComparison.Ordinal);
            button.SetResourceReference(BackgroundProperty, selected ? "AccentSoftBrush" : "PanelHoverBrush");
            button.SetResourceReference(BorderBrushProperty, selected ? "AccentBrush" : "StrokeBrush");
        }
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
        var resultArea = 36 + (resultCount > 0 ? Math.Min(PageSize, resultCount) * 48 + 8 : 72);
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

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filter })
            return;
        _activeFilter = filter;
        _pageIndex = 0;
        ApplyCurrentPage();
        _ = LoadIconsAsync(_results.ToList(), _searchGeneration, _searchCancellation?.Token ?? CancellationToken.None);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var filteredCount = _allResults.Count(MatchesActiveFilter);
        var pageCount = Math.Max(1, (int)Math.Ceiling(filteredCount / (double)PageSize));
        _pageIndex = (_pageIndex + 1) % pageCount;
        ApplyCurrentPage();
        _ = LoadIconsAsync(_results.ToList(), _searchGeneration, _searchCancellation?.Token ?? CancellationToken.None);
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

        if (e.Key is Key.PageDown or Key.PageUp && MoreButton.Visibility == Visibility.Visible)
        {
            var filteredCount = _allResults.Count(MatchesActiveFilter);
            var pageCount = Math.Max(1, (int)Math.Ceiling(filteredCount / (double)PageSize));
            _pageIndex = e.Key == Key.PageDown
                ? (_pageIndex + 1) % pageCount
                : (_pageIndex - 1 + pageCount) % pageCount;
            ApplyCurrentPage();
            _ = LoadIconsAsync(_results.ToList(), _searchGeneration, _searchCancellation?.Token ?? CancellationToken.None);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _ = QuickSwitchSelectedAsync();
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
            if (selected.Kind != LauncherResultKind.Calculation)
                _search.RecordLaunch(selected);
            HideLauncher();
        }
    }

    private void RevealSelected()
    {
        if (ResultsList.SelectedItem is not LauncherResult selected)
            return;
        if (!selected.IsFileSystemItem)
        {
            ResultExecutionService.CopyPath(selected);
            StatusText.Text = "已复制";
            return;
        }
        ResultExecutionService.Reveal(selected);
        HideLauncher();
    }

    private void ShowActions()
    {
        if (ResultsList.SelectedItem is not LauncherResult selected)
            return;

        var menu = new System.Windows.Controls.ContextMenu
        {
            Background = (System.Windows.Media.Brush)FindResource("PanelBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("StrokeBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            ItemContainerStyle = (Style)FindResource("LumaMenuItem")
        };
        AddMenuItem(menu, selected.Kind == LauncherResultKind.Calculation ? "复制结果" : "打开", () => OpenSelected(false));
        if (selected.Kind is LauncherResultKind.Folder or LauncherResultKind.File && _quickSwitch.HasTarget)
            AddMenuItem(menu, selected.Kind == LauncherResultKind.Folder
                ? "切换文件对话框到这里"
                : "切换文件对话框到父目录", () => _ = QuickSwitchSelectedAsync());
        if (selected.IsFileSystemItem)
        {
            AddMenuItem(menu, "在文件资源管理器中显示", RevealSelected);
            if (selected.Kind == LauncherResultKind.File)
                AddMenuItem(menu, "打开方式…", () => ResultExecutionService.OpenWith(selected));
            AddMenuItem(menu, "在此处打开终端", () => ResultExecutionService.OpenTerminal(selected));
            AddMenuItem(menu, "属性", () => ResultExecutionService.ShowProperties(selected));
        }
        AddMenuItem(menu, selected.Kind == LauncherResultKind.Calculation ? "复制" : "复制路径", () => ResultExecutionService.CopyPath(selected));
        if (selected.IsFileSystemItem)
            AddMenuItem(menu, "复制父目录", () => ResultExecutionService.CopyParent(selected));
        if (selected.CanRunAsAdministrator)
            AddMenuItem(menu, "以管理员身份运行", () => OpenSelected(true));
        if (selected.Kind != LauncherResultKind.Calculation)
        {
            menu.Items.Add(new Separator());
            var favoriteTitle = _search.IsFavorite(selected) ? "取消收藏" : "加入收藏";
            AddMenuItem(menu, favoriteTitle, () => ToggleFavorite(selected));
            AddMenuItem(menu, "从最近使用中移除", () => RemoveFromHistory(selected));
        }
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

    private void ResultsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container is not null)
            ResultsList.SelectedItem = container.DataContext;
        ShowActions();
        e.Handled = true;
    }

    private async Task QuickSwitchSelectedAsync()
    {
        if (ResultsList.SelectedItem is not LauncherResult selected)
            return;
        var folder = selected.Kind switch
        {
            LauncherResultKind.Folder => selected.Target,
            LauncherResultKind.File => Path.GetDirectoryName(selected.Target),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(folder) || !_quickSwitch.HasTarget)
        {
            StatusText.Text = "没有可用的文件对话框，或当前结果不包含目录";
            return;
        }
        HideLauncher();
        if (!await _quickSwitch.SwitchAsync(folder))
        {
            ShowLauncher();
            StatusText.Text = "快速切换失败；当前应用可能使用了非标准文件对话框";
        }
    }

    private void ToggleFavorite(LauncherResult selected)
    {
        var favorite = _search.ToggleFavorite(selected);
        selected.IsFavorite = favorite;
        StatusText.Text = favorite ? "已加入收藏" : "已取消收藏";
    }

    private void RemoveFromHistory(LauncherResult selected)
    {
        _search.RemoveFromHistory(selected);
        _allResults.RemoveAll(result => result.Target.Equals(selected.Target, StringComparison.OrdinalIgnoreCase));
        ApplyCurrentPage();
        StatusText.Text = "已从最近使用中移除";
    }

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
