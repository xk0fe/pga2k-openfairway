<#
Dot-source this from a developer tool to get $game, the install folder of the game to work on, and $edition, its
entry in src/Shared/games.json (id, name, executable...). The game is the one named by $GameId when the tool
defines it (pga2k25 or pga2k23; pga2k25 by default), found through Steam the same way the installer does. The
OPENFAIRWAY_GAME environment variable, when set to a game folder, wins over both.
#>
$openFairwayGames = Get-Content (Join-Path $PSScriptRoot '..\src\Shared\games.json') -Raw | ConvertFrom-Json

function Find-OpenFairwayGame([string]$Id) {
    if ($env:OPENFAIRWAY_GAME -and (Test-Path $env:OPENFAIRWAY_GAME)) {
        $folder = $env:OPENFAIRWAY_GAME
        $found = $openFairwayGames | Where-Object { Test-Path (Join-Path $folder "$($_.executable).exe") } | Select-Object -First 1
        if (-not $found) { throw "OPENFAIRWAY_GAME ($folder) holds none of the supported games." }
        return @{ Dir = $folder; Edition = $found }
    }
    $wanted = $openFairwayGames | Where-Object id -eq $Id
    if (-not $wanted) { throw "Unknown game '$Id'. Supported: $(($openFairwayGames | ForEach-Object id) -join ', ')." }
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { throw "Steam not found. Set OPENFAIRWAY_GAME to the $($wanted.name) folder." }
    $libraries = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        $libraries += Select-String -Path $vdf -Pattern '"path"\s+"(.+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\', '\' }
    }
    foreach ($library in $libraries | Select-Object -Unique) {
        $dir = Join-Path $library "steamapps\common\$($wanted.installDir)"
        if (Test-Path (Join-Path $dir "$($wanted.executable).exe")) { return @{ Dir = $dir; Edition = $wanted } }
    }
    throw "$($wanted.name) not found in any Steam library. Set OPENFAIRWAY_GAME to its folder."
}

<# The running process of whichever supported game is open (only one runs at a time while developing). #>
function Get-OpenFairwayGameProcess {
    foreach ($candidate in $openFairwayGames) {
        $p = Get-Process $candidate.executable -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p) { return $p }
    }
    throw 'No supported game is running.'
}

# Tools that only talk to the running game set $NoGameFolder before dot-sourcing this.
if (-not $NoGameFolder) {
    if (-not $GameId) { $GameId = 'pga2k25' }
    $found = Find-OpenFairwayGame $GameId
    $game = $found.Dir
    $edition = $found.Edition
}
