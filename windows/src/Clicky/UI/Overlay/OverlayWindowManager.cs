using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using Clicky.Core;
using Clicky.Platform;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace Clicky.UI.Overlay;

/// <summary>
/// Owns one transparent click-through overlay window per monitor and broadcasts
/// companion state to all of them. Mirrors the macOS OverlayWindowManager: the
/// cursor's monitor renders the following companion, and any monitor can host a
/// pointing flight. All work happens on the WPF UI thread.
/// </summary>
public sealed class OverlayWindowManager
{
    private readonly Dispatcher _uiDispatcher = Application.Current.Dispatcher;
    private readonly List<CursorOverlayWindow> _overlayWindows = new();
    private bool _isVisible;

    public bool IsVisible => _isVisible;

    public void ShowOverlay()
    {
        _uiDispatcher.Invoke(() =>
        {
            if (_overlayWindows.Count == 0)
            {
                CreateOverlayWindowsForAllMonitors();
            }

            foreach (var window in _overlayWindows)
            {
                window.Opacity = 1.0;
                window.Show();
            }
            _isVisible = true;
        });
    }

    private void CreateOverlayWindowsForAllMonitors()
    {
        foreach (var screen in WinFormsScreen.AllScreens)
        {
            var dpiScale = DpiHelper.GetMonitorScaleForBounds(screen.Bounds);
            var window = new CursorOverlayWindow(screen.Bounds, dpiScale);
            _overlayWindows.Add(window);
        }
    }

    public void HideOverlay()
    {
        _uiDispatcher.Invoke(() =>
        {
            foreach (var window in _overlayWindows)
            {
                window.Hide();
            }
            _isVisible = false;
        });
    }

    /// <summary>Fades all overlays out over ~0.4s, then hides them — for transient cursor mode.</summary>
    public void FadeOutAndHideOverlay()
    {
        _uiDispatcher.Invoke(() =>
        {
            if (_overlayWindows.Count == 0)
            {
                _isVisible = false;
                return;
            }

            var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            fadeTimer.Tick += (_, _) =>
            {
                var stillVisible = false;
                foreach (var window in _overlayWindows)
                {
                    window.Opacity = Math.Max(0, window.Opacity - 0.04);
                    if (window.Opacity > 0)
                    {
                        stillVisible = true;
                    }
                }

                if (!stillVisible)
                {
                    fadeTimer.Stop();
                    foreach (var window in _overlayWindows)
                    {
                        window.Hide();
                    }
                    _isVisible = false;
                }
            };
            fadeTimer.Start();
        });
    }

    /// <summary>Pushes the latest companion state to every overlay window.</summary>
    public void UpdateCompanionState(
        CompanionVoiceState voiceState,
        double audioPowerLevel,
        bool cursorVisible,
        string? responseBubbleText)
    {
        _uiDispatcher.Invoke(() =>
        {
            foreach (var window in _overlayWindows)
            {
                // Each window self-gates on whether the cursor is on its monitor,
                // so passing isActiveMonitor: true is correct for all of them.
                window.SetCompanionState(voiceState, audioPowerLevel, isActiveMonitor: true, cursorVisible, responseBubbleText);
            }
        });
    }

    /// <summary>Sends the companion flying to a desktop point on its monitor.</summary>
    public void PointTo(PointingTarget target, string? bubbleText, Action onArrived)
    {
        _uiDispatcher.Invoke(() =>
        {
            var targetWindow = FindWindowForMonitorBounds(target.Monitor.MonitorBounds);
            if (targetWindow is null)
            {
                onArrived();
                return;
            }

            var dpiScale = DpiHelper.GetMonitorScaleForBounds(target.Monitor.MonitorBounds);
            var localDipX = (target.GlobalDeviceX - target.Monitor.MonitorBounds.X) / dpiScale;
            var localDipY = (target.GlobalDeviceY - target.Monitor.MonitorBounds.Y) / dpiScale;

            targetWindow.FlyToLocalPoint(localDipX, localDipY, bubbleText, onArrived);
        });
    }

    public void CancelPointing()
    {
        _uiDispatcher.Invoke(() =>
        {
            foreach (var window in _overlayWindows)
            {
                window.CancelFlight();
            }
        });
    }

    // ── Screenshot integration ──────────────────────────────────────────

    /// <summary>Temporarily hides overlays so they're not captured in screenshots.</summary>
    public Task HideForCaptureAsync()
    {
        return _uiDispatcher.InvokeAsync(() =>
        {
            foreach (var window in _overlayWindows)
            {
                window.Visibility = Visibility.Hidden;
            }
        }).Task;
    }

    public void RestoreAfterCapture()
    {
        _uiDispatcher.Invoke(() =>
        {
            if (!_isVisible)
            {
                return;
            }
            foreach (var window in _overlayWindows)
            {
                window.Visibility = Visibility.Visible;
            }
        });
    }

    private CursorOverlayWindow? FindWindowForMonitorBounds(Rectangle monitorBounds)
    {
        // Match by monitor origin — overlay windows are created from the same Screen bounds.
        var index = 0;
        foreach (var screen in WinFormsScreen.AllScreens)
        {
            if (screen.Bounds == monitorBounds && index < _overlayWindows.Count)
            {
                return _overlayWindows[index];
            }
            index++;
        }

        return _overlayWindows.Count > 0 ? _overlayWindows[0] : null;
    }

    public void ShutdownAll()
    {
        _uiDispatcher.Invoke(() =>
        {
            foreach (var window in _overlayWindows)
            {
                window.ShutdownOverlay();
            }
            _overlayWindows.Clear();
        });
    }
}
