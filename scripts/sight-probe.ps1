<#
.SYNOPSIS
Asks the vendor the same question Sight asks, and prints what comes back.

.DESCRIPTION
Sight's failures all classify as one word ("unreachable"), and a look that
fails and a look that is refused are the same word. This sends the request
OpenAiCompatibleSubstrateProvider builds -- same model, same image part, same
detail, same reasoning_effort, same max_tokens -- against the newest screenshot
on disk, and prints the status and body verbatim.

Then it sends it again with one parameter dropped at a time, because the whole
question is which of them the model refuses: max_tokens is deprecated in favour
of max_completion_tokens on newer models, reasoning_effort "none" is not a
value every model takes, and either is a 400 that says nothing about images.

Run it in a shell where OPENAI_API_KEY is set. It costs a fraction of a cent.
#>
[CmdletBinding()]
param(
    [string]$Model = 'gpt-5.6-luna',
    [string]$BaseUrl = 'https://api.openai.com/v1/',
    [string]$Shot
)

$ErrorActionPreference = 'Stop'

if (-not $env:OPENAI_API_KEY) {
    Write-Host 'OPENAI_API_KEY is not set in this shell. Set it and run again.'
    exit 1
}

if (-not $Shot) {
    $repo = Split-Path $PSScriptRoot -Parent
    $Shot = Get-ChildItem (Join-Path $repo 'src/EciCas.Shell/bin') -Filter *.jpg -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
}

if (-not $Shot -or -not (Test-Path $Shot)) {
    Write-Host 'no screenshot found. Run Morrow once with the voice key, or pass -Shot <path>.'
    exit 1
}

Write-Host "shot:  $Shot"
Write-Host "model: $Model"
$dataUrl = 'data:image/jpeg;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($Shot))

function Ask([string]$Label, [hashtable]$Extra) {
    $body = @{
        model    = $Model
        messages = @(@{
            role    = 'user'
            content = @(
                @{ type = 'text'; text = 'Describe this screen in one sentence.' },
                @{ type = 'image_url'; image_url = @{ url = $dataUrl; detail = 'low' } }
            )
        })
    }
    foreach ($k in $Extra.Keys) { $body[$k] = $Extra[$k] }

    Write-Host ''
    Write-Host "--- $Label"
    try {
        $reply = Invoke-RestMethod -Method Post -Uri ($BaseUrl.TrimEnd('/') + '/chat/completions') `
            -Headers @{ Authorization = "Bearer $env:OPENAI_API_KEY" } `
            -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 12)
        Write-Host ('ok: ' + $reply.choices[0].message.content)
    } catch {
        Write-Host "FAILED: $($_.Exception.Message)"
        $response = $_.Exception.Response
        if ($response) {
            $reader = [IO.StreamReader]::new($response.GetResponseStream())
            Write-Host $reader.ReadToEnd()
        }
    }
}

# Exactly what Sight sends today.
Ask 'as Sight sends it (reasoning_effort=none, max_tokens=2048)' @{ reasoning_effort = 'none'; max_tokens = 2048 }

# One at a time, to name the offender rather than guess at it.
Ask 'without max_tokens' @{ reasoning_effort = 'none' }
Ask 'with max_completion_tokens instead' @{ reasoning_effort = 'none'; max_completion_tokens = 2048 }
Ask 'without reasoning_effort' @{ max_tokens = 2048 }
Ask 'picture only, nothing else' @{}
