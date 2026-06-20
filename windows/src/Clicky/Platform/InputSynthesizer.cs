namespace Clicky.Platform;

/// <summary>
/// Synthesizes real mouse/keyboard input for Agent Mode via SendInput. Kept tiny and
/// explicit: move + left click, type a unicode string, and press a named special key.
/// Agent Mode is opt-in and step-capped; this class is the only thing that actually
/// touches the user's input, so it deliberately exposes nothing destructive on its own.
/// </summary>
public static class InputSynthesizer
{
    /// <summary>Moves the cursor to a global device pixel and left-clicks once.</summary>
    public static void ClickAt(int globalDeviceX, int globalDeviceY)
    {
        NativeMethods.SetCursorPos(globalDeviceX, globalDeviceY);
        System.Threading.Thread.Sleep(40);

        var inputs = new[]
        {
            MouseInput(NativeMethods.MOUSEEVENTF_LEFTDOWN),
            MouseInput(NativeMethods.MOUSEEVENTF_LEFTUP),
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>Types a string as unicode key events (handles any character).</summary>
    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var character in text)
        {
            var inputs = new[]
            {
                UnicodeKey(character, keyUp: false),
                UnicodeKey(character, keyUp: true),
            };
            NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
            System.Threading.Thread.Sleep(8);
        }
    }

    /// <summary>Presses a named special key (enter, tab, escape, backspace, space, up/down/left/right).</summary>
    public static bool PressKey(string keyName)
    {
        var virtualKey = MapVirtualKey(keyName);
        if (virtualKey is not { } vk)
        {
            return false;
        }

        var inputs = new[]
        {
            VirtualKey(vk, keyUp: false),
            VirtualKey(vk, keyUp: true),
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
        return true;
    }

    private static NativeMethods.INPUT MouseInput(uint flags) => new()
    {
        type = NativeMethods.INPUT_MOUSE,
        u = new NativeMethods.InputUnion { mi = new NativeMethods.MOUSEINPUT { dwFlags = flags } }
    };

    private static NativeMethods.INPUT UnicodeKey(char character, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = 0,
                wScan = character,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0),
            }
        }
    };

    private static NativeMethods.INPUT VirtualKey(ushort virtualKey, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = virtualKey,
                dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
            }
        }
    };

    private static ushort? MapVirtualKey(string keyName) => keyName.Trim().ToLowerInvariant() switch
    {
        "enter" or "return" => 0x0D,
        "tab" => 0x09,
        "escape" or "esc" => 0x1B,
        "backspace" => 0x08,
        "space" => 0x20,
        "delete" or "del" => 0x2E,
        "up" => 0x26,
        "down" => 0x28,
        "left" => 0x25,
        "right" => 0x27,
        "home" => 0x24,
        "end" => 0x23,
        _ => null,
    };
}
