<#
.SYNOPSIS
    Downloads the offline runtime engines and small models that ship inside the
    Clicky installer: the llama.cpp server (vision LLM host), Piper (TTS), and the
    Whisper STT model. The large vision GGUF is intentionally NOT fetched here —
    it is downloaded on first run so the installer stays small.

.DESCRIPTION
    Populates a staging directory mirroring the layout the app expects:
        <stage>\tools\llama-server.exe (+ dlls)
        <stage>\tools\piper\piper.exe (+ files)
        <stage>\models\ggml-base.en.bin
        <stage>\models\en_US-amy-medium.onnx (+ .json)

    Point the Inno Setup script's source paths at this staging directory.

.NOTES
    Pin the URLs/versions below to the releases you have validated. They are left
    as the canonical release pages because exact asset URLs change per version.
#>

param(
    [string]$StageDir = "$PSScriptRoot\..\stage"
)

$ErrorActionPreference = "Stop"

$toolsDir  = Join-Path $StageDir "tools"
$piperDir  = Join-Path $toolsDir "piper"
$modelsDir = Join-Path $StageDir "models"

New-Item -ItemType Directory -Force -Path $toolsDir, $piperDir, $modelsDir | Out-Null

function Get-File($url, $destination) {
    if (Test-Path $destination) {
        Write-Host "Already present: $destination"
        return
    }
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $destination
}

# 1) Whisper STT model (small, ships in the installer).
Get-File `
    "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin" `
    (Join-Path $modelsDir "ggml-base.en.bin")

# 2) Piper voice model + config (small, ships in the installer).
Get-File `
    "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium/en_US-amy-medium.onnx" `
    (Join-Path $modelsDir "en_US-amy-medium.onnx")
Get-File `
    "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium/en_US-amy-medium.onnx.json" `
    (Join-Path $modelsDir "en_US-amy-medium.onnx.json")

Write-Host ""
Write-Host "MANUAL STEPS (binary release archives — unzip into the staging dirs):"
Write-Host "  * llama.cpp Windows release (llama-server.exe + *.dll, CUDA or CPU build)"
Write-Host "      https://github.com/ggml-org/llama.cpp/releases  ->  $toolsDir"
Write-Host "  * Piper Windows release (piper.exe + espeak-ng-data, *.dll)"
Write-Host "      https://github.com/rhasspy/piper/releases  ->  $piperDir"
Write-Host ""
Write-Host "Staging directory ready at: $StageDir"
