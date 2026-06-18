using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Clicky.Core;
using Clicky.Platform;

namespace Clicky.UI.Overlay;

/// <summary>
/// One transparent, click-through, always-on-top window covering a single monitor.
/// Renders the blue triangle companion, the listening waveform, the processing
/// spinner, and the response bubble — the Windows port of the macOS OverlayWindow /
/// BlueCursorView. All visuals live on a 16 ms render loop.
///
/// The window is positioned and sized in device-independent pixels (DIPs); the
/// monitor's device-pixel bounds are divided by its DPI scale so it lines up exactly.
/// </summary>
public partial class CursorOverlayWindow : Window
{
    private readonly double _monitorDeviceLeft;
    private readonly double _monitorDeviceTop;
    private readonly double _dpiScale;

    private readonly DispatcherTimer _renderTimer;
    private readonly Polygon[] _waveformBars;
    private const int WaveformBarCount = 5;

    // Companion state pushed in by the manager.
    private CompanionVoiceState _voiceState = CompanionVoiceState.Idle;
    private double _audioPowerLevel;
    private bool _isActiveMonitor;
    private bool _cursorVisible = true;
    private string? _responseBubbleText;

    // Bezier flight state.
    private bool _isFlying;
    private Point _flightStart;
    private Point _flightControl;
    private Point _flightTarget;
    private double _flightProgress;
    private const double FlightProgressPerTick = 0.018; // ~1s flight at 60fps
    private DateTime _flightHoldUntil;
    private Action? _flightArrivalCallback;
    private string? _flightBubbleText;

    private Point _lastTrianglePosition;

    public CursorOverlayWindow(System.Drawing.Rectangle monitorDeviceBounds, double dpiScale)
    {
        InitializeComponent();

        _monitorDeviceLeft = monitorDeviceBounds.X;
        _monitorDeviceTop = monitorDeviceBounds.Y;
        _dpiScale = dpiScale <= 0 ? 1.0 : dpiScale;

        // Position/size the window in DIPs so it exactly overlays the monitor.
        Left = monitorDeviceBounds.X / _dpiScale;
        Top = monitorDeviceBounds.Y / _dpiScale;
        Width = monitorDeviceBounds.Width / _dpiScale;
        Height = monitorDeviceBounds.Height / _dpiScale;

        _waveformBars = BuildWaveformBars();

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _renderTimer.Tick += OnRenderTick;
    }

    protected override void OnSourceInitialized(EventArgs eventArgs)
    {
        base.OnSourceInitialized(eventArgs);

        // Apply the extended styles that make this a true click-through, focus-safe
        // overlay: layered + transparent (clicks pass beneath), no-activate (never
        // steals focus), tool-window (hidden from Alt+Tab).
        var windowHandle = new WindowInteropHelper(this).Handle;
        var existingExStyle = NativeMethods.GetWindowLong(windowHandle, NativeMethods.GWL_EXSTYLE);
        var newExStyle = existingExStyle
            | NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_NOACTIVATE
            | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(windowHandle, NativeMethods.GWL_EXSTYLE, newExStyle);

        _renderTimer.Start();
    }

    private Polygon[] BuildWaveformBars()
    {
        var bars = new Polygon[WaveformBarCount];
        for (var barIndex = 0; barIndex < WaveformBarCount; barIndex++)
        {
            var bar = new Polygon
            {
                Points = new PointCollection { new(0, 0), new(6, 0), new(6, 30), new(0, 30) },
                Fill = new SolidColorBrush(Color.FromRgb(0x33, 0x80, 0xFF))
            };
            Canvas.SetLeft(bar, barIndex * 10);
            WaveformCanvas.Children.Add(bar);
            bars[barIndex] = bar;
        }

        return bars;
    }

    // ── State updates from the manager ──────────────────────────────────

    public void SetCompanionState(
        CompanionVoiceState voiceState,
        double audioPowerLevel,
        bool isActiveMonitor,
        bool cursorVisible,
        string? responseBubbleText)
    {
        _voiceState = voiceState;
        _audioPowerLevel = audioPowerLevel;
        _isActiveMonitor = isActiveMonitor;
        _cursorVisible = cursorVisible;
        _responseBubbleText = responseBubbleText;
    }

    /// <summary>Begins a bezier flight from the current triangle position to a local DIP target.</summary>
    public void FlyToLocalPoint(double localDipX, double localDipY, string? bubbleText, Action onArrived)
    {
        _flightStart = _lastTrianglePosition;
        _flightTarget = new Point(localDipX, localDipY);

        // Control point lifted off the straight line for an arc (matches the macOS bezier).
        var midpoint = new Point((_flightStart.X + _flightTarget.X) / 2, (_flightStart.Y + _flightTarget.Y) / 2);
        var arcHeight = Math.Min(220, Distance(_flightStart, _flightTarget) * 0.35);
        _flightControl = new Point(midpoint.X, midpoint.Y - arcHeight);

        _flightProgress = 0;
        _flightBubbleText = bubbleText;
        _flightArrivalCallback = onArrived;
        _isFlying = true;
    }

    public void CancelFlight()
    {
        _isFlying = false;
        _flightArrivalCallback = null;
        _flightBubbleText = null;
    }

    // ── Render loop ─────────────────────────────────────────────────────

    private void OnRenderTick(object? sender, EventArgs eventArgs)
    {
        if (_isFlying)
        {
            RenderFlight();
            return;
        }

        // Spinner always advances when visible.
        SpinnerRotation.Angle = (SpinnerRotation.Angle + 9) % 360;

        var cursorOnThisMonitor = TryGetCursorLocalPoint(out var cursorLocal);
        var shouldShowCompanion = _cursorVisible && _isActiveMonitor && cursorOnThisMonitor;

        if (!shouldShowCompanion)
        {
            HideAllVisuals();
            return;
        }

        _lastTrianglePosition = cursorLocal;
        PositionFollowingVisualsAt(cursorLocal);
    }

    private void RenderFlight()
    {
        // Hold at the target for a beat so the user can see the bubble, then resume.
        if (_flightProgress >= 1.0)
        {
            if (_flightHoldUntil == default)
            {
                _flightHoldUntil = DateTime.UtcNow + TimeSpan.FromSeconds(2.5);
            }

            if (DateTime.UtcNow >= _flightHoldUntil)
            {
                _isFlying = false;
                _flightHoldUntil = default;
                BubbleBorder.Visibility = Visibility.Collapsed;
                var callback = _flightArrivalCallback;
                _flightArrivalCallback = null;
                callback?.Invoke();
            }
            return;
        }

        _flightProgress = Math.Min(1.0, _flightProgress + FlightProgressPerTick);
        var oneMinusT = 1 - _flightProgress;

        // Quadratic bezier position B(t) = (1-t)^2 P0 + 2(1-t)t P1 + t^2 P2.
        var position = new Point(
            oneMinusT * oneMinusT * _flightStart.X + 2 * oneMinusT * _flightProgress * _flightControl.X + _flightProgress * _flightProgress * _flightTarget.X,
            oneMinusT * oneMinusT * _flightStart.Y + 2 * oneMinusT * _flightProgress * _flightControl.Y + _flightProgress * _flightProgress * _flightTarget.Y);

        // Tangent B'(t) for rotation; +90 because the triangle tip points up at 0.
        var tangentX = 2 * oneMinusT * (_flightControl.X - _flightStart.X) + 2 * _flightProgress * (_flightTarget.X - _flightControl.X);
        var tangentY = 2 * oneMinusT * (_flightControl.Y - _flightStart.Y) + 2 * _flightProgress * (_flightTarget.Y - _flightControl.Y);

        TriangleShape.Visibility = Visibility.Visible;
        WaveformCanvas.Visibility = Visibility.Collapsed;
        SpinnerShape.Visibility = Visibility.Collapsed;

        TriangleRotation.Angle = Math.Atan2(tangentY, tangentX) * (180.0 / Math.PI) + 90.0;

        // Scale pulse: grows to 1.3x at the midpoint of the flight.
        var scale = 1.0 + 0.3 * Math.Sin(_flightProgress * Math.PI);
        TriangleScale.ScaleX = scale;
        TriangleScale.ScaleY = scale;
        TriangleGlow.BlurRadius = 8 + (scale - 1.0) * 20;

        Canvas.SetLeft(TriangleShape, position.X - 10);
        Canvas.SetTop(TriangleShape, position.Y);
        _lastTrianglePosition = position;

        if (!string.IsNullOrEmpty(_flightBubbleText))
        {
            ShowBubble(_flightBubbleText, position);
        }
    }

    private void PositionFollowingVisualsAt(Point cursorLocal)
    {
        switch (_voiceState)
        {
            case CompanionVoiceState.Listening:
                TriangleShape.Visibility = Visibility.Collapsed;
                SpinnerShape.Visibility = Visibility.Collapsed;
                RenderWaveform(cursorLocal);
                break;

            case CompanionVoiceState.Processing:
                TriangleShape.Visibility = Visibility.Collapsed;
                WaveformCanvas.Visibility = Visibility.Collapsed;
                SpinnerShape.Visibility = Visibility.Visible;
                Canvas.SetLeft(SpinnerShape, cursorLocal.X + 14);
                Canvas.SetTop(SpinnerShape, cursorLocal.Y + 14);
                break;

            default: // Idle or Responding — show the triangle following the cursor.
                WaveformCanvas.Visibility = Visibility.Collapsed;
                SpinnerShape.Visibility = Visibility.Collapsed;
                TriangleShape.Visibility = Visibility.Visible;
                TriangleScale.ScaleX = 1;
                TriangleScale.ScaleY = 1;
                TriangleRotation.Angle = -35;
                TriangleGlow.BlurRadius = 8;
                Canvas.SetLeft(TriangleShape, cursorLocal.X);
                Canvas.SetTop(TriangleShape, cursorLocal.Y);
                break;
        }

        // Show the response bubble while responding, anchored near the cursor.
        if (_voiceState == CompanionVoiceState.Responding && !string.IsNullOrEmpty(_responseBubbleText))
        {
            ShowBubble(_responseBubbleText, cursorLocal);
        }
        else if (!_isFlying)
        {
            BubbleBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderWaveform(Point cursorLocal)
    {
        WaveformCanvas.Visibility = Visibility.Visible;
        Canvas.SetLeft(WaveformCanvas, cursorLocal.X + 12);
        Canvas.SetTop(WaveformCanvas, cursorLocal.Y - 20);

        // Each bar height scales with the audio level, with a little per-bar variation.
        for (var barIndex = 0; barIndex < _waveformBars.Length; barIndex++)
        {
            var variation = 0.6 + 0.4 * Math.Abs(Math.Sin(barIndex + Environment.TickCount / 120.0));
            var height = Math.Max(4, _audioPowerLevel * 30 * variation);
            var top = (30 - height) / 2;
            _waveformBars[barIndex].Points = new PointCollection
            {
                new(0, top), new(6, top), new(6, top + height), new(0, top + height)
            };
        }
    }

    private void ShowBubble(string text, Point anchor)
    {
        BubbleText.Text = text;
        BubbleBorder.Visibility = Visibility.Visible;
        BubbleBorder.UpdateLayout();

        // Default to the right of the cursor; flip left if it would run off-screen.
        var bubbleX = anchor.X + 22;
        if (bubbleX + BubbleBorder.ActualWidth > Width)
        {
            bubbleX = anchor.X - BubbleBorder.ActualWidth - 12;
        }

        var bubbleY = anchor.Y - 6;
        if (bubbleY + BubbleBorder.ActualHeight > Height)
        {
            bubbleY = Height - BubbleBorder.ActualHeight - 8;
        }

        Canvas.SetLeft(BubbleBorder, Math.Max(0, bubbleX));
        Canvas.SetTop(BubbleBorder, Math.Max(0, bubbleY));
    }

    private void HideAllVisuals()
    {
        TriangleShape.Visibility = Visibility.Collapsed;
        WaveformCanvas.Visibility = Visibility.Collapsed;
        SpinnerShape.Visibility = Visibility.Collapsed;
        if (!_isFlying)
        {
            BubbleBorder.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Reads the global cursor position and converts it to this window's local DIP space if it's on this monitor.</summary>
    private bool TryGetCursorLocalPoint(out Point localPoint)
    {
        localPoint = default;
        if (!NativeMethods.GetCursorPos(out var cursorDevice))
        {
            return false;
        }

        var localDeviceX = cursorDevice.X - _monitorDeviceLeft;
        var localDeviceY = cursorDevice.Y - _monitorDeviceTop;

        // Outside this monitor's device bounds → not ours to render.
        if (localDeviceX < 0 || localDeviceY < 0 ||
            localDeviceX > Width * _dpiScale || localDeviceY > Height * _dpiScale)
        {
            return false;
        }

        localPoint = new Point(localDeviceX / _dpiScale, localDeviceY / _dpiScale);
        return true;
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public void ShutdownOverlay()
    {
        _renderTimer.Stop();
        Close();
    }
}
