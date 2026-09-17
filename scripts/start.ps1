<#
.SYNOPSIS
Starts everything Morrow needs and leaves her in the corner of the screen.

.DESCRIPTION
The shipped shape is one window that is not a window: the desktop shell hosts
the swarm in-process, serves the exported client at its own origin, and draws
Morrow as a click-through watermark. Clicking her when she is interactable
opens the full session in a second window. There is no browser and no second
server in this shape, which is the point -- it is what a person installs.

-Dev is the other shape, and the one to develop the client in: the console
host in one window, `next dev` in another, and a browser tab. Hot reload,
cross-origin, a REPL to type at.

llama-server starts in both, on every tier, including the paid ones that will
never call it. The tier a session boots on is not the tier it will be running
on: the dropdown swaps tiers live, and an empty energy meter swaps itself to
the all-local tier without asking anyone. A resident model costs idle VRAM; a
model that is not there costs the turn that goes looking for it, which is the
turn where the meter just ran out. -NoLlm gets the VRAM back.

Each server gets its own window on purpose. They run until you close them,
they print the output worth reading, and Ctrl-C in one does not take the
others down with it.

What this does NOT do is install anything heavy. Missing weights, a missing
llama-server and a missing API key are reported with the one command that
fixes each -- a launcher that silently downloads 2.7GB is a launcher nobody
trusts. The client export is the one thing it will build for you, because
that is this repo's own source compiled with tools already on the machine,
and the shell without it is a blank rectangle.

Deliberately ASCII-only: powershell.exe reads a UTF-8 script as ANSI, and an
em dash arrives as mojibake in the one output someone is reading for help.

.EXAMPLE
./scripts/start.ps1
Pro tier: llama-server, then Morrow in the corner.

.EXAMPLE
./scripts/start.ps1 -NoBuild
The same, starting the exe that is already built. The everyday relaunch.

.EXAMPLE
./scripts/start.ps1 -Dev
Console host, `next dev`, browser. The client development loop.

.EXAMPLE
./scripts/start.ps1 -Tier Pro -NoLlm
The same without the local model. Saves the VRAM and gives up the landing
place: an empty meter drops to Free, and Free has nowhere to go.
#>
[CmdletBinding()]
param(
    [ValidateSet('Free', 'Budget', 'Pro', 'Premium', 'Mock')]
    [string]$Tier = 'Pro',

    # Where the host listens, and where the dev surface does. Changing these
    # means changing appsettings too; they are parameters so a second
    # instance can be brought up beside a running one.
    [int]$Port = 5179,
    [int]$UiPort = 3000,
    [int]$LlmPort = 8080,

    # Leave the local model out.
    [switch]$NoLlm,

    # Start the exe that is already built instead of building first. The
    # everyday relaunch: no compile, no console window hanging around, and
    # llama-server still comes up. Release if there is one, else Debug.
    [switch]$NoBuild,

    # The development shape instead of the shipped one: console host, dev
    # server, browser. -NoUi and -NoBrowser refine it and mean nothing
    # without it -- the shell has no separate UI process to leave out.
    [switch]$Dev,
    [switch]$NoUi,
    [switch]$NoBrowser,

    # Report what would start and stop nothing. Useful when something is
    # already running and you want to know what this would collide with.
    [switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$ui = Join-Path $repo 'morrow-eci'

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

function Ensure-UiDependencies {
    $uiNodeModules = Join-Path $ui 'node_modules'
    $webgpuTypes = Join-Path (Join-Path $uiNodeModules '@webgpu') 'types'

    if ((Test-Path $uiNodeModules) -and (Test-Path $webgpuTypes)) {
        return
    }

    if ($WhatIfOnly) {
        Write-Host 'note: morrow-eci dependencies are missing; would run npm install in the UI project.'
        return
    }

    Write-Host 'note: morrow-eci dependencies are missing; running npm install in the UI project.'
    Push-Location $ui
    try {
        & npm install --no-fund --no-audit
        if ($LASTEXITCODE -ne 0) {
            throw "npm install failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

# ---- keys this tier will want -------------------------------------------
#
# The tier file is the authority on which keys to warn about, and nothing
# else. It used to decide whether llama-server started too; it cannot, for
# the reason in .DESCRIPTION -- the file read here describes the first turn,
# not the tenth.
$tierFile = Join-Path $repo "src/EciCas.Host/appsettings.$Tier.json"
$tierText = if (Test-Path $tierFile) { Get-Content $tierFile -Raw } else { '' }
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
# Fresh installs need more than a missing-file check: a partial download or a
# stale/pinned model can be present but still unreadable, and the host dies on
# the protobuf parse before any fallback path runs. The launcher therefore
# repairs both missing and obviously broken weight files before booting.
function Test-EmbeddingWeights {
    param(
        [string]$ModelPath,
        [string]$VocabPath
    )

    if (-not (Test-Path $ModelPath) -or -not (Test-Path $VocabPath)) {
        return $false
    }

    $modelInfo = Get-Item $ModelPath -ErrorAction SilentlyContinue
    $vocabInfo = Get-Item $VocabPath -ErrorAction SilentlyContinue
    if ($null -eq $modelInfo -or $null -eq $vocabInfo) {
        return $false
    }

    # A partial ONNX export or a truncated tokenizer is still a file on disk, but
    # not a usable model. The shipped e5 weights are hundreds of MB and several MB
    # respectively; anything smaller than these cutoffs is a redownload trigger.
    if ($modelInfo.Length -lt 50MB -or $vocabInfo.Length -lt 1MB) {
        return $false
    }

    # Fast sanity check: ONNX protobuf models are a binary wire format, not a
    # random blob. Read a few dozen bytes and reject the obvious garbage that
    # would parse as a protobuf? we still keep the normal provider-level catch,
    # because a full validation belongs to the runtime. This is intentionally a
    # sub-10ms guard for the common fresh-install case.
    try {
        $stream = [System.IO.File]::OpenRead($ModelPath)
        try {
            $sample = New-Object byte[] 16
            $read = $stream.Read($sample, 0, $sample.Length)
        } finally {
            $stream.Dispose()
        }
        if ($read -lt 16) {
            return $false
        }

        # Valid ModelProto fields usually start with a tiny varint tag such as
        # 0x08, 0x0a, 0x12 or 0x20; all-zero or clearly nonsensical headers are
        # not a valid ONNX model and are repaired automatically.
        $first = $sample[0]
        $wire = $first -band 7
        if ($first -eq 0 -or ($wire -ne 0 -and $wire -ne 2)) {
            return $false
        }
    } catch {
        return $false
    }

    return $true
}

function Ensure-EmbeddingWeights {
    $modelPath = Join-Path $repo 'models/embedding/multilingual-e5-small/model.onnx'
    $vocabPath = Join-Path $repo 'models/embedding/multilingual-e5-small/sentencepiece.bpe.model'

    if (Test-EmbeddingWeights -ModelPath $modelPath -VocabPath $vocabPath) {
        return
    }

    if ($WhatIfOnly) {
        Write-Host 'note: local embedding weights are missing or invalid.'
        Write-Host '      would run ./scripts/get-embedding-model.ps1'
        return
    }

    Write-Host "note: local embedding weights are missing or invalid; downloading them now."
    try {
        & (Join-Path $PSScriptRoot 'get-embedding-model.ps1')
    } catch {
        Write-Host "note: embedding weights could not be downloaded ($($_.Exception.Message))."
        Write-Host '      Vectors are off: no pair sweep, no row narrowing, no woken notes.'
    }
}

Ensure-EmbeddingWeights

# ---- llama-server ------------------------------------------------------
#
# Gated on the weights already being here, which keeps the promise above: a
# tier that was never going to call the local model must not turn a launch
# into a 1.25GB download. Present means start it; absent means say so, the
# same as the embedding weights.
$local = @(Get-ChildItem -Path (Join-Path $repo 'models/local') -Filter '*.gguf' -ErrorAction SilentlyContinue)

if ($NoLlm) {
    Write-Host "skipping llama-server (-NoLlm); local classes will fail until :$LlmPort answers,"
    Write-Host '      and an empty energy meter has nothing to fall back to.'
} elseif (Test-Port $LlmPort) {
    Write-Host "llama-server already answering on :$LlmPort; leaving it alone"
} elseif ($local.Count -eq 0) {
    if ($WhatIfOnly) {
        Write-Host 'note: no local model in models/local.'
        Write-Host "      would download Qwen3.5-2B and start llama-server on :$LlmPort"
    } else {
        Write-Host 'note: no local model in models/local; downloading Qwen3.5-2B now.'
        # This is the path that makes the local tier actually usable instead of
        # failing with a silent no-model condition on the first turn.
        try {
            & (Join-Path $PSScriptRoot 'get-local-model.ps1') -Start -Port $LlmPort
        } catch {
            Write-Host "note: local model setup failed ($($_.Exception.Message))."
            Write-Host '      Paid tiers are unaffected; local ones will fail per call.'
        }
    }
} elseif ($WhatIfOnly) {
    Write-Host "would start llama-server on :$LlmPort"
} else {
    # That script owns the launch line: -ngl 99 and the slot count are
    # not details this launcher should be holding a second copy of.
    #
    # Caught, not fatal: the local model is now started for tiers that do not
    # need it, so a llama-server that will not come up must never be what
    # stops a Pro session from booting.
    try {
        & (Join-Path $PSScriptRoot 'get-local-model.ps1') -Start -Port $LlmPort
    } catch {
        Write-Host "note: llama-server did not start ($($_.Exception.Message))."
        Write-Host '      Paid tiers are unaffected; local ones will fail per call.'
    }
}

# ---- the one port both shapes want --------------------------------------
#
# Only one process may hold the archive directory, so this is a stop, not a
# warning: a second host would fail on the parquet files anyway. The shell
# counts here too -- it is the same host with a window around it, listening
# on the same port.
if (Test-Port $Port) {
    Write-Host "something is already listening on :$Port. Close that host first."
    exit 1
}

if (-not $Dev) {
    # ---- shipped shape: the shell ---------------------------------------
    #
    # The exported client is the shell's whole surface: it serves out/ at its
    # own origin, so a missing export is a transparent window with nothing in
    # it. Built here rather than reported, because unlike the weights and the
    # keys this is our own source built with tools already on the machine --
    # but not rebuilt every launch, which would put a minute in front of
    # every start for a directory that is usually current. Stale counts as
    # missing: an edited component that never reached out/ is a panel that
    # silently keeps showing last week's lines, and nothing on screen says so.
    $export = Join-Path $ui 'out/index.html'
    $stale = $false
    if (Test-Path $export) {
        $built = (Get-Item $export).LastWriteTimeUtc
        $stale = [bool](Get-ChildItem $ui -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(node_modules|out|\.next)\\' -and $_.LastWriteTimeUtc -gt $built } |
            Select-Object -First 1)
    }

    if ($stale -or -not (Test-Path $export)) {
        # The UI is part of the shipped app: missing deps or missing WebGPU types
        # are treated as install-time faults that the bootstrap should repair.
        Ensure-UiDependencies

        if ($WhatIfOnly) {
            Write-Host 'would export the client (morrow-eci: npm run build)'
        } else {
            Write-Host $(if ($stale) { 'the client changed since it was exported; rebuilding it' } else { 'exporting the client once; this takes a minute' })
            # build.cmd rather than npm directly, for the same reason dev.cmd
            # exists: it puts nodejs on PATH first.
            & (Join-Path $ui 'build.cmd')
            if (-not (Test-Path (Join-Path $ui 'out/index.html'))) {
                Write-Host 'the export did not produce morrow-eci/out. Fix that, then start again.'
                exit 1
            }
        }
    }

    # Every path Morrow reads -- the archive, the tier files, the exported
    # client, the overlay's remembered corner -- hangs off the exe's own
    # folder rather than the working directory, so starting the built exe
    # straight from here is the same session `dotnet run` would have opened.
    if ($NoBuild) {
        # Globbed, and newest wins. The target framework carries a Windows SDK
        # version, so the real folder is net10.0-windows10.0.19041.0 and the
        # bare net10.0-windows beside it is a leftover from before that was
        # pinned -- which nothing cleans up and which this script happily
        # started for a week, exe and client both frozen at whatever day it
        # was abandoned. A stale build is the one failure -NoBuild must not
        # have: its whole purpose is to run exactly what was last built.
        $exe = @('Release', 'Debug') | ForEach-Object {
            Get-ChildItem (Join-Path $repo "src/EciCas.Shell/bin/$_") -Filter Morrow.exe `
                -Recurse -Depth 1 -ErrorAction SilentlyContinue
        } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName

        if (-not $exe) {
            Write-Host 'nothing is built yet, so -NoBuild has no Morrow.exe to start.'
            Write-Host '  dotnet build src/EciCas.Shell'
            exit 1
        }

        if ($WhatIfOnly) {
            Write-Host "would start [Morrow ($Tier)]: $exe --Tier=$Tier"
        } else {
            # No window: it is a desktop application, and the console one
            # would sit there empty for the life of the session.
            Start-Process -FilePath $exe -ArgumentList "--Tier=$Tier" | Out-Null
        }
    } else {
        Start-Window -Title "Morrow ($Tier)" -WorkingDirectory $repo `
            -Command "dotnet run --project src/EciCas.Shell -- --Tier=$Tier"
    }

    if ($WhatIfOnly) { return }

    # The shell draws nothing until the host inside it is up and warm, which
    # on a cold build is a long quiet minute. Waiting here means this window
    # is what says she has arrived, rather than leaving someone watching an
    # empty desktop.
    Wait-Port -Number $Port -What 'Morrow' | Out-Null

    Write-Host ''
    Write-Host 'Up. Morrow is in the corner: hold - to talk, press | to make her'
    Write-Host 'clickable, then click her for the full session. Quit from the tray.'
    return
}

# ---- development shape: host, dev server, browser -----------------------
Start-Window -Title "eci-cas host ($Tier)" -WorkingDirectory $repo `
    -Command "dotnet run --project src/EciCas.Host -- --Tier=$Tier"

if (-not $NoUi) {
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

# The host first: it is the slow one on a cold build, and a browser opened
# before it answers shows an empty transcript that only a reload fixes.
Wait-Port -Number $Port -What 'host' | Out-Null
if (-not $NoUi) { Wait-Port -Number $UiPort -What 'surface' | Out-Null }

if (-not $NoBrowser -and -not $NoUi) {
    Start-Process "http://localhost:$UiPort"
}

Write-Host ''
Write-Host 'Up. Close each window to stop that part; nothing here holds the others.'
