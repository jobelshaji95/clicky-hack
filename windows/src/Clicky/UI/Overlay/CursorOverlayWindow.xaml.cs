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
/// Renders the blue arrow companion, the listening waveform, the processing spinner,
/// and the streaming response card — the Windows port of the macOS OverlayWindow /
/// BlueCursorView. All visuals live on a ~60fps render loop.
///
/// The companion follows the cursor with a spring (damped, slightly trailing) for a
/// fluid feel, sits a little below-right of the pointer, and fades in gracefully on
/// first appearance. The window is positioned/sized in device-independent pixels
/// (DIPs); the monitor's device-pixel bounds are divided by its DPI scale so it lines
/// up exactly.
/// </summary>
public partial class CursorOverlayWindow : Window
{
    private readonly double _monitorDeviceLeft;
    private readonly double _monitorDeviceTop;
    private readonly double _dpiScale;

    private readonly DispatcherTimer _renderTimer;
    private readonly Polygon[] _waveformBars;
    private const int WaveformBarCount = 5;

    // How far below-right of the actual pointer the companion sits (DIPs). This gap
    // is the "spacing" that keeps the arrow from covering whatever it points at.
    private const double FollowOffsetX = 14;
    private const double FollowOffsetY = 17;

    // Spring tuning for the follow motion. Response is the rough time to settle;
    // a damping below 1.0 gives a touch of lively trailing without wobble.
    private const double SpringResponseSeconds = 0.20;
    private const double SpringDampingFraction = 0.65;

    // Companion state pushed in by the manager.
    private CompanionVoiceState _voiceState = CompanionVoiceState.Idle;
    private double _audioPowerLevel;
    private bool _isActiveMonitor;
    private bool _cursorVisible = true;

    // Smoothed follow position + velocity for the spring integrator.
    private Point _followPosition;
    private Vector _followVelocity;
    private bool _followInitialized;

    // Graceful fade-in the first time the companion becomes visible on this monitor.
    private double _entranceProgress;
    private const double EntranceDurationSeconds = 1.1;

    // ── Streaming response card ──────────────────────────────────────────
    private string? _responseText;
    private bool _responseStreaming;
    private double _responseCardOpacity;
    private double _responseCardOpacityTarget;

    // Caret blink.
    private DateTime _lastCaretToggle = DateTime.UtcNow;
    private bool _caretOn = true;

    // Live partial transcript shown while the user speaks.
    private string? _listeningCaptionText;

    // ── One-time welcome ─────────────────────────────────────────────────
    private static bool _welcomeConsumed;
    private enum WelcomePhase { Pending, Revealing, Holding, FadingOut, Done }
    private WelcomePhase _welcomePhase = WelcomePhase.Pending;
    private const string WelcomeMessage = "hey, i'm clicky";
    private int _welcomeRevealedCharacters;
    private DateTime _welcomeNextCharacterTime;
    private DateTime _welcomeHoldUntil;
    private double _welcomeOpacity;

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
                Points = new PointCollection { new(0, 0), new(4, 0), new(4, 30), new(0, 30) },
                Fill = new SolidColorBrush(Color.FromRgb(0x33, 0x80, 0xFF)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Color.FromRgb(0x33, 0x80, 0xFF),
                    BlurRadius = 6,
                    ShadowDepth = 0,
                    Opacity = 0.6
                }
            };
            Canvas.SetLeft(bar, barIndex * 8);
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
        bool cursorVisible)
    {
        _voiceState = voiceState;
        _audioPowerLevel = audioPowerLevel;
        _isActiveMonitor = isActiveMonitor;
        _cursorVisible = cursorVisible;
    }

    /// <summary>
    /// Sets (or clears) the streaming response card text. <paramref name="isStreaming"/>
    /// keeps the blinking caret alive while tokens are still arriving. Passing null text
    /// dismisses the card with a fade-out.
    /// </summary>
    public void SetResponseCard(string? text, bool isStreaming)
    {
        _responseText = text;
        _responseStreaming = isStreaming;
        _responseCardOpacityTarget = string.IsNullOrEmpty(text) ? 0.0 : 1.0;
    }

    /// <summary>Sets (or clears) the live partial-transcript caption shown while listening.</summary>
    public void SetListeningCaption(string? text) => _listeningCaptionText = text;

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
        var cursorOnThisMonitor = TryGetCursorLocalPoint(out var cursorRaw);

        // The response card and welcome pill track the actual pointer independently of
        // the companion's spring, so they stay glued to the cursor in every state.
        UpdateResponseCard(cursorOnThisMonitor, cursorRaw);
        UpdateListeningCaption(cursorOnThisMonitor, cursorRaw);

        if (_isFlying)
        {
            RenderFlight();
            return;
        }

        // Spinner always advances when visible.
        SpinnerRotation.Angle = (SpinnerRotation.Angle + 9) % 360;

        var shouldShowCompanion = _cursorVisible && _isActiveMonitor && cursorOnThisMonitor;

        UpdateEntranceOpacity(shouldShowCompanion);
        UpdateWelcome(shouldShowCompanion, cursorRaw);

        if (!shouldShowCompanion)
        {
            HideCompanionVisuals();
            return;
        }

        // Spring the companion toward a point just below-right of the pointer.
        var followTarget = new Point(cursorRaw.X + FollowOffsetX, cursorRaw.Y + FollowOffsetY);
        StepFollowSpring(followTarget);

        _lastTrianglePosition = _followPosition;
        PositionFollowingVisualsAt(_followPosition);
    }

    /// <summary>Damped-spring integrator that eases the companion toward the pointer.</summary>
    private void StepFollowSpring(Point target)
    {
        if (!_followInitialized)
        {
            _followPosition = target;
            _followVelocity = new Vector(0, 0);
            _followInitialized = true;
            return;
        }

        // Critically-shaped spring: omega from the response time, damping below 1.
        var angularFrequency = 2.0 * Math.PI / SpringResponseSeconds;
        var stiffness = angularFrequency * angularFrequency;
        var damping = 2.0 * SpringDampingFraction * angularFrequency;

        // Sub-step for stability at a stiff response.
        const int subSteps = 2;
        var stepSeconds = 0.016 / subSteps;
        for (var step = 0; step < subSteps; step++)
        {
            var accelerationX = -stiffness * (_followPosition.X - target.X) - damping * _followVelocity.X;
            var accelerationY = -stiffness * (_followPosition.Y - target.Y) - damping * _followVelocity.Y;
            _followVelocity = new Vector(
                _followVelocity.X + accelerationX * stepSeconds,
                _followVelocity.Y + accelerationY * stepSeconds);
            _followPosition = new Point(
                _followPosition.X + _followVelocity.X * stepSeconds,
                _followPosition.Y + _followVelocity.Y * stepSeconds);
        }
    }

    private void UpdateEntranceOpacity(bool shouldShowCompanion)
    {
        if (!shouldShowCompanion)
        {
            return;
        }

        if (_entranceProgress < 1.0)
        {
            _entranceProgress = Math.Min(1.0, _entranceProgress + 0.016 / EntranceDurationSeconds);
        }
    }

    /// <summary>Smoothstep eased entrance opacity (0→1).</summary>
    private double EntranceOpacity =>
        _entranceProgress * _entranceProgress * (3.0 - 2.0 * _entranceProgress);

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
                // Resume the follow spring from where the flight ended.
                _followInitialized = false;
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
        TriangleShape.Opacity = 1.0;
        WaveformCanvas.Visibility = Visibility.Collapsed;
        SpinnerShape.Visibility = Visibility.Collapsed;

        TriangleRotation.Angle = Math.Atan2(tangentY, tangentX) * (180.0 / Math.PI) + 90.0;

        // Scale pulse: grows to 1.3x at the midpoint of the flight.
        var scale = 1.0 + 0.3 * Math.Sin(_flightProgress * Math.PI);
        TriangleScale.ScaleX = scale;
        TriangleScale.ScaleY = scale;
        TriangleGlow.BlurRadius = 10 + (scale - 1.0) * 22;

        Canvas.SetLeft(TriangleShape, position.X - 8);
        Canvas.SetTop(TriangleShape, position.Y - 7);
        _lastTrianglePosition = position;
        _followPosition = position;

        if (!string.IsNullOrEmpty(_flightBubbleText))
        {
            ShowFlightBubble(_flightBubbleText, position);
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
                SpinnerShape.Opacity = EntranceOpacity;
                Canvas.SetLeft(SpinnerShape, cursorLocal.X + 14);
                Canvas.SetTop(SpinnerShape, cursorLocal.Y + 14);
                break;

            default: // Idle or Responding — show the arrow following the cursor.
                WaveformCanvas.Visibility = Visibility.Collapsed;
                SpinnerShape.Visibility = Visibility.Collapsed;
                TriangleShape.Visibility = Visibility.Visible;
                TriangleShape.Opacity = EntranceOpacity;
                TriangleScale.ScaleX = 1;
                TriangleScale.ScaleY = 1;
                TriangleRotation.Angle = -35;
                TriangleGlow.BlurRadius = 10;
                Canvas.SetLeft(TriangleShape, cursorLocal.X);
                Canvas.SetTop(TriangleShape, cursorLocal.Y);
                break;
        }

        if (!_isFlying)
        {
            BubbleBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderWaveform(Point cursorLocal)
    {
        WaveformCanvas.Visibility = Visibility.Visible;
        WaveformCanvas.Opacity = EntranceOpacity;
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
                new(0, top), new(4, top), new(4, top + height), new(0, top + height)
            };
        }
    }

    // ── Response card ────────────────────────────────────────────────────

    private void UpdateResponseCard(bool cursorOnThisMonitor, Point cursorRaw)
    {
        // Fade toward the target opacity each frame.
        var fadeStep = 0.12;
        if (_responseCardOpacity < _responseCardOpacityTarget)
        {
            _responseCardOpacity = Math.Min(_responseCardOpacityTarget, _responseCardOpacity + fadeStep);
        }
        else if (_responseCardOpacity > _responseCardOpacityTarget)
        {
            _responseCardOpacity = Math.Max(_responseCardOpacityTarget, _responseCardOpacity - fadeStep);
        }

        // Fully faded out → collapse and stop.
        if (_responseCardOpacity <= 0.001 && _responseCardOpacityTarget == 0.0)
        {
            ResponseCard.Visibility = Visibility.Collapsed;
            return;
        }

        // Only the monitor the cursor is on hosts the card; others stay hidden.
        if (!cursorOnThisMonitor)
        {
            ResponseCard.Visibility = Visibility.Collapsed;
            return;
        }

        ResponseBodyRun.Text = _responseText ?? string.Empty;

        // Blink the caret while streaming; hide it once the answer is final.
        if (_responseStreaming)
        {
            if ((DateTime.UtcNow - _lastCaretToggle).TotalMilliseconds > 500)
            {
                _caretOn = !_caretOn;
                _lastCaretToggle = DateTime.UtcNow;
            }
            ResponseCaretRun.Text = _caretOn ? "▍" : " ";
        }
        else
        {
            ResponseCaretRun.Text = string.Empty;
        }

        ResponseCard.Visibility = Visibility.Visible;
        ResponseCard.Opacity = _responseCardOpacity;
        ResponseCard.UpdateLayout();

        // Anchor below-right of the cursor; flip if it would run off the monitor.
        var cardX = cursorRaw.X + 18;
        if (cardX + ResponseCard.ActualWidth > Width)
        {
            cardX = cursorRaw.X - ResponseCard.ActualWidth - 16;
        }

        var cardY = cursorRaw.Y + 22;
        if (cardY + ResponseCard.ActualHeight > Height)
        {
            cardY = cursorRaw.Y - ResponseCard.ActualHeight - 14;
        }

        Canvas.SetLeft(ResponseCard, Math.Max(8, cardX));
        Canvas.SetTop(ResponseCard, Math.Max(8, cardY));
    }

    // ── Listening caption ────────────────────────────────────────────────

    private void UpdateListeningCaption(bool cursorOnThisMonitor, Point cursorRaw)
    {
        // Only while listening, with text, on the cursor's monitor.
        if (_voiceState != CompanionVoiceState.Listening ||
            string.IsNullOrWhiteSpace(_listeningCaptionText) ||
            !cursorOnThisMonitor)
        {
            ListeningCaption.Visibility = Visibility.Collapsed;
            return;
        }

        ListeningCaptionText.Text = _listeningCaptionText;
        ListeningCaption.Visibility = Visibility.Visible;
        ListeningCaption.UpdateLayout();

        // Sit above-right of the cursor (above the waveform); flip below if no room.
        var captionX = cursorRaw.X + 16;
        if (captionX + ListeningCaption.ActualWidth > Width)
        {
            captionX = Width - ListeningCaption.ActualWidth - 8;
        }

        var captionY = cursorRaw.Y - ListeningCaption.ActualHeight - 30;
        if (captionY < 8)
        {
            captionY = cursorRaw.Y + 34;
        }

        Canvas.SetLeft(ListeningCaption, Math.Max(8, captionX));
        Canvas.SetTop(ListeningCaption, Math.Max(8, captionY));
    }

    // ── Welcome ──────────────────────────────────────────────────────────

    private void UpdateWelcome(bool shouldShowCompanion, Point cursorRaw)
    {
        if (_welcomePhase == WelcomePhase.Done)
        {
            return;
        }

        // Wait until the companion has gracefully faded in, and claim the one-time
        // welcome globally so it only ever appears on a single monitor.
        if (_welcomePhase == WelcomePhase.Pending)
        {
            if (!shouldShowCompanion || _entranceProgress < 0.85 || _welcomeConsumed)
            {
                if (_welcomeConsumed)
                {
                    _welcomePhase = WelcomePhase.Done;
                }
                return;
            }

            _welcomeConsumed = true;
            _welcomePhase = WelcomePhase.Revealing;
            _welcomeRevealedCharacters = 0;
            _welcomeNextCharacterTime = DateTime.UtcNow;
            WelcomeText.Text = string.Empty;
            WelcomeBubble.Visibility = Visibility.Visible;
        }

        if (!shouldShowCompanion)
        {
            // Cursor left this monitor mid-welcome — just end it.
            WelcomeBubble.Visibility = Visibility.Collapsed;
            _welcomePhase = WelcomePhase.Done;
            return;
        }

        switch (_welcomePhase)
        {
            case WelcomePhase.Revealing:
                _welcomeOpacity = Math.Min(1.0, _welcomeOpacity + 0.1);
                if (DateTime.UtcNow >= _welcomeNextCharacterTime && _welcomeRevealedCharacters < WelcomeMessage.Length)
                {
                    _welcomeRevealedCharacters++;
                    WelcomeText.Text = WelcomeMessage[.._welcomeRevealedCharacters];
                    _welcomeNextCharacterTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(45);
                    if (_welcomeRevealedCharacters >= WelcomeMessage.Length)
                    {
                        _welcomePhase = WelcomePhase.Holding;
                        _welcomeHoldUntil = DateTime.UtcNow + TimeSpan.FromSeconds(2.4);
                    }
                }
                break;

            case WelcomePhase.Holding:
                if (DateTime.UtcNow >= _welcomeHoldUntil)
                {
                    _welcomePhase = WelcomePhase.FadingOut;
                }
                break;

            case WelcomePhase.FadingOut:
                _welcomeOpacity = Math.Max(0.0, _welcomeOpacity - 0.06);
                if (_welcomeOpacity <= 0.001)
                {
                    WelcomeBubble.Visibility = Visibility.Collapsed;
                    _welcomePhase = WelcomePhase.Done;
                    return;
                }
                break;
        }

        WelcomeBubble.Opacity = _welcomeOpacity;
        WelcomeBubble.UpdateLayout();
        Canvas.SetLeft(WelcomeBubble, cursorRaw.X + 16);
        Canvas.SetTop(WelcomeBubble, cursorRaw.Y + 22);
    }

    // ── Pointing bubble ──────────────────────────────────────────────────

    private void ShowFlightBubble(string text, Point anchor)
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

    private void HideCompanionVisuals()
    {
        TriangleShape.Visibility = Visibility.Collapsed;
        WaveformCanvas.Visibility = Visibility.Collapsed;
        SpinnerShape.Visibility = Visibility.Collapsed;
        if (!_isFlying)
        {
            BubbleBorder.Visibility = Visibility.Collapsed;
        }
        // Leaving this monitor resets the follow spring so it re-seats cleanly on return.
        _followInitialized = false;
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
