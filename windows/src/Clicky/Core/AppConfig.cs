using System.IO;
using Microsoft.Extensions.Configuration;

namespace Clicky.Core;

/// <summary>
/// Strongly-typed view over appsettings.json. All file paths are stored
/// relative to the application directory and resolved to absolute paths here,
/// so the app works whether launched from the install folder or a dev build.
/// </summary>
public sealed class AppConfig
{
    public HotkeyConfig Hotkey { get; set; } = new();
    public VisionLlmConfig VisionLlm { get; set; } = new();
    public SttConfig Stt { get; set; } = new();
    public TtsConfig Tts { get; set; } = new();
    public ModelDownloadConfig ModelDownloads { get; set; } = new();
    public OverlayConfig Overlay { get; set; } = new();

    /// <summary>Directory the executable lives in — the anchor for every relative path.</summary>
    public static string ApplicationDirectory =>
        AppContext.BaseDirectory;

    public static AppConfig Load()
    {
        var configurationRoot = new ConfigurationBuilder()
            .SetBasePath(ApplicationDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        var appConfig = new AppConfig();
        configurationRoot.Bind(appConfig);
        return appConfig;
    }

    /// <summary>Turns a config-relative path into an absolute path under the app directory.</summary>
    public static string ResolveRelativePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(ApplicationDirectory, relativePath));
}

public sealed class HotkeyConfig
{
    public bool RequireControl { get; set; } = true;
    public bool RequireAlt { get; set; } = true;
    public bool RequireShift { get; set; }
    public bool RequireWindows { get; set; }
}

public sealed class VisionLlmConfig
{
    public bool ManageServerProcess { get; set; } = true;
    public string ServerExecutableRelativePath { get; set; } = "tools\\llama-server.exe";
    public string ModelRelativePath { get; set; } = "";
    public string MultimodalProjectorRelativePath { get; set; } = "";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8080;
    public string ModelName { get; set; } = "qwen2.5-vl";
    public int MaxTokens { get; set; } = 1024;
    public int ContextSize { get; set; } = 8192;
    public int GpuLayers { get; set; } = 999;

    public string ChatCompletionsUrl => $"http://{Host}:{Port}/v1/chat/completions";
    public string HealthUrl => $"http://{Host}:{Port}/health";
}

public sealed class SttConfig
{
    public string WhisperModelRelativePath { get; set; } = "models\\ggml-base.en.bin";
    public string Language { get; set; } = "en";
}

public sealed class TtsConfig
{
    public string PiperExecutableRelativePath { get; set; } = "tools\\piper\\piper.exe";
    public string PiperVoiceModelRelativePath { get; set; } = "models\\en_US-amy-medium.onnx";
}

public sealed class ModelDownloadConfig
{
    public string VisionModelUrl { get; set; } = "";
    public string VisionMmprojUrl { get; set; } = "";
}

public sealed class OverlayConfig
{
    public bool ShowCursorByDefault { get; set; } = true;
}
