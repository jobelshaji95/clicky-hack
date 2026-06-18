using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using WinFormsScreen = System.Windows.Forms.Screen;
using WinFormsCursor = System.Windows.Forms.Cursor;

namespace Clicky.Capture;

/// <summary>
/// Multi-monitor screenshot capture using GDI BitBlt (Graphics.CopyFromScreen).
/// GDI is more than fast enough for single still frames and works seamlessly
/// across all displays. This is the Windows replacement for ScreenCaptureKit.
///
/// The cursor's monitor is captured first and labeled "primary focus" so the LLM
/// prioritizes it, matching the macOS ordering.
/// </summary>
public sealed class ScreenCaptureService
{
    private const int MaximumScreenshotDimension = 1280;
    private const long JpegQuality = 80L;

    /// <summary>Hook the overlay manager can use to hide overlay windows during capture so they're not in the shot.</summary>
    public Func<Task>? HideOverlayBeforeCapture { get; set; }
    public Action? RestoreOverlayAfterCapture { get; set; }

    public async Task<IReadOnlyList<MonitorScreenCapture>> CaptureAllScreensAsJpegAsync()
    {
        if (HideOverlayBeforeCapture is not null)
        {
            await HideOverlayBeforeCapture().ConfigureAwait(true);
        }

        try
        {
            // Capture off the UI thread — GDI work and JPEG encoding are CPU-bound.
            return await Task.Run(CaptureAllScreensCore).ConfigureAwait(true);
        }
        finally
        {
            RestoreOverlayAfterCapture?.Invoke();
        }
    }

    private IReadOnlyList<MonitorScreenCapture> CaptureAllScreensCore()
    {
        var cursorPosition = WinFormsCursor.Position;

        // Order so the monitor containing the cursor is first.
        var orderedScreens = WinFormsScreen.AllScreens
            .OrderByDescending(screen => screen.Bounds.Contains(cursorPosition))
            .ToList();

        var captures = new List<MonitorScreenCapture>(orderedScreens.Count);
        for (var screenIndex = 0; screenIndex < orderedScreens.Count; screenIndex++)
        {
            var screen = orderedScreens[screenIndex];
            var isCursorScreen = screen.Bounds.Contains(cursorPosition);
            var screenNumber = screenIndex + 1;

            var (jpegBytes, widthInPixels, heightInPixels) = CaptureMonitorAsJpeg(screen.Bounds);

            var label = isCursorScreen
                ? $"screen {screenNumber} (primary focus — cursor is here)"
                : $"screen {screenNumber}";

            captures.Add(new MonitorScreenCapture
            {
                ImageData = jpegBytes,
                Label = label,
                IsCursorScreen = isCursorScreen,
                ScreenNumber = screenNumber,
                ScreenshotWidthInPixels = widthInPixels,
                ScreenshotHeightInPixels = heightInPixels,
                MonitorBounds = screen.Bounds
            });
        }

        return captures;
    }

    private static (byte[] Jpeg, int Width, int Height) CaptureMonitorAsJpeg(Rectangle monitorBounds)
    {
        using var fullResolutionBitmap = new Bitmap(monitorBounds.Width, monitorBounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(fullResolutionBitmap))
        {
            graphics.CopyFromScreen(monitorBounds.X, monitorBounds.Y, 0, 0, monitorBounds.Size, CopyPixelOperation.SourceCopy);
        }

        // Scale down so the longest edge is at most MaximumScreenshotDimension,
        // keeping aspect ratio (matches the macOS 1280px cap).
        var scaleFactor = Math.Min(1.0,
            MaximumScreenshotDimension / (double)Math.Max(monitorBounds.Width, monitorBounds.Height));
        var targetWidth = Math.Max(1, (int)Math.Round(monitorBounds.Width * scaleFactor));
        var targetHeight = Math.Max(1, (int)Math.Round(monitorBounds.Height * scaleFactor));

        using var scaledBitmap = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scaledBitmap))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(fullResolutionBitmap, 0, 0, targetWidth, targetHeight);
        }

        using var memoryStream = new MemoryStream();
        var jpegEncoder = GetJpegEncoder();
        var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
        scaledBitmap.Save(memoryStream, jpegEncoder, encoderParameters);

        return (memoryStream.ToArray(), targetWidth, targetHeight);
    }

    private static ImageCodecInfo GetJpegEncoder() =>
        ImageCodecInfo.GetImageEncoders().First(encoder => encoder.FormatID == ImageFormat.Jpeg.Guid);
}
