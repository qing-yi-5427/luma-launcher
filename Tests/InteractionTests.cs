using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LumaLauncher.Models;
using LumaLauncher.Services;

namespace LumaLauncher.Tests;

// Uses the real TextChanged -> coordinator -> ApplyBatch pipeline on the WPF
// dispatcher, but never activates a window, opens a target or touches user data.
internal static class InteractionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run()
    {
        var previous = SynchronizationContext.Current;
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = VerifyAsync();
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static async Task VerifyAsync()
    {
        var fileCalls = new List<string>();
        var fileRelease = new TaskCompletionSource<EverythingSearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayFiles = true;
        var search = new SearchCoordinator(async (query, _, token, _, _) =>
        {
            lock (fileCalls) fileCalls.Add(query);
            if (delayFiles) return await fileRelease.Task.WaitAsync(token);
            return new EverythingSearchResponse([Item(query, LauncherResultKind.File)], true, "fake", 1, false);
        });
        search.TestApplicationQuery = (context, _) => Task.FromResult<IReadOnlyList<LauncherResult>>(
            [Item(context.Query, LauncherResultKind.Application)]);
        var store = new SettingsStore();
        store.Save(new AppSettings { EverythingLifecycle = "Connect", EnableWindowSwitcher = false, EnableBookmarks = false });
        var window = new MainWindow(store, true, search);
        search.Configure(store.Current);
        var input = (TextBox)window.FindName("SearchBox");
        var list = (ListBox)window.FindName("ResultsList");
        var latencies = new List<double>();
        try
        {
            // Block files indefinitely. Local results still become selectable;
            // measure TextChanged to actionable list, including dispatcher work.
            for (var i = 0; i < 12; i++)
            {
                var query = "local-" + i;
                var watch = Stopwatch.StartNew();
                input.Text = query;
                await Until(() => list.SelectedItem is LauncherResult r && r.Title == query && !Pending(window));
                latencies.Add(watch.Elapsed.TotalMilliseconds);
            }
            var p95 = latencies.Order().ElementAt((int)Math.Ceiling(latencies.Count * .95) - 1);
            Check(p95 < 200, $"Local input-to-actionable P95 regressed: {p95:F1} ms");

            var executed = new List<string>();
            window.TestExecute = item => executed.Add(item.Title);
            input.Text = "immediate-enter";
            var submit = (Task)typeof(MainWindow).GetMethod("SearchAndExecuteAsync", Private)!.Invoke(window, [ModifierKeys.None])!;
            await submit.WaitAsync(TimeSpan.FromSeconds(3));
            Check(executed.SequenceEqual(["immediate-enter"]), "Enter executed stale/duplicate result or waited for files");

            // A source ignoring cancellation must not overwrite newer results.
            var stale = new TaskCompletionSource<IReadOnlyList<LauncherResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            search.TestApplicationQuery = (c, _) => c.Query == "stale" ? stale.Task :
                Task.FromResult<IReadOnlyList<LauncherResult>>([Item(c.Query, LauncherResultKind.Application)]);
            input.Text = "stale";
            input.Text = "latest";
            await Until(() => list.SelectedItem is LauncherResult r && r.Title == "latest" && !Pending(window));
            stale.SetResult([Item("stale", LauncherResultKind.Application)]);
            await Task.Delay(30);
            Check(list.SelectedItem is LauncherResult latest && latest.Title == "latest", "Stale result replaced active query");

            // Composition must disable old results and never launch on Enter.
            var composition = new TextComposition(InputManager.Current, input, "中");
            input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
            Check(Pending(window), "IME composition left old results actionable");
            typeof(MainWindow).GetMethod("ExecuteSelected", Private)!.Invoke(window, [ModifierKeys.None]);
            Check(executed.Count == 1, "Composition executed an old result");
            input.Text = "中文";
            input.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            await Until(() => list.SelectedItem is LauncherResult r && r.Title == "中文" && !Pending(window));

            // Cancelled tail debounce must never reach the file provider.
            delayFiles = false;
            lock (fileCalls) fileCalls.Clear();
            for (var i = 0; i < 8; i++)
            {
                input.Text = "burst-" + i;
                await Task.Delay(10);
            }
            await Until(() => (string?)typeof(MainWindow).GetField("_completedQuery", Private)!.GetValue(window) == "burst-7");
            lock (fileCalls) Check(fileCalls.SequenceEqual(["burst-7"]), "Typing burst did not coalesce file requests");

            // Selection is retained when the file batch arrives after local apps.
            delayFiles = true;
            search.TestApplicationQuery = (c, _) => Task.FromResult<IReadOnlyList<LauncherResult>>(
                [Item(c.Query, LauncherResultKind.Application), Item("second-choice", LauncherResultKind.Application)]);
            input.Text = "selection";
            await Until(() => !Pending(window) && list.Items.Count == 2);
            list.SelectedIndex = 1;
            fileRelease.TrySetResult(new EverythingSearchResponse([Item("selection", LauncherResultKind.File)], true, "fake"));
            await Until(() => (string?)typeof(MainWindow).GetField("_completedQuery", Private)!.GetValue(window) == "selection");
            Check(list.SelectedItem is LauncherResult selected && selected.Title == "second-choice", "Late file batch moved selection");
            // File-only Enter bypasses debounce, and double Enter while pending
            // must submit once. The pending result cannot execute after hiding.
            search.TestApplicationQuery = (_, _) => Task.FromResult<IReadOnlyList<LauncherResult>>([]);
            typeof(MainWindow).GetField("_activeFilter", Private)!.SetValue(window, "File");
            fileRelease = new TaskCompletionSource<EverythingSearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            input.Text = "file-enter";
            var fileSubmit = (Task)typeof(MainWindow).GetMethod("SearchAndExecuteAsync", Private)!.Invoke(window, [ModifierKeys.None])!;
            var duplicateSubmit = (Task)typeof(MainWindow).GetMethod("SearchAndExecuteAsync", Private)!.Invoke(window, [ModifierKeys.None])!;
            await duplicateSubmit;
            fileRelease.SetResult(new EverythingSearchResponse([Item("file-enter", LauncherResultKind.File)], true, "fake"));
            await fileSubmit.WaitAsync(TimeSpan.FromSeconds(3));
            Check(executed.SequenceEqual(["immediate-enter", "file-enter"]), "File Enter did not execute exactly once");

            fileRelease = new TaskCompletionSource<EverythingSearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            input.Text = "hidden-enter";
            var hiddenSubmit = (Task)typeof(MainWindow).GetMethod("SearchAndExecuteAsync", Private)!.Invoke(window, [ModifierKeys.None])!;
            window.HideLauncher();
            fileRelease.SetResult(new EverythingSearchResponse([Item("hidden-enter", LauncherResultKind.File)], true, "fake"));
            await hiddenSubmit.WaitAsync(TimeSpan.FromSeconds(3));
            Check(executed.Count == 2, "Hidden launcher executed late result");
            Console.WriteLine($"PASS interaction: input-to-actionable P95={p95:F2} ms ({latencies.Count} synthetic samples); Enter, stale results, IME events, debounce and selection");
        }
        finally { fileRelease.TrySetResult(new EverythingSearchResponse([], true, "done")); window.CloseForExit(); }
    }

    private static LauncherResult Item(string title, LauncherResultKind kind) => new()
    { Title = title, Target = kind + ":" + title, Subtitle = "synthetic", Kind = kind, Score = 100 };
    private static bool Pending(MainWindow window) => (bool)typeof(MainWindow).GetField("_searchPending", Private)!.GetValue(window)!;
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Interaction did not settle");
            await Task.Delay(1);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
