using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LumaLauncher.Models;

namespace LumaLauncher.Services;

public sealed class WindowSwitcherService
{
    public IReadOnlyList<LauncherResult> Search(string query, int limit, UsageStore usage)
    {
        var prepared = FuzzyMatcher.Prepare(query);
        var matches = new List<LauncherResult>();
        var seen = new HashSet<IntPtr>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsAltTabWindow(hwnd) || !seen.Add(hwnd))
                return true;
            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            var processName = GetProcessName(pid);
            var score = FuzzyMatcher.Score(prepared, FuzzyMatcher.PrepareCandidate(title), FuzzyMatcher.PrepareCandidate(processName));
            if (double.IsNegativeInfinity(score))
                return true;
            var boost = usage.GetBoost($"hwnd:{hwnd.ToInt64()}");
            matches.Add(new LauncherResult
            {
                Title = title,
                Subtitle = processName.Length == 0 ? "窗口" : processName,
                Target = $"hwnd:{hwnd.ToInt64()}",
                Kind = LauncherResultKind.Window,
                Score = score + boost + 80,
                IsFavorite = usage.IsFavorite($"hwnd:{hwnd.ToInt64()}")
            });
            return true;
        }, IntPtr.Zero);

        return matches
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title.Length)
            .Take(limit)
            .ToList();
    }

    public static bool Activate(LauncherResult result)
    {
        if (result.Kind != LauncherResultKind.Window || !TryParseHwnd(result.Target, out var hwnd))
            return false;
        if (!IsWindow(hwnd))
            return false;
        if (IsIconic(hwnd))
            ShowWindow(hwnd, 9); // SW_RESTORE
        if (!SetForegroundWindow(hwnd))
            return false;
        // Bring the thread's window to the foreground reliably on Win10/11.
        var foreground = GetForegroundWindow();
        return foreground == hwnd || BringWindowToTop(hwnd);
    }

    private static bool TryParseHwnd(string target, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (!target.StartsWith("hwnd:", StringComparison.OrdinalIgnoreCase))
            return false;
        return long.TryParse(target[5..], out var value) && (hwnd = new IntPtr(value)) != IntPtr.Zero;
    }

    private static bool IsAltTabWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero)
            return false;
        if (GetWindowTextLength(hwnd) == 0)
            return false;
        var className = new StringBuilder(128);
        GetClassName(hwnd, className, className.Capacity);
        var name = className.ToString();
        if (name is "Windows.UI.Core.CoreWindow" or "ApplicationFrameWindow")
        {
            // Keep UWP frames only when they have a non-empty DWM title.
            return GetWindowTextLength(hwnd) > 0;
        }
        if (name.StartsWith("Shell_TrayWnd", StringComparison.Ordinal) ||
            name.StartsWith("Progman", StringComparison.Ordinal) ||
            name.StartsWith("WorkerW", StringComparison.Ordinal))
            return false;
        var exStyle = GetWindowLongPtr(hwnd, -20).ToInt64(); // GWL_EXSTYLE
        const long WsExToolWindow = 0x00000080;
        const long WsExNoActivate = 0x08000000;
        return (exStyle & WsExToolWindow) == 0 && (exStyle & WsExNoActivate) == 0;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return string.Empty;
        var builder = new StringBuilder(length + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetProcessName(uint pid)
    {
        if (pid == 0)
            return string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);
}
