using System.ComponentModel;
using System.Windows;
using Clicky.Ai;
using Clicky.Audio;
using Clicky.Capture;
using Clicky.Input;
using Clicky.Platform;
using Clicky.Services;
using Clicky.UI.Overlay;

namespace Clicky.Core;

/// <summary>
/// Central state machine for the companion, ported from the macOS CompanionManager.
/// Owns the full push-to-talk pipeline: global hotkey → microphone capture →
/// local Whisper transcription → multi-monitor screenshot → local vision LLM →
/// local Piper TTS → blue cursor pointing. Exposes observable state for the panel UI.
/// </summary>
public sealed class CompanionManager : INotifyPropertyChanged, IDisposable
{
    private readonly AppConfig _config;
    private readonly GlobalHotkeyHook _hotkeyHook;
    private readonly MicrophoneCaptureService _microphone = new();
    private readonly ITranscriptionProvider _transcriptionProvider;
    private readonly ScreenCaptureService _screenCapture = new();
    private readonly LlamaServerProcess _llamaServer;
    private readonly LocalVisionLlmClient _visionLlm;
    private readonly PiperTtsClient _tts;
    private readonly OverlayWindowManager _overlayManager = new();
    private readonly PermissionService _permissionService = new();
    private readonly ConversationHistory _conversationHistory = new();

    private CancellationTokenSource? _currentResponseCancellation;
    private CancellationTokenSource? _transientHideCancellation;
    private double _currentAudioPowerLevel;
    private string? _activeResponseBubbleText;

    public CompanionManager(AppConfig config)
    {
        _config = config;
        _hotkeyHook = new GlobalHotkeyHook(config.Hotkey);
        _transcriptionProvider = new WhisperLocalTranscriptionProvider(config.Stt);
        _llamaServer = new LlamaServerProcess(config.VisionLlm);
        _visionLlm = new LocalVisionLlmClient(config.VisionLlm);
        _tts = new PiperTtsClient(config.Tts);

        IsCursorEnabled = config.Overlay.ShowCursorByDefault;

        // The screen capture service hides overlays so they aren't in the shot.
        _screenCapture.HideOverlayBeforeCapture = () => _overlayManager.HideForCaptureAsync();
        _screenCapture.RestoreOverlayAfterCapture = () => _overlayManager.RestoreAfterCapture();

        _hotkeyHook.Pressed += OnHotkeyPressed;
        _hotkeyHook.Released += OnHotkeyReleased;
        _microphone.AudioPowerLevelChanged += OnAudioPowerLevelChanged;
    }

    // ── Observable state ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private CompanionVoiceState _voiceState = CompanionVoiceState.Idle;
    public CompanionVoiceState VoiceState
    {
        get => _voiceState;
        private set
        {
            if (_voiceState == value)
            {
                return;
            }
            _voiceState = value;
            RaisePropertyChanged(nameof(VoiceState));
            RaisePropertyChanged(nameof(StatusText));
            PushOverlayState();
        }
    }

    public bool IsCursorEnabled { get; private set; }

    private string _statusDetail = "Starting up…";
    public string StatusText
    {
        get => _statusDetail;
        private set
        {
            if (_statusDetail == value)
            {
                return;
            }
            _statusDetail = value;
            RaisePropertyChanged(nameof(StatusText));
        }
    }

    public string? LastTranscript { get; private set; }

    // ── Lifecycle ───────────────────────────────────────────────────────

    public void Start()
    {
        _hotkeyHook.Start();

        if (IsCursorEnabled)
        {
            _overlayManager.ShowOverlay();
        }

        // Warm up the local models in the background so the first interaction is fast.
        _ = InitializeModelsAsync();
    }

    private async Task InitializeModelsAsync()
    {
        try
        {
            StatusText = "Loading speech model…";
            await _transcriptionProvider.InitializeAsync().ConfigureAwait(false);

            StatusText = "Starting vision model…";
            await _llamaServer.StartAndWaitUntilReadyAsync().ConfigureAwait(false);

            StatusText = _permissionService.HasMicrophone()
                ? "Ready — hold Ctrl+Alt and talk"
                : "No microphone detected";
        }
        catch (Exception exception)
        {
            StatusText = $"Setup error: {exception.Message}";
        }
    }

    public void SetCursorEnabled(bool enabled)
    {
        IsCursorEnabled = enabled;
        RaisePropertyChanged(nameof(IsCursorEnabled));

        CancelTransientHide();
        if (enabled)
        {
            _overlayManager.ShowOverlay();
        }
        else
        {
            _overlayManager.HideOverlay();
        }
    }

    // ── Hotkey handling ─────────────────────────────────────────────────

    private void OnHotkeyPressed()
    {
        if (_microphone.IsCapturing)
        {
            return;
        }

        // Cancel any in-flight response / TTS / pointing from a previous utterance.
        CancelCurrentResponse();
        CancelTransientHide();
        _tts.StopPlayback();
        _overlayManager.CancelPointing();

        // Bring the cursor back transiently if it's hidden.
        if (!IsCursorEnabled && !_overlayManager.IsVisible)
        {
            _overlayManager.ShowOverlay();
        }

        _microphone.StartCapture();
        VoiceState = CompanionVoiceState.Listening;
    }

    private void OnHotkeyReleased()
    {
        if (!_microphone.IsCapturing)
        {
            return;
        }

        var samples = _microphone.StopCaptureAndExtractSamples();
        VoiceState = CompanionVoiceState.Processing;

        if (samples.Length == 0)
        {
            VoiceState = CompanionVoiceState.Idle;
            ScheduleTransientHideIfNeeded();
            return;
        }

        RunResponsePipeline(samples);
    }

    private void OnAudioPowerLevelChanged(float level)
    {
        _currentAudioPowerLevel = level;
        PushOverlayState();
    }

    // ── Response pipeline ───────────────────────────────────────────────

    private void RunResponsePipeline(float[] samples)
    {
        _currentResponseCancellation = new CancellationTokenSource();
        var cancellationToken = _currentResponseCancellation.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var transcript = await _transcriptionProvider.TranscribeAsync(samples, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(transcript))
                {
                    SetVoiceStateOnUi(CompanionVoiceState.Idle);
                    return;
                }

                LastTranscript = transcript;

                var screenCaptures = await _screenCapture.CaptureAllScreensAsJpegAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var labeledImages = screenCaptures
                    .Select(capture => new LabeledImage(
                        capture.ImageData,
                        $"{capture.Label} (image dimensions: {capture.ScreenshotWidthInPixels}x{capture.ScreenshotHeightInPixels} pixels)"))
                    .ToList();

                var fullResponse = await _visionLlm.AnalyzeImagesStreamingAsync(
                    labeledImages,
                    SystemPrompts.CompanionVoiceResponse,
                    _conversationHistory.Exchanges,
                    transcript,
                    onTextChunk: _ => { /* spinner stays until TTS plays */ },
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                var parseResult = PointTagParser.Parse(fullResponse);
                var spokenText = parseResult.SpokenText;

                HandlePointing(parseResult, screenCaptures);

                _conversationHistory.Append(transcript, spokenText);

                if (!string.IsNullOrWhiteSpace(spokenText))
                {
                    _activeResponseBubbleText = spokenText;
                    await _tts.SpeakTextAsync(spokenText, cancellationToken).ConfigureAwait(false);
                    SetVoiceStateOnUi(CompanionVoiceState.Responding);

                    // Wait for playback to finish before returning to idle.
                    while (_tts.IsPlaying && !cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // User spoke again — interrupted on purpose.
            }
            catch (Exception exception)
            {
                StatusText = $"Response error: {exception.Message}";
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _activeResponseBubbleText = null;
                    SetVoiceStateOnUi(CompanionVoiceState.Idle);
                    ScheduleTransientHideIfNeeded();
                }
            }
        }, cancellationToken);
    }

    private void HandlePointing(PointingParseResult parseResult, IReadOnlyList<MonitorScreenCapture> screenCaptures)
    {
        if (!parseResult.HasCoordinate)
        {
            return;
        }

        // Pick the screen the LLM referenced, else fall back to the cursor screen.
        MonitorScreenCapture? targetMonitor = null;
        if (parseResult.ScreenNumber is { } screenNumber &&
            screenNumber >= 1 && screenNumber <= screenCaptures.Count)
        {
            targetMonitor = screenCaptures[screenNumber - 1];
        }
        targetMonitor ??= screenCaptures.FirstOrDefault(capture => capture.IsCursorScreen);
        if (targetMonitor is null)
        {
            return;
        }

        var target = CoordinateMapper.MapScreenshotPointToDesktop(
            parseResult.PointX!.Value, parseResult.PointY!.Value, targetMonitor);

        // Show the triangle (idle visual) so the flight is visible, then fly.
        SetVoiceStateOnUi(CompanionVoiceState.Idle);
        _overlayManager.PointTo(target, parseResult.ElementLabel, onArrived: () => { /* returns to cursor-follow automatically */ });
    }

    // ── Transient cursor mode ───────────────────────────────────────────

    private void ScheduleTransientHideIfNeeded()
    {
        if (IsCursorEnabled || !_overlayManager.IsVisible)
        {
            return;
        }

        CancelTransientHide();
        _transientHideCancellation = new CancellationTokenSource();
        var token = _transientHideCancellation.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (_tts.IsPlaying)
                {
                    await Task.Delay(200, token).ConfigureAwait(false);
                }

                await Task.Delay(1000, token).ConfigureAwait(false);
                _overlayManager.FadeOutAndHideOverlay();
            }
            catch (OperationCanceledException)
            {
                // User started another interaction — keep the overlay visible.
            }
        }, token);
    }

    private void CancelTransientHide()
    {
        _transientHideCancellation?.Cancel();
        _transientHideCancellation = null;
    }

    private void CancelCurrentResponse()
    {
        _currentResponseCancellation?.Cancel();
        _currentResponseCancellation = null;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void PushOverlayState() =>
        _overlayManager.UpdateCompanionState(_voiceState, _currentAudioPowerLevel, IsCursorEnabled, _activeResponseBubbleText);

    private void SetVoiceStateOnUi(CompanionVoiceState state) =>
        Application.Current.Dispatcher.Invoke(() => VoiceState = state);

    private void RaisePropertyChanged(string propertyName) =>
        Application.Current.Dispatcher.Invoke(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));

    public void OpenMicrophoneSettings() => _permissionService.OpenMicrophonePrivacySettings();

    public void Stop()
    {
        _hotkeyHook.Stop();
        _microphone.CancelCapture();
        CancelCurrentResponse();
        CancelTransientHide();
        _tts.StopPlayback();
        _overlayManager.ShutdownAll();
    }

    public void Dispose()
    {
        Stop();
        _hotkeyHook.Dispose();
        _microphone.Dispose();
        (_transcriptionProvider as IDisposable)?.Dispose();
        _llamaServer.Dispose();
        _tts.Dispose();
    }
}
