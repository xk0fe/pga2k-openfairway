<#
OpenFairway setup. Run through "Install OpenFairway.cmd" / "Uninstall OpenFairway.cmd".
Finds the game through Steam, ADDS the platform files next to it (no game file is changed) and creates an
"OpenFairway" shortcut. Starting the game from Steam as usual stays completely unmodified.
#>
param(
    [string]$GameDir,
    [switch]$Uninstall,
    [switch]$Purge,        # with -Uninstall: also delete mods, logs and the local type dump
    [switch]$Quiet,        # no dialogs (for scripted use)
    [switch]$NoShortcuts
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$exeName = 'PGATOUR2K25.exe'
$installDirName = 'PGA TOUR 2K25'
$shortcutName = 'OpenFairway.lnk'
$oldShortcutNames = @('OpenFairway (offline mods).lnk')
$payload = Join-Path $PSScriptRoot 'payload'

function Say([string]$text, [string]$icon = 'Information') {
    Write-Host $text
    if (-not $Quiet) { [void][System.Windows.Forms.MessageBox]::Show($text, 'OpenFairway', 'OK', $icon) }
}

function Find-Game {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $libraries = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        $libraries += Select-String -Path $vdf -Pattern '"path"\s+"(.+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\', '\' }
    }
    foreach ($library in $libraries | Select-Object -Unique) {
        $apps = Join-Path $library 'steamapps'
        if (-not (Test-Path $apps)) { continue }
        foreach ($manifest in Get-ChildItem $apps -Filter 'appmanifest_*.acf') {
            if ((Get-Content $manifest.FullName -Raw) -match ('"installdir"\s+"' + [regex]::Escape($installDirName) + '"')) {
                $dir = Join-Path $apps "common\$installDirName"
                if (Test-Path (Join-Path $dir $exeName)) {
                    return @{ Dir = $dir; AppId = ($manifest.BaseName -replace 'appmanifest_', '') }
                }
            }
        }
    }
    return $null
}

function Shortcut-Paths([string[]]$names = @($shortcutName)) {
    foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
        foreach ($name in $names) { Join-Path $folder $name }
    }
}

try {
    $found = Find-Game
    $appId = if ($found) { $found.AppId } else { '' }
    if (-not $GameDir -and $found) { $GameDir = $found.Dir }
    if (-not $GameDir -and -not $Quiet) {
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = "Steam could not locate the game automatically. Select the folder that contains $exeName."
        if ($dialog.ShowDialog() -eq 'OK') { $GameDir = $dialog.SelectedPath }
    }
    if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir $exeName))) { throw "The game was not found. Install it through Steam first." }
    if (Get-Process ([IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue) { throw 'Close the game before running setup.' }

    $homeDir = Join-Path $GameDir 'OpenFairway'
    $manifestFile = Join-Path $homeDir 'installed-files.txt'

    if ($Uninstall) {
        if (Test-Path $manifestFile) {
            foreach ($relative in Get-Content $manifestFile) {
                $path = Join-Path $GameDir $relative
                if (Test-Path $path) { Remove-Item $path -Recurse -Force }
            }
            Remove-Item $manifestFile -Force
        }
        Shortcut-Paths (@($shortcutName) + $oldShortcutNames) | Where-Object { Test-Path $_ } | Remove-Item -Force
        if ($Purge -and (Test-Path $homeDir)) { Remove-Item $homeDir -Recurse -Force }
        Say 'OpenFairway has been removed. Your game is exactly as Steam installed it.'
        return
    }

    # The loader is installed as version.dll (a tiny, stable system library), never as winhttp.dll:
    # standing in for the Windows HTTP library interferes with the game's own licence activation.
    $proxy = Join-Path $GameDir 'version.dll'
    if (Test-Path $manifestFile) {
        # Upgrade: remove what the previous version installed.
        foreach ($relative in Get-Content $manifestFile) {
            $path = Join-Path $GameDir $relative
            if (Test-Path $path) { Remove-Item $path -Recurse -Force }
        }
    }
    foreach ($other in 'version.dll', 'winhttp.dll') {
        if (Test-Path (Join-Path $GameDir $other)) { throw "Another mod loader ($other) is already installed in the game folder. Remove it first." }
    }

    New-Item -ItemType Directory -Force $homeDir | Out-Null
    foreach ($folder in 'core', 'dotnet') {
        $target = Join-Path $homeDir $folder
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Copy-Item (Join-Path $payload $folder) $target -Recurse
    }
    Copy-Item (Join-Path $payload 'launcher\*') $homeDir -Force
    # Bundled mods are refreshed; mods the player added themselves are left alone.
    $modsDir = Join-Path $homeDir 'mods'
    New-Item -ItemType Directory -Force $modsDir | Out-Null
    $bundledMods = @(Get-ChildItem (Join-Path $payload 'mods') -Directory -ErrorAction SilentlyContinue)
    foreach ($mod in $bundledMods) {
        $target = Join-Path $modsDir $mod.Name
        New-Item -ItemType Directory -Force $target | Out-Null
        Copy-Item (Join-Path $mod.FullName '*') $target -Recurse -Force
    }
    Copy-Item (Join-Path $payload 'winhttp.dll') $proxy -Force

    # The loader ships disabled: only the OpenFairway shortcut switches it on, for that one launch.
    @"
[General]
enabled=false
target_assembly=OpenFairway\core\OpenFairway.Core.dll
redirect_output_log=false
boot_config_override=
ignore_disable_switch=false

[Il2Cpp]
coreclr_path=OpenFairway\dotnet\coreclr.dll
corlib_dir=OpenFairway\dotnet
"@ | Set-Content (Join-Path $GameDir 'doorstop_config.ini') -Encoding ascii
    Set-Content (Join-Path $homeDir 'appid.txt') $appId -Encoding ascii

    $installed = @('version.dll', 'doorstop_config.ini', 'OpenFairway\core', 'OpenFairway\dotnet', 'OpenFairway\appid.txt')
    $installed += Get-ChildItem (Join-Path $payload 'launcher') | ForEach-Object { "OpenFairway\$($_.Name)" }
    $installed += $bundledMods | ForEach-Object { "OpenFairway\mods\$($_.Name)\$($_.Name).dll" }
    $installed | Set-Content $manifestFile -Encoding utf8

    if (-not $NoShortcuts) {
        Shortcut-Paths $oldShortcutNames | Where-Object { Test-Path $_ } | Remove-Item -Force
        $shell = New-Object -ComObject WScript.Shell
        foreach ($path in Shortcut-Paths) {
            $link = $shell.CreateShortcut($path)
            $link.TargetPath = Join-Path $homeDir 'OpenFairway.exe'
            $link.WorkingDirectory = $homeDir
            $link.IconLocation = "$(Join-Path $GameDir $exeName),0"
            $link.Description = 'OpenFairway mod launcher'
            $link.Save()
        }
    }

    Say ("OpenFairway is installed.`n`n" +
        "- Open 'OpenFairway' from your desktop to choose mods and play. With mods the game runs offline.`n" +
        "- Start the game from Steam as usual to play the normal, unmodified online game.`n`n" +
        "Mods go in: $homeDir\mods")
}
catch {
    Say $_.Exception.Message 'Error'
    exit 1
}
