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
    }

    private void LoadControls(AppSettings settings)
    {
        settings.Normalize();
        _webSearchUrl = settings.WebSearchUrl;
        HotkeyBox.Text = settings.Hotkey;
        LanguageBox.SelectedValue = settings.Language;
        ThemeBox.SelectedValue = settings.Theme;
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
            EverythingPathHint.Text = "快捷键格式无效。示例：Alt+E、Ctrl+Shift+F12、Win+Space。";
            EverythingPathHint.SetResourceReference(ForegroundProperty, "AccentBrush");
            HotkeyBox.Focus();
            return;
        }

        var settings = new AppSettings
        {
            Hotkey = hotkey,
            Theme = ThemeBox.SelectedValue as string ?? "System",
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
        if (ThemeBox.SelectedValue is string theme)
            ThemeService.Apply(theme);
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

    private void HotkeyPreset_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (HotkeyPresetBox?.SelectedValue is string preset && preset.Length > 0 && HotkeyBox is not null)
            HotkeyBox.Text = preset;
    }

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
        var rounded = 2;
        NativeMethods.DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        var backdrop = 2;
        NativeMethods.DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
    }
}
