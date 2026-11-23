<#
Developer loop: quit the running game cleanly, copy the current build into the game folder, start it again
through Steam with the loader on, and put it in a small window. Requires "DevConsole": true in config.json.
#>
param([switch]$NoDeploy, [int]$Width = 1280, [int]$Height = 720)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'game-path.ps1')
$home2 = Join-Path $game 'OpenFairway'
$python = (Get-Command python).Source
function Con { $old = $ErrorActionPreference; $ErrorActionPreference = 'SilentlyContinue'; try { & $python -I (Join-Path $repo 'tools\devcon.py') @args 2>$null } finally { $ErrorActionPreference = $old } }

if (Get-Process PGATOUR2K25 -ErrorAction SilentlyContinue) {
    try { Con 'call @UnityEngine.CoreModule|UnityEngine|Application Quit 0 null' | Out-Null } catch { }
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Process PGATOUR2K25 -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    Get-Process PGATOUR2K25 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

if (-not $NoDeploy) {
    Copy-Item (Join-Path $repo 'build\OpenFairway.Core.dll'), (Join-Path $repo 'build\OpenFairway.API.dll') (Join-Path $home2 'core') -Force
    foreach ($mod in Get-ChildItem (Join-Path $repo 'build\mods') -Directory -ErrorAction SilentlyContinue) {
        $target = Join-Path $home2 "mods\$($mod.Name)"
        New-Item -ItemType Directory -Force $target | Out-Null
        Copy-Item (Join-Path $mod.FullName "$($mod.Name).dll") $target -Force
        $manifest = Join-Path $repo "mods\$($mod.Name)\mod.json"
        if (Test-Path $manifest) { Copy-Item $manifest $target -Force }
        $assets = Join-Path $repo "mods\$($mod.Name)\assets"
        if (Test-Path $assets) { Copy-Item $assets $target -Recurse -Force }
    }
}

& (Join-Path $PSScriptRoot 'dev-launch.ps1')

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class DevWin { [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool r); }
"@
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline) {
    $r = Con 'call @UnityEngine.CoreModule|UnityEngine|Screen set_fullScreenMode 1 null i:3'
    if ($r -match 'void') { break }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 1
$p = Get-Process PGATOUR2K25
[void][DevWin]::MoveWindow($p.MainWindowHandle, 40, 40, $Width + 16, $Height + 39, $true)
"windowed $(Get-Date -Format HH:mm:ss)"
