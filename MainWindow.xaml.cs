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
    private const double CompactWidth = 700;
    private const double FullResultsWidth = 1040;
    private const double FullResultsHeight = 680;
    private const int PageSize = 8;
    private const int QuickSearchResultLimit = 64;
    private const int FullSearchResultLimit = 512;
    private const int SearchDebounceMilliseconds = 250;
    private readonly ObservableCollection<LauncherResult> _results = [];
    private readonly List<LauncherResult> _allResults = [];
    private readonly HashSet<LauncherResult> _iconLoads = [];
    private readonly SearchCoordinator _search = new();
    private readonly QuickSwitchService _quickSwitch = new();
    private readonly SettingsStore _settings;
    private readonly HotkeyService _hotkey = new();
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _detailCancellation;
    private CancellationTokenSource? _idleMaintenanceCancellation;
    private HwndSource? _source;
    private HotkeyRegistration? _registration;
    private long _searchGeneration;
    private bool _allowClose;
    private bool _contextMenuOpen;
    private string _activeFilter = "All";
    private string _batchStatus = string.Empty;
    private int _pageIndex;
    private bool _searchPending;
    private bool _fullResultsMode;
    private long _detailGeneration;
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
        if (_fullResultsMode)
            LeaveFullResultsMode(animate: false);
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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = SearchCurrentTextAsync(SearchDebounceMilliseconds);
    }

    private async Task<bool> SearchCurrentTextAsync(int delayMilliseconds)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        var generation = Interlocked.Increment(ref _searchGeneration);

        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            SetSearchPending(false);
            await ShowRecommendationsAsync();
            return false;
        }

        var pendingQuery = SearchBox.Text.Trim();
        SetSearchPending(true);

        try
        {
            await Task.Delay(delayMilliseconds, token);
            if (generation != _searchGeneration || !pendingQuery.Equals(SearchBox.Text.Trim(), StringComparison.Ordinal))
                return false;

            var keepExistingResults = _fullResultsMode && _allResults.Count > 0;
            SetExpanded(keepExistingResults ? _results.Count : 0, showBody: true);
            if (!keepExistingResults)
            {
                _allResults.Clear();
                _results.Clear();
                MoreButton.Visibility = Visibility.Collapsed;
                EmptyText.Text = "正在搜索…";
                EmptyText.Visibility = Visibility.Visible;
                ResultsList.Visibility = Visibility.Collapsed;
            }
            StatusText.Text = keepExistingResults ? "正在加载更多结果…" : "Everything + 应用";

            var resultLimit = _fullResultsMode ? FullSearchResultLimit : QuickSearchResultLimit;
            var batch = await _search.SearchAsync(pendingQuery, resultLimit, token);
            if (generation != _searchGeneration || !pendingQuery.Equals(SearchBox.Text.Trim(), StringComparison.Ordinal))
                return false;
            ApplyBatch(batch, "没有找到匹配项");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception) when (generation == _searchGeneration)
        {
            DiagnosticsService.Log("search", exception);
            ApplyBatch(new SearchBatch([], "搜索暂时不可用", false), "搜索暂时不可用，请稍后重试");
            SetExpanded(0, showBody: true);
            return false;
        }
        finally
        {
            if (generation == _searchGeneration)
                SetSearchPending(false);
        }
    }

    private void SetSearchPending(bool pending)
    {
        _searchPending = pending;
        ResultsHost.IsHitTestVisible = !pending;
        ResultsHost.Opacity = pending ? 0.55 : 1;
        if (pending)
        {
            ResultsList.SelectedIndex = -1;
            if (_allResults.Count > 0)
                StatusText.Text = "等待输入完成…";
        }
    }

    private async Task ShowRecommendationsAsync()
    {
        var generation = Interlocked.Increment(ref _searchGeneration);
        var token = _searchCancellation?.Token ?? CancellationToken.None;
        try
        {
            var resultLimit = _fullResultsMode ? FullSearchResultLimit : QuickSearchResultLimit;
            var batch = await _search.GetRecommendationsAsync(resultLimit, token);
            if (generation != _searchGeneration || SearchBox.Text.Length != 0)
                return;
            ApplyBatch(batch, "输入应用、文件名或 Everything 语法");
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
        var visibleResults = _fullResultsMode
            ? filtered
            : filtered.Skip(_pageIndex * PageSize).Take(PageSize);
        foreach (var result in visibleResults)
            _results.Add(result);
        ResultsList.SelectedIndex = _results.Count > 0 ? 0 : -1;
        EmptyText.Text = emptyMessage;
        EmptyText.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = _results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = filtered.Count == 0 ? _batchStatus : $"{filtered.Count} 个结果 · {_batchStatus}";
        SortModeText.Text = $"{filtered.Count} 条 · {GetSortModeLabel(_settings.Current.ResultSort)}排序";
        MoreButton.Visibility = _fullResultsMode || filtered.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        MoreButton.Content = _fullResultsMode ? "收起" : "查看全部";
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

    private async void ResultItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LauncherResult result } ||
            result.Icon is not null || !_iconLoads.Add(result))
            return;

        var generation = _searchGeneration;
        var token = _searchCancellation?.Token ?? CancellationToken.None;
        try
        {
            var icon = await _search.LoadIconAsync(result, token);
            if (generation != _searchGeneration || token.IsCancellationRequested)
                return;
            result.Icon = icon;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { DiagnosticsService.Log("icon-hydration", exception); }
        finally { _iconLoads.Remove(result); }
    }

    private void SetExpanded(int resultCount, bool showBody)
    {
        ResultsRow.Height = new GridLength(showBody ? 1 : 0, showBody ? GridUnitType.Star : GridUnitType.Pixel);
        FooterRow.Height = new GridLength(showBody ? 38 : 0);
        ResultsHost.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;
        Footer.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;
        var resultArea = 36 + (resultCount > 0 ? Math.Min(PageSize, resultCount) * 50 + 8 : 72);
        var targetHeight = _fullResultsMode
            ? GetFullResultsSize().Height
            : showBody ? Math.Min(ExpandedHeight, CompactHeight + resultArea + 38) : CompactHeight;
        var targetWidth = _fullResultsMode ? GetFullResultsSize().Width : CompactWidth;
        AnimateWindowSize(targetWidth, targetHeight);
    }

    private void AnimateWindowSize(double targetWidth, double targetHeight)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            Width = targetWidth;
            Height = targetHeight;
            PositionOnCursorMonitor();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(_fullResultsMode ? 175 : 135);
        if (Math.Abs(ActualWidth - targetWidth) > 0.5)
        {
            var startWidth = ActualWidth;
            Width = targetWidth;
            var widthAnimation = new DoubleAnimation(startWidth, targetWidth, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            widthAnimation.Completed += (_, _) => PositionOnCursorMonitor();
            BeginAnimation(WidthProperty, widthAnimation, HandoffBehavior.SnapshotAndReplace);
        }

        var heightAnimation = new DoubleAnimation(targetHeight, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(HeightProperty, heightAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private (double Width, double Height) GetFullResultsSize()
    {
        if (!NativeMethods.TryGetCursorWorkArea(out var area))
            return (FullResultsWidth, FullResultsHeight);
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        var scaleX = fromDevice?.M11 ?? 1;
        var scaleY = fromDevice?.M22 ?? 1;
        var availableWidth = (area.Right - area.Left) * scaleX - 48;
        var availableHeight = (area.Bottom - area.Top) * scaleY - 64;
        return (
            Math.Max(CompactWidth, Math.Min(FullResultsWidth, availableWidth)),
            Math.Max(ExpandedHeight, Math.Min(FullResultsHeight, availableHeight)));
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
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_fullResultsMode)
            LeaveFullResultsMode();
        else
            EnterFullResultsMode();
    }

    private void EnterFullResultsMode()
    {
        if (_fullResultsMode)
            return;
        _fullResultsMode = true;
        ResultsPaneColumn.Width = new GridLength(0.58, GridUnitType.Star);
        DetailsDividerColumn.Width = new GridLength(21);
        DetailsPaneColumn.Width = new GridLength(0.42, GridUnitType.Star);
        DetailsDivider.Visibility = Visibility.Visible;
        DetailsPane.Visibility = Visibility.Visible;
        SortModeText.Visibility = Visibility.Visible;
        _pageIndex = 0;
        ApplyCurrentPage();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text) && _allResults.Count >= QuickSearchResultLimit)
            _ = SearchCurrentTextAsync(0);
    }

    private void LeaveFullResultsMode(bool animate = true)
    {
        if (!_fullResultsMode)
            return;
        _fullResultsMode = false;
        _detailCancellation?.Cancel();
        ResultsPaneColumn.Width = new GridLength(1, GridUnitType.Star);
        DetailsDividerColumn.Width = new GridLength(0);
        DetailsPaneColumn.Width = new GridLength(0);
        DetailsDivider.Visibility = Visibility.Collapsed;
        DetailsPane.Visibility = Visibility.Collapsed;
        SortModeText.Visibility = Visibility.Collapsed;
        _pageIndex = 0;
        ApplyCurrentPage();
        if (!animate)
        {
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            Width = CompactWidth;
            var showBody = _allResults.Count > 0 || SearchBox.Text.Length > 0;
            var resultArea = 36 + (_results.Count > 0 ? Math.Min(PageSize, _results.Count) * 50 + 8 : 72);
            Height = showBody ? Math.Min(ExpandedHeight, CompactHeight + resultArea + 38) : CompactHeight;
        }
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void PositionOnCursorMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.TryGetCursorWorkArea(out var area))
            return;
        if (!NativeMethods.GetWindowRect(handle, out var rectangle))
            return;
        var windowWidth = rectangle.Right - rectangle.Left;
        var windowHeight = rectangle.Bottom - rectangle.Top;
        var areaWidth = area.Right - area.Left;
        var areaHeight = area.Bottom - area.Top;
        var centeredX = area.Left + (areaWidth - windowWidth) / 2;
        var x = Math.Max(area.Left + 12, Math.Min(centeredX, area.Right - windowWidth - 12));
        var preferredY = area.Top + Math.Max(42, (int)(areaHeight * 0.13));
        var y = Math.Max(area.Top + 12, Math.Min(preferredY, area.Bottom - windowHeight - 20));
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    private static string GetSortModeLabel(string? mode) => ResultRanker.Normalize(mode) switch
    {
        ResultRanker.Relevance => "匹配度",
        ResultRanker.Usage => "常用与收藏",
        ResultRanker.Name => "名称 A–Z",
        _ => "智能"
    };

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
            if (_fullResultsMode)
                LeaveFullResultsMode();
            else
                HideLauncher();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Down or Key.Up && _results.Count > 0 && !_searchPending)
        {
            var delta = e.Key == Key.Down ? 1 : -1;
            var next = (ResultsList.SelectedIndex + delta + _results.Count) % _results.Count;
            ResultsList.SelectedIndex = next;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.PageDown or Key.PageUp && !_searchPending)
        {
            if (_fullResultsMode && _results.Count > 0)
            {
                var delta = e.Key == Key.PageDown ? PageSize : -PageSize;
                ResultsList.SelectedIndex = Math.Clamp(ResultsList.SelectedIndex + delta, 0, _results.Count - 1);
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                e.Handled = true;
                return;
            }
            if (!_fullResultsMode && MoreButton.Visibility == Visibility.Visible)
            {
                EnterFullResultsMode();
                e.Handled = true;
                return;
            }
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
            if (_searchPending)
            {
                _ = SearchAndExecuteAsync(modifiers);
                e.Handled = true;
                return;
            }
            ExecuteSelected(modifiers);
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

    private async Task SearchAndExecuteAsync(ModifierKeys modifiers)
    {
        if (await SearchCurrentTextAsync(delayMilliseconds: 0))
            ExecuteSelected(modifiers);
    }

    private void ExecuteSelected(ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Shift))
            OpenSelected(runAsAdministrator: true);
        else if (modifiers.HasFlag(ModifierKeys.Control))
            RevealSelected();
        else
            OpenSelected(runAsAdministrator: false);
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
            Padding = new Thickness(4)
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
            menu.Items.Add(new Separator { Style = (Style)FindResource("LumaMenuSeparator") });
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
        var item = new System.Windows.Controls.MenuItem
        {
            Header = title,
            Style = (Style)menu.FindResource("LumaMenuItem")
        };
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
        if (_fullResultsMode)
            DetailFavoriteButton.Content = favorite ? "取消收藏" : "收藏";
        StatusText.Text = favorite ? "已加入收藏" : "已取消收藏";
        if (_settings.Current.ResultSort.Equals(ResultRanker.Usage, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(SearchBox.Text))
            _ = SearchCurrentTextAsync(delayMilliseconds: 0);
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
        {
            StatusText.Text = selected.Target;
            UpdateDetailActions(selected);
            if (_fullResultsMode)
                _ = LoadSelectedDetailsAsync(selected);
        }
        else
        {
            _detailCancellation?.Cancel();
            ClearDetailPanel();
        }
    }

    private async Task LoadSelectedDetailsAsync(LauncherResult selected)
    {
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
        _detailCancellation = new CancellationTokenSource();
        var token = _detailCancellation.Token;
        var generation = Interlocked.Increment(ref _detailGeneration);

        DetailKindText.Text = selected.SourceLabel;
        DetailLocationText.Text = selected.Target;
        DetailSizeText.Text = "正在读取…";
        DetailModifiedText.Text = "正在读取…";
        DetailDescriptionText.Text = selected.Subtitle;

        try
        {
            var details = await ResultDetailsService.LoadAsync(selected, token);
            if (!_fullResultsMode || generation != _detailGeneration ||
                !ReferenceEquals(ResultsList.SelectedItem, selected))
                return;
            DetailKindText.Text = details.Kind.ToUpperInvariant();
            DetailLocationText.Text = details.Location;
            DetailSizeText.Text = details.Size;
            DetailModifiedText.Text = details.Modified;
            DetailDescriptionText.Text = details.Description;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            DiagnosticsService.Log("result-details", exception);
            if (generation == _detailGeneration)
            {
                DetailSizeText.Text = "—";
                DetailModifiedText.Text = "—";
                DetailDescriptionText.Text = "暂时无法读取详细信息。";
            }
        }
    }

    private void UpdateDetailActions(LauncherResult selected)
    {
        DetailActionsPanel.Visibility = Visibility.Visible;
        DetailOpenButton.Content = selected.Kind == LauncherResultKind.Calculation ? "复制结果" : "打开";
        DetailCopyButton.Content = selected.Kind == LauncherResultKind.Calculation ? "复制结果" : "复制路径";
        DetailRevealButton.Visibility = selected.IsFileSystemItem ? Visibility.Visible : Visibility.Collapsed;
        DetailAdminButton.Visibility = selected.CanRunAsAdministrator ? Visibility.Visible : Visibility.Collapsed;
        DetailQuickSwitchButton.Visibility = selected.Kind is LauncherResultKind.File or LauncherResultKind.Folder && _quickSwitch.HasTarget
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailFavoriteButton.Visibility = selected.Kind == LauncherResultKind.Calculation
            ? Visibility.Collapsed
            : Visibility.Visible;
        DetailFavoriteButton.Content = _search.IsFavorite(selected) ? "取消收藏" : "收藏";
    }

    private void ClearDetailPanel()
    {
        DetailKindText.Text = "未选择结果";
        DetailLocationText.Text = "—";
        DetailSizeText.Text = "—";
        DetailModifiedText.Text = "—";
        DetailDescriptionText.Text = "选择左侧结果以查看详细信息。";
        DetailActionsPanel.Visibility = Visibility.Collapsed;
    }

    private void DetailOpen_Click(object sender, RoutedEventArgs e) => OpenSelected(false);

    private void DetailReveal_Click(object sender, RoutedEventArgs e) => RevealSelected();

    private void DetailCopy_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not LauncherResult selected)
            return;
        ResultExecutionService.CopyPath(selected);
        StatusText.Text = selected.Kind == LauncherResultKind.Calculation ? "已复制结果" : "已复制路径";
    }

    private void DetailFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is LauncherResult selected)
            ToggleFavorite(selected);
    }

    private void DetailQuickSwitch_Click(object sender, RoutedEventArgs e) => _ = QuickSwitchSelectedAsync();

    private void DetailAdmin_Click(object sender, RoutedEventArgs e) => OpenSelected(true);

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
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
        _search.Dispose();
        _hotkey.Unregister();
        _source?.RemoveHook(WindowProcedure);
    }
}
