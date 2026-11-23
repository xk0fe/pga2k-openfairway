<#
Builds OpenFairway and assembles the end-user package in dist\OpenFairway:

  Install OpenFairway.cmd / Uninstall OpenFairway.cmd
  files\setup.ps1
  files\payload\{core, dotnet, launcher, mods, winhttp.dll}

The package bundles the .NET runtime and the Unity Doorstop loader, so users need nothing pre-installed.
It contains no game files.
#>
param([string]$DoorstopVersion = '4.6.0')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $repo 'dist\OpenFairway'
$payload = Join-Path $dist 'files\payload'

$dotnetRoot = Join-Path $repo '.dotnet'
$dotnet = Join-Path $dotnetRoot 'dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet).Source; $dotnetRoot = Split-Path $dotnet -Parent }

& $dotnet build (Join-Path $repo 'src\OpenFairway.Core\OpenFairway.Core.csproj') -c Release -o (Join-Path $repo 'build') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $dotnet build (Join-Path $repo 'src\OpenFairway.Launcher\OpenFairway.Launcher.csproj') -c Release -o (Join-Path $repo 'build\launcher') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
$modProjects = Get-ChildItem (Join-Path $repo 'mods') -Directory | Where-Object { Test-Path (Join-Path $_.FullName "$($_.Name).csproj") }
foreach ($mod in $modProjects) {
    & $dotnet build (Join-Path $mod.FullName "$($mod.Name).csproj") -c Release -o (Join-Path $repo "build\mods\$($mod.Name)") --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Mod $($mod.Name) failed to build." }
}

$doorstop = Join-Path $repo 'third_party\doorstop\x64\winhttp.dll'
if (-not (Test-Path $doorstop)) {
    $zip = Join-Path $env:TEMP "doorstop_$DoorstopVersion.zip"
    Invoke-WebRequest "https://github.com/NeighTools/UnityDoorstop/releases/download/v$DoorstopVersion/doorstop_win_release_$DoorstopVersion.zip" -OutFile $zip
    Expand-Archive $zip (Join-Path $repo 'third_party\doorstop') -Force
    Remove-Item $zip
}

$runtime = Get-ChildItem (Join-Path $dotnetRoot 'shared\Microsoft.NETCore.App') -Directory |
    Where-Object Name -like '8.*' | Sort-Object { [version]$_.Name } | Select-Object -Last 1
if (-not $runtime) { throw 'No .NET 8 runtime found to bundle.' }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $payload 'core') | Out-Null
Copy-Item (Join-Path $repo 'build\OpenFairway.Core.dll'), (Join-Path $repo 'build\OpenFairway.API.dll') (Join-Path $payload 'core')
Copy-Item $runtime.FullName (Join-Path $payload 'dotnet') -Recurse
Copy-Item $doorstop $payload
New-Item -ItemType Directory -Force (Join-Path $payload 'launcher') | Out-Null
Copy-Item (Join-Path $repo 'build\launcher\OpenFairway.exe'), (Join-Path $repo 'build\launcher\OpenFairway.exe.config') (Join-Path $payload 'launcher')
foreach ($mod in $modProjects) {
    # Only the mod's own assembly and manifest: a second copy of the API assembly would break type identity.
    $target = Join-Path $payload "mods\$($mod.Name)"
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $repo "build\mods\$($mod.Name)\$($mod.Name).dll"), (Join-Path $mod.FullName 'mod.json') $target
    $assets = Join-Path $mod.FullName 'assets'
    if (Test-Path $assets) { Copy-Item $assets $target -Recurse }
}
Copy-Item (Join-Path $repo 'installer\setup.ps1') (Join-Path $dist 'files')
Copy-Item (Join-Path $repo 'installer\*.cmd') $dist
Copy-Item (Join-Path $repo 'installer\README.txt') $dist
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $dist 'LICENSE.txt')
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $dist
Copy-Item (Join-Path $repo 'third_party\doorstop\LICENSE') (Join-Path $payload 'Doorstop-LICENSE.txt')
Copy-Item (Join-Path $dotnetRoot 'LICENSE.txt') (Join-Path $payload 'dotnet-LICENSE.txt') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') (Join-Path $payload 'dotnet-ThirdPartyNotices.txt') -ErrorAction SilentlyContinue

$size = [math]::Round((Get-ChildItem $dist -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "Package ready: $dist ($size MB, .NET $($runtime.Name))"
