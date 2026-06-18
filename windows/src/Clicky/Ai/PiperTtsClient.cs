using System.Diagnostics;
using System.IO;
using Clicky.Core;
using NAudio.Wave;

namespace Clicky.Ai;

/// <summary>
/// Fully offline text-to-speech using Piper (piper.exe + an ONNX voice model).
/// Piper reads text on stdin and writes a WAV stream on stdout; we play it with
/// NAudio. This is the local replacement for ElevenLabsTTSClient — same surface
/// (SpeakTextAsync, StopPlayback, IsPlaying) so the pipeline is unchanged.
/// </summary>
public sealed class PiperTtsClient : IDisposable
{
    private readonly TtsConfig _config;
    private WaveOutEvent? _waveOut;
    private WaveFileReader? _waveReader;
    private readonly object _playbackLock = new();

    public PiperTtsClient(TtsConfig config)
    {
        _config = config;
    }

    public bool IsPlaying
    {
        get
        {
            lock (_playbackLock)
            {
                return _waveOut?.PlaybackState == PlaybackState.Playing;
            }
        }
    }

    /// <summary>
    /// Synthesizes <paramref name="text"/> with Piper and starts playback. Returns
    /// once audio is playing (so the caller can flip to the "responding" state),
    /// mirroring the macOS speakText contract.
    /// </summary>
    public async Task SpeakTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var wavBytes = await SynthesizeWavAsync(text, cancellationToken).ConfigureAwait(false);
        StartPlayback(wavBytes);
    }

    private async Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancellationToken)
    {
        var piperPath = AppConfig.ResolveRelativePath(_config.PiperExecutableRelativePath);
        var voicePath = AppConfig.ResolveRelativePath(_config.PiperVoiceModelRelativePath);

        if (!File.Exists(piperPath))
        {
            throw new FileNotFoundException($"Piper not found at '{piperPath}'.", piperPath);
        }
        if (!File.Exists(voicePath))
        {
            throw new FileNotFoundException($"Piper voice model not found at '{voicePath}'.", voicePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = piperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
            // stderr is left attached (not redirected) so its progress logging
            // can't fill an unread pipe and stall synthesis.
        };
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(voicePath);
        // "--output_file -" streams a WAV to stdout instead of writing a file.
        startInfo.ArgumentList.Add("--output_file");
        startInfo.ArgumentList.Add("-");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Piper.");

        // Feed the text, then close stdin so Piper synthesizes and exits.
        await process.StandardInput.WriteAsync(text).ConfigureAwait(false);
        process.StandardInput.Close();

        using var wavBuffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(wavBuffer, cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return wavBuffer.ToArray();
    }

    private void StartPlayback(byte[] wavBytes)
    {
        if (wavBytes.Length == 0)
        {
            return;
        }

        lock (_playbackLock)
        {
            DisposePlaybackResources();

            _waveReader = new WaveFileReader(new MemoryStream(wavBytes));
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_waveReader);
            _waveOut.Play();
        }
    }

    public void StopPlayback()
    {
        lock (_playbackLock)
        {
            _waveOut?.Stop();
            DisposePlaybackResources();
        }
    }

    private void DisposePlaybackResources()
    {
        _waveOut?.Dispose();
        _waveOut = null;
        _waveReader?.Dispose();
        _waveReader = null;
    }

    public void Dispose()
    {
        lock (_playbackLock)
        {
            DisposePlaybackResources();
        }
    }
}
