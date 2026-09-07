using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LumaLauncher.Services;

/// <summary>Suspends the global hotkey while a fullscreen foreground app (e.g. game) is active.</summary>
public sealed class GameModeService
{
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private bool _suppressed;
    private bool _enabled;

    public bool Enabled
    {
        get { lock (_sync) return _enabled; }
        set
        {
            lock (_sync)
            {
                if (_enabled == value)
                    return;
                _enabled = value;
                if (!value)
                    StopPolling();
                else
                    StartPolling();
            }
            SuppressedChanged?.Invoke(Suppressed);
        }
    }

    public bool ManualSuspend { get; set; }

    public bool Suppressed
    {
        get
        {
            lock (_sync)
                return ManualSuspend || _suppressed;
        }
    }

    public event Action<bool>? SuppressedChanged;

    public void ToggleManualSuspend()
    {
        ManualSuspend = !ManualSuspend;
        SuppressedChanged?.Invoke(Suppressed);
    }

    private void StartPolling()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PollInterval, token).ConfigureAwait(false);
                    var fullscreen = IsFullscreenForeground();
                    bool changed;
                    lock (_sync)
                    {
                        changed = _suppressed != fullscreen;
                        _suppressed = fullscreen;
                    }
                    if (changed)
                        SuppressedChanged?.Invoke(Suppressed || ManualSuspend);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    DiagnosticsService.Log("game-mode", exception);
                }
            }
        }, token);
    }

    private void StopPolling()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        lock (_sync) _suppressed = false;
    }

    public void Dispose() => StopPolling();

    private static bool IsFullscreenForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;
        if (!GetWindowRect(hwnd, out var rect))
            return false;
        var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return false;

        var coversMonitor = rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top &&
                            rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
        if (!coversMonitor)
            return false;

        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        var hasChrome = (style & WsCaption) != 0 || (style & WsThickFrame) != 0;
        return !hasChrome || IsExclusiveModeProcess();
    }

    private static bool IsExclusiveModeProcess()
    {
        // Cheap heuristic: many exclusive-mode games still report a caption, but the
        // process is not explorer/ShellExperienceHost/Windows Terminal.
        var hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            return name is not ("explorer" or "ShellExperienceHost" or "ApplicationFrameHost" or "WindowsTerminal" or "cmd" or "powershell" or "pwsh" or "OpenConsole");
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.Rect rectangle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfo
    {
        internal int Size;
        internal NativeMethods.Rect Monitor;
        internal NativeMethods.Rect Work;
        internal uint Flags;
    }
}
