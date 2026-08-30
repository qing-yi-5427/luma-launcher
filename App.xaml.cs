using System.Windows;
using System.Windows.Interop;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher;

public sealed partial class App : System.Windows.Application
{
    private InstanceCoordinator? _instance;
    private SettingsStore? _settingsStore;
    private TrayIconService? _trayIcon;
    private MainWindow? _launcherWindow;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        DiagnosticsService.Initialize(this);
        base.OnStartup(e);
        _instance = new InstanceCoordinator();
        if (!_instance.IsPrimary)
        {
            Shutdown();
            return;
        }

        _settingsStore = new SettingsStore();
        ThemeService.Apply(_settingsStore.Current.Theme);
        _launcherWindow = new MainWindow(_settingsStore);
        MainWindow = _launcherWindow;

        _launcherWindow.SettingsRequested += OpenSettings;
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

        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            OpenSettings();
        else if (!e.Args.Contains("--silent", StringComparer.OrdinalIgnoreCase))
            _launcherWindow.ShowLauncher();
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

        _settingsWindow = new SettingsWindow(_settingsStore.Current.Copy());
        _settingsWindow.SettingsSaved += SaveSettings;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void SaveSettings(AppSettings settings)
    {
        if (_settingsStore is null || _launcherWindow is null)
            return;
        _settingsStore.Save(settings);
        StartupService.Apply(settings.StartWithWindows);
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
        base.OnExit(e);
    }
}
