using System.IO;
using System.Text;
using Clicky.Core;
using Whisper.net;

namespace Clicky.Ai;

/// <summary>
/// Fully offline speech-to-text using whisper.cpp via Whisper.net. The whisper
/// model file (for example ggml-base.en.bin) is bundled with the app. No network,
/// no API key — this is the local replacement for AssemblyAI/OpenAI transcription.
/// </summary>
public sealed class WhisperLocalTranscriptionProvider : ITranscriptionProvider, IDisposable
{
    private readonly SttConfig _config;
    private WhisperFactory? _whisperFactory;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    public WhisperLocalTranscriptionProvider(SttConfig config)
    {
        _config = config;
    }

    public string DisplayName => "Whisper (local)";

    public bool IsReady => _whisperFactory is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_whisperFactory is not null)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_whisperFactory is not null)
            {
                return;
            }

            var modelPath = AppConfig.ResolveRelativePath(_config.WhisperModelRelativePath);
            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException(
                    $"Whisper model not found at '{modelPath}'. The installer should ship this file.",
                    modelPath);
            }

            // Loading the model off the UI thread — it can take a moment.
            _whisperFactory = await Task.Run(() => WhisperFactory.FromPath(modelPath), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<string> TranscribeAsync(float[] monoPcmSamples, CancellationToken cancellationToken = default)
    {
        if (monoPcmSamples.Length == 0)
        {
            return string.Empty;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // A fresh processor per utterance keeps state clean between push-to-talk presses.
        await using var processor = _whisperFactory!
            .CreateBuilder()
            .WithLanguage(_config.Language)
            .Build();

        var transcriptBuilder = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(monoPcmSamples, cancellationToken).ConfigureAwait(false))
        {
            transcriptBuilder.Append(segment.Text);
        }

        return transcriptBuilder.ToString().Trim();
    }

    public void Dispose()
    {
        _whisperFactory?.Dispose();
        _whisperFactory = null;
        _initializationLock.Dispose();
    }
}
