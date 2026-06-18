using Clicky.Core;
using Clicky.Platform;

namespace Clicky.Input;

/// <summary>
/// System-wide push-to-talk monitor. Uses a WH_KEYBOARD_LL low-level keyboard hook
/// to detect a modifier-only combination being held and released — the Windows
/// equivalent of the macOS listen-only CGEvent tap. Default combo is Ctrl+Alt
/// (macOS ctrl+option), configurable via appsettings.json.
///
/// The hook is installed on the UI thread (which owns a message loop), so the
/// callback fires on the UI thread and Pressed/Released can be consumed directly.
/// </summary>
public sealed class GlobalHotkeyHook : IDisposable
{
    // Virtual key codes for the modifier keys we care about.
    private const uint VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_CONTROL = 0x11;
    private const uint VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_MENU = 0x12; // Alt
    private const uint VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_SHIFT = 0x10;
    private const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private readonly HotkeyConfig _config;

    // The delegate must be kept alive for the lifetime of the hook, otherwise the
    // GC collects it and Windows calls into freed memory.
    private readonly NativeMethods.LowLevelKeyboardProc _hookCallback;
    private IntPtr _hookHandle = IntPtr.Zero;

    private bool _controlDown, _altDown, _shiftDown, _windowsDown;
    private bool _combinationCurrentlySatisfied;

    /// <summary>Raised when the configured push-to-talk combination becomes fully held.</summary>
    public event Action? Pressed;

    /// <summary>Raised when the combination stops being fully held.</summary>
    public event Action? Released;

    public GlobalHotkeyHook(HotkeyConfig config)
    {
        _config = config;
        _hookCallback = HookCallback;
    }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        var moduleHandle = NativeMethods.GetModuleHandle(null);
        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _hookCallback, moduleHandle, 0);
    }

    public void Stop()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == NativeMethods.HC_ACTION)
        {
            var keyboardData = System.Runtime.InteropServices.Marshal
                .PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

            var message = (int)wParam;
            var isKeyDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            var isKeyUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

            if (isKeyDown || isKeyUp)
            {
                UpdateModifierState(keyboardData.VirtualKeyCode, isKeyDown);
                EvaluateCombination();
            }
        }

        // Listen-only: always pass the event on so modifiers still work normally.
        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void UpdateModifierState(uint virtualKeyCode, bool isDown)
    {
        switch (virtualKeyCode)
        {
            case VK_LCONTROL or VK_RCONTROL or VK_CONTROL:
                _controlDown = isDown;
                break;
            case VK_LMENU or VK_RMENU or VK_MENU:
                _altDown = isDown;
                break;
            case VK_LSHIFT or VK_RSHIFT or VK_SHIFT:
                _shiftDown = isDown;
                break;
            case VK_LWIN or VK_RWIN:
                _windowsDown = isDown;
                break;
        }
    }

    private void EvaluateCombination()
    {
        var satisfied =
            (!_config.RequireControl || _controlDown) &&
            (!_config.RequireAlt || _altDown) &&
            (!_config.RequireShift || _shiftDown) &&
            (!_config.RequireWindows || _windowsDown) &&
            // At least one modifier must actually be required, and held, to count.
            (_controlDown || _altDown || _shiftDown || _windowsDown);

        if (satisfied == _combinationCurrentlySatisfied)
        {
            return;
        }

        _combinationCurrentlySatisfied = satisfied;
        if (satisfied)
        {
            Pressed?.Invoke();
        }
        else
        {
            Released?.Invoke();
        }
    }

    public void Dispose() => Stop();
}
