<#
.SYNOPSIS
Downloads the sentence-transformer weights the passage corpus needs.

.DESCRIPTION
Facts, passages and Hindsight's wake all read through one embedder:
multilingual-e5-small, because it is the one that finds a Norwegian fact from
an English question (tools/retrieval-bench/lang_v4.py). ONNX export plus its
SentencePiece model, ~470MB, deliberately not committed: git would carry them forever and diff them badly.

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
    [string]$Model = 'intfloat/multilingual-e5-small',
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

if (-not $Destination) {
    $repo = Split-Path -Parent $PSScriptRoot
    $Destination = Join-Path $repo ('models/embedding/' + ($Model -split '/')[-1])
}

if (-not (Test-Path $Destination)) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
}

$base = "https://huggingface.co/$Model/resolve/main"
$files = @{ 'onnx/model.onnx' = 'model.onnx'; 'sentencepiece.bpe.model' = 'sentencepiece.bpe.model' }

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
$vocabPath = (Resolve-Path (Join-Path $Destination 'sentencepiece.bpe.model')).Path

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
