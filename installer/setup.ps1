<#
OpenFairway setup. Run through "Install OpenFairway.cmd" / "Uninstall OpenFairway.cmd".
Finds every supported game through Steam (games.json beside this script: PGA TOUR 2K25 and 2K23), ADDS the
platform files next to each one (no game file is changed) and creates one "OpenFairway" shortcut; the launcher
switches between the games. Starting a game from Steam as usual stays completely unmodified.
#>
param(
    [string]$GameDir,      # one game folder, or several separated by ';' (the launcher's updater passes them all)
    [switch]$Uninstall,
    [switch]$Purge,        # with -Uninstall: also delete mods, logs and the local type dump
    [switch]$Quiet,        # no dialogs (for scripted use)
    [switch]$NoShortcuts
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
# Assigned first: Windows PowerShell hands a JSON array over as one object, which @() would wrap in another array.
$games = Get-Content (Join-Path $PSScriptRoot 'games.json') -Raw | ConvertFrom-Json
$games = @($games)
$firstGame = $games[0].id   # mods whose mod.json names no games were made for it
$shortcutName = 'OpenFairway.lnk'
$oldShortcutNames = @('OpenFairway (offline mods).lnk')
$payload = Join-Path $PSScriptRoot 'payload'

function Say([string]$text, [string]$icon = 'Information') {
    Write-Host $text
    if (-not $Quiet) { [void][System.Windows.Forms.MessageBox]::Show($text, 'OpenFairway', 'OK', $icon) }
}

function Get-SteamLibraries {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return @() }
    $libraries = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        $libraries += Select-String -Path $vdf -Pattern '"path"\s+"(.+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\', '\' }
    }
    return $libraries | Select-Object -Unique
}

<# Every supported game Steam has installed: @{ Dir; Game (its games.json entry); AppId }. #>
function Find-Games {
    $found = @()
    foreach ($library in Get-SteamLibraries) {
        $apps = Join-Path $library 'steamapps'
        if (-not (Test-Path $apps)) { continue }
        foreach ($game in $games) {
            $dir = Join-Path $apps "common\$($game.installDir)"
            if (-not (Test-Path (Join-Path $dir "$($game.executable).exe"))) { continue }
            if ($found | Where-Object { $_.Dir -eq $dir }) { continue }
            $manifest = Join-Path $apps "appmanifest_$($game.steamAppId).acf"
            $appId = if (Test-Path $manifest) { $game.steamAppId } else { '' }
            $found += @{ Dir = $dir; Game = $game; AppId = $appId }
        }
    }
    return $found | Sort-Object { [array]::IndexOf(@($games.id), $_.Game.id) }
}

<# The supported game in a folder the player or the updater named. #>
function Resolve-Game([string]$dir) {
    $game = $games | Where-Object { Test-Path (Join-Path $dir "$($_.executable).exe") } | Select-Object -First 1
    if (-not $game) { throw "No supported game in $dir (looked for $(($games | ForEach-Object { "$($_.executable).exe" }) -join ', '))." }
    $known = Find-Games | Where-Object { $_.Dir -eq $dir } | Select-Object -First 1
    return @{ Dir = $dir; Game = $game; AppId = $(if ($known) { $known.AppId } else { $game.steamAppId }) }
}

function Shortcut-Paths([string[]]$names = @($shortcutName)) {
    foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
        foreach ($name in $names) { Join-Path $folder $name }
    }
}

function Remove-Installed([string]$dir) {
    $manifestFile = Join-Path $dir 'OpenFairway\installed-files.txt'
    if (-not (Test-Path $manifestFile)) { return $false }
    foreach ($relative in Get-Content $manifestFile) {
        # A damaged line names no file of ours; skip it rather than stop half-way.
        if (-not $relative.Trim() -or $relative.IndexOfAny([IO.Path]::GetInvalidPathChars()) -ge 0) { continue }
        $path = Join-Path $dir $relative.Trim()
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    Remove-Item $manifestFile -Force
    return $true
}

<# The games a mod is made for: mod.json "games", else the games it has a folder for, else the first game. #>
function Test-ModForGame([string]$modDir, $game) {
    $listed = @()
    $manifest = Join-Path $modDir 'mod.json'
    if (Test-Path $manifest) { $listed = @((Get-Content $manifest -Raw | ConvertFrom-Json).games) | Where-Object { $_ } }
    if ($listed.Count -eq 0) { $listed = @($games | Where-Object { Test-Path (Join-Path $modDir $_.id) } | ForEach-Object id) }
    if ($listed.Count -eq 0) { $listed = @($firstGame) }
    return $listed -contains $game.id
}

<# Copies a mod into one game: everything but the folders of the other games it supports. #>
function Copy-ModForGame([string]$modDir, [string]$destination, $game) {
    $others = @($games | Where-Object { $_.id -ne $game.id } | ForEach-Object id)
    New-Item -ItemType Directory -Force $destination | Out-Null
    Get-ChildItem $modDir | Where-Object { -not ($_.PSIsContainer -and $others -contains $_.Name) } |
        ForEach-Object { Copy-Item $_.FullName $destination -Recurse -Force }
}

<# What the uninstaller removes of a mod (its saved data stays): its DLL and the game's and shared folders. #>
function Get-ModFiles([string]$modDir, [string]$name, $game) {
    $own = Join-Path $modDir "$($game.id)\$name.dll"
    if (Test-Path $own) { "OpenFairway\mods\$name\$($game.id)\$name.dll" } else { "OpenFairway\mods\$name\$name.dll" }
    foreach ($part in @($game.id, 'shared')) { if (Test-Path (Join-Path $modDir $part)) { "OpenFairway\mods\$name\$part" } }
}

function Install-Into($target) {
    $dir = $target.Dir
    $homeDir = Join-Path $dir 'OpenFairway'
    $manifestFile = Join-Path $homeDir 'installed-files.txt'

    # The loader is installed as version.dll (a tiny, stable system library), never as winhttp.dll:
    # standing in for the Windows HTTP library interferes with the game's own licence activation.
    $proxy = Join-Path $dir 'version.dll'
    [void](Remove-Installed $dir)   # an upgrade: remove what the previous version installed
    foreach ($other in 'version.dll', 'winhttp.dll') {
        if (Test-Path (Join-Path $dir $other)) { throw "Another mod loader ($other) is already installed in $dir. Remove it first." }
    }

    New-Item -ItemType Directory -Force $homeDir | Out-Null
    foreach ($folder in 'core', 'dotnet') {
        $destination = Join-Path $homeDir $folder
        if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
        Copy-Item (Join-Path $payload $folder) $destination -Recurse
    }
    Copy-Item (Join-Path $payload 'launcher\*') $homeDir -Force
    # Bundled mods made for this game are refreshed; mods the player added themselves are left alone.
    $modsDir = Join-Path $homeDir 'mods'
    New-Item -ItemType Directory -Force $modsDir | Out-Null
    $bundledMods = @(Get-ChildItem (Join-Path $payload 'mods') -Directory -ErrorAction SilentlyContinue | Where-Object { Test-ModForGame $_.FullName $target.Game })
    foreach ($mod in $bundledMods) { Copy-ModForGame $mod.FullName (Join-Path $modsDir $mod.Name) $target.Game }
    # A copy of every bundled mod, so the launcher can set OpenFairway up in another game later with its own mods.
    $bundled = Join-Path $homeDir 'bundled\mods'
    if (Test-Path $bundled) { Remove-Item $bundled -Recurse -Force }
    New-Item -ItemType Directory -Force $bundled | Out-Null
    Copy-Item (Join-Path $payload 'mods\*') $bundled -Recurse -Force
    Copy-Item (Join-Path $payload 'winhttp.dll') $proxy -Force

    # The loader ships disabled: only the OpenFairway launcher switches it on, for that one launch.
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
"@ | Set-Content (Join-Path $dir 'doorstop_config.ini') -Encoding ascii
    Set-Content (Join-Path $homeDir 'appid.txt') $target.AppId -Encoding ascii

    $installed = @('version.dll', 'doorstop_config.ini', 'OpenFairway\core', 'OpenFairway\dotnet', 'OpenFairway\appid.txt', 'OpenFairway\bundled')
    $installed += Get-ChildItem (Join-Path $payload 'launcher') | ForEach-Object { "OpenFairway\$($_.Name)" }
    $installed += $bundledMods | ForEach-Object { Get-ModFiles (Join-Path $modsDir $_.Name) $_.Name $target.Game }
    $installed | Set-Content $manifestFile -Encoding utf8
    return $homeDir
}

try {
    $targets = @()
    if ($GameDir) {
        $targets = @($GameDir -split ';' | Where-Object { $_.Trim() } | ForEach-Object { Resolve-Game $_.Trim() })
    }
    else {
        $targets = @(Find-Games)
        if ($Uninstall) { $targets = @($targets | Where-Object { Test-Path (Join-Path $_.Dir 'OpenFairway\installed-files.txt') }) }
        if ($targets.Count -eq 0 -and -not $Uninstall -and -not $Quiet) {
            $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
            $dialog.Description = "Steam could not locate a supported game. Select the folder that contains $(($games | ForEach-Object { "$($_.executable).exe" }) -join ' or ')."
            if ($dialog.ShowDialog() -eq 'OK') { $targets = @(Resolve-Game $dialog.SelectedPath) }
        }
    }
    if ($targets.Count -eq 0 -and -not $Uninstall) { throw "No supported game was found ($(($games | ForEach-Object name) -join ', ')). Install one through Steam first." }
    foreach ($target in $targets) {
        if (Get-Process $target.Game.executable -ErrorAction SilentlyContinue) { throw "Close $($target.Game.name) before running setup." }
        # Checked before anything is removed: a running launcher keeps its files locked, and setup would stop half-way.
        $launcherHome = Join-Path $target.Dir 'OpenFairway'
        $running = Get-Process OpenFairway -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($launcherHome, [StringComparison]::OrdinalIgnoreCase) }
        if ($running) { throw 'Close the OpenFairway launcher before running setup.' }
    }

    if ($Uninstall) {
        foreach ($target in $targets) {
            [void](Remove-Installed $target.Dir)
            if ($Purge) {
                $homeDir = Join-Path $target.Dir 'OpenFairway'
                if (Test-Path $homeDir) { Remove-Item $homeDir -Recurse -Force }
            }
        }
        # The shortcut goes with the last game; while another game keeps OpenFairway, it opens that game's launcher.
        $left = @(Find-Games | Where-Object { Test-Path (Join-Path $_.Dir 'OpenFairway\installed-files.txt') })
        if ($left.Count -eq 0) {
            Shortcut-Paths (@($shortcutName) + $oldShortcutNames) | Where-Object { Test-Path $_ } | Remove-Item -Force
            Say 'OpenFairway has been removed. Your games are exactly as Steam installed them.'
        }
        else {
            $shell = New-Object -ComObject WScript.Shell
            $launcherHome = Join-Path $left[0].Dir 'OpenFairway'
            foreach ($path in Shortcut-Paths | Where-Object { Test-Path $_ }) {
                $link = $shell.CreateShortcut($path)
                $link.TargetPath = Join-Path $launcherHome 'OpenFairway.exe'
                $link.WorkingDirectory = $launcherHome
                $link.IconLocation = "$(Join-Path $left[0].Dir "$($left[0].Game.executable).exe"),0"
                $link.Save()
            }
            Say ("OpenFairway has been removed from $(($targets | ForEach-Object { $_.Game.name }) -join ' and '); that game is exactly as Steam installed it. " +
                "It stays in $(($left | ForEach-Object { $_.Game.name }) -join ' and ').")
        }
        return
    }

    $homes = @()
    foreach ($target in $targets) { $homes += @{ Home = (Install-Into $target); Target = $target } }

    if (-not $NoShortcuts) {
        # One shortcut, to the first game's launcher; the launcher switches between games and remembers the choice.
        $first = $homes[0]
        Shortcut-Paths $oldShortcutNames | Where-Object { Test-Path $_ } | Remove-Item -Force
        $shell = New-Object -ComObject WScript.Shell
        foreach ($path in Shortcut-Paths) {
            $link = $shell.CreateShortcut($path)
            $link.TargetPath = Join-Path $first.Home 'OpenFairway.exe'
            $link.WorkingDirectory = $first.Home
            $link.IconLocation = "$(Join-Path $first.Target.Dir "$($first.Target.Game.executable).exe"),0"
            $link.Description = 'OpenFairway mod launcher'
            $link.Save()
        }
    }

    $names = ($homes | ForEach-Object { $_.Target.Game.name }) -join ' and '
    Say ("OpenFairway is installed in $names.`n`n" +
        "- Open 'OpenFairway' from your desktop to choose mods and play. With mods the game runs offline." +
        $(if ($homes.Count -gt 1) { " Pick the game at the top left." } else { "" }) + "`n" +
        "- Start a game from Steam as usual to play the normal, unmodified online game.`n`n" +
        "Mods go in each game's OpenFairway\mods folder:`n" + (($homes | ForEach-Object { Join-Path $_.Home 'mods' }) -join "`n"))
}
catch {
    Say $_.Exception.Message 'Error'
    exit 1
}
