using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Clicky.Core;

namespace Clicky.Ai;

/// <summary>
/// Launches and owns the bundled llama.cpp server (llama-server.exe) that hosts the
/// local vision model. The app starts it on launch and kills it on exit so users
/// never manage a separate service. If ManageServerProcess is false (e.g. the user
/// runs their own llama.cpp or Ollama on the same port), this is a no-op and we just
/// talk to whatever is already listening.
/// </summary>
public sealed class LlamaServerProcess : IDisposable
{
    private readonly VisionLlmConfig _config;
    private readonly HttpClient _healthClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private Process? _serverProcess;

    public LlamaServerProcess(VisionLlmConfig config)
    {
        _config = config;
    }

    public bool IsManaged => _config.ManageServerProcess;

    /// <summary>Starts the server (if managed) and waits until /health reports ready.</summary>
    public async Task StartAndWaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (_config.ManageServerProcess)
        {
            StartServerProcess();
        }

        await WaitForHealthyAsync(cancellationToken).ConfigureAwait(false);
    }

    private void StartServerProcess()
    {
        if (_serverProcess is { HasExited: false })
        {
            return;
        }

        var executablePath = AppConfig.ResolveRelativePath(_config.ServerExecutableRelativePath);
        var modelPath = AppConfig.ResolveRelativePath(_config.ModelRelativePath);
        var mmprojPath = AppConfig.ResolveRelativePath(_config.MultimodalProjectorRelativePath);

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException($"llama-server not found at '{executablePath}'.", executablePath);
        }

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"Vision model not found at '{modelPath}'. It is downloaded on first run.", modelPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            UseShellExecute = false,
            CreateNoWindow = true
            // Intentionally do NOT redirect stdout/stderr: this is a long-running
            // server and unread redirected pipes would fill and deadlock it.
        };

        // llama.cpp server arguments. --mmproj enables the vision projector.
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(modelPath);
        if (File.Exists(mmprojPath))
        {
            startInfo.ArgumentList.Add("--mmproj");
            startInfo.ArgumentList.Add(mmprojPath);
        }
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(_config.Host);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(_config.Port.ToString());
        startInfo.ArgumentList.Add("--ctx-size");
        startInfo.ArgumentList.Add(_config.ContextSize.ToString());
        startInfo.ArgumentList.Add("--n-gpu-layers");
        startInfo.ArgumentList.Add(_config.GpuLayers.ToString());

        _serverProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start llama-server process.");
    }

    private async Task WaitForHealthyAsync(CancellationToken cancellationToken)
    {
        // Loading a multi-GB vision model can take a while, so poll generously.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_serverProcess is { HasExited: true })
            {
                throw new InvalidOperationException("llama-server exited before becoming ready.");
            }

            if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("llama-server did not become healthy in time.");
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _healthClient.GetAsync(_config.HealthUrl, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
    public void Dispose()
    {
        _healthClient.Dispose();
        try
        {
            if (_serverProcess is { HasExited: false })
            {
                _serverProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort shutdown.
        }
        _serverProcess?.Dispose();
        _serverProcess = null;
    }
}
