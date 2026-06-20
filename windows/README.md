# Clicky for Windows (fully offline)

A native Windows port of Clicky — the menu-bar voice companion — rebuilt in
**.NET 8 / C# (WPF + Win32)** and running **completely locally**: no Cloudflare
Worker, no API keys, no internet for inference. Hold **Ctrl + Alt**, speak,
release; Clicky sees your screen, **streams its answer into an on-screen card**,
and a blue cursor flies to the thing it's talking about.

> **Audio output is disabled.** The companion *shows* its answer in a polished
> response card rather than speaking it — there is no text-to-speech playback.

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
| Response | ElevenLabs voice (cloud) | streamed **on-screen response card** (audio output removed) |
| Pointing math | `parsePointingCoordinates` + AppKit Y-flip | `PointTagParser` + `CoordinateMapper` (top-left origin) |

The system prompt and the `[POINT:x,y:label:screenN]` tag contract are ported
verbatim (`Ai/SystemPrompts.cs`, `Core/PointTagParser.cs`) so behavior matches.

## Local AI engines (offline)

| Stage | Engine | Where it lives |
|-------|--------|----------------|
| STT | whisper.cpp via Whisper.net | `models\ggml-base.en.bin` (fetched by `fetch-runtime.ps1`) |
| LLM | llama.cpp `llama-server.exe` (Vulkan GPU build) + Qwen2.5-VL GGUF | server in `tools\`, model in `models\` |

> Piper TTS files are still fetched for completeness, but audio playback is
> disabled in the app — the answer is shown, not spoken.

The app launches and owns the llama.cpp server on `127.0.0.1:8080` and shuts it
down on exit, so there's no separate service to manage. All paths/ports live in
`appsettings.json`.

## Build & run (developer)

```powershell
# From windows\
dotnet restore Clicky.sln
dotnet build  Clicky.sln -c Release

# Fetch ALL offline engines + small models into a staging dir — fully automated,
# no manual unzip steps (llama.cpp Vulkan server, Piper, Whisper + voice models).
# Add -IncludeVisionModel to also pull the ~6 GB Qwen2.5-VL GGUF now instead of
# on first run.
powershell -ExecutionPolicy Bypass -File scripts\fetch-runtime.ps1   # [-IncludeVisionModel]

# Copy the staged engines/models next to the build output, then run.
$out = "src\Clicky\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"
Copy-Item stage\tools  $out -Recurse -Force
Copy-Item stage\models $out -Recurse -Force
& "$out\Clicky.exe"
# (On first launch, if the vision model isn't present, it's downloaded with a progress UI.)
```

> Requires Windows 10/11 x64, a microphone, and (recommended) a GPU for snappy
> vision inference. The bundled llama.cpp is a **Vulkan** build, so it accelerates
> on any modern NVIDIA/AMD/Intel GPU without a CUDA toolkit. CPU works but is slower.

## Agent Mode (opt-in)

Clicky can optionally *act* — clicking and typing to carry out a spoken task — not
just point. It's **off by default** because it drives the real mouse and keyboard.
To enable it, set `"Agent": { "Enabled": true }` in `appsettings.json`, then say
**"agent, &lt;task&gt;"** (e.g. "agent, open notepad and type a reminder").

Guardrails: it's capped at `MaxSteps` actions per task, narrates each step in the
response card, logs every action, and is prompted to refuse destructive or sensitive
steps (deleting, sending, purchasing, passwords) — stopping and asking you to do those
yourself.

## Logs & history (debugging)

Everything is written under `%LOCALAPPDATA%\Clicky` (open it from the tray menu →
**Open Logs & History…**):

- `logs\clicky-<date>.log` — timestamped session log: model startup, hotkey events,
  per-stage timings, and errors.
- `history\interactions.jsonl` — one JSON line per push-to-talk turn: transcript,
  response, whether/where it pointed, and transcription/vision/total timings. Greppable
  for tuning and bug reports.

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
- **Dropped from v1**: PostHog analytics and Sparkle auto-update (the macOS app's
  cloud-tied extras). The onboarding video/music flow is also omitted. **Audio output
  (Piper TTS playback) is intentionally disabled** — responses are shown in the
  on-screen card instead.
- WPF/Win32 are Windows-only; this project does not build on macOS or Linux.
