using System.Text;
using System.Windows.Threading;

namespace LumaLauncher.Services;

internal static class DiagnosticsService
{
    private static readonly object Sync = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaLauncher");
    private static readonly string LogPath = Path.Combine(DirectoryPath, "luma.log");
    private static bool _initialized;

    internal static void Initialize(System.Windows.Application application)
    {
        if (_initialized)
            return;
        _initialized = true;
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("appdomain", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("task", e.Exception);
            e.SetObserved();
        };
        Log("startup", "Luma starting.");
    }

    internal static void Log(string area, Exception exception) => Log(area, exception.ToString());

    internal static void Log(string area, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                RotateIfNeeded();
                File.AppendAllText(LogPath,
                    $"{DateTimeOffset.Now:O} [{area}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        Log("dispatcher", e.Exception);

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < 512 * 1024)
            return;
        File.Move(LogPath, Path.Combine(DirectoryPath, "luma.previous.log"), true);
    }
}
