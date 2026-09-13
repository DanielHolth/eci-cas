<#
.SYNOPSIS
Downloads the speech-to-text weights the voice key needs.

.DESCRIPTION
whisper.cpp's GGML models, the same files the upstream project ships. The
shell transcribes in-process on the CPU: the microphone is the one input that
carries the room, and a key held down in a game is not a thing to send to a
vendor.

The multilingual model by default, not base.en, at the same size: this
persona is spoken to in two languages and the archive keeps Norwegian facts.
-Model small is the upgrade worth making if Norwegian dictation is the main
use -- 466MB instead of 148MB, and noticeably better at it.

Without these the voice key says it has no speech model and records nothing.
That is an announced state, not a failure, and a launch never downloads
weights.

Files land in <repo>/models/whisper/, outside bin/, so `dotnet clean` does not
delete them and every build configuration shares one copy. The shell resolves
its configured path by walking up from the binary, so nothing has to be
copied anywhere.

.EXAMPLE
./scripts/get-whisper-model.ps1

.EXAMPLE
./scripts/get-whisper-model.ps1 -Model small
#>
[CmdletBinding()]
param(
    [ValidateSet('tiny', 'tiny.en', 'base', 'base.en', 'small', 'small.en', 'medium', 'medium.en', 'large-v3-turbo')]
    [string]$Model = 'base',
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
if (-not $Destination) {
    $Destination = Join-Path $repo 'models/whisper'
}

if (-not (Test-Path $Destination)) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
}

$file = "ggml-$Model.bin"
$target = Join-Path $Destination $file

if (Test-Path $target) {
    Write-Host "already present, skipping: $target"
}
else {
    $uri = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$file"
    Write-Host "downloading $uri"

    # To .part and then into place. A half-written model is worse than none:
    # the shell would find a file, hand it to whisper.cpp, and report a parse
    # error instead of an absence.
    $partial = "$target.part"

    # Windows PowerShell draws a progress bar per byte otherwise, which costs
    # more than the transfer does.
    $progress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $uri -OutFile $partial
        Move-Item $partial $target -Force
    }
    finally {
        $ProgressPreference = $progress
        if (Test-Path $partial) { Remove-Item $partial -Force }
    }
}

$size = [Math]::Round((Get-Item $target).Length / 1MB)
Write-Host ''
Write-Host "Done: $target ($size MB)"

if ($Model -ne 'base') {
    Write-Host ''
    Write-Host 'Not the default model, so name it in the Shell section of'
    Write-Host 'src/EciCas.Host/appsettings.json:'
    Write-Host ''
    Write-Host '  "Dictation": {'
    Write-Host ("    ""ModelPath"": ""models/whisper/{0}""" -f $file)
    Write-Host '  }'
    Write-Host ''
    Write-Host 'Or leave it: one ggml file in that directory is used whatever it'
    Write-Host 'is called. Two, and the configured name decides.'
}

Write-Host ''
Write-Host 'Hold the voice key (-) and talk. An .en model cannot do Norwegian;'
Write-Host 'the multilingual ones detect the language per take.'
