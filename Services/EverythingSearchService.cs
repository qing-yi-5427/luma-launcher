using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using LumaLauncher.Models;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LumaLauncher.Services;

public sealed record EverythingSearchResponse(IReadOnlyList<LauncherResult> Results, bool Available, string StatusText,
    int TotalMatches = 0, bool HasMore = false);

public sealed class EverythingSearchService : IDisposable
{
    private const uint RequestFullPath = 0x00000004;
    private const uint ErrorIpc = 2;
    // The Everything SDK stores global query state, shared by all service instances.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly SemaphoreSlim _gate = Gate;
    private static int _replySequence;
    private DateTimeOffset _nextStartAttempt;
    private Process? _ownedProcess;
    private int _shutdownRequested;
    private string _pathMode = "Auto";
    private string _configuredPath = string.Empty;
    private string _lifecycle = "Managed";

    public void Configure(string pathMode, string configuredPath, string lifecycle = "Managed")
    {
        _pathMode = pathMode;
        _configuredPath = configuredPath.Trim().Trim('"');
        _lifecycle = lifecycle.Equals("Connect", StringComparison.OrdinalIgnoreCase) ? "Connect" : "Managed";
        _nextStartAttempt = DateTimeOffset.MinValue;
        Interlocked.Exchange(ref _shutdownRequested, 0);
    }

    public async Task<bool> EnsureRunningAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (EverythingNative.IsDbLoaded())
                return true;
            if (DateTimeOffset.UtcNow < _nextStartAttempt || !TryStartEverything())
                return false;

            _nextStartAttempt = DateTimeOffset.UtcNow.AddSeconds(3);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(150, token).ConfigureAwait(false);
                if (EverythingNative.IsDbLoaded())
                    return true;
            }
            return false;
        }
        catch (DllNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EverythingSearchResponse> SearchAsync(string query, int maximumResults, CancellationToken token, string filter = "All", string sortMode = "Smart")
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<EverythingSearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { completion.TrySetResult(SearchCore(query, maximumResults, token, filter, ResultRanker.Normalize(sortMode))); }
                catch (OperationCanceledException) { completion.TrySetCanceled(token); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true, Name = "Luma Everything query" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await completion.Task.ConfigureAwait(false);
        }
        catch (DllNotFoundException)
        {
            return new EverythingSearchResponse([], false, "Everything SDK 缺失");
        }
        catch (BadImageFormatException)
        {
            return new EverythingSearchResponse([], false, "Everything SDK 架构不匹配");
        }
        catch (EntryPointNotFoundException)
        {
            return new EverythingSearchResponse([], false, "Everything SDK 版本不兼容");
        }
        finally
        {
            _gate.Release();
        }
    }

    private EverythingSearchResponse SearchCore(string query, int maximumResults, CancellationToken token, string filter, string sortMode)
    {
        EverythingSearchResponse Execute(string text)
        {
            if (ResultRanker.IsSizeSort(sortMode))
            {
                // Directories have no comparable file size. Query files first so unknown
                // directory sizes cannot consume the top-N; append folders by name.
                if (filter == "Folder") return ExecuteQuery($"folder: <{text}>", maximumResults, token, 1);
                var files = ExecuteQuery($"file: <{text}>", maximumResults, token, ResultRanker.EverythingSort(sortMode));
                if (filter == "File" || !files.Available) return files;
                var remaining = Math.Max(0, maximumResults - files.Results.Count);
                var folders = ExecuteQuery($"folder: <{text}>", Math.Max(1, remaining), token, 1);
                var combined = files.Results.Concat(folders.Results.Take(remaining)).ToList();
                return new EverythingSearchResponse(combined, folders.Available, folders.Available ? "Everything" : folders.StatusText,
                    (int)Math.Min(int.MaxValue, (long)files.TotalMatches + folders.TotalMatches),
                    files.HasMore || folders.TotalMatches > remaining);
            }
            return ExecuteQuery(filter switch
                { "File" => $"file: <{text}>", "Folder" => $"folder: <{text}>", _ => text },
                maximumResults, token, ResultRanker.EverythingSort(sortMode));
        }
        var response = Execute(query);
        if (!response.Available && EverythingNative.GetLastError() == ErrorIpc && DateTimeOffset.UtcNow >= _nextStartAttempt)
        {
            _nextStartAttempt = DateTimeOffset.UtcNow.AddSeconds(3);
            if (TryStartEverything())
            {
                token.WaitHandle.WaitOne(450);
                token.ThrowIfCancellationRequested();
                response = Execute(query);
            }
        }

        // Explicit ordering describes the literal Everything match set. Independent
        // fuzzy/pinyin queries cannot be concatenated into a globally ordered top-N.
        if (ResultRanker.IsProviderSort(sortMode)) return response;
        var hasSyntax = LooksLikeSyntax(query);
        var fallbackThreshold = Math.Min(32, maximumResults);
        if (response.Available && response.Results.Count < fallbackThreshold && !hasSyntax)
        {
            var fallbackQuery = CreateFuzzyQuery(query);
            if (!string.IsNullOrWhiteSpace(fallbackQuery) && !fallbackQuery.Equals(query, StringComparison.Ordinal))
                response = Merge(response, Execute(fallbackQuery), maximumResults);
        }
        if (response.Available && !hasSyntax && SupportsPinyin() && query.Length is >= 2 and <= 32 &&
            query.All(character => char.IsAsciiLetterOrDigit(character) || char.IsWhiteSpace(character)))
            response = Merge(response, Execute($"pinyin:<{query}>"), maximumResults);
        return response;
    }

    private static EverythingSearchResponse Merge(EverythingSearchResponse primary, EverythingSearchResponse fallback, int maximum)
    {
        if (!fallback.Available || fallback.Results.Count == 0)
            return primary;
        var candidates = primary.Results.Concat(fallback.Results)
            .DistinctBy(result => result.Target, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var merged = candidates.Take(maximum).ToList();
        // Fallback queries can overlap; do not present their sum as an exact total.
        return new EverythingSearchResponse(merged, true, "Everything", Math.Max(primary.TotalMatches, fallback.TotalMatches),
            candidates.Count > maximum || primary.HasMore || fallback.HasMore);
    }

    private static string CreateFuzzyQuery(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', tokens.Select(token => token.Length < 2
            ? token
            : "*" + string.Join('*', token.ToCharArray()) + "*"));
    }

    private static bool LooksLikeSyntax(string query) =>
        query.IndexOfAny([':', '\\', '/', '*', '?', '|', '!', '<', '>', '"']) >= 0;

    private static bool SupportsPinyin()
    {
        try
        {
            var major = EverythingNative.GetMajorVersion();
            var minor = EverythingNative.GetMinorVersion();
            return major > 1 || major == 1 && minor >= 5;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static EverythingSearchResponse ExecuteQuery(string query, int maximumResults, CancellationToken token, uint sort)
    {
        EverythingNative.Reset();
        EverythingNative.SetSearch(query);
        EverythingNative.SetRequestFlags(RequestFullPath | (sort is 5 or 6 ? 0x10u : sort is 13 or 14 ? 0x40u : 0));
        EverythingNative.SetSort(sort);
        EverythingNative.SetMax((uint)Math.Max(1, maximumResults));
        token.ThrowIfCancellationRequested();
        using var replyWindow = new HwndSource(new HwndSourceParameters("Luma Everything reply")
        { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        var frame = new DispatcherFrame();
        var received = false;
        var replyId = unchecked((uint)Interlocked.Increment(ref _replySequence));
        replyWindow.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (EverythingNative.IsQueryReply((uint)message, wParam, lParam, replyId))
            { received = true; handled = true; frame.Continue = false; }
            return IntPtr.Zero;
        });
        EverythingNative.SetReplyWindow(replyWindow.Handle);
        EverythingNative.SetReplyId(replyId);
        if (!EverythingNative.Query(wait: false))
        {
            var error = EverythingNative.GetLastError();
            return new EverythingSearchResponse([], false, error == ErrorIpc ? "Everything 未运行" : $"Everything 错误 {error}");
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            if (token.IsCancellationRequested || DateTimeOffset.UtcNow >= deadline) frame.Continue = false;
        };
        timer.Start();
        try { if (!received) Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); EverythingNative.SetReplyWindow(IntPtr.Zero); }
        token.ThrowIfCancellationRequested();
        if (!received) return new EverythingSearchResponse([], false, "Everything 响应超时，可重试或重新检测");

        var count = Math.Min((int)EverythingNative.GetNumResults(), maximumResults);
        var results = new List<LauncherResult>(count);
        var pathBuffer = new StringBuilder(32768);
        for (uint index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            pathBuffer.Clear();
            EverythingNative.GetResultFullPathName(index, pathBuffer, (uint)pathBuffer.Capacity);
            var fullPath = pathBuffer.ToString();
            if (string.IsNullOrWhiteSpace(fullPath))
                continue;

            var folder = EverythingNative.IsFolderResult(index);
            var title = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(title)) title = fullPath;
            results.Add(new LauncherResult
            {
                Title = title,
                Subtitle = folder ? fullPath : Path.GetDirectoryName(fullPath) ?? string.Empty,
                Target = fullPath,
                Kind = folder ? LauncherResultKind.Folder : LauncherResultKind.File,
                IndexedSize = !folder && sort is 5 or 6 && EverythingNative.GetResultSize(index, out var size) && size >= 0 ? size : null,
                IndexedModifiedFileTime = sort is 13 or 14 && EverythingNative.GetResultDateModified(index, out var modified) && modified > 0 ? modified : null,
                Score = 0
            });
        }

        var total = (int)Math.Min(int.MaxValue, EverythingNative.GetTotResults());
        return new EverythingSearchResponse(results, true, "Everything", total, total > results.Count);
    }

    private bool TryStartEverything()
    {
        if (_lifecycle == "Connect" || Volatile.Read(ref _shutdownRequested) != 0)
            return false;
        var executable = FindExecutable(_pathMode, _configuredPath);
        if (executable is null)
            return false;

        try
        {
            // Existing clients belong to the user. Wait for their index instead of changing their settings.
            var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
            using var current = Process.GetCurrentProcess();
            try
            {
                if (running.Any(process => { try { return process.SessionId == current.SessionId; } catch { return false; } }))
                    return false;
            }
            finally { foreach (var process in running) process.Dispose(); }
            var config = CreateManagedConfiguration(executable);
            var database = Path.Combine(AppDataPaths.DirectoryPath, "Everything-managed.db");
            _ownedProcess?.Dispose();
            _ownedProcess = Process.Start(new ProcessStartInfo(executable, $"-startup -config \"{config}\" -db \"{database}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return true;
        }
        catch (Exception exception)
        {
            DiagnosticsService.Log("everything-start", exception);
            return false;
        }
    }

    public static string? FindExecutable(string pathMode = "Auto", string configuredPath = "")
    {
        if (pathMode.Equals("Manual", StringComparison.OrdinalIgnoreCase))
        {
            var manualPath = configuredPath.Trim().Trim('"');
            return File.Exists(manualPath) ? Path.GetFullPath(manualPath) : null;
        }

        var candidates = new List<string?>
        {
            GetRegistryAppPath(RegistryHive.CurrentUser, RegistryView.Default),
            GetRegistryAppPath(RegistryHive.LocalMachine, RegistryView.Registry64),
            GetRegistryAppPath(RegistryHive.LocalMachine, RegistryView.Registry32),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "Everything.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", "Everything.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "Everything.exe")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    public void ShutdownClient()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
            return;
        var entered = false;
        try
        {
            entered = _gate.Wait(TimeSpan.FromSeconds(2));
            if (entered && _ownedProcess is { HasExited: false })
            {
                // Target the IPC window, not a search window. Verify ownership before
                // sending WM_USER / EVERYTHING_IPC_EXIT (official SDK IPC protocol).
                var window = EverythingNative.FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null);
                EverythingNative.GetWindowThreadProcessId(window, out var processId);
                if (window != IntPtr.Zero && processId == _ownedProcess.Id)
                {
                    if (EverythingNative.SendMessageTimeout(window, 0x0400, new IntPtr(4), IntPtr.Zero,
                            0x0002, 1500, out _) == IntPtr.Zero || !_ownedProcess.WaitForExit(1500))
                        DiagnosticsService.Log("everything-exit", "Owned client did not confirm exit within the deadline.");
                }
                else DiagnosticsService.Log("everything-exit", "IPC owner changed; unrelated client was preserved.");
            }
            else if (!entered)
                DiagnosticsService.Log("everything-exit", "Timed out waiting for an active query to finish.");
        }
        catch (DllNotFoundException) { }
        catch (BadImageFormatException) { }
        catch (EntryPointNotFoundException) { }
        finally
        {
            if (entered)
                _gate.Release();
        }
    }

    private static string CreateManagedConfiguration(string executable)
    {
        var portableIni = Path.Combine(Path.GetDirectoryName(executable) ?? string.Empty, "Everything.ini");
        var appDataIni = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everything", "Everything.ini");
        var sourcePath = UsesPortableSettings(portableIni) ? portableIni : appDataIni;
        var configPath = Path.Combine(AppDataPaths.DirectoryPath, "Everything-managed.ini");
        var lines = File.Exists(sourcePath) ? File.ReadAllLines(sourcePath).ToList() : new List<string> { "[Everything]" };
        SetIniValue(lines, "show_tray_icon", "0");
        SetIniValue(lines, "run_in_background", "1");
        AtomicFileService.WriteAllText(configPath, string.Join(Environment.NewLine, lines));
        return configPath;
    }

    private static bool UsesPortableSettings(string iniPath)
    {
        try
        {
            return File.Exists(iniPath) && File.ReadLines(iniPath).Any(line =>
                line.Trim().Equals("app_data=0", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static void SetIniValue(List<string> lines, string key, string value)
    {
        var prefix = key + "=";
        var index = lines.FindIndex(line => line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            lines[index] = prefix + value;
        else
            lines.Add(prefix + value);
    }

    private static string? GetRegistryAppPath(RegistryHive hive, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Everything.exe");
            return key?.GetValue(null) is string path ? Environment.ExpandEnvironmentVariables(path.Trim('"')) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        var entered = false;
        try
        {
            entered = _gate.Wait(TimeSpan.FromSeconds(2));
            if (!entered)
            {
                DiagnosticsService.Log("everything-dispose", "Left the query gate alive because an IPC query did not finish in time.");
                return;
            }
        }
        catch (ObjectDisposedException) { return; }
        finally
        {
            if (entered)
                _gate.Release();
        }
        _ownedProcess?.Dispose();
        _ownedProcess = null;
    }

    private static class EverythingNative
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string className, string? windowName);
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("Everything64.dll", EntryPoint = "Everything_SetReplyWindow")]
        internal static extern void SetReplyWindow(IntPtr hwnd);
        [DllImport("Everything64.dll", EntryPoint = "Everything_SetReplyID")]
        internal static extern void SetReplyId(uint id);
        [DllImport("Everything64.dll", EntryPoint = "Everything_IsQueryReply")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsQueryReply(uint message, IntPtr wParam, IntPtr lParam, uint id);
        [DllImport("Everything64.dll", EntryPoint = "Everything_GetTotResults")]
        internal static extern uint GetTotResults();
        [DllImport("Everything64.dll", CharSet = CharSet.Unicode, EntryPoint = "Everything_SetSearchW")]
        internal static extern void SetSearch(string search);

        [DllImport("Everything64.dll", EntryPoint = "Everything_SetRequestFlags")]
        internal static extern void SetRequestFlags(uint flags);

        [DllImport("Everything64.dll", EntryPoint = "Everything_SetMax")]
        internal static extern void SetMax(uint maximum);

        [DllImport("Everything64.dll", EntryPoint = "Everything_SetSort")]
        internal static extern void SetSort(uint sortType);

        [DllImport("Everything64.dll", CharSet = CharSet.Unicode, EntryPoint = "Everything_QueryW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Query([MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport("Everything64.dll", EntryPoint = "Everything_GetNumResults")]
        internal static extern uint GetNumResults();

        [DllImport("Everything64.dll", CharSet = CharSet.Unicode, EntryPoint = "Everything_GetResultFullPathNameW")]
        internal static extern uint GetResultFullPathName(uint index, StringBuilder buffer, uint maximumCount);

        [DllImport("Everything64.dll", EntryPoint = "Everything_IsFolderResult")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsFolderResult(uint index);

        [DllImport("Everything64.dll", EntryPoint = "Everything_GetResultSize")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetResultSize(uint index, out long size);

        [DllImport("Everything64.dll", EntryPoint = "Everything_GetResultDateModified")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetResultDateModified(uint index, out long fileTime);

        [DllImport("Everything64.dll", EntryPoint = "Everything_GetLastError")]
        internal static extern uint GetLastError();

        [DllImport("Everything64.dll", EntryPoint = "Everything_IsDBLoaded")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsDbLoaded();

        [DllImport("Everything64.dll", EntryPoint = "Everything_GetMajorVersion")]
        internal static extern uint GetMajorVersion();

        [DllImport("Everything64.dll", EntryPoint = "Everything_GetMinorVersion")]
        internal static extern uint GetMinorVersion();

        [DllImport("Everything64.dll", EntryPoint = "Everything_Exit")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Exit();

        [DllImport("Everything64.dll", EntryPoint = "Everything_Reset")]
        internal static extern void Reset();
    }
}
