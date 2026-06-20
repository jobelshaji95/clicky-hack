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

    /// <summary>
    /// Finds the real control whose name matches the model's label (e.g. "close button")
    /// in the foreground window and returns its center, using the model's coordinate only
    /// as a tie-breaking hint. This is the strong fix for the local vision model's shaky
    /// coordinates — it points at the actual named control instead of where the model
    /// guessed. Returns null if there's no confident match. Time-boxed so a big window's
    /// UIA tree can never stall pointing.
    /// </summary>
    public static (double X, double Y)? RefineToLabeledElement(
        string label,
        double hintX,
        double hintY,
        double monitorLeft,
        double monitorTop,
        double monitorRight,
        double monitorBottom)
    {
        var keywords = ExtractKeywords(label);
        if (keywords.Length == 0)
        {
            return null;
        }

        // Fast-path for "close the window/app": the caption close button is always at the
        // top-right of the foreground window. This is computed from the window rectangle,
        // so it works even for Chromium/Electron apps whose UIA tree hides the caption
        // buttons — exactly the case the local model points at wrong most often.
        var closeButton = TryGetCloseButtonPoint(keywords, monitorLeft, monitorTop, monitorRight, monitorBottom);
        if (closeButton is not null)
        {
            return closeButton;
        }

        try
        {
            var search = System.Threading.Tasks.Task.Run(() => FindLabeledElementCenter(
                keywords, hintX, hintY, monitorLeft, monitorTop, monitorRight, monitorBottom));

            return search.Wait(TimeSpan.FromMilliseconds(900)) ? search.Result : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the foreground window's caption close-button center when the label is about
    /// closing/exiting, computed from the window rectangle so it's reliable across app types.
    /// </summary>
    private static (double X, double Y)? TryGetCloseButtonPoint(
        string[] keywords,
        double monitorLeft,
        double monitorTop,
        double monitorRight,
        double monitorBottom)
    {
        var wantsClose = keywords.Any(keyword => keyword is "close" or "exit" or "quit");
        if (!wantsClose)
        {
            return null;
        }

        var windowHandle = NativeMethods.GetForegroundWindow();
        if (windowHandle == IntPtr.Zero)
        {
            return null;
        }

        if (!NativeMethods.GetWindowRect(windowHandle, out var rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        // Ignore tiny/borderless windows where there's no standard caption to aim at.
        if (width < 120 || height < 60)
        {
            return null;
        }

        var dpiScale = DpiHelper.GetMonitorScaleForBounds(
            new System.Drawing.Rectangle(rect.Left, rect.Top, width, height));

        // The rightmost caption button (Close) sits ~24 logical px from the right edge and
        // ~16 logical px down from the top, scaled for the monitor's DPI.
        var closeX = rect.Right - 24 * dpiScale;
        var closeY = rect.Top + 16 * dpiScale;

        if (closeX < monitorLeft || closeX > monitorRight || closeY < monitorTop || closeY > monitorBottom)
        {
            return null;
        }

        return (closeX, closeY);
    }

    private static (double X, double Y)? FindLabeledElementCenter(
        string[] keywords,
        double hintX,
        double hintY,
        double monitorLeft,
        double monitorTop,
        double monitorRight,
        double monitorBottom)
    {
        var foregroundWindowHandle = NativeMethods.GetForegroundWindow();
        if (foregroundWindowHandle == IntPtr.Zero)
        {
            return null;
        }

        AutomationElement window;
        try
        {
            window = AutomationElement.FromHandle(foregroundWindowHandle);
        }
        catch
        {
            return null;
        }

        if (window is null)
        {
            return null;
        }

        // Breadth-first walk bounded in depth and node count. A single FindAll(Descendants)
        // forces UIA to flatten the whole tree, which is slow and can fault on heavy apps
        // (browsers). Title-bar / toolbar controls like Close, Save, Send live near the top
        // of the tree, so a shallow bounded walk finds them without diving into page content.
        var controlWalker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((window, 0));

        const int maxNodesVisited = 600;
        const int maxDepth = 6;
        var nodesVisited = 0;

        (double X, double Y)? best = null;
        var bestScore = double.MaxValue;

        while (queue.Count > 0 && nodesVisited < maxNodesVisited)
        {
            var (element, depth) = queue.Dequeue();
            nodesVisited++;

            try
            {
                var info = element.Current;
                if (info.ControlType == ControlType.Button && !string.IsNullOrEmpty(info.Name))
                {
                    var loweredName = info.Name.ToLowerInvariant();
                    var matchedExactly = false;
                    var matched = false;
                    foreach (var keyword in keywords)
                    {
                        if (loweredName == keyword)
                        {
                            matchedExactly = true;
                            matched = true;
                            break;
                        }
                        if (loweredName.Contains(keyword))
                        {
                            matched = true;
                        }
                    }

                    if (matched)
                    {
                        var bounds = info.BoundingRectangle;
                        if (!bounds.IsEmpty && bounds.Width >= 1 && bounds.Height >= 1)
                        {
                            var centerX = bounds.Left + bounds.Width / 2.0;
                            var centerY = bounds.Top + bounds.Height / 2.0;

                            // The control must be on the monitor the model was looking at.
                            if (centerX >= monitorLeft && centerX <= monitorRight &&
                                centerY >= monitorTop && centerY <= monitorBottom)
                            {
                                // Prefer exact name matches strongly; break ties by closeness
                                // to the model's guess so "reply" picks the nearest one.
                                var distance = Math.Abs(centerX - hintX) + Math.Abs(centerY - hintY);
                                var score = (matchedExactly ? 0.0 : 1_000_000.0) + distance;
                                if (score < bestScore)
                                {
                                    bestScore = score;
                                    best = (centerX, centerY);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Skip elements that fault when read; keep walking.
            }

            if (depth < maxDepth)
            {
                try
                {
                    var child = controlWalker.GetFirstChild(element);
                    while (child is not null && nodesVisited + queue.Count < maxNodesVisited)
                    {
                        queue.Enqueue((child, depth + 1));
                        child = controlWalker.GetNextSibling(child);
                    }
                }
                catch
                {
                    // Some subtrees fault when enumerated — ignore and continue.
                }
            }
        }

        return best;
    }

    private static readonly HashSet<string> LabelStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "button", "icon", "menu", "the", "a", "an", "bar", "field", "box",
        "item", "tab", "link", "to", "of", "on", "in", "your", "this",
    };

    /// <summary>Pulls the meaningful words out of a label like "close button" → ["close"].</summary>
    private static string[] ExtractKeywords(string label)
    {
        return label
            .Split(new[] { ' ', '-', '_', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.ToLowerInvariant())
            .Where(word => word.Length >= 2 && !LabelStopWords.Contains(word))
            .ToArray();
    }
}
