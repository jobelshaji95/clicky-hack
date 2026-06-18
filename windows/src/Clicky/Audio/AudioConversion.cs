namespace Clicky.Audio;

/// <summary>
/// Audio format helpers. The microphone is captured directly as 16 kHz mono PCM16
/// (Whisper's native input rate), so conversion is just byte → float scaling plus
/// an RMS level for the waveform UI — the Windows analog of BuddyAudioConversionSupport.
/// </summary>
public static class AudioConversion
{
    public const int WhisperSampleRate = 16000;

    /// <summary>Converts little-endian PCM16 bytes into normalized float samples in [-1, 1].</summary>
    public static float[] Pcm16BytesToFloatSamples(byte[] pcm16Bytes, int validByteCount)
    {
        var sampleCount = validByteCount / 2;
        var samples = new float[sampleCount];
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var byteOffset = sampleIndex * 2;
            short sample = (short)(pcm16Bytes[byteOffset] | (pcm16Bytes[byteOffset + 1] << 8));
            samples[sampleIndex] = sample / 32768f;
        }

        return samples;
    }

    /// <summary>
    /// Computes a 0..1 audio power level from a PCM16 chunk for the waveform,
    /// using the same RMS-with-boost shape as the macOS dictation manager.
    /// </summary>
    public static float ComputeAudioPowerLevel(byte[] pcm16Bytes, int validByteCount, float previousLevel)
    {
        var sampleCount = validByteCount / 2;
        if (sampleCount == 0)
        {
            return previousLevel * 0.72f;
        }

        double summedSquares = 0;
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var byteOffset = sampleIndex * 2;
            short sample = (short)(pcm16Bytes[byteOffset] | (pcm16Bytes[byteOffset + 1] << 8));
            var normalized = sample / 32768f;
            summedSquares += normalized * (double)normalized;
        }

        var rootMeanSquare = (float)Math.Sqrt(summedSquares / sampleCount);
        var boosted = Math.Clamp(rootMeanSquare * 10.2f, 0f, 1f);

        // Smooth decay so the waveform doesn't flicker between chunks.
        return Math.Max(boosted, previousLevel * 0.72f);
    }
}
