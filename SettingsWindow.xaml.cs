using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        HotkeyBox.SelectedValue = settings.Hotkey;
        ThemeBox.SelectedValue = settings.Theme;
        StartupBox.IsChecked = settings.StartWithWindows;
        EverythingModeBox.SelectedValue = settings.EverythingPathMode.Equals("Manual", StringComparison.OrdinalIgnoreCase)
            ? "Manual"
            : "Auto";
        EverythingPathBox.Text = settings.EverythingPath;
        UpdateEverythingControls();
        SourceInitialized += (_, _) => ApplyDwmStyling();
    }

    public event Action<AppSettings>? SettingsSaved;

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

        SettingsSaved?.Invoke(new AppSettings
        {
            Hotkey = HotkeyBox.SelectedValue as string ?? "Alt+Space",
            Theme = ThemeBox.SelectedValue as string ?? "System",
            StartWithWindows = StartupBox.IsChecked == true,
            EverythingPathMode = everythingMode,
            EverythingPath = everythingPath
        });
        Close();
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
        if (manual)
        {
            EverythingPathHint.Text = "选择 Everything.exe；保存后会立即以隐藏模式启动。";
            return;
        }

        var detected = EverythingSearchService.FindExecutable();
        EverythingPathHint.Text = detected is null ? "未自动找到 Everything，可切换为手动指定。" : $"已找到：{detected}";
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

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

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
