using System.Drawing;

namespace Clicky.Capture;

/// <summary>
/// A captured screenshot of one monitor plus the geometry needed to map the
/// LLM's pixel coordinates back to on-screen positions. Mirrors the macOS
/// CompanionScreenCapture struct.
///
/// Windows uses a single top-left-origin virtual desktop, so unlike macOS there
/// is no Y-axis flip — bounds are already in the coordinate space the overlay uses.
/// </summary>
public sealed class MonitorScreenCapture
{
    /// <summary>JPEG-encoded screenshot bytes.</summary>
    public required byte[] ImageData { get; init; }

    /// <summary>Human label sent to the LLM, e.g. "screen 1 (primary focus)".</summary>
    public required string Label { get; init; }

    /// <summary>True when the mouse cursor is currently on this monitor.</summary>
    public required bool IsCursorScreen { get; init; }

    /// <summary>1-based index matching the "screenN" the LLM references.</summary>
    public required int ScreenNumber { get; init; }

    public required int ScreenshotWidthInPixels { get; init; }
    public required int ScreenshotHeightInPixels { get; init; }

    /// <summary>The monitor's bounds in the virtual-desktop coordinate space (device pixels, top-left origin).</summary>
    public required Rectangle MonitorBounds { get; init; }
}
