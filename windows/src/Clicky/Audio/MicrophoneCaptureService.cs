using NAudio.Wave;

namespace Clicky.Audio;

/// <summary>
/// Push-to-talk microphone capture using NAudio's WaveInEvent. Captures directly
/// at 16 kHz mono PCM16 so the buffer can be fed straight to Whisper on release.
/// This is the Windows replacement for the macOS AVAudioEngine input tap.
/// </summary>
public sealed class MicrophoneCaptureService : IDisposable
{
    private WaveInEvent? _waveIn;
    private readonly List<byte> _capturedPcm16Bytes = new();
    private readonly object _bufferLock = new();
    private float _currentAudioPowerLevel;

    /// <summary>Raised on every captured chunk with a 0..1 level for the waveform UI.</summary>
    public event Action<float>? AudioPowerLevelChanged;

    public bool IsCapturing { get; private set; }

    public void StartCapture()
    {
        if (IsCapturing)
        {
            return;
        }

        lock (_bufferLock)
        {
            _capturedPcm16Bytes.Clear();
        }

        _currentAudioPowerLevel = 0;

        _waveIn = new WaveInEvent
        {
            // 16 kHz, 16-bit, mono — Whisper's native input format.
            WaveFormat = new WaveFormat(AudioConversion.WhisperSampleRate, 16, 1),
            BufferMilliseconds = 50
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
        IsCapturing = true;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        lock (_bufferLock)
        {
            _capturedPcm16Bytes.AddRange(new ArraySegment<byte>(args.Buffer, 0, args.BytesRecorded));
        }

        _currentAudioPowerLevel = AudioConversion.ComputeAudioPowerLevel(
            args.Buffer, args.BytesRecorded, _currentAudioPowerLevel);
        AudioPowerLevelChanged?.Invoke(_currentAudioPowerLevel);
    }

    /// <summary>
    /// Stops recording and returns the full utterance as 16 kHz mono float samples
    /// ready for Whisper. Returns an empty array if nothing meaningful was captured.
    /// </summary>
    public float[] StopCaptureAndExtractSamples()
    {
        if (!IsCapturing)
        {
            return Array.Empty<float>();
        }

        IsCapturing = false;

        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _waveIn = null;
        }

        AudioPowerLevelChanged?.Invoke(0);

        byte[] capturedBytes;
        lock (_bufferLock)
        {
            capturedBytes = _capturedPcm16Bytes.ToArray();
            _capturedPcm16Bytes.Clear();
        }

        return AudioConversion.Pcm16BytesToFloatSamples(capturedBytes, capturedBytes.Length);
    }

    public void CancelCapture()
    {
        if (!IsCapturing)
        {
            return;
        }

        _ = StopCaptureAndExtractSamples();
    }

    public void Dispose()
    {
        _waveIn?.Dispose();
        _waveIn = null;
    }
}
