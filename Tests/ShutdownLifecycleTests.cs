using System.Reflection;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

internal static class ShutdownLifecycleTests
{
    internal static void Run()
    {
        // The test host uses a temporary data directory. Connect mode cannot own or stop
        // a user's Everything process, and no query or global hotkey is started here.
        var store = new SettingsStore();
        store.Save(new AppSettings { EverythingLifecycle = "Connect" });
        var window = new MainWindow(store, previewMode: true);
        var field = typeof(MainWindow).GetField("_searchCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var pendingSearch = new CancellationTokenSource();
        field.SetValue(window, pendingSearch);

        // Before the fix, Closed disposed this source but App.OnExit called
        // ShutdownEverything again; its Cancel then threw ObjectDisposedException.
        window.CloseForExit();
        window.ShutdownEverything();
        window.ShutdownEverything();
        window.CloseForExit();
        if (field.GetValue(window) is not null || !pendingSearch.IsCancellationRequested)
            throw new InvalidOperationException("Search cancellation was not completed and detached during exit.");
        Console.WriteLine("PASS close then repeated shutdown uses disposed resources safely");
    }
}
