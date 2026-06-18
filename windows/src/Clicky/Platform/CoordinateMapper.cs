using Clicky.Capture;

namespace Clicky.Platform;

/// <summary>The on-screen target the cursor should fly to, expressed in global device pixels.</summary>
public readonly record struct PointingTarget(
    double GlobalDeviceX,
    double GlobalDeviceY,
    MonitorScreenCapture Monitor);

/// <summary>
/// Maps the LLM's pixel coordinates (in the downscaled screenshot's space) onto the
/// real desktop. This is the Windows port of the coordinate math in
/// CompanionManager.sendTranscriptToClaudeWithScreenshot — minus the AppKit Y-flip,
/// because Windows already uses a top-left origin everywhere.
/// </summary>
public static class CoordinateMapper
{
    public static PointingTarget MapScreenshotPointToDesktop(
        double screenshotPixelX,
        double screenshotPixelY,
        MonitorScreenCapture monitor)
    {
        // Clamp into the screenshot's pixel space.
        var clampedX = Math.Clamp(screenshotPixelX, 0, monitor.ScreenshotWidthInPixels);
        var clampedY = Math.Clamp(screenshotPixelY, 0, monitor.ScreenshotHeightInPixels);

        // Scale up from the downscaled screenshot back to the monitor's full device-pixel size.
        var monitorBounds = monitor.MonitorBounds;
        var deviceXWithinMonitor = clampedX * (monitorBounds.Width / (double)monitor.ScreenshotWidthInPixels);
        var deviceYWithinMonitor = clampedY * (monitorBounds.Height / (double)monitor.ScreenshotHeightInPixels);

        // Offset into the virtual desktop. No Y-flip needed on Windows.
        var globalDeviceX = monitorBounds.X + deviceXWithinMonitor;
        var globalDeviceY = monitorBounds.Y + deviceYWithinMonitor;

        return new PointingTarget(globalDeviceX, globalDeviceY, monitor);
    }
}
