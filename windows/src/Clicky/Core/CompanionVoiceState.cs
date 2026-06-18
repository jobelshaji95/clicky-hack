namespace Clicky.Core;

/// <summary>
/// The four discrete states of the push-to-talk pipeline, mirroring the macOS
/// CompanionVoiceState enum. The overlay renders a different visual per state:
/// triangle (idle), waveform (listening), spinner (processing), triangle (responding).
/// </summary>
public enum CompanionVoiceState
{
    Idle,
    Listening,
    Processing,
    Responding
}
