using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher;

public sealed partial class SettingsWindow : Window
{
    private readonly string _originalTheme;
    private string _webSearchUrl;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _saved;

    public SettingsWindow(AppSettings settings)
    {
        _originalTheme = settings.Theme;
        _webSearchUrl = settings.WebSearchUrl;
        InitializeComponent();
        Width = Math.Min(540, Math.Max(320, SystemParameters.WorkArea.Width - 32));
        Height = Math.Min(760, Math.Max(280, SystemParameters.WorkArea.Height - 32));
        foreach (var (mode, label) in ResultRanker.Options)
            ResultSortBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = label, Tag = mode });
        LoadControls(settings);
        VersionText.Text = $"Luma {UpdateService.CurrentVersion} · Windows x64";
        SourceInitialized += (_, _) => ApplyDwmStyling();
        ShowSection("General");
    }

    private void SettingsSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (SettingsNav is null)
            return;
        var q = (SettingsSearchBox.Text ?? string.Empty).Trim();
        if (q.Length == 0)
        {
            foreach (System.Windows.Controls.ListBoxItem item in SettingsNav.Items)
                item.Visibility = Visibility.Visible;
            return;
        }
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["General"] = ["快捷键", "hotkey", "语言", "language", "密度", "density", "启动", "startup", "位置", "position"],
            ["Appearance"] = ["主题", "theme", "外观", "墨青", "赭暮", "素笺", "晴空", "日夜"],
            ["Search"] = ["排序", "sort", "别名", "alias", "命令", "command", "引擎", "engine", "目录", "folder"],
            ["Sources"] = ["everything", "索引", "index", "windows", "数据源"],
            ["Features"] = ["窗口", "window", "系统", "system", "书签", "bookmark", "游戏", "game", "预览", "preview", "快速切换"],
            ["Privacy"] = ["历史", "history", "剪贴板", "clipboard", "隐私", "导入", "导出"],
            ["About"] = ["更新", "update", "版本", "version", "许可", "license"]
        };
        foreach (System.Windows.Controls.ListBoxItem item in SettingsNav.Items)
        {
            var tag = item.Tag as string ?? string.Empty;
            var visible = map.TryGetValue(tag, out var keys) &&
                          keys.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                        q.Contains(k, StringComparison.OrdinalIgnoreCase)) ||
                          (item.Content as string ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase);
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SettingsNav_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SettingsNav?.SelectedItem is System.Windows.Controls.ListBoxItem { Tag: string tag })
            ShowSection(tag);
    }

    private void ShowSection(string tag)
    {
        if (SectionGeneral is null)
            return;
        SectionGeneral.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        SectionAppearance.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        SectionSearch.Visibility = tag == "Search" ? Visibility.Visible : Visibility.Collapsed;
        SectionSources.Visibility = tag == "Sources" ? Visibility.Visible : Visibility.Collapsed;
        SectionFeatures.Visibility = tag == "Features" ? Visibility.Visible : Visibility.Collapsed;
        SectionPrivacy.Visibility = tag == "Privacy" ? Visibility.Visible : Visibility.Collapsed;
        SectionAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll?.ScrollToHome();
    }

    private void LoadControls(AppSettings settings)
    {
        settings.Normalize();
        _webSearchUrl = settings.WebSearchUrl;
        HotkeyBox.Text = settings.Hotkey;
        LanguageBox.SelectedValue = settings.Language;
        DensityBox.SelectedValue = settings.Density == "Compact" ? "Compact" : "Comfortable";
        ThemeBox.SelectedValue = ThemeService.ResolveRequestedForUi(settings.Theme);
        DayThemeBox.SelectedValue = settings.DayTheme;
        NightThemeBox.SelectedValue = settings.NightTheme;
        ThemeService.ConfigureAutoPair(settings.DayTheme, settings.NightTheme);
        UpdateThemeChrome(settings.Theme);
        StartupBox.IsChecked = settings.StartWithWindows;
        EverythingModeBox.SelectedValue = settings.EverythingPathMode.Equals("Manual", StringComparison.OrdinalIgnoreCase)
            ? "Manual"
            : "Auto";
        EverythingPathBox.Text = settings.EverythingPath;
        EverythingLifecycleBox.SelectedValue = settings.EverythingLifecycle.Equals("Connect", StringComparison.OrdinalIgnoreCase)
            ? "Connect"
            : "Managed";
        QuickSwitchBox.IsChecked = settings.EnableQuickSwitch;
        WindowSwitcherBox.IsChecked = settings.EnableWindowSwitcher;
        SystemCommandsBox.IsChecked = settings.EnableSystemCommands;
        BookmarksBox.IsChecked = settings.EnableBookmarks;
        GameModeBox.IsChecked = settings.EnableGameMode;
        PreviewBox.IsChecked = settings.EnablePreview;
        WindowsIndexBox.IsChecked = settings.PreferWindowsIndex;
        HistoryBox.IsChecked = settings.RecordHistory;
        QueryHistoryBox.IsChecked = settings.RecordQueryHistory;
        ClipboardBox.IsChecked = settings.EnableClipboardHistory;
        RememberPositionBox.IsChecked = settings.RememberWindowPosition;
        ResultSortBox.SelectedValue = ResultRanker.Normalize(settings.ResultSort);
        AliasesBox.Text = settings.Aliases;
        AppFoldersBox.Text = settings.AppFolders;
        CommandsBox.Text = settings.CustomCommands;
        EnginesBox.Text = settings.SearchEngines;
        UpdateEverythingControls();
    }

    public void SyncResultSort(string mode) => ResultSortBox.SelectedValue = ResultRanker.Normalize(mode);

    public event Action<AppSettings>? SettingsSaved;
    public event Action? ClearHistoryRequested;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var everythingMode = EverythingModeBox.SelectedValue as string ?? "Auto";
        var everythingPath = EverythingPathBox.Text.Trim().Trim('"');
        if (everythingMode == "Manual" && !File.Exists(everythingPath))
        {
            EverythingPathHint.Text = "找不到这个 Everything.exe，请重新选择。";
            EverythingPathHint.SetResourceReference(ForegroundProperty, "AccentBrush");
            EverythingPathBox.Focus();
            return;
        }

        var hotkey = HotkeyBox.Text.Trim();
        if (!HotkeyGesture.TryParse(hotkey, out _))
        {
            EverythingPathHint.Text = "快捷键格式无效。点击输入框后按下组合键。";
            EverythingPathHint.SetResourceReference(ForegroundProperty, "AccentBrush");
            HotkeyBox.Focus();
            return;
        }
        if (!HotkeyService.TryProbe(hotkey, out var probeError))
        {
            EverythingPathHint.Text = $"快捷键 {hotkey} 可能已被占用（错误 {probeError}），请换一个组合。";
            EverythingPathHint.SetResourceReference(ForegroundProperty, "DangerBrush");
            HotkeyBox.Focus();
            return;
        }

        // Validate custom commands before save so format errors surface immediately.
        var commandIssues = ValidateCustomCommands(CommandsBox.Text);
        if (commandIssues.Count > 0)
        {
            EverythingPathHint.Text = "自定义命令有问题：" + commandIssues[0];
            EverythingPathHint.SetResourceReference(ForegroundProperty, "DangerBrush");
            return;
        }

        var settings = new AppSettings
        {
            Hotkey = hotkey,
            Theme = ThemeBox.SelectedValue as string ?? "Auto",
            DayTheme = DayThemeBox.SelectedValue as string ?? "Paper",
            NightTheme = NightThemeBox.SelectedValue as string ?? "InkTeal",
            Density = DensityBox.SelectedValue as string ?? "Comfortable",
            StartWithWindows = StartupBox.IsChecked == true,
            EverythingPathMode = everythingMode,
            EverythingPath = everythingPath,
            EverythingLifecycle = EverythingLifecycleBox.SelectedValue as string ?? "Managed",
            EnableQuickSwitch = QuickSwitchBox.IsChecked == true,
            EnableWindowSwitcher = WindowSwitcherBox.IsChecked == true,
            EnableSystemCommands = SystemCommandsBox.IsChecked == true,
            EnableBookmarks = BookmarksBox.IsChecked == true,
            EnableGameMode = GameModeBox.IsChecked == true,
            EnablePreview = PreviewBox.IsChecked == true,
            PreferWindowsIndex = WindowsIndexBox.IsChecked == true,
            RecordHistory = HistoryBox.IsChecked == true,
            RecordQueryHistory = QueryHistoryBox.IsChecked == true,
            EnableClipboardHistory = ClipboardBox.IsChecked == true,
            RememberWindowPosition = RememberPositionBox.IsChecked == true,
            Language = LanguageBox.SelectedValue as string ?? "zh-CN",
            Aliases = AliasesBox.Text.Trim(),
            AppFolders = AppFoldersBox.Text.Trim(),
            CustomCommands = CommandsBox.Text.Trim(),
            SearchEngines = EnginesBox.Text.Trim(),
            WebSearchUrl = _webSearchUrl,
            ResultSort = ResultSortBox.SelectedValue as string ?? ResultRanker.Smart
        };
        try
        {
            SettingsSaved?.Invoke(settings);
            _saved = true;
            Close();
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("settings-save", exception);
            EverythingPathHint.Text = "设置保存失败，请稍后重试。";
            EverythingPathHint.SetResourceReference(ForegroundProperty, "AccentBrush");
        }
    }

    private void EverythingMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (EverythingPathGrid is not null)
            UpdateEverythingControls();
    }

    private void UpdateEverythingControls()
    {
        var manual = (EverythingModeBox.SelectedValue as string) == "Manual";
        EverythingPathGrid.IsEnabled = manual;
        EverythingPathGrid.Opacity = manual ? 1 : 0.5;
        EverythingPathHint.SetResourceReference(ForegroundProperty, "FaintTextBrush");
        var connectOnly = (EverythingLifecycleBox.SelectedValue as string) == "Connect";
        if (manual)
        {
            EverythingPathHint.Text = connectOnly
                ? "选择用于检测的 Everything.exe；Luma 不会负责启动或关闭它。"
                : "选择 Everything.exe；保存后会立即以隐藏模式启动。";
            return;
        }

        var detected = EverythingSearchService.FindExecutable();
        EverythingPathHint.Text = detected is null
            ? "未自动找到 Everything，可切换为手动指定。"
            : connectOnly ? $"将仅连接已有实例：{detected}" : $"已找到：{detected}";
    }

    private void BrowseEverything_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 Everything.exe",
            Filter = "Everything (Everything.exe)|Everything.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
            FileName = "Everything.exe"
        };
        if (dialog.ShowDialog(this) == true)
            EverythingPathBox.Text = dialog.FileName;
    }

    private void ThemeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedValue is not string theme)
            return;
        ThemeService.ConfigureAutoPair(
            DayThemeBox?.SelectedValue as string ?? "Paper",
            NightThemeBox?.SelectedValue as string ?? "InkTeal");
        ThemeService.Apply(theme);
        UpdateThemeChrome(theme);
    }

    private void DayThemeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ThemeService.ConfigureAutoPair(
            DayThemeBox.SelectedValue as string ?? "Paper",
            NightThemeBox?.SelectedValue as string ?? "InkTeal");
        ThemeService.Apply(ThemeBox.SelectedValue as string ?? ThemeService.Auto);
        UpdateThemeChrome(ThemeBox.SelectedValue as string ?? ThemeService.Auto);
    }

    private void NightThemeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ThemeService.ConfigureAutoPair(
            DayThemeBox?.SelectedValue as string ?? "Paper",
            NightThemeBox.SelectedValue as string ?? "InkTeal");
        ThemeService.Apply(ThemeBox.SelectedValue as string ?? ThemeService.Auto);
        UpdateThemeChrome(ThemeBox.SelectedValue as string ?? ThemeService.Auto);
    }

    private void UpdateThemeChrome(string theme)
    {
        if (ThemeEffectiveHint is null || AutoPairPanel is null)
            return;
        var isAuto = theme.Equals("Auto", StringComparison.OrdinalIgnoreCase) ||
                     theme.Equals("System", StringComparison.OrdinalIgnoreCase);
        AutoPairPanel.Visibility = isAuto ? Visibility.Visible : Visibility.Collapsed;
        AutoPairPanel.Opacity = isAuto ? 1 : 0.45;
        AutoPairPanel.IsEnabled = isAuto;

        var effective = ThemeService.ResolveEffectiveTheme(theme);
        var label = effective switch
        {
            ThemeService.InkTeal => "墨青 · 夜",
            ThemeService.Dusk => "赭暮 · 夜",
            ThemeService.Paper => "素笺 · 日",
            ThemeService.Sky => "晴空 · 日",
            _ => effective
        };
        ThemeEffectiveHint.Text = isAuto
            ? $"当前生效：{label}（随系统浅色/深色自动切换）"
            : $"当前生效：{label}";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Apply(_originalTheme);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        if (!_saved)
            ThemeService.Apply(_originalTheme);
        base.OnClosed(e);
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "正在检查更新…";
        try { UpdateStatusText.Text = await UpdateService.CheckAsync(_lifetime.Token); }
        catch (OperationCanceledException) { UpdateStatusText.Text = "检查已取消或超时，可重试。"; }
        catch (Exception exception) { DiagnosticsService.Log("update-check", exception); UpdateStatusText.Text = "无法连接更新服务器，请重试或打开下载页。"; }
    }

    private void OpenRelease_Click(object sender, RoutedEventArgs e) => OpenWebsite(UpdateService.ReleasesUrl);
    private void InstallEverything_Click(object sender, RoutedEventArgs e) => OpenWebsite("https://www.voidtools.com/downloads/");
    private static void OpenWebsite(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "无法打开浏览器"); }
    }

    private async void DetectEverything_Click(object sender, RoutedEventArgs e)
    {
        EverythingPathHint.Text = "正在检测连接…";
        using var service = new EverythingSearchService();
        service.Configure("Auto", string.Empty, "Connect");
        try
        {
            var response = await service.SearchAsync("file: ext:exe", 1, _lifetime.Token);
            EverythingPathHint.Text = response.Available ? "Everything 已连接，可进行文件搜索。" :
                response.StatusText + "。请安装普通版（Lite 版不支持连接），启动后重试。";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { EverythingPathHint.Text = "检测失败：" + exception.Message; }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        try { ClearHistoryRequested?.Invoke(); PrivacyStatusText.Text = "使用历史已清空，收藏已保留。"; }
        catch (Exception exception) { PrivacyStatusText.Text = "清空失败：" + exception.Message; }
    }

    public event Action? ClearQueryHistoryRequested;

    private void ClearQueryHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ClearQueryHistoryRequested?.Invoke();
            PrivacyStatusText.Text = "搜索词历史已清空。";
        }
        catch (Exception exception) { PrivacyStatusText.Text = "清空失败：" + exception.Message; }
    }

    private string _hotkeyBeforeCapture = "Alt+Space";
    private bool _capturingHotkey;

    private static List<string> ValidateCustomCommands(string value)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
            return issues;
        var lineNo = 0;
        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            lineNo++;
            if (line.StartsWith('#'))
                continue;
            var parts = line.Split('|');
            if (parts.Length < 3)
            {
                issues.Add($"第 {lineNo} 行字段不足（需 关键词|标题|程序）");
                continue;
            }
            if (string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[2]))
                issues.Add($"第 {lineNo} 行关键词或程序为空");
        }
        return issues;
    }

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _hotkeyBeforeCapture = HotkeyBox.Text;
        _capturingHotkey = true;
        HotkeyBox.Text = "按下快捷键…";
        if (HotkeyCaptureHint is not null)
        {
            HotkeyCaptureHint.Text = "正在录制：请按下组合键（至少一个修饰键）。Esc 取消。";
            HotkeyCaptureHint.SetResourceReference(ForegroundProperty, "AccentBrush");
        }
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_capturingHotkey)
            return;
        _capturingHotkey = false;
        if (HotkeyBox.Text == "按下快捷键…")
            HotkeyBox.Text = _hotkeyBeforeCapture;
        if (HotkeyCaptureHint is not null)
        {
            HotkeyCaptureHint.Text = "点击输入框后，直接按下你要用的组合键。Esc 取消。";
            HotkeyCaptureHint.SetResourceReference(ForegroundProperty, "FaintTextBrush");
        }
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey)
            return;

        // Modifier-only presses: keep waiting for the real key.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.ImeConvert or Key.ImeAccept)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            HotkeyBox.Text = _hotkeyBeforeCapture;
            _capturingHotkey = false;
            if (HotkeyCaptureHint is not null)
            {
                HotkeyCaptureHint.Text = "已取消录制。";
                HotkeyCaptureHint.SetResourceReference(ForegroundProperty, "FaintTextBrush");
            }
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            if (HotkeyCaptureHint is not null)
                HotkeyCaptureHint.Text = "需要至少一个修饰键（Ctrl / Alt / Shift / Win）。";
            e.Handled = true;
            return;
        }

        var gesture = FormatGesture(modifiers, key);
        if (!HotkeyGesture.TryParse(gesture, out _))
        {
            if (HotkeyCaptureHint is not null)
                HotkeyCaptureHint.Text = $"不支持的按键：{gesture}";
            e.Handled = true;
            return;
        }

        HotkeyBox.Text = gesture;
        _capturingHotkey = false;
        if (HotkeyCaptureHint is not null)
        {
            HotkeyCaptureHint.Text = $"已录制：{gesture}（保存后生效）";
            HotkeyCaptureHint.SetResourceReference(ForegroundProperty, "AccentBrush");
        }
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private static string FormatGesture(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyToToken(key));
        return string.Join("+", parts);
    }

    private static string KeyToToken(Key key) => key switch
    {
        Key.Space => "Space",
        Key.Tab => "Tab",
        Key.Escape => "Esc",
        Key.Enter or Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Left => "Left",
        Key.Up => "Up",
        Key.Right => "Right",
        Key.Down => "Down",
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.F1 and <= Key.F24 => "F" + (1 + key - Key.F1),
        _ => key.ToString()
    };

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "正在下载并校验…";
        try
        {
            UpdateStatusText.Text = await UpdateService.DownloadAndStageAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) { UpdateStatusText.Text = "下载已取消或超时，可重试。"; }
        catch (Exception exception)
        {
            DiagnosticsService.Log("update-download", exception);
            UpdateStatusText.Text = "下载失败，请打开下载页手动替换。";
        }
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Luma 配置 (*.json)|*.json", FileName = "luma-settings.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var store = new SettingsStore();
            if (store.CompatibilityWarning is { } warning) throw new InvalidOperationException(warning);
            var settings = store.Current;
            AtomicFileService.WriteAllText(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(settings,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            PrivacyStatusText.Text = "已导出已保存的配置（不含历史和收藏）。";
        }
        catch (Exception exception) { PrivacyStatusText.Text = "导出失败：" + exception.Message; }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Luma 配置 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 1024 * 1024) throw new InvalidDataException("配置文件不能超过 1 MB。");
            var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(dialog.FileName))
                ?? throw new InvalidDataException("配置为空。");
            LoadControls(settings.Normalize());
            PrivacyStatusText.Text = "配置已载入，请检查自定义命令后点击保存应用。";
        }
        catch (Exception exception) { PrivacyStatusText.Text = "导入失败：" + exception.Message; }
    }

    private void Licenses_Click(object sender, RoutedEventArgs e)
    {
        using var stream = typeof(SettingsWindow).Assembly.GetManifestResourceStream("Luma.ThirdPartyNotices.md");
        if (stream is null) return;
        using var reader = new StreamReader(stream);
        new Window { Title = "Luma · 开源许可", Owner = this, Width = Math.Min(680, SystemParameters.WorkArea.Width - 40),
            Height = Math.Min(560, SystemParameters.WorkArea.Height - 40), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new System.Windows.Controls.TextBox { Text = "Luma Launcher · MIT License\n\n" + reader.ReadToEnd(),
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(18),
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto } }.ShowDialog();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void ApplyDwmStyling()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var corner = 1; // DWMWCP_DONOTROUND — Border owns the rounded shape
        NativeMethods.DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
        var backdrop = 0; // DWMSBT_NONE
        NativeMethods.DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
    }
}
