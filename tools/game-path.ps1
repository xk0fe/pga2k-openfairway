<#
Dot-source this from a developer tool to get $game, the PGA TOUR 2K25 install folder: the OPENFAIRWAY_GAME
environment variable when set, otherwise the game is looked up through Steam the same way the installer does.
#>
function Find-OpenFairwayGame {
    if ($env:OPENFAIRWAY_GAME -and (Test-Path $env:OPENFAIRWAY_GAME)) { return $env:OPENFAIRWAY_GAME }
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { throw 'Steam not found. Set OPENFAIRWAY_GAME to the PGA TOUR 2K25 folder.' }
    $libraries = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        $libraries += Select-String -Path $vdf -Pattern '"path"\s+"(.+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\', '\' }
    }
    foreach ($library in $libraries | Select-Object -Unique) {
        $dir = Join-Path $library 'steamapps\common\PGA TOUR 2K25'
        if (Test-Path (Join-Path $dir 'PGATOUR2K25.exe')) { return $dir }
    }
    throw 'PGA TOUR 2K25 not found in any Steam library. Set OPENFAIRWAY_GAME to its folder.'
}

$game = Find-OpenFairwayGame
