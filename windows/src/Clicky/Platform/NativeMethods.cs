using System.Runtime.InteropServices;

namespace Clicky.Platform;

/// <summary>
/// Win32 P/Invoke surface. Kept in one place and documented because the "why"
/// behind each call (focus-stealing avoidance, click-through, low-level hooks)
/// is non-obvious. These are the building blocks that replace AppKit's NSPanel
/// flags and CGEvent tap on Windows.
/// </summary>
internal static class NativeMethods
{
    // ── Extended window styles ──────────────────────────────────────────
    public const int GWL_EXSTYLE = -20;

    /// <summary>Window is layered — required for per-pixel transparency on the overlay.</summary>
    public const int WS_EX_LAYERED = 0x00080000;

    /// <summary>Clicks pass through to whatever is underneath (click-through overlay).</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>Window never becomes active / never steals focus from the user's app.</summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Keeps the window out of the Alt+Tab list.</summary>
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    public const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr windowHandle, int index, int newValue);

    // ── Cursor position ─────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT cursorPosition);

    // ── Low-level keyboard hook (global push-to-talk) ───────────────────
    public const int WH_KEYBOARD_LL = 13;
    public const int HC_ACTION = 0;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int hookType, LowLevelKeyboardProc callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hookHandle, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    // ── Per-monitor DPI ─────────────────────────────────────────────────
    public const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr monitorHandle, int dpiType, out uint dpiX, out uint dpiY);
}
