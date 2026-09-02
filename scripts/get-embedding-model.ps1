<#
.SYNOPSIS
Downloads the sentence-transformer weights the passage corpus needs.

.DESCRIPTION
The passage corpus — what Reflection writes and Hindsight wakes — needs a
BERT-family ONNX export and its vocab.txt. They are ~90MB and deliberately
not committed: git would carry them forever and diff them badly.

Without them the swarm runs exactly as it did before vectors existed. That
is a normal, announced state, not a failure, so nothing here is required to
boot — this only exists to make Hindsight actually able to wake something.

Files land in <repo>/models/embedding/, outside bin/, so `dotnet clean`
does not delete them and every build configuration shares one copy. The
script then prints the absolute paths to put in appsettings.json, because
Embedding:ModelPath resolves relative paths against the build output.

.EXAMPLE
./scripts/get-embedding-model.ps1
#>
[CmdletBinding()]
param(
    [string]$Model = 'sentence-transformers/all-MiniLM-L6-v2',
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

if (-not $Destination) {
    $repo = Split-Path -Parent $PSScriptRoot
    $Destination = Join-Path $repo 'models/embedding'
}

if (-not (Test-Path $Destination)) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
}

$base = "https://huggingface.co/$Model/resolve/main"
$files = @{ 'onnx/model.onnx' = 'model.onnx'; 'vocab.txt' = 'vocab.txt' }

foreach ($remote in $files.Keys) {
    $target = Join-Path $Destination $files[$remote]
    if (Test-Path $target) {
        Write-Host "already present, skipping: $target"
        continue
    }

    Write-Host "downloading $base/$remote"
    Invoke-WebRequest -Uri "$base/$remote" -OutFile $target
}

$modelPath = (Resolve-Path (Join-Path $Destination 'model.onnx')).Path
$vocabPath = (Resolve-Path (Join-Path $Destination 'vocab.txt')).Path

Write-Host ''
Write-Host 'Done. Point the host at them in src/EciCas.Host/appsettings.json:'
Write-Host ''
Write-Host '  "Embedding": {'
Write-Host '    "Provider": "onnx",'
Write-Host ("    ""ModelPath"": ""{0}""," -f $modelPath.Replace('\', '\'))
Write-Host ("    ""VocabPath"": ""{0}""" -f $vocabPath.Replace('\', '\'))
Write-Host '  }'
Write-Host ''
Write-Host 'Changing model later is a startup error, not a silent swap: the corpus'
Write-Host 'stamps which model wrote it and the host refuses to search it with another.'
