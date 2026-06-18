# Clicky for Windows (fully offline)

A native Windows port of Clicky — the menu-bar voice companion — rebuilt in
**.NET 8 / C# (WPF + Win32)** and running **completely locally**: no Cloudflare
Worker, no API keys, no internet for inference. Hold **Ctrl + Alt**, speak,
release; Clicky sees your screen, answers out loud, and a blue cursor flies to
the thing it's talking about.

## How it maps to the macOS app

| Concern | macOS (Swift/AppKit) | Windows (this port) |
|---------|----------------------|---------------------|
| Tray icon + panel | NSStatusItem + NSPanel | `TrayIconManager` (Hardcodet.NotifyIcon.Wpf) + `PanelWindow` |
| Cursor overlay | `OverlayWindow` (NSPanel per screen) | `CursorOverlayWindow` per monitor (layered, click-through, topmost) |
| Screen capture | ScreenCaptureKit | `ScreenCaptureService` (GDI `CopyFromScreen`, multi-monitor) |
| Push-to-talk | CGEvent tap (ctrl+option) | `GlobalHotkeyHook` (`WH_KEYBOARD_LL`, Ctrl+Alt) |
| Mic capture | AVAudioEngine | `MicrophoneCaptureService` (NAudio, 16 kHz mono PCM16) |
| Speech-to-text | AssemblyAI (cloud) | `WhisperLocalTranscriptionProvider` (whisper.cpp, **local**) |
| Vision LLM | Claude via proxy (cloud) | `LocalVisionLlmClient` → llama.cpp server + Qwen2.5-VL (**local**) |
| Text-to-speech | ElevenLabs (cloud) | `PiperTtsClient` (Piper, **local**) |
| Pointing math | `parsePointingCoordinates` + AppKit Y-flip | `PointTagParser` + `CoordinateMapper` (top-left origin) |

The system prompt and the `[POINT:x,y:label:screenN]` tag contract are ported
verbatim (`Ai/SystemPrompts.cs`, `Core/PointTagParser.cs`) so behavior matches.

## Local AI engines (offline)

| Stage | Engine | Where it lives |
|-------|--------|----------------|
| STT | whisper.cpp via Whisper.net | `models\ggml-base.en.bin` (ships in installer) |
| LLM | llama.cpp `llama-server.exe` + Qwen2.5-VL GGUF | server in `tools\`, model in `models\` (downloaded on first run) |
| TTS | Piper + ONNX voice | `tools\piper\piper.exe`, `models\en_US-amy-medium.onnx` (ships in installer) |

The app launches and owns the llama.cpp server on `127.0.0.1:8080` and shuts it
down on exit, so there's no separate service to manage. All paths/ports live in
`appsettings.json`.

## Build & run (developer)

```powershell
# From windows\
dotnet restore Clicky.sln
dotnet build  Clicky.sln -c Debug

# Fetch the offline engines + small models into a staging dir, then copy
# stage\tools and stage\models next to the build output (bin\...\net8.0-windows...\).
powershell -ExecutionPolicy Bypass -File scripts\fetch-runtime.ps1

# Run. On first launch it downloads the vision model (~4-5 GB) with a progress UI.
dotnet run --project src\Clicky\Clicky.csproj
```

> Requires Windows 10/11 x64, a microphone, and (recommended) a GPU for snappy
> vision inference. CPU works but is slower.

## Build the installer

```powershell
# 1) Self-contained publish (no .NET prerequisite on the target machine)
dotnet publish src\Clicky\Clicky.csproj -c Release -r win-x64 --self-contained -o publish

# 2) Stage the offline engines + small models, then copy into publish\
powershell -ExecutionPolicy Bypass -File scripts\fetch-runtime.ps1
Copy-Item stage\tools  publish\ -Recurse -Force
Copy-Item stage\models publish\ -Recurse -Force

# 3) Compile the installer (Inno Setup must be installed)
iscc installer\Clicky.iss
# -> windows\installer\output\ClickySetup.exe
```

`ClickySetup.exe` is the single deliverable: it installs the app + offline
engines, adds a Start-menu shortcut, optionally runs at sign-in, and the only
online step is the one-time vision-model download on first launch.

### Want a 100% air-gapped installer?

Pre-download the vision GGUF + mmproj into `publish\models\` before step 3 and
set `"ManageServerProcess": true` (default). The installer balloons to ~5 GB but
needs no internet ever. Otherwise the small installer + first-run download is the
recommended balance.

## Notes & known trade-offs

- **Pointing accuracy** depends on the local vision model's visual grounding.
  Qwen2.5-VL is the strongest open option; expect it to be less pinpoint than
  cloud Claude. Swap models via `appsettings.json` → `VisionLlm`.
- **Dropped from v1**: PostHog analytics and Sparkle auto-update (the macoS app's
  cloud-tied extras). The onboarding video/music flow is also omitted.
- WPF/Win32 are Windows-only; this project does not build on macOS or Linux.
