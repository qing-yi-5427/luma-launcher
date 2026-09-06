using System.Runtime.InteropServices;
using System.Text;

namespace LumaLauncher.Services;

internal sealed class QuickSwitchService
{
    private IntPtr _dialog;

    internal bool Enabled { get; set; } = true;
    internal bool HasTarget => Enabled && _dialog != IntPtr.Zero && IsWindow(_dialog);

    internal void CaptureForegroundDialog()
    {
        _dialog = IntPtr.Zero;
        if (!Enabled)
            return;
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return;
        var className = new StringBuilder(128);
        GetClassName(window, className, className.Capacity);
        if (className.ToString().Equals("#32770", StringComparison.Ordinal) &&
            IsFileDialog(window))
            _dialog = window;
    }

    private static bool IsFileDialog(IntPtr window)
    {
        // Modern common item dialogs have a shell view and breadcrumb address bar.
        // A generic #32770/Edit combination also occurs in login and message dialogs.
        var shellView = false;
        var addressBar = false;
        EnumChildWindows(window, (child, _) =>
        {
            var name = new StringBuilder(128);
            GetClassName(child, name, name.Capacity);
            shellView |= name.ToString() == "SHELLDLL_DefView";
            addressBar |= name.ToString() == "Breadcrumb Parent";
            return true;
        }, IntPtr.Zero);
        return shellView && addressBar;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr parameter);

    internal async Task<bool> SwitchAsync(string folder)
    {
        if (!HasTarget || !IsFileDialog(_dialog) || !Directory.Exists(folder))
            return false;

        var target = _dialog;
        if (!SetForegroundWindow(target))
            return false;
        await Task.Delay(90).ConfigureAwait(false);
        if (GetForegroundWindow() != target || !SendChord(0x11, 0x4C)) return false;
        await Task.Delay(40).ConfigureAwait(false);
        if (GetForegroundWindow() != target || !SendUnicode(folder)) return false;
        await Task.Delay(30).ConfigureAwait(false);
        return GetForegroundWindow() == target && SendKey(0x0D);
    }

    private static bool SendChord(ushort modifier, ushort key)
    {
        return Send([Key(modifier), Key(key), Key(key, keyUp: true), Key(modifier, keyUp: true)]);
    }

    private static bool SendKey(ushort key) => Send([Key(key), Key(key, keyUp: true)]);

    private static bool SendUnicode(string text)
    {
        var inputs = new List<Input>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(UnicodeKey(character));
            inputs.Add(UnicodeKey(character, keyUp: true));
        }
        return Send(inputs.ToArray());
    }

    private static Input Key(ushort key, bool keyUp = false) => new()
    {
        Type = 1,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput { VirtualKey = key, Flags = keyUp ? 0x0002u : 0u }
        }
    };

    private static Input UnicodeKey(char character, bool keyUp = false) => new()
    {
        Type = 1,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                ScanCode = character,
                Flags = 0x0004u | (keyUp ? 0x0002u : 0u)
            }
        }
    };

    private static bool Send(Input[] inputs) =>
        inputs.Length > 0 && SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;

    internal static int NativeInputSize => Marshal.SizeOf<Input>();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    // INPUT's union includes MOUSEINPUT (32 bytes on x64), even for keyboard events.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }
}
