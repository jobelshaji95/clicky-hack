<#
.SYNOPSIS
    Downloads ALL offline runtime engines and small models Clicky needs, fully
    automatically — no manual steps. Populates a staging directory mirroring the
    layout the app expects:
        <stage>\tools\llama-server.exe (+ dlls)        (llama.cpp, Vulkan GPU build)
        <stage>\tools\piper\piper.exe (+ espeak-ng-data, dlls)
        <stage>\models\ggml-base.en.bin                (Whisper STT)
        <stage>\models\en_US-amy-medium.onnx (+ .json) (Piper voice)

    The large vision GGUF is intentionally NOT fetched here — it is downloaded on
    first run by the app so the installer stays small. (Pass -IncludeVisionModel
    to also pull it here for a fully air-gapped setup.)

.NOTES
    URLs/versions are pinned to releases validated for this port. The llama.cpp
    Vulkan build runs on any modern GPU (NVIDIA/AMD/Intel) via the driver's Vulkan
    ICD — no CUDA toolkit required.
#>

param(
    [string]$StageDir = "$PSScriptRoot\..\stage",
    [switch]$IncludeVisionModel
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # Invoke-WebRequest is far faster without the progress bar.

# Pinned release versions.
$LlamaTag  = "b9707"
$LlamaUrl  = "https://github.com/ggml-org/llama.cpp/releases/download/$LlamaTag/llama-$LlamaTag-bin-win-vulkan-x64.zip"
$PiperUrl  = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip"

$WhisperModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin"
$PiperVoiceUrl   = "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium/en_US-amy-medium.onnx"
$PiperVoiceCfg   = "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium/en_US-amy-medium.onnx.json"

$VisionModelUrl  = "https://huggingface.co/ggml-org/Qwen2.5-VL-7B-Instruct-GGUF/resolve/main/Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf"
$VisionMmprojUrl = "https://huggingface.co/ggml-org/Qwen2.5-VL-7B-Instruct-GGUF/resolve/main/mmproj-Qwen2.5-VL-7B-Instruct-f16.gguf"

$toolsDir  = Join-Path $StageDir "tools"
$piperDir  = Join-Path $toolsDir "piper"
$modelsDir = Join-Path $StageDir "models"
$tempDir   = Join-Path $StageDir "_tmp"

New-Item -ItemType Directory -Force -Path $toolsDir, $modelsDir, $tempDir | Out-Null

function Get-File($url, $destination) {
    if (Test-Path $destination) {
        Write-Host "Already present: $destination"
        return
    }
    Write-Host "Downloading $url"
    $tmp = "$destination.part"
    Invoke-WebRequest -Uri $url -OutFile $tmp
    Move-Item -Force $tmp $destination
}

# ── 1) llama.cpp server (Vulkan GPU build) → tools\ ──────────────────────────
if (-not (Test-Path (Join-Path $toolsDir "llama-server.exe"))) {
    $llamaZip = Join-Path $tempDir "llama.zip"
    Get-File $LlamaUrl $llamaZip
    $llamaExtract = Join-Path $tempDir "llama"
    if (Test-Path $llamaExtract) { Remove-Item -Recurse -Force $llamaExtract }
    Expand-Archive -Path $llamaZip -DestinationPath $llamaExtract -Force

    # Release zips put the binaries either at the root or under build\bin — find them.
    $serverExe = Get-ChildItem -Path $llamaExtract -Recurse -Filter "llama-server.exe" | Select-Object -First 1
    if (-not $serverExe) { throw "llama-server.exe not found in $LlamaUrl" }
    $binDir = $serverExe.Directory.FullName
    Get-ChildItem -Path $binDir -File | Where-Object { $_.Extension -in ".exe", ".dll" } |
        ForEach-Object { Copy-Item $_.FullName -Destination $toolsDir -Force }
    Write-Host "llama.cpp server staged into $toolsDir"
} else {
    Write-Host "Already present: llama-server.exe"
}

# ── 2) Piper TTS → tools\piper\ ──────────────────────────────────────────────
if (-not (Test-Path (Join-Path $piperDir "piper.exe"))) {
    $piperZip = Join-Path $tempDir "piper.zip"
    Get-File $PiperUrl $piperZip
    $piperExtract = Join-Path $tempDir "piper_x"
    if (Test-Path $piperExtract) { Remove-Item -Recurse -Force $piperExtract }
    Expand-Archive -Path $piperZip -DestinationPath $piperExtract -Force

    # The zip contains a top-level "piper" folder; copy its contents into tools\piper.
    $piperExe = Get-ChildItem -Path $piperExtract -Recurse -Filter "piper.exe" | Select-Object -First 1
    if (-not $piperExe) { throw "piper.exe not found in $PiperUrl" }
    New-Item -ItemType Directory -Force -Path $piperDir | Out-Null
    Copy-Item -Path (Join-Path $piperExe.Directory.FullName "*") -Destination $piperDir -Recurse -Force
    Write-Host "Piper staged into $piperDir"
} else {
    Write-Host "Already present: piper.exe"
}

# ── 3) Whisper STT model (small) → models\ ───────────────────────────────────
Get-File $WhisperModelUrl (Join-Path $modelsDir "ggml-base.en.bin")

# ── 4) Piper voice model + config (small) → models\ ──────────────────────────
Get-File $PiperVoiceUrl (Join-Path $modelsDir "en_US-amy-medium.onnx")
Get-File $PiperVoiceCfg (Join-Path $modelsDir "en_US-amy-medium.onnx.json")

# ── 5) (optional) Large vision GGUF — only with -IncludeVisionModel ──────────
if ($IncludeVisionModel) {
    Write-Host "Fetching large vision model (this is several GB)..."
    Get-File $VisionModelUrl  (Join-Path $modelsDir "qwen2.5-vl-7b-instruct-q4_k_m.gguf")
    Get-File $VisionMmprojUrl (Join-Path $modelsDir "qwen2.5-vl-7b-instruct-mmproj-f16.gguf")
}

# Clean up temp archives/extractions.
if (Test-Path $tempDir) { Remove-Item -Recurse -Force $tempDir }

Write-Host ""
Write-Host "Runtime staging complete at: $StageDir"
Write-Host "  tools\llama-server.exe : $(Test-Path (Join-Path $toolsDir 'llama-server.exe'))"
Write-Host "  tools\piper\piper.exe  : $(Test-Path (Join-Path $piperDir 'piper.exe'))"
Write-Host "  models\ggml-base.en.bin: $(Test-Path (Join-Path $modelsDir 'ggml-base.en.bin'))"
Write-Host "  models\en_US-amy-medium.onnx: $(Test-Path (Join-Path $modelsDir 'en_US-amy-medium.onnx'))"
