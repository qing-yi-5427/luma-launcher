using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher;

public sealed partial class App : System.Windows.Application
{
    internal static bool IsTestHost { get; set; }
    private InstanceCoordinator? _instance;
    private SettingsStore? _settingsStore;
    private TrayIconService? _trayIcon;
    private MainWindow? _launcherWindow;
    private SettingsWindow? _settingsWindow;

    /// <summary>
    /// Single-file WPF ContextMenu/Popup probes Accessibility 4.0.0.0 for the
    /// MSAA→UIA bridge. If that assembly is not resolvable, opening any menu
    /// throws FileNotFoundException and takes the process down.
    /// </summary>
    private static void InstallAccessibilityResolver()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            if (!args.Name.StartsWith("Accessibility", StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, "Accessibility", StringComparison.OrdinalIgnoreCase))
                    return loaded;
            }

            try
            {
                return Assembly.Load(new AssemblyName("Accessibility"));
            }
            catch (Exception loadException)
            {
                DiagnosticsService.Log("accessibility-resolve", loadException);
            }

            var baseDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                var local = Path.Combine(baseDir, "Accessibility.dll");
                if (File.Exists(local))
                {
                    try { return Assembly.LoadFrom(local); }
                    catch (Exception localException) { DiagnosticsService.Log("accessibility-local", localException); }
                }
            }

            var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (string.IsNullOrWhiteSpace(root))
                root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var desktop = Path.Combine(root, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(desktop))
                desktop = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
            if (Directory.Exists(desktop))
            {
                try
                {
                    var dll = Directory.EnumerateFiles(desktop, "Accessibility.dll", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (dll is not null)
                        return Assembly.LoadFrom(dll);
                }
                catch (Exception frameworkException)
                {
                    DiagnosticsService.Log("accessibility-framework", frameworkException);
                }
            }
            return null;
        };
        try
        {
            _ = Assembly.Load(new AssemblyName("Accessibility"));
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("accessibility-preload", exception);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        InstallAccessibilityResolver();
        if (IsTestHost) { base.OnStartup(e); return; }
        DiagnosticsService.Initialize(this);
        base.OnStartup(e);
        _instance = new InstanceCoordinator();
        if (!_instance.IsPrimary)
        {
            Shutdown();
            return;
        }

        _settingsStore = new SettingsStore();
        if (_settingsStore.CompatibilityWarning is { } warning)
            MessageBox.Show(warning, "Luma · 配置版本不兼容", MessageBoxButton.OK, MessageBoxImage.Warning);
        ThemeService.ConfigureAutoPair(_settingsStore.Current.DayTheme, _settingsStore.Current.NightTheme);
        ThemeService.Apply(_settingsStore.Current.Theme);
        ThemeService.StartFollowingSystem();
        _launcherWindow = new MainWindow(_settingsStore);
        MainWindow = _launcherWindow;

        _launcherWindow.SettingsRequested += OpenSettings;
        _launcherWindow.ResultSortChanged += mode => _settingsWindow?.SyncResultSort(mode);
        _launcherWindow.ExitRequested += ExitApplication;
        _launcherWindow.HotkeyRegistrationChanged += RegistrationChanged;
        _instance.ActivationRequested += () => Dispatcher.Invoke(_launcherWindow.ShowLauncher);
        _instance.DrainPendingActivation();

        var registration = _launcherWindow.InitializeLauncher();
        _trayIcon = new TrayIconService(
            new WindowInteropHelper(_launcherWindow).Handle,
            _launcherWindow.ToggleLauncher,
            OpenSettings,
            _launcherWindow.ReloadAppsAsync,
            ExitApplication,
            registration.Active);
        _launcherWindow.TrayMessageHandler = _trayIcon.HandleMessage;
        if (registration.UsedFallback)
            _trayIcon.ShowHotkeyFallback(registration.Requested, registration.Active);

        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(AppDataPaths.DirectoryPath, "settings.json")))
            OpenSettings();
        else if (!e.Args.Contains("--silent", StringComparer.OrdinalIgnoreCase))
        {
            _launcherWindow.ShowLauncher();
            if (_settingsStore.Current.ShowOnboarding)
            {
                _launcherWindow.ShowHelpOnce();
                var next = _settingsStore.Current.Copy();
                next.ShowOnboarding = false;
                try { _settingsStore.Save(next); } catch { }
            }
        }
        else
            _launcherWindow.ScheduleIdleTrim();
    }

    private void RegistrationChanged(HotkeyRegistration registration)
    {
        _trayIcon?.UpdateHotkey(registration.Active);
        if (registration.UsedFallback)
            _trayIcon?.ShowHotkeyFallback(registration.Requested, registration.Active);
    }

    private void OpenSettings()
    {
        if (_settingsStore is null || _launcherWindow is null)
            return;
        _launcherWindow.HideLauncher();

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settingsStore.Current.Copy(), _launcherWindow.ActiveHotkey);
        _settingsWindow.SettingsSaved += SaveSettings;
        _settingsWindow.ClearHistoryRequested += _launcherWindow.ClearHistory;
        _settingsWindow.ClearQueryHistoryRequested += () => _launcherWindow.ClearQueryHistory();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void SaveSettings(AppSettings settings)
    {
        if (_settingsStore is null || _launcherWindow is null)
            return;
        // Preserve window geometry and dismiss onboarding; the settings dialog
        // builds a fresh AppSettings without those runtime fields.
        settings.WindowLeft = _settingsStore.Current.WindowLeft;
        settings.WindowTop = _settingsStore.Current.WindowTop;
        settings.ShowOnboarding = false;
        _settingsStore.Save(settings);
        StartupService.Apply(settings.StartWithWindows);
        ThemeService.ConfigureAutoPair(settings.DayTheme, settings.NightTheme);
        ThemeService.Apply(settings.Theme);
        _launcherWindow.ApplySettings();
    }

    private void ExitApplication()
    {
        _launcherWindow?.ShutdownEverything();
        _settingsWindow?.Close();
        _launcherWindow?.CloseForExit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _launcherWindow?.ShutdownEverything();
        if (_launcherWindow is not null)
            _launcherWindow.TrayMessageHandler = null;
        _trayIcon?.Dispose();
        _instance?.Dispose();
        ThemeService.StopFollowingSystem();
        base.OnExit(e);
    }
}
