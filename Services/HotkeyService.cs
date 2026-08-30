namespace LumaLauncher.Services;

public sealed record HotkeyRegistration(string Requested, string Active, bool UsedFallback, int ErrorCode);

public sealed class HotkeyService
{
    private const int HotkeyId = 0x4C55;
    private IntPtr _window;
    private bool _registered;

    public HotkeyRegistration Register(IntPtr window, string requested)
    {
        Unregister();
        _window = window;

        var candidates = new[] { requested, "Alt+Space", "Ctrl+Space", "Ctrl+Alt+Space" }
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var lastError = 0;

        foreach (var candidate in candidates)
        {
            var modifiers = ParseModifiers(candidate) | NativeMethods.ModNoRepeat;
            if (NativeMethods.RegisterHotKey(window, HotkeyId, modifiers, NativeMethods.VkSpace))
            {
                _registered = true;
                return new HotkeyRegistration(requested, candidate, !candidate.Equals(requested, StringComparison.OrdinalIgnoreCase), lastError);
            }
            lastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        }

        return new HotkeyRegistration(requested, "未注册", true, lastError);
    }

    public bool IsHotkeyMessage(int message, IntPtr wParam) =>
        message == NativeMethods.WmHotkey && wParam.ToInt32() == HotkeyId;

    public void Unregister()
    {
        if (_registered && _window != IntPtr.Zero)
            NativeMethods.UnregisterHotKey(_window, HotkeyId);
        _registered = false;
    }

    private static uint ParseModifiers(string gesture)
    {
        var modifiers = 0u;
        if (gesture.Contains("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= NativeMethods.ModAlt;
        if (gesture.Contains("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= NativeMethods.ModControl;
        if (gesture.Contains("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= NativeMethods.ModShift;
        return modifiers;
    }
}
