namespace LumaLauncher.Services;

/// <summary>Parses free-form hotkey strings such as "Alt+E", "Ctrl+Shift+F12", "Win+Space".</summary>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey, string Display)
{
    public const uint ModWin = 0x0008;

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        uint modifiers = 0;
        string? keyToken = null;
        foreach (var raw in parts)
        {
            switch (raw.ToUpperInvariant())
            {
                case "ALT" or "MENU":
                    modifiers |= NativeMethods.ModAlt;
                    break;
                case "CTRL" or "CONTROL":
                    modifiers |= NativeMethods.ModControl;
                    break;
                case "SHIFT":
                    modifiers |= NativeMethods.ModShift;
                    break;
                case "WIN" or "WINDOWS" or "META":
                    modifiers |= ModWin;
                    break;
                default:
                    if (keyToken is not null)
                        return false;
                    keyToken = raw;
                    break;
            }
        }

        if (keyToken is null || !TryParseKey(keyToken, out var vk))
            return false;

        gesture = new HotkeyGesture(modifiers, vk, NormalizeDisplay(modifiers, keyToken));
        return true;
    }

    private static bool TryParseKey(string token, out uint vk)
    {
        vk = 0;
        var upper = token.ToUpperInvariant();
        if (upper.Length == 1)
        {
            var ch = upper[0];
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = ch;
                return true;
            }
        }

        if (upper.StartsWith('F') && int.TryParse(upper[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = 0x70u + (uint)(fn - 1); // VK_F1 = 0x70
            return true;
        }

        vk = upper switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "ENTER" or "RETURN" => 0x0D,
            "BACKSPACE" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "OEM_PLUS" or "PLUS" or "=" => 0xBB,
            "OEM_MINUS" or "MINUS" or "-" => 0xBD,
            "OEM_COMMA" or "," => 0xBC,
            "OEM_PERIOD" or "." => 0xBE,
            _ => 0
        };
        return vk != 0;
    }

    private static string NormalizeDisplay(uint modifiers, string keyToken)
    {
        var parts = new List<string>(4);
        if ((modifiers & NativeMethods.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(keyToken.Length == 1 ? keyToken.ToUpperInvariant() : keyToken.ToUpperInvariant());
        return string.Join("+", parts);
    }
}
