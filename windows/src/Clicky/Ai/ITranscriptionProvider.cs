namespace Clicky.Ai;

/// <summary>
/// Abstraction over speech-to-text backends, mirroring the macOS
/// BuddyTranscriptionProvider protocol. Push-to-talk on Windows buffers the whole
/// clip and transcribes on release (like the macOS OpenAI/upload provider), so the
/// interface is intentionally simple. Keeping the abstraction lets a cloud or
/// streaming provider be swapped in later without touching the pipeline.
/// </summary>
public interface ITranscriptionProvider
{
    string DisplayName { get; }

    /// <summary>True once the backing model is loaded and ready to transcribe.</summary>
    bool IsReady { get; }

    /// <summary>Loads the model into memory. Safe to call more than once.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Transcribes a complete utterance.
    /// </summary>
    /// <param name="monoPcmSamples">16 kHz mono float samples in the range [-1, 1].</param>
    Task<string> TranscribeAsync(float[] monoPcmSamples, CancellationToken cancellationToken = default);
}
