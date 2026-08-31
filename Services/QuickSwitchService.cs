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
        if (className.ToString().Equals("#32770", StringComparison.Ordinal))
            _dialog = window;
    }

    internal async Task<bool> SwitchAsync(string folder)
    {
        if (!HasTarget || !Directory.Exists(folder))
            return false;

        var target = _dialog;
        if (!SetForegroundWindow(target))
            return false;
        await Task.Delay(90).ConfigureAwait(false);
        SendChord(0x11, 0x4C); // Ctrl+L
        await Task.Delay(40).ConfigureAwait(false);
        SendUnicode(folder);
        await Task.Delay(30).ConfigureAwait(false);
        SendKey(0x0D); // Enter
        return true;
    }

    private static void SendChord(ushort modifier, ushort key)
    {
        Send([Key(modifier), Key(key), Key(key, keyUp: true), Key(modifier, keyUp: true)]);
    }

    private static void SendKey(ushort key) => Send([Key(key), Key(key, keyUp: true)]);

    private static void SendUnicode(string text)
    {
        var inputs = new List<Input>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(UnicodeKey(character));
            inputs.Add(UnicodeKey(character, keyUp: true));
        }
        Send(inputs.ToArray());
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

    private static void Send(Input[] inputs)
    {
        if (inputs.Length > 0)
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

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

    [StructLayout(LayoutKind.Explicit)]
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
