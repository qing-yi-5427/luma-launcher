namespace LumaLauncher.Services;

public sealed record HotkeyRegistration(string Requested, string Active, bool UsedFallback, int ErrorCode);

public sealed class HotkeyService
{
    private const int HotkeyId = 0x4C55;
    private const int ProbeId = 0x4C56;
    private IntPtr _window;
    private bool _registered;
    private HotkeyRegistration? _current;

    public HotkeyRegistration Register(IntPtr window, string requested)
    {
        // Applying unrelated settings must not create a gap in the active global hotkey.
        if (_registered && window == _window && _current is { UsedFallback: false } current &&
            current.Requested.Equals(requested, StringComparison.OrdinalIgnoreCase))
            return current;
        Unregister();
        _window = window;

        var candidates = BuildCandidates(requested);
        var lastError = 0;

        foreach (var candidate in candidates)
        {
            if (!HotkeyGesture.TryParse(candidate, out var gesture))
                continue;
            var modifiers = gesture.Modifiers | NativeMethods.ModNoRepeat;
            if (NativeMethods.RegisterHotKey(window, HotkeyId, modifiers, gesture.VirtualKey))
            {
                _registered = true;
                _current = new HotkeyRegistration(requested, candidate, !candidate.Equals(requested, StringComparison.OrdinalIgnoreCase), lastError);
                return _current;
            }
            lastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        }

        return _current = new HotkeyRegistration(requested, "未注册", true, lastError);
    }

    /// <summary>Probe whether a gesture can be registered without touching the live hotkey.</summary>
    public static bool TryProbe(string gesture, out int errorCode)
    {
        errorCode = 0;
        if (!HotkeyGesture.TryParse(gesture, out var parsed))
            return false;

        var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("LumaHotkeyProbe")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        });
        try
        {
            var handle = source.Handle;
            var modifiers = parsed.Modifiers | NativeMethods.ModNoRepeat;
            if (NativeMethods.RegisterHotKey(handle, ProbeId, modifiers, parsed.VirtualKey))
            {
                NativeMethods.UnregisterHotKey(handle, ProbeId);
                return true;
            }
            errorCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }
        finally
        {
            source.Dispose();
        }
    }

    internal static bool TryProbeForSettings(string gesture, string? activeGesture, out int errorCode)
    {
        errorCode = 0;
        if (HotkeyGesture.TryParse(gesture, out var requested) &&
            HotkeyGesture.TryParse(activeGesture, out var active) &&
            requested.Modifiers == active.Modifiers && requested.VirtualKey == active.VirtualKey) return true;
        return TryProbe(gesture, out errorCode);
    }

    public bool IsHotkeyMessage(int message, IntPtr wParam) =>
        message == NativeMethods.WmHotkey && wParam.ToInt32() == HotkeyId;

    public void Unregister()
    {
        if (_registered && _window != IntPtr.Zero)
            NativeMethods.UnregisterHotKey(_window, HotkeyId);
        _registered = false;
        _current = null;
    }

    private static IEnumerable<string> BuildCandidates(string requested)
    {
        if (HotkeyGesture.TryParse(requested, out _))
            yield return requested;
        foreach (var fallback in new[] { "Alt+Space", "Ctrl+Space", "Ctrl+Alt+Space" })
        {
            if (!fallback.Equals(requested, StringComparison.OrdinalIgnoreCase))
                yield return fallback;
        }
    }
}
