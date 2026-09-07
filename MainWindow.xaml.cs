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
    private const double CompactHeight = 76;
    private const double ExpandedHeight = 600;
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
    private LauncherController _controller = null!;
    private readonly QuickSwitchService _quickSwitch = new();
    private readonly SettingsStore _settings;
    private readonly bool _previewMode;
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
    private string? _completedQuery;
    private bool _hasMore;
    private int? _fileMatchCount;
    private int _fullResultLimit = FullSearchResultLimit;
    private bool _composing;
    private bool _historyPanelOpen;
    private readonly System.Diagnostics.Stopwatch _queryTimer = new();
    private System.Windows.Threading.DispatcherTimer? _progressTimer;
    private DateTimeOffset _toastUntil;

    /// <summary>Bound from the result item template; density-aware row height.</summary>
    public double ResultRowHeight => _settings.Current.Density == "Compact" ? 46 : 54;

    internal Func<int, IntPtr, IntPtr, bool>? TrayMessageHandler { get; set; }
    public GameModeService GameMode => _controller.GameMode;

    public MainWindow(SettingsStore settings, bool previewMode = false)
    {
        _previewMode = previewMode;
        _settings = settings;
        _controller = new LauncherController(_search, settings);
        InitializeComponent();
        ResultsList.ItemsSource = _results;
        UpdateSortButton();
        SearchHint.Text = UiStrings.Get("SearchHint");
        TextCompositionManager.AddPreviewTextInputStartHandler(SearchBox, (_, _) =>
        { _composing = true; _completedQuery = null; _searchCancellation?.Cancel(); });
        TextCompositionManager.AddPreviewTextInputHandler(SearchBox, (_, _) =>
        {
            _composing = false;
            Dispatcher.BeginInvoke(() => SearchBox_TextChanged(SearchBox, null!), System.Windows.Threading.DispatcherPriority.Background);
        });
        SearchBox.LostKeyboardFocus += (_, _) => _composing = false;
        _controller.GameMode.SuppressedChanged += suppressed =>
            Dispatcher.BeginInvoke(() => StatusText.Text = suppressed
                ? UiStrings.Get("GameModeOn")
                : UiStrings.Get("GameModeOff"));
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
        UiStrings.SetCulture(_settings.Current.Language);
        SearchHint.Text = UiStrings.Get("SearchHint");
        var sortChanged = !Equals(SortButton.Tag, ResultRanker.Normalize(_settings.Current.ResultSort));
        var foldersChanged = _search.Configure(_settings.Current);
        _controller.ApplySettings(_settings.Current);
        UpdateSortButton();
        UpdateFilterButtons();
        if (sortChanged)
        {
            _completedQuery = null;
            _searchCancellation?.Cancel();
            if (IsVisible) _ = SearchCurrentTextAsync(0);
        }
        _quickSwitch.Enabled = _settings.Current.EnableQuickSwitch;
        if (!_previewMode) _ = EnsureEverythingRunningAsync();
        if (foldersChanged && _search.IsInitialized)
            _ = ReloadAppsAsync();
        if (_source is null || _previewMode)
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
        if (_settings.Current.RememberWindowPosition &&
            _settings.Current.WindowLeft is double left && _settings.Current.WindowTop is double top)
        {
            Left = left;
            Top = top;
        }
        else
        {
            PositionOnCursorMonitor();
        }
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
        QuickSwitchHint.Visibility = _quickSwitch.HasTarget ? Visibility.Visible : Visibility.Collapsed;
        AnimateShow();
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
            _ = SearchCurrentTextAsync(0);
        else if (_completedQuery != SearchBox.Text.Trim())
            _ = SearchCurrentTextAsync(0);
    }

    public void HideLauncher()
    {
        _composing = false;
        _searchCancellation?.Cancel();
        if (HelpOverlay is not null)
            HelpOverlay.Visibility = Visibility.Collapsed;
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
        }
        catch (OperationCanceledException) { }
    }

    public void OpenSettings() => SettingsRequested?.Invoke();
    public void RequestExit() => ExitRequested?.Invoke();

    public void ShutdownEverything()
    {
        _searchCancellation?.Cancel();
        _search.ShutdownEverything();
    }

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
            if (_controller.GameMode.Suppressed)
            {
                // Swallow the hotkey while a fullscreen app is in front.
                handled = true;
                return IntPtr.Zero;
            }
            ToggleLauncher();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _completedQuery = null;
        _fullResultLimit = FullSearchResultLimit;
        if (_composing) return;
        _queryTimer.Restart();
        _ = SearchCurrentTextAsync(SearchDebounceMilliseconds);
    }

    private async Task<bool> SearchCurrentTextAsync(int delayMilliseconds, bool preserveResults = false)
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
        var previousSelection = preserveResults ? ResultsList.SelectedItem as LauncherResult : null;
        SetSearchPending(true);
        if (previousSelection is not null) ResultsList.SelectedItem = previousSelection;

        try
        {
            await Task.Delay(delayMilliseconds, token);
            if (generation != _searchGeneration || !pendingQuery.Equals(SearchBox.Text.Trim(), StringComparison.Ordinal))
                return false;

            var keepExistingResults = (_fullResultsMode || preserveResults) && _allResults.Count > 0;
            SetExpanded(keepExistingResults ? _results.Count : 0, showBody: true);
            if (!keepExistingResults)
            {
                _allResults.Clear();
                _results.Clear();
                MoreButton.Visibility = Visibility.Collapsed;
                ShowEmptyState(true, "正在搜索…", "可按 Enter 立即提交");
                ResultsList.Visibility = Visibility.Collapsed;
            }
            StatusText.Text = keepExistingResults ? "正在加载更多结果…" : "综合搜索";
            SetProgressVisible(!keepExistingResults);

            var resultLimit = _fullResultsMode ? _fullResultLimit : QuickSearchResultLimit;
            var publishedPartial = false;
            var batch = await _search.SearchAsync(pendingQuery, resultLimit, token, _activeFilter, partial =>
                Dispatcher.Invoke(() =>
                {
                    if (generation != _searchGeneration || token.IsCancellationRequested || preserveResults) return;
                    ApplyBatch(partial, "文件搜索中…");
                    publishedPartial = true;
                    SetSearchPending(false);
                }));
            if (generation != _searchGeneration || !pendingQuery.Equals(SearchBox.Text.Trim(), StringComparison.Ordinal))
                return false;
            if (token.IsCancellationRequested) return false;
            SetProgressVisible(false);
            _completedQuery = batch.EverythingAvailable ? pendingQuery : null;
            ApplyBatch(batch, "没有找到匹配项", preserveResults || publishedPartial);
            return true;
        }
        catch (OperationCanceledException) { SetProgressVisible(false); return false; }
        catch (Exception exception) when (generation == _searchGeneration)
        {
            DiagnosticsService.Log("search", exception);
            ApplyBatch(new SearchBatch([], "搜索暂时不可用", false), "搜索暂时不可用，请稍后重试");
            SetProgressVisible(false);
            SetExpanded(0, showBody: true);
            return false;
        }
        finally
        {
            if (generation == _searchGeneration)
            {
                SetProgressVisible(false);
                SetSearchPending(false);
            }
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
            var resultLimit = _fullResultsMode ? _fullResultLimit : QuickSearchResultLimit;
            var batch = await _search.GetRecommendationsAsync(resultLimit, token);
            if (generation != _searchGeneration || !string.IsNullOrWhiteSpace(SearchBox.Text))
                return;
            ApplyBatch(batch, "输入应用、文件名或 Everything 语法");
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyBatch(SearchBatch batch, string emptyMessage, bool preserveSelection = false)
    {
        var selection = preserveSelection ? (ResultsList.SelectedItem as LauncherResult)?.Target : null;
        _hasMore = batch.HasMore;
        _fileMatchCount = batch.FileMatchCount;
        _allResults.Clear();
        _allResults.AddRange(batch.Results);
        _batchStatus = batch.StatusText;
        _pageIndex = 0;
        ApplyCurrentPage(emptyMessage);
        if (selection is not null)
            ResultsList.SelectedItem = _results.FirstOrDefault(item => item.Target == selection) ?? _results.FirstOrDefault();
        if (_queryTimer.IsRunning)
        {
            _queryTimer.Stop();
            DiagnosticsService.Log("first-results", $"input_to_results_ms={_queryTimer.ElapsedMilliseconds}; items={batch.Results.Count}");
        }
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
        ShowEmptyState(_results.Count == 0, emptyMessage,
            _activeFilter != "All" ? "可点上方筛选切回「全部」" : "试试 Everything 语法或更短的关键词");
        ResultsList.Visibility = _results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateSortButton();
        MoreButton.Visibility = _fullResultsMode || filtered.Count > 0 || _hasMore
            ? Visibility.Visible
            : Visibility.Collapsed;
        MoreButton.Content = _fullResultsMode ? "收起" : "展开";
        LoadMoreButton.Visibility = _fullResultsMode && _hasMore ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = filtered.Count == 0
            ? _batchStatus
            : $"{filtered.Count} 个结果{(_fileMatchCount is > 0 ? $" · 文件 ≥ {_fileMatchCount}" : "")}{(_hasMore ? " · 可继续加载" : "")} · {_batchStatus}";
        UpdateFilterButtons();
        SetExpanded(_results.Count, _allResults.Count > 0 || SearchBox.Text.Length > 0);
        FadeResultsIn();
    }

    private void ShowEmptyState(bool show, string title, string hint)
    {
        EmptyState.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;
        EmptyTitle.Text = title;
        EmptyHint.Text = hint;
    }

    private void SetProgressVisible(bool visible)
    {
        if (ProgressHost is null)
            return;
        ProgressHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            _progressTimer?.Stop();
            return;
        }
        _progressTimer ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(16), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                if (ProgressPulse is null)
                    return;
                var x = ProgressPulse.Margin.Left + 6;
                var max = Math.Max(0, ProgressHost.ActualWidth - ProgressPulse.Width);
                if (x > max)
                    x = -ProgressPulse.Width;
                ProgressPulse.Margin = new Thickness(x, 0, 0, 0);
            }, Dispatcher);
        ProgressPulse.Margin = new Thickness(0, 0, 0, 0);
        _progressTimer.Start();
    }

    private void FadeResultsIn()
    {
        if (!SystemParameters.ClientAreaAnimation || ResultsList is null)
            return;
        ResultsList.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(90)));
    }

    private void ShowToast(string message)
    {
        StatusText.Text = message;
        _toastUntil = DateTimeOffset.UtcNow.AddMilliseconds(1200);
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
            button.SetResourceReference(BackgroundProperty, selected ? "AccentSoftBrush" : "SurfaceSubtleBrush");
            button.SetResourceReference(ForegroundProperty, selected ? "AccentBrush" : "MutedTextBrush");
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
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

    private void ResultItem_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        ResultItem_Loaded(sender, new RoutedEventArgs());

    private void SetExpanded(int resultCount, bool showBody)
    {
        ResultsRow.Height = new GridLength(showBody ? 1 : 0, showBody ? GridUnitType.Star : GridUnitType.Pixel);
        FooterRow.Height = new GridLength(showBody ? 36 : 0);
        ResultsHost.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;
        Footer.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;
        var row = ResultRowHeight + 2;
        var resultArea = 34 + (resultCount > 0 ? Math.Min(PageSize, resultCount) * row + 8 : 64);
        var available = GetFullResultsSize();
        var targetHeight = _fullResultsMode
            ? available.Height
            : showBody ? Math.Min(available.Height, Math.Min(ExpandedHeight, CompactHeight + resultArea + 36)) : CompactHeight;
        var targetWidth = _fullResultsMode ? available.Width : Math.Min(CompactWidth, available.Width);
        if (_fullResultsMode)
        {
            var showDetails = available.Width >= 840;
            DetailsPane.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
            DetailsDivider.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
            DetailsPaneColumn.Width = new GridLength(showDetails ? 0.42 : 0, GridUnitType.Star);
            DetailsDividerColumn.Width = new GridLength(showDetails ? 21 : 0);
        }
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
            Math.Max(320, Math.Min(FullResultsWidth, availableWidth)),
            Math.Max(260, Math.Min(FullResultsHeight, availableHeight)));
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
        _fullResultLimit = FullSearchResultLimit;
        _pageIndex = 0;
        _ = SearchCurrentTextAsync(0);
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
        _pageIndex = 0;
        ApplyCurrentPage();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text) && (_hasMore || _allResults.Count >= QuickSearchResultLimit))
            _ = SearchCurrentTextAsync(0, true);
    }

    private void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_searchPending) return;
        _fullResultLimit = checked(_fullResultLimit + FullSearchResultLimit);
        _ = SearchCurrentTextAsync(0, true);
    }

    public void ClearHistory() { _search.ClearHistory(); _completedQuery = null; }
    public void ClearQueryHistory() => _controller.ClearQueryHistory();

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
        _pageIndex = 0;
        ApplyCurrentPage();
        if (!animate)
        {
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            Width = Math.Min(CompactWidth, GetFullResultsSize().Width);
            var showBody = _allResults.Count > 0 || SearchBox.Text.Length > 0;
            var resultArea = 36 + (_results.Count > 0 ? Math.Min(PageSize, _results.Count) * 52 + 8 : 72);
            Height = showBody ? Math.Min(GetFullResultsSize().Height, Math.Min(ExpandedHeight, CompactHeight + resultArea + 38)) : CompactHeight;
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

    private void UpdateSortButton()
    {
        SortButton.Tag = ResultRanker.Normalize(_settings.Current.ResultSort);
        SortButton.Content = $"排序 · {ResultRanker.Label(_settings.Current.ResultSort)} ▾";
        System.Windows.Automation.AutomationProperties.SetName(SortButton, $"结果排序：{ResultRanker.Label(_settings.Current.ResultSort)}");
    }

    internal System.Windows.Controls.ContextMenu CreateSortMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            Style = (Style)FindResource("LumaContextMenu")
        };
        foreach (var (mode, label) in ResultRanker.Options)
        {
            var selected = mode == ResultRanker.Normalize(_settings.Current.ResultSort);
            // The shared menu template has no checkmark presenter; include a visible mark.
            var item = new System.Windows.Controls.MenuItem { Header = (selected ? "✓  " : "    ") + label,
                Tag = mode, IsCheckable = true, IsChecked = selected, Style = (Style)FindResource("LumaMenuItem") };
            item.Click += (_, _) => ChangeSort(mode);
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => _contextMenuOpen = false;
        return menu;
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = CreateSortMenu();
        menu.PlacementTarget = SortButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        _contextMenuOpen = true;
        menu.IsOpen = true;
    }

    internal void ChangeSort(string mode)
    {
        mode = ResultRanker.Normalize(mode);
        if (mode == _settings.Current.ResultSort) return;
        try
        {
            var settings = _settings.Current.Copy();
            settings.ResultSort = mode;
            _settings.Save(settings);
            _search.Configure(_settings.Current);
            UpdateSortButton();
            ResultSortChanged?.Invoke(mode);
            _completedQuery = null;
            _pageIndex = 0;
            _fullResultLimit = FullSearchResultLimit;
            _searchCancellation?.Cancel();
            if (IsVisible) _ = SearchCurrentTextAsync(0);
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("sort-save", exception);
            StatusText.Text = "排序保存失败，请重试";
        }
    }

    public event Action<string>? ResultSortChanged;

    private static void ApplyDwmStyling(IntPtr handle)
    {
        // Window is AllowsTransparency + transparent background; the Border alone
        // owns the rounded silhouette. Force DWM not to add a second rounded clip.
        var corner = 1; // DWMWCP_DONOTROUND
        NativeMethods.DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
        var backdrop = 0; // DWMSBT_NONE
        NativeMethods.DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_contextMenuOpen || _composing || e.Key == Key.ImeProcessed) return;
        if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase && e.Key is Key.Enter or Key.Space) return;
        if (e.Key == Key.Escape)
        {
            if (HelpOverlay?.Visibility == Visibility.Visible)
            {
                HelpOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
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

        if (e.Key == Key.F1)
        {
            ToggleHelpOverlay();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleHistoryPanel();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _controller.GameMode.ToggleManualSuspend();
            StatusText.Text = _controller.GameMode.Suppressed
                ? UiStrings.Get("GameModeOn")
                : UiStrings.Get("GameModeOff");
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab && SearchBox.IsKeyboardFocusWithin && !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            var suggestion = _controller.QueryHistory.Suggest(SearchBox.Text.Trim(), 1).FirstOrDefault();
            if (suggestion is not null)
            {
                SearchBox.Text = suggestion;
                SearchBox.CaretIndex = SearchBox.Text.Length;
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None &&
            ResultsList.SelectedItem is LauncherResult spaceSelected && _fullResultsMode)
        {
            _ = LoadSelectedPreviewAsync(spaceSelected);
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

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control &&
            !SearchBox.IsKeyboardFocusWithin && ResultsList.SelectedItem is LauncherResult copyResult)
        {
            ResultExecutionService.CopyPath(copyResult);
            ShowToast("已复制路径");
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right && !SearchBox.IsKeyboardFocusWithin || e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
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
        if (_historyPanelOpen)
        {
            SearchBox.Text = selected.Target;
            _historyPanelOpen = false;
            HistoryPanel.Visibility = Visibility.Collapsed;
            _ = SearchCurrentTextAsync(0);
            return;
        }
        if (ResultExecutionService.Open(selected, runAsAdministrator))
        {
            if (selected.Kind != LauncherResultKind.Calculation)
                _search.RecordLaunch(selected);
            _controller.RecordCompletedQuery(SearchBox.Text);
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
            Style = (Style)FindResource("LumaContextMenu")
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
            {
                _ = LoadSelectedDetailsAsync(selected);
                if (_settings.Current.EnablePreview)
                    _ = LoadSelectedPreviewAsync(selected);
            }
        }
        else
        {
            _detailCancellation?.Cancel();
            ClearDetailPanel();
        }
    }

    private async Task LoadSelectedPreviewAsync(LauncherResult selected)
    {
        try
        {
            var info = await _controller.LoadPreviewAsync(selected, CancellationToken.None);
            if (info is null || !ReferenceEquals(ResultsList.SelectedItem, selected))
                return;
            DetailDescriptionText.Text = string.IsNullOrWhiteSpace(info.Description)
                ? info.KindLabel
                : $"{info.KindLabel} · {info.Description}";
            if (!string.IsNullOrWhiteSpace(info.SizeText) && (string.IsNullOrEmpty(DetailSizeText.Text) ||
                DetailSizeText.Text is "—" or "正在读取…"))
                DetailSizeText.Text = info.SizeText;
            if (!string.IsNullOrWhiteSpace(info.ModifiedText))
                DetailModifiedText.Text = info.ModifiedText;
            if (info.ThumbnailPath is not null && File.Exists(info.ThumbnailPath))
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(info.ThumbnailPath);
                bitmap.EndInit();
                DetailPreviewImage.Source = bitmap;
                DetailPreviewImage.Visibility = Visibility.Visible;
                DetailPreviewHost.Visibility = Visibility.Visible;
            }
            else
            {
                DetailPreviewImage.Source = null;
                DetailPreviewImage.Visibility = Visibility.Collapsed;
                DetailPreviewHost.Visibility = Visibility.Collapsed;
            }
            DetailSkeleton.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("preview", exception);
        }
    }

    private void ToggleHistoryPanel()
    {
        if (_historyPanelOpen)
        {
            _historyPanelOpen = false;
            HistoryPanel.Visibility = Visibility.Collapsed;
            SearchBox.Focus();
            return;
        }

        var entries = _controller.QueryHistory.Recent(20);
        HistoryList.ItemsSource = entries;
        HistoryEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = Visibility.Visible;
        _historyPanelOpen = true;
        HistoryList.Focus();
    }

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is not string query)
            return;
        SearchBox.Text = query;
        _historyPanelOpen = false;
        HistoryPanel.Visibility = Visibility.Collapsed;
        SearchBox.Focus();
        _ = SearchCurrentTextAsync(0);
    }

    private void ClearHistoryPanel_Click(object sender, RoutedEventArgs e)
    {
        _controller.ClearQueryHistory();
        HistoryList.ItemsSource = Array.Empty<string>();
        HistoryEmpty.Visibility = Visibility.Visible;
    }

    private void HistoryClose_Click(object sender, RoutedEventArgs e)
    {
        _historyPanelOpen = false;
        HistoryPanel.Visibility = Visibility.Collapsed;
        SearchBox.Focus();
    }

    private async Task LoadSelectedDetailsAsync(LauncherResult selected)
    {
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
        _detailCancellation = new CancellationTokenSource();
        var token = _detailCancellation.Token;
        var generation = Interlocked.Increment(ref _detailGeneration);

        DetailKindText.Text = selected.SourceLabel;
        SearchHighlight.SetText(DetailLocationText, selected.Target);
        DetailSizeText.Text = "正在读取…";
        DetailModifiedText.Text = "正在读取…";
        DetailDescriptionText.Text = selected.Subtitle;
        DetailPreviewHost.Visibility = Visibility.Collapsed;
        if (_settings.Current.EnablePreview && selected.IsFileSystemItem)
            DetailSkeleton.Visibility = Visibility.Visible;
        else
            DetailSkeleton.Visibility = Visibility.Collapsed;

        try
        {
            var details = await ResultDetailsService.LoadAsync(selected, token);
            if (!_fullResultsMode || generation != _detailGeneration ||
                !ReferenceEquals(ResultsList.SelectedItem, selected))
                return;
            DetailKindText.Text = details.Kind.ToUpperInvariant();
            SearchHighlight.SetText(DetailLocationText, details.Location);
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
        SearchHighlight.SetText(DetailLocationText, "—");
        DetailSizeText.Text = "—";
        DetailModifiedText.Text = "—";
        DetailDescriptionText.Text = "选择左侧结果以查看详细信息。";
        DetailActionsPanel.Visibility = Visibility.Collapsed;
        DetailPreviewHost.Visibility = Visibility.Collapsed;
        DetailSkeleton.Visibility = Visibility.Collapsed;
        DetailPreviewImage.Source = null;
    }

    private void DetailOpen_Click(object sender, RoutedEventArgs e) => OpenSelected(false);

    private void DetailReveal_Click(object sender, RoutedEventArgs e) => RevealSelected();

    private void DetailCopy_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not LauncherResult selected)
            return;
        ResultExecutionService.CopyPath(selected);
        ShowToast(selected.Kind == LauncherResultKind.Calculation ? "已复制结果" : "已复制路径");
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
        if (!_previewMode && IsVisible && !_contextMenuOpen && DateTimeOffset.UtcNow >= _ignoreDeactivateUntil)
            HideLauncher();
    }

    private void ToggleHelpOverlay()
    {
        if (HelpOverlay is null)
            return;
        HelpTitleText.Text = UiStrings.Get("HelpTitle");
        HelpOverlay.Visibility = HelpOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public void ShowHelpOnce() => ToggleHelpOverlay();

    private void HelpOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (HelpOverlay is not null)
            HelpOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private Point _dragStart;
    private bool _dragging;

    private void ResultsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (ResultsList.SelectedItem is not LauncherResult selected || !selected.IsFileSystemItem)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragging = false;
            return;
        }
        var pos = e.GetPosition(ResultsList);
        if (!_dragging)
        {
            _dragStart = pos;
            _dragging = true;
            return;
        }
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragging = false;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { selected.Target });
            DragDrop.DoDragDrop(ResultsList, data, DragDropEffects.Copy);
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("drag-drop", exception);
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1 || IsInteractiveElement(e.OriginalSource as DependencyObject))
            return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        if (_settings.Current.RememberWindowPosition)
        {
            try
            {
                var next = _settings.Current.Copy();
                next.WindowLeft = Left;
                next.WindowTop = Top;
                _settings.Save(next);
            }
            catch (Exception exception)
            {
                DiagnosticsService.Log("window-position-save", exception);
            }
        }
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
        if (_allowClose || _previewMode)
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
