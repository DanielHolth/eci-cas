<#
.SYNOPSIS
Starts everything the swarm needs, in windows of their own, and opens the UI.

.DESCRIPTION
Four moving parts and none of them depends on the others being up first:
llama-server (only for tiers that route a class at "local"), the host on
:5179, the Next.js surface on :3000, and a browser pointed at it. The host
and the surface each retry the other, so the order here is convenience, not
a protocol.

Each server gets its own window on purpose. They run until you close them,
they print the output worth reading, and Ctrl-C in one does not take the
others down with it.

What this does NOT do is install anything heavy. Missing weights, a missing
llama-server and a missing API key are reported with the one command that
fixes each -- a launcher that silently downloads 2.7GB is a launcher nobody
trusts.

Deliberately ASCII-only: powershell.exe reads a UTF-8 script as ANSI, and an
em dash arrives as mojibake in the one output someone is reading for help.

.EXAMPLE
./scripts/start.ps1
Default tier: host, surface, browser.

.EXAMPLE
./scripts/start.ps1 -Tier Minimal
Adds llama-server, because Minimal routes every class at localhost.

.EXAMPLE
./scripts/start.ps1 -Tier Mock -NoBrowser
#>
[CmdletBinding()]
param(
    [ValidateSet('Minimal', 'Budget', 'Default', 'Super', 'Mock')]
    [string]$Tier = 'Default',

    # Where the host listens, and where the surface does. Changing these
    # means changing appsettings too; they are parameters so a second
    # instance can be brought up beside a running one.
    [int]$Port = 5179,
    [int]$UiPort = 3000,
    [int]$LlmPort = 8080,

    # Leave a part out. -NoUi implies no browser: there would be nothing to
    # open.
    [switch]$NoLlm,
    [switch]$NoUi,
    [switch]$NoBrowser,

    # Report what would start and stop nothing. Useful when something is
    # already running and you want to know what this would collide with.
    [switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

# A port test that answers in milliseconds. Test-NetConnection takes seconds
# to decide a closed port is closed, and this runs up to five times.
function Test-Port {
    param([int]$Number, [int]$TimeoutMs = 300)
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        if (-not $client.ConnectAsync('127.0.0.1', $Number).Wait($TimeoutMs)) { return $false }
        return $client.Connected
    } catch {
        return $false
    } finally {
        $client.Dispose()
    }
}

function Wait-Port {
    param([int]$Number, [string]$What, [int]$Seconds = 90)
    Write-Host "waiting for $What on :$Number" -NoNewline
    for ($i = 0; $i -lt $Seconds; $i++) {
        if (Test-Port $Number) { Write-Host ' up'; return $true }
        Write-Host '.' -NoNewline
        Start-Sleep -Seconds 1
    }
    Write-Host ' gave up'
    return $false
}

# One window per server. pwsh when it is here, powershell when it is not;
# -NoExit so a crash leaves its own stack trace on screen instead of a
# window that blinks and is gone.
$shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

function Start-Window {
    param([string]$Title, [string]$WorkingDirectory, [string]$Command)
    if ($WhatIfOnly) {
        Write-Host "would start [$Title]: $Command"
        return
    }
    $line = "`$host.UI.RawUI.WindowTitle = '$Title'; Set-Location '$WorkingDirectory'; $Command"
    Start-Process -FilePath $shell -ArgumentList '-NoExit', '-Command', $line | Out-Null
    Write-Host "started [$Title]"
}

Write-Host "eci-cas: tier $Tier, repo $repo"

# ---- what this tier actually needs -------------------------------------
#
# The tier file is the authority, not a list kept here: a class routed at
# "local" is a class that will hang on a dead :8080, and that is the only
# thing that decides whether llama-server is part of this.
$tierFile = Join-Path $repo "src/EciCas.Host/appsettings.$Tier.json"
$tierText = if (Test-Path $tierFile) { Get-Content $tierFile -Raw } else { '' }
$needsLlm = $tierText -match '"Provider"\s*:\s*"local"'
$needsMistral = $tierText -match '"Provider"\s*:\s*"mistral"'
$needsOpenAi = $tierText -match '"Provider"\s*:\s*"openai"'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'dotnet is not on PATH. Install the .NET 10 SDK, then re-run this.'
    exit 1
}

# Keys are warnings rather than errors. A missing key degrades one class,
# the host says so per call, and the rest of the swarm still runs -- which
# is more useful than refusing to start.
if ($needsMistral -and -not $env:MISTRAL_API_KEY) {
    Write-Host 'note: MISTRAL_API_KEY is not set; fast-* classes on this tier will fail per call.'
}
if ($needsOpenAi -and -not $env:OPENAI_API_KEY) {
    Write-Host 'note: OPENAI_API_KEY is not set; slow-* classes and API embeddings will fail per call.'
}

# The embedder is in-process ONNX, so there is no server to start -- only
# weights that may not be there. Without them the swarm runs unembedded,
# which is a supported state, not a fault.
#
# Every tier, not every tier but Minimal: the exemption dated from when
# Minimal had no embedder of its own, and it outlived that by long enough to
# hide a real outage.
#
# Both files, not just the model: a tokenizer that did not come down with the
# weights fails the host's own check and produces exactly the same silence.
$weights = @('models/embedding/multilingual-e5-small/model.onnx', 'models/embedding/multilingual-e5-small/sentencepiece.bpe.model')
$missing = @($weights | Where-Object { -not (Test-Path (Join-Path $repo $_)) })
if ($missing.Count -gt 0) {
    Write-Host "note: no local embedding weights ($($missing -join ', '))."
    Write-Host '      Vectors are off: no pair sweep, no row narrowing, no woken notes.'
    Write-Host '      ./scripts/get-embedding-model.ps1'
}

# ---- llama-server ------------------------------------------------------
if ($needsLlm -and -not $NoLlm) {
    if (Test-Port $LlmPort) {
        Write-Host "llama-server already answering on :$LlmPort; leaving it alone"
    } elseif ($WhatIfOnly) {
        Write-Host "would start llama-server on :$LlmPort"
    } else {
        # That script owns the launch line: -ngl 99 and the slot count are
        # not details this launcher should be holding a second copy of.
        & (Join-Path $PSScriptRoot 'get-local-model.ps1') -Start -Port $LlmPort
    }
} elseif ($needsLlm) {
    Write-Host "skipping llama-server (-NoLlm); local classes will fail until :$LlmPort answers"
}

# ---- host --------------------------------------------------------------
if (Test-Port $Port) {
    # Only one process may hold the archive directory, so this is a stop,
    # not a warning: a second host would fail on the parquet files anyway.
    Write-Host "something is already listening on :$Port. Close that host first."
    exit 1
}

Start-Window -Title "eci-cas host ($Tier)" -WorkingDirectory $repo `
    -Command "dotnet run --project src/EciCas.Host -- --Tier=$Tier"

# ---- surface -----------------------------------------------------------
if (-not $NoUi) {
    $ui = Join-Path $repo 'morrow-eci'

    if (Test-Port $UiPort) {
        # Still opened below: a surface left running is the one to point at.
        Write-Host "surface already answering on :$UiPort; leaving it alone"
    } elseif (-not (Test-Path (Join-Path $ui 'node_modules'))) {
        Write-Host 'morrow-eci has no node_modules. Run this once, then start again:'
        Write-Host '  cd morrow-eci; npm install'
        $NoUi = $true
    } else {
        # dev.cmd rather than npm directly: it puts nodejs on PATH first,
        # which is the difference between this working and not on a machine
        # where node was installed for one shell.
        Start-Window -Title 'morrow-eci' -WorkingDirectory $ui -Command './dev.cmd'
    }
}

if ($WhatIfOnly) { return }

# ---- wait, then open ---------------------------------------------------
#
# The host first: it is the slow one on a cold build, and a browser opened
# before it answers shows an empty transcript that only a reload fixes.
Wait-Port -Number $Port -What 'host' | Out-Null
if (-not $NoUi) { Wait-Port -Number $UiPort -What 'surface' | Out-Null }

if (-not $NoBrowser -and -not $NoUi) {
    Start-Process "http://localhost:$UiPort"
}

Write-Host ''
Write-Host 'Up. Close each window to stop that part; nothing here holds the others.'
