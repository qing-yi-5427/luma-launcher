using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

public sealed record EverythingSearchResponse(IReadOnlyList<LauncherResult> Results, bool Available, string StatusText);

public sealed class EverythingSearchService : IDisposable
{
    private const uint RequestFullPath = 0x00000004;
    private const uint ErrorIpc = 2;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _startAttempted;
    private int _shutdownRequested;
    private string _pathMode = "Auto";
    private string _configuredPath = string.Empty;

    public void Configure(string pathMode, string configuredPath)
    {
        _pathMode = pathMode;
        _configuredPath = configuredPath.Trim().Trim('"');
        _startAttempted = false;
    }

    public async Task<bool> EnsureRunningAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (EverythingNative.IsDbLoaded())
                return true;
            if (_startAttempted || !TryStartEverything())
                return false;

            _startAttempted = true;
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

    public async Task<EverythingSearchResponse> SearchAsync(string query, int maximumResults, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => SearchCore(query, maximumResults, token), token).ConfigureAwait(false);
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

    private EverythingSearchResponse SearchCore(string query, int maximumResults, CancellationToken token)
    {
        var response = ExecuteQuery(query, maximumResults, token);
        if (!response.Available && EverythingNative.GetLastError() == ErrorIpc && !_startAttempted)
        {
            _startAttempted = true;
            if (TryStartEverything())
            {
                Thread.Sleep(450);
                token.ThrowIfCancellationRequested();
                response = ExecuteQuery(query, maximumResults, token);
            }
        }
        return response;
    }

    private static EverythingSearchResponse ExecuteQuery(string query, int maximumResults, CancellationToken token)
    {
        EverythingNative.Reset();
        EverythingNative.SetSearch(query);
        EverythingNative.SetRequestFlags(RequestFullPath);
        EverythingNative.SetSort(1);
        EverythingNative.SetMax((uint)Math.Max(1, maximumResults));

        if (!EverythingNative.Query(wait: true))
        {
            var error = EverythingNative.GetLastError();
            return new EverythingSearchResponse([], false, error == ErrorIpc ? "Everything 未运行" : $"Everything 错误 {error}");
        }

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
                Score = 0
            });
        }

        return new EverythingSearchResponse(results, true, "Everything");
    }

    private bool TryStartEverything()
    {
        var executable = FindExecutable(_pathMode, _configuredPath);
        if (executable is null)
            return false;

        try
        {
            ConfigureHiddenTray(executable);
            using var process = Process.Start(new ProcessStartInfo(executable, "-startup")
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
            if (entered)
                EverythingNative.Exit();
            else
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

    private static void ConfigureHiddenTray(string executable)
    {
        var portableIni = Path.Combine(Path.GetDirectoryName(executable) ?? string.Empty, "Everything.ini");
        var appDataIni = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everything", "Everything.ini");
        var configPath = UsesPortableSettings(portableIni) ? portableIni : appDataIni;
        var directory = Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            Directory.CreateDirectory(directory);
            var lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : [];
            SetIniValue(lines, "show_tray_icon", "0");
            SetIniValue(lines, "run_in_background", "1");
            var temporary = configPath + ".luma.tmp";
            File.WriteAllLines(temporary, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, configPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticsService.Log("everything-config", exception);
        }
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
        _gate.Dispose();
    }

    private static class EverythingNative
    {
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

        [DllImport("Everything64.dll", EntryPoint = "Everything_GetLastError")]
        internal static extern uint GetLastError();

        [DllImport("Everything64.dll", EntryPoint = "Everything_IsDBLoaded")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsDbLoaded();

        [DllImport("Everything64.dll", EntryPoint = "Everything_Exit")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Exit();

        [DllImport("Everything64.dll", EntryPoint = "Everything_Reset")]
        internal static extern void Reset();
    }
}
