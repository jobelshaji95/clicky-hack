using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using Clicky.Ai;
using Clicky.Audio;
using Clicky.Capture;
using Clicky.Diagnostics;
using Clicky.Input;
using Clicky.Platform;
using Clicky.Services;
using Clicky.UI.Overlay;

namespace Clicky.Core;

/// <summary>
/// Central state machine for the companion, ported from the macOS CompanionManager.
/// Owns the full push-to-talk pipeline: global hotkey → microphone capture →
/// local Whisper transcription → multi-monitor screenshot → local vision LLM →
/// streamed on-screen response card → blue cursor pointing. Audio output is
/// intentionally disabled — the response is shown, not spoken. Exposes observable
/// state for the panel UI.
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
    private readonly OverlayWindowManager _overlayManager = new();
    private readonly PermissionService _permissionService = new();
    private readonly ConversationHistory _conversationHistory = new();

    private CancellationTokenSource? _currentResponseCancellation;
    private CancellationTokenSource? _transientHideCancellation;
    private double _currentAudioPowerLevel;
    private string? _startupWarningStatusText;

    // Playful phrases shown in the flight bubble when the cursor points at something,
    // matching the macOS companion's "right here!" personality.
    private static readonly string[] PointingPhrases =
    {
        "right here!", "this one!", "over here!", "click this!", "here it is!", "found it!", "look here!"
    };
    private static readonly Random PointingPhraseRandom = new();

    public CompanionManager(AppConfig config)
    {
        _config = config;
        _hotkeyHook = new GlobalHotkeyHook(config.Hotkey);
        _transcriptionProvider = new WhisperLocalTranscriptionProvider(config.Stt);
        _llamaServer = new LlamaServerProcess(config.VisionLlm);
        _visionLlm = new LocalVisionLlmClient(config.VisionLlm);

        IsCursorEnabled = config.Overlay.ShowCursorByDefault;
        EngineSummary = $"{config.VisionLlm.ModelName} · whisper · 100% local";

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

    /// <summary>Short description of the local engines, shown in the panel footer.</summary>
    public string EngineSummary { get; }

    // ── Lifecycle ───────────────────────────────────────────────────────

    public void Start()
    {
        try
        {
            _hotkeyHook.Start();
            ClickyLog.Info("Hotkey", "Global push-to-talk hook installed (Ctrl+Alt).");
        }
        catch (Exception exception)
        {
            _startupWarningStatusText = $"Hotkey error: {exception.Message}";
            StatusText = _startupWarningStatusText;
            ClickyLog.Error("Hotkey", "Failed to install global hotkey hook", exception);
        }

        if (IsCursorEnabled)
        {
            _overlayManager.ShowOverlay();
            // Push the initial state so the overlays activate (_isActiveMonitor) and
            // render the idle companion right away. Without this the first state push
            // only happens on the first Ctrl+Alt press, leaving the cursor companion
            // invisible at startup.
            PushOverlayState();
        }

        // Warm up the local models in the background so the first interaction is fast.
        _ = InitializeModelsAsync();
    }

    private async Task InitializeModelsAsync()
    {
        try
        {
            StatusText = "Loading speech model…";
            ClickyLog.Info("Init", "Loading Whisper speech model…");
            await _transcriptionProvider.InitializeAsync().ConfigureAwait(false);
            ClickyLog.Info("Init", "Whisper model loaded.");

            StatusText = "Starting vision model…";
            ClickyLog.Info("Init", "Starting llama.cpp vision server…");
            await _llamaServer.StartAndWaitUntilReadyAsync().ConfigureAwait(false);
            ClickyLog.Info("Init", "Vision server healthy.");

            var hasMicrophone = _permissionService.HasMicrophone();
            var readyStatusText = hasMicrophone
                ? "Ready — hold Ctrl+Alt and talk"
                : "No microphone detected";
            StatusText = _startupWarningStatusText ?? readyStatusText;
            ClickyLog.Info("Init", $"Ready. microphone={hasMicrophone}.");
        }
        catch (Exception exception)
        {
            StatusText = $"Setup error: {exception.Message}";
            ClickyLog.Error("Init", "Model warm-up failed", exception);
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

        // Cancel any in-flight response / pointing from a previous utterance.
        CancelCurrentResponse();
        CancelTransientHide();
        _overlayManager.HideResponse();
        _overlayManager.CancelPointing();

        // Bring the cursor back transiently if it's hidden.
        if (!IsCursorEnabled && !_overlayManager.IsVisible)
        {
            _overlayManager.ShowOverlay();
        }

        _microphone.StartCapture();
        VoiceState = CompanionVoiceState.Listening;
        ClickyLog.Info("Hotkey", "Pressed — listening.");
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
            // Capture this interaction for the debug history (transcript, response,
            // pointing, per-stage timings, any error) and time the whole thing.
            var interaction = new InteractionRecord();
            var totalStopwatch = Stopwatch.StartNew();

            try
            {
                ClickyLog.Info("Pipeline", $"Interaction start — {samples.Length} audio samples.");

                var transcriptionStopwatch = Stopwatch.StartNew();
                var transcript = await _transcriptionProvider.TranscribeAsync(samples, cancellationToken).ConfigureAwait(false);
                transcriptionStopwatch.Stop();
                interaction.TranscriptionMs = transcriptionStopwatch.ElapsedMilliseconds;
                cancellationToken.ThrowIfCancellationRequested();

                interaction.Transcript = transcript;
                ClickyLog.Info("Pipeline", $"Transcribed in {interaction.TranscriptionMs}ms: \"{transcript}\"");

                if (string.IsNullOrWhiteSpace(transcript))
                {
                    ClickyLog.Info("Pipeline", "Empty transcript — nothing to send.");
                    SetVoiceStateOnUi(CompanionVoiceState.Idle);
                    return;
                }

                LastTranscript = transcript;

                var screenCaptures = await _screenCapture.CaptureAllScreensAsJpegAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ClickyLog.Info("Pipeline", $"Captured {screenCaptures.Count} screen(s).");

                var labeledImages = screenCaptures
                    .Select(capture => new LabeledImage(
                        capture.ImageData,
                        $"{capture.Label} (image dimensions: {capture.ScreenshotWidthInPixels}x{capture.ScreenshotHeightInPixels} pixels)"))
                    .ToList();

                // Stream the model's text straight into the on-screen response card.
                // The first token flips the state to Responding (so the cursor's
                // spinner gives way to the card) and the card reveals text as it
                // arrives — no audio is produced.
                var accumulatedResponse = new StringBuilder();
                var hasStartedResponding = false;
                var visionStopwatch = Stopwatch.StartNew();

                var fullResponse = await _visionLlm.AnalyzeImagesStreamingAsync(
                    labeledImages,
                    SystemPrompts.CompanionVoiceResponse,
                    _conversationHistory.Exchanges,
                    transcript,
                    onTextChunk: chunk =>
                    {
                        accumulatedResponse.Append(chunk);

                        if (!hasStartedResponding)
                        {
                            hasStartedResponding = true;
                            SetVoiceStateOnUi(CompanionVoiceState.Responding);
                        }

                        // Hide the trailing [POINT:...] machine tag while streaming so
                        // the user only ever sees clean prose.
                        var visibleText = StripTrailingPointTag(accumulatedResponse.ToString());
                        if (!string.IsNullOrWhiteSpace(visibleText))
                        {
                            _overlayManager.UpdateResponse(visibleText, isStreaming: true);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                visionStopwatch.Stop();
                interaction.VisionMs = visionStopwatch.ElapsedMilliseconds;
                cancellationToken.ThrowIfCancellationRequested();

                var parseResult = PointTagParser.Parse(fullResponse);
                var spokenText = parseResult.SpokenText;

                interaction.Response = spokenText;
                interaction.Pointed = parseResult.HasCoordinate;
                interaction.PointLabel = parseResult.ElementLabel;
                interaction.PointX = parseResult.PointX is { } pointX ? (int)pointX : null;
                interaction.PointY = parseResult.PointY is { } pointY ? (int)pointY : null;
                interaction.ScreenNumber = parseResult.ScreenNumber;
                ClickyLog.Info("Pipeline",
                    $"Vision answered in {interaction.VisionMs}ms (pointed={interaction.Pointed}): \"{spokenText}\"");

                _conversationHistory.Append(transcript, spokenText);

                if (!string.IsNullOrWhiteSpace(spokenText))
                {
                    // Settle on the final, fully-parsed text (caret off), fly the cursor
                    // to anything it referenced, then hold the card long enough to read.
                    _overlayManager.UpdateResponse(spokenText, isStreaming: false);
                    HandlePointing(parseResult, screenCaptures);

                    await Task.Delay(ComputeReadDurationMilliseconds(spokenText), cancellationToken).ConfigureAwait(false);
                    _overlayManager.HideResponse();
                }
                else
                {
                    _overlayManager.HideResponse();
                    HandlePointing(parseResult, screenCaptures);
                }
            }
            catch (OperationCanceledException)
            {
                interaction.Error = "canceled";
                ClickyLog.Info("Pipeline", "Interaction canceled (user interrupted).");
            }
            catch (Exception exception)
            {
                interaction.Error = exception.Message;
                ClickyLog.Error("Pipeline", "Response pipeline failed", exception);
                StatusText = $"Response error: {exception.Message}";
            }
            finally
            {
                totalStopwatch.Stop();
                interaction.TotalMs = totalStopwatch.ElapsedMilliseconds;

                // Only record interactions that actually carried a transcript or failed.
                if (!string.IsNullOrWhiteSpace(interaction.Transcript) || interaction.Error is not null)
                {
                    InteractionHistory.Append(interaction);
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    _overlayManager.HideResponse();
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

        // Show the triangle (idle visual) so the flight is visible, then fly. The
        // flight bubble shows a playful phrase (matching the macOS companion) rather
        // than the raw element label, which feels more like a buddy nudging you over.
        var pointingPhrase = PointingPhrases[PointingPhraseRandom.Next(PointingPhrases.Length)];
        SetVoiceStateOnUi(CompanionVoiceState.Idle);
        _overlayManager.PointTo(target, pointingPhrase, onArrived: () => { /* returns to cursor-follow automatically */ });
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
        _overlayManager.UpdateCompanionState(_voiceState, _currentAudioPowerLevel, IsCursorEnabled);

    /// <summary>
    /// Removes a trailing (possibly partial) "[POINT:...]" tag from streaming text so
    /// the response card never flashes the machine-readable pointing tag. Handles both
    /// a complete tag and a half-streamed prefix like "[POIN" at the very end.
    /// </summary>
    private static string StripTrailingPointTag(string text)
    {
        const string marker = "[POINT";

        var markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            return text[..markerIndex].TrimEnd();
        }

        // Trim a trailing partial prefix of the marker (e.g. text ending in "[PO").
        for (var prefixLength = Math.Min(marker.Length - 1, text.Length); prefixLength > 0; prefixLength--)
        {
            var suffix = text[^prefixLength..];
            if (suffix[0] == '[' && marker.StartsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return text[..^prefixLength].TrimEnd();
            }
        }

        return text;
    }

    /// <summary>
    /// How long to keep the finished response card on screen, scaled to its length so
    /// short answers don't linger and long ones stay readable. Clamped to 2.5s–10s.
    /// </summary>
    private static int ComputeReadDurationMilliseconds(string text)
    {
        var wordCount = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        // ~3.3 words/sec reading speed, plus a fixed cushion to start and finish reading.
        var estimatedMilliseconds = 1500 + (int)(wordCount / 3.3 * 1000);
        return Math.Clamp(estimatedMilliseconds, 2500, 10000);
    }

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
        _overlayManager.ShutdownAll();
    }

    public void Dispose()
    {
        Stop();
        _hotkeyHook.Dispose();
        _microphone.Dispose();
        (_transcriptionProvider as IDisposable)?.Dispose();
        _llamaServer.Dispose();
    }
}
