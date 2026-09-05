<#
.SYNOPSIS
Gets the minimal tier running: weights, llama.cpp, and the server itself.

.DESCRIPTION
`--Tier=Minimal` points every substrate class at an OpenAI-compatible server
on localhost. That server is llama.cpp and the weights are a Qwen3.5 4B --
~2.7GB, deliberately not committed, for the same reason the embedding
weights are not.

This does the whole path, because the parts a person is left to do by hand
are exactly where it breaks: winget installs llama-server somewhere that is
not on PATH, and the launch line needs -ngl or the model runs on CPU and
feels broken rather than slow.

Nothing here is required to boot. `--Tier=Mock` is the tier that needs
nothing, and an unreachable server degrades cleanly rather than hanging --
this only exists so the free tier can actually think.

Weights land in <repo>/models/local/, outside bin/, so `dotnet clean` does
not delete them and every build configuration shares one copy.

Deliberately ASCII-only: powershell.exe reads a UTF-8 script as ANSI, and an
em dash arrives as mojibake in the one output someone is reading for help.

.EXAMPLE
./scripts/get-local-model.ps1

.EXAMPLE
./scripts/get-local-model.ps1 -Start
Downloads if needed, then starts the server in a window of its own.

.EXAMPLE
./scripts/get-local-model.ps1 -Quant Q6_K -NoInstall
#>
[CmdletBinding()]
param(
    [string]$Repo = 'unsloth/Qwen3.5-4B-GGUF',

    # UD-Q4_K_XL is Unsloth's dynamic 4-bit: ~2.7GB, which leaves an 8GB card
    # room for KV cache across a couple of slots. Go up only if there is VRAM
    # to spare; the fan-out wants slots more than it wants bits.
    [string]$Quant = 'UD-Q4_K_XL',

    [string]$Destination,

    # Start the server when everything is in place.
    [switch]$Start,

    # Never invoke winget; only report what is missing.
    [switch]$NoInstall,

    [int]$Port = 8080,
    [int]$Context = 16384,
    [int]$Slots = 2
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $repoRoot 'models/local' }
if (-not (Test-Path $Destination)) { New-Item -ItemType Directory -Path $Destination -Force | Out-Null }

# ---------------------------------------------------------------- weights --

# Ask the Hub what it actually holds rather than assuming a filename. Quant
# naming drifts between repos and a 404 halfway through 2.7GB is a poor way
# to find that out. A large quant may also be split across several parts.
Write-Host "looking up $Quant in $Repo"
$manifest = Invoke-RestMethod -Uri "https://huggingface.co/api/models/$Repo"
$files = @($manifest.siblings.rfilename | Where-Object { $_ -like "*$Quant*.gguf" })

if ($files.Count -eq 0) {
    $available = ($manifest.siblings.rfilename |
        Where-Object { $_ -like '*.gguf' } |
        ForEach-Object { [regex]::Match($_, '(?<=-)((UD-)?[IQ]Q?\d.*?|BF16|Q\d[^.]*)(?=\.gguf$)').Value } |
        Where-Object { $_ } |
        Sort-Object -Unique) -join ', '
    throw "No file matching '$Quant' in $Repo. Available: $available"
}

foreach ($remote in $files) {
    $target = Join-Path $Destination (Split-Path -Leaf $remote)
    if (Test-Path $target) {
        Write-Host "already present, skipping: $target"
        continue
    }

    Write-Host "downloading $remote (this is the slow part)"
    # BITS beats Invoke-WebRequest by a wide margin on a file this size: it
    # streams to disk and shows progress, where Invoke-WebRequest buffers.
    try {
        Start-BitsTransfer -Source "https://huggingface.co/$Repo/resolve/main/$remote" -Destination $target -Description $remote
    }
    catch {
        Write-Host 'BITS unavailable, falling back to Invoke-WebRequest'
        Invoke-WebRequest -Uri "https://huggingface.co/$Repo/resolve/main/$remote" -OutFile $target
    }
}

$gguf = (Resolve-Path (Join-Path $Destination (Split-Path -Leaf $files[0]))).Path

# -------------------------------------------------------------- llama.cpp --

<#
.SYNOPSIS
Finds llama-server.exe wherever winget actually put it.

.DESCRIPTION
winget installs into a versioned package directory and does not always add a
Links shim, so Get-Command alone reports "not installed" for something that
is sitting on disk. Searching the package root is what stops the script from
lying to a person who just ran the install it recommended.
#>
function Find-LlamaServer {
    $onPath = Get-Command llama-server -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $roots = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links'),
        $env:ProgramFiles
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $roots) {
        $hit = Get-ChildItem -Path $root -Filter 'llama-server.exe' -Recurse -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    return $null
}

$server = Find-LlamaServer

if (-not $server -and -not $NoInstall) {
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Write-Host ''
        Write-Host 'llama-server not found; installing llama.cpp via winget'
        winget install --id ggml.llamacpp --accept-package-agreements --accept-source-agreements
        $server = Find-LlamaServer
    }
    else {
        Write-Host 'winget is unavailable; install llama.cpp by hand: https://github.com/ggml-org/llama.cpp/releases'
    }
}

if (-not $server) {
    Write-Host ''
    Write-Host 'llama-server is still missing. Install it, then re-run this script:'
    Write-Host ''
    Write-Host '  winget install llama.cpp'
    Write-Host ''
    return
}

Write-Host ''
Write-Host "llama-server: $server"

# Report what it can actually compute on. A build that sees no GPU still
# runs, at roughly a tenth of the speed -- slow enough that a person assumes
# the swarm is broken rather than the backend unaccelerated.
$devices = & $server --list-devices 2>&1 | Out-String
$gpu = ($devices -split "`n" | Where-Object { $_ -match '^\s+(Vulkan|CUDA|ROCm|SYCL|Metal)\d+:' })

if ($gpu) {
    Write-Host 'GPU devices visible to llama.cpp:'
    $gpu | ForEach-Object { Write-Host "  $($_.Trim())" }
    Write-Host ''
    Write-Host 'First call after a cold start pays one-off shader compilation and can'
    Write-Host 'take several seconds. Warm picking calls land in tens of milliseconds.'
}
else {
    Write-Host 'WARNING: no GPU device visible -- this will run on CPU.'
    Write-Host 'Expect a turn to take long enough that it feels broken rather than slow.'
}

# ----------------------------------------------------------------- launch --

# -ngl 99 is not optional: without it the layers stay on the CPU even when a
# GPU is present and idle.
# --jinja is not optional either: without it the tier's Thinking flag never
# reaches the chat template, and slow-* silently stops reasoning.
$argList = @('-m', $gguf, '-c', $Context, '-np', $Slots, '-ngl', '99', '--port', $Port, '--jinja')
$launch = "& `"$server`" " + (($argList | ForEach-Object { if ("$_" -match '\s') { "`"$_`"" } else { "$_" } }) -join ' ')

Write-Host ''
if ($Start) {
    $existing = try { Invoke-RestMethod "http://localhost:$Port/health" -TimeoutSec 2 } catch { $null }
    if ($existing) {
        Write-Host "something is already serving :$Port -- leaving it alone"
    }
    else {
        Write-Host "starting llama-server on :$Port"
        Start-Process -FilePath $server -ArgumentList $argList
        foreach ($attempt in 1..60) {
            Start-Sleep -Seconds 1
            $health = try { Invoke-RestMethod "http://localhost:$Port/health" -TimeoutSec 2 } catch { $null }
            if ($health) { Write-Host "server is up after ${attempt}s"; break }
        }
    }
}
else {
    Write-Host 'Start the server (its own terminal -- it runs until you stop it):'
    Write-Host ''
    Write-Host "  $launch"
    Write-Host ''
    Write-Host 'Or re-run this script with -Start to launch it for you.'
}

Write-Host ''
Write-Host 'Then, in another terminal:'
Write-Host ''
Write-Host '  dotnet run --project src/EciCas.Host -- --Tier=Minimal'
Write-Host ''
Write-Host "The tier expects http://localhost:8080/v1/ -- change Substrates:Providers:local"
Write-Host 'in appsettings.json if you serve it somewhere else. No API key is needed.'
