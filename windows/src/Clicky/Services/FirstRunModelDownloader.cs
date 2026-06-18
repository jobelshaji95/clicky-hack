using System.IO;
using System.Net.Http;
using Clicky.Core;

namespace Clicky.Services;

/// <summary>Progress for a model download: 0..1 fraction plus a human label.</summary>
public readonly record struct ModelDownloadProgress(string FileName, double Fraction, long BytesReceived, long? TotalBytes);

/// <summary>
/// Downloads the large vision model (and its multimodal projector) on first run so
/// the installer stays small. After this completes once, everything the app needs
/// is on disk and it runs fully offline. The small models (Whisper, Piper) ship in
/// the installer and are not handled here.
/// </summary>
public sealed class FirstRunModelDownloader
{
    private readonly VisionLlmConfig _visionConfig;
    private readonly ModelDownloadConfig _downloadConfig;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromHours(2) };

    public FirstRunModelDownloader(VisionLlmConfig visionConfig, ModelDownloadConfig downloadConfig)
    {
        _visionConfig = visionConfig;
        _downloadConfig = downloadConfig;
    }

    /// <summary>True when both required model files already exist on disk.</summary>
    public bool AreModelsPresent()
    {
        var modelPath = AppConfig.ResolveRelativePath(_visionConfig.ModelRelativePath);
        var mmprojPath = AppConfig.ResolveRelativePath(_visionConfig.MultimodalProjectorRelativePath);
        return File.Exists(modelPath) && File.Exists(mmprojPath);
    }

    public async Task EnsureModelsDownloadedAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var modelPath = AppConfig.ResolveRelativePath(_visionConfig.ModelRelativePath);
        var mmprojPath = AppConfig.ResolveRelativePath(_visionConfig.MultimodalProjectorRelativePath);

        if (!File.Exists(modelPath))
        {
            await DownloadFileAsync(_downloadConfig.VisionModelUrl, modelPath, progress, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(mmprojPath))
        {
            await DownloadFileAsync(_downloadConfig.VisionMmprojUrl, mmprojPath, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException($"No download URL configured for '{Path.GetFileName(destinationPath)}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        // Download to a temp file and move into place so a half-finished download
        // is never mistaken for a complete model.
        var temporaryPath = destinationPath + ".part";
        var fileName = Path.GetFileName(destinationPath);

        using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;

            await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destinationStream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[1024 * 1024];
            long bytesReceived = 0;
            int read;
            while ((read = await sourceStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesReceived += read;

                var fraction = totalBytes.HasValue && totalBytes.Value > 0
                    ? bytesReceived / (double)totalBytes.Value
                    : 0;
                progress?.Report(new ModelDownloadProgress(fileName, fraction, bytesReceived, totalBytes));
            }
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }
}
