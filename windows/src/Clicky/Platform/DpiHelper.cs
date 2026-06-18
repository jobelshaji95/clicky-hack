using System.Drawing;

namespace Clicky.Platform;

/// <summary>
/// Per-monitor DPI lookup. Windows reports monitor bounds and cursor position in
/// physical device pixels, but WPF positions windows in device-independent pixels
/// (DIPs, 96 dpi == scale 1.0). Multi-monitor setups frequently mix scale factors,
/// so every device→DIP conversion must use the DPI of the specific monitor.
/// </summary>
public static class DpiHelper
{
    private const double DefaultDevicePixelsPerInch = 96.0;

    /// <summary>Returns the effective DPI scale (1.0 == 100%) for the monitor containing the given device point.</summary>
    public static double GetMonitorScaleForDevicePoint(int deviceX, int deviceY)
    {
        var point = new NativeMethods.POINT { X = deviceX, Y = deviceY };
        var monitorHandle = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);

        if (NativeMethods.GetDpiForMonitor(monitorHandle, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0)
        {
            return dpiX / DefaultDevicePixelsPerInch;
        }

        return 1.0;
    }

    /// <summary>Returns the DPI scale for the monitor a rectangle of device pixels lives on (uses its top-left).</summary>
    public static double GetMonitorScaleForBounds(Rectangle deviceBounds) =>
        GetMonitorScaleForDevicePoint(deviceBounds.X, deviceBounds.Y);
}
