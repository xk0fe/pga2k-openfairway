<# Developer helper: start the game through Steam with the loader on, then switch the loader back off. #>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'game-path.ps1')
$ini = Join-Path $game 'doorstop_config.ini'
function Set-Loader([string]$v) { (Get-Content $ini) -replace '^enabled=.*$', "enabled=$v" | Set-Content $ini -Encoding ascii }
if (Get-Process PGATOUR2K25 -ErrorAction SilentlyContinue) { 'already running'; exit 0 }
Set-Loader 'true'
try {
    Start-Process "steam://rungameid/$((Get-Content (Join-Path $game 'OpenFairway\appid.txt')).Trim())"
    $deadline = (Get-Date).AddMinutes(3)
    while (-not (Get-Process PGATOUR2K25 -ErrorAction SilentlyContinue)) { if ((Get-Date) -gt $deadline) { throw 'game did not start' }; Start-Sleep -Milliseconds 300 }
    "started $(Get-Date -Format HH:mm:ss)"
    Start-Sleep -Seconds 12
} finally { Set-Loader 'false' }
