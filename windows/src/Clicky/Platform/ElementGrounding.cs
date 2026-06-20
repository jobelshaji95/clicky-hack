using System.Windows.Automation;

namespace Clicky.Platform;

/// <summary>
/// Refines the vision model's approximate pointing coordinate to the real UI control
/// under it, using Windows UI Automation — the Windows analog of the macOS
/// ElementLocationDetector. The model is good at "roughly there"; UIA snaps the cursor
/// to the actual button/field center so pointing lands precisely. Every step is
/// guarded so this can only ever improve a point, never throw or send it somewhere wild.
/// </summary>
public static class ElementGrounding
{
    /// <summary>
    /// Returns the center of the interactive control at <paramref name="globalDeviceX"/>,
    /// <paramref name="globalDeviceY"/> (global device pixels), or null if there's no
    /// suitable element or the snap would move the point too far to trust.
    /// </summary>
    public static (double X, double Y)? RefineToElementCenter(double globalDeviceX, double globalDeviceY)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(globalDeviceX, globalDeviceY));
            if (element is null)
            {
                return null;
            }

            // Never snap to our own click-through overlay (defensive — WS_EX_TRANSPARENT
            // already hides it from hit-testing).
            if (element.Current.ProcessId == Environment.ProcessId)
            {
                return null;
            }

            System.Windows.Rect bounds;
            try
            {
                bounds = element.Current.BoundingRectangle;
            }
            catch
            {
                return null;
            }

            if (bounds.IsEmpty || bounds.Width <= 1 || bounds.Height <= 1)
            {
                return null;
            }

            // Only snap to control-sized elements — not whole panes, lists, or windows.
            if (bounds.Width > 600 || bounds.Height > 400)
            {
                return null;
            }

            var centerX = bounds.Left + bounds.Width / 2.0;
            var centerY = bounds.Top + bounds.Height / 2.0;

            // Only accept a small correction near the model's guess; a big jump means we
            // grabbed the wrong element, so leave the original coordinate alone.
            if (Math.Abs(centerX - globalDeviceX) > 160 || Math.Abs(centerY - globalDeviceY) > 160)
            {
                return null;
            }

            return (centerX, centerY);
        }
        catch
        {
            return null;
        }
    }
}
