# Changelog

## 0.6.0 - 2026-11-25

### PGA TOUR 2K23
- OpenFairway runs in PGA TOUR 2K23 as well as 2K25. The installer sets it up in every supported game Steam has, each
  with its own mods. In the launcher the GAME button above SETTINGS picks the game (remembered for next time); its list
  also offers a game Steam has without OpenFairway, and a scan of the Steam libraries.
- In 2K23: the offline guard (its servers are tgc5pc and tgc4pc.hb-studios.com), the mod loader, the developer console
  and the type dump; custom screens and the in-game mod manager; Skip Intro Videos.
- The offline main menu in 2K23, four rows to a tab as the game draws them: HOME with MyPLAYER, MyCAREER and MODS under
  the wordmark, CASUAL with LOCAL MATCH, TOPGOLF and TRAINING, DESIGNER with NEW COURSE and MY COURSES, and the offline
  alerts replaced. The game's own MyPLAYER, local matches (with its course picker) and Topgolf work offline as they are.
- **Offline Career** 0.3.0 runs MyCAREER offline in 2K23 too: Q-School to the PGA TOUR, rounds, results and seasons,
  saved on this PC. The game keeps its own golfer there, so the mod leaves MyPLAYER alone, and it tells the game it is
  online only to get into the career, so finished rounds are not sent anywhere and no network errors show.
- The course list (`ctx.Ui.Courses`) in 2K23 comes from the game's own offline storage, the way its course picker
  gets it, a moment after the main menu first shows. `OpenMatch` works there too, with the round length chosen.
- Not in 2K23 yet: The Climb and Offline Progression.
- Settings, GAMES: "Find PGA TOUR games" says which games OpenFairway supports and scans the Steam libraries for them;
  under it, how each game stands. ADD sets OpenFairway up in a game installed later, from the copy the launcher runs
  from, with the mods made for that game (the installer now keeps a copy of every bundled mod for this). Updating
  from 0.5.0 in the launcher keeps 2K25 up to date; ADD (or the installer) then adds 2K23.

### Platform
- The game layer: everything that knows a game's own code is in `src/OpenFairway.Core/Games`, and what differs between
  the games is one profile each (`GameProfile.cs`): where their code is, their offline options, their menu layout,
  how they start a match. The platform, the mod loader and the mods reach the game only through it.
- `mod.json` `"games"` lists the games a mod is made for (`pga2k25`, `pga2k23`); a mod without it is taken to be for
  2K25. A mod for another game is left out and not listed. `ctx.Game` says which game is running.
- A mod can support several games from one folder: a folder per game named after it (`pga2k25`, `pga2k23`) with
  that game's DLL and files, and `shared` for what they share, so assets and libraries are not kept twice. The
  platform loads the running game's DLL and finds libraries in `shared`; `ctx.ModFile("assets/x.png")` finds a file
  the same way. Installing such a mod into a game copies only that game's folder and the shared one. A mod's games
  come from its game folders when mod.json does not list them.
- Classes asked for by 2K25's assembly names (`HB.Golf`, `HB.Golf.Plugins`) are found in 2K23 too, where the game's
  code is `Assembly-CSharp`.
- `GameTile.MyPlayer` (2K23's own MyPLAYER row) and `GameTile.Topgolf` (the local Topgolf match).
- Looking up a method of a class that was not found no longer reaches the game's runtime (which would crash it).

### Launcher
- The scene is a blueprint: the turning scan of the hole dissolves, along a ragged edge traced in ink, into a plan of
  the same course drawn on the same ground and turning with it (OpenStreetMap's fairways, greens, bunkers, water,
  paths and hole routings, the lidar's contour lines, a symbol for every tree), on a drafting sheet with a grid and a
  title block. It makes one slow turn every 40 seconds. Map data © OpenStreetMap contributors (ODbL).
- The hole is drawn live in 3D on the graphics card (OpenGL 3.3), a fresh picture every display refresh, so the turn
  is perfectly smooth on any display. Without OpenGL the launcher shows a still. The launcher is smaller for it: the
  scene it draws is 2.3 MB, where the turning pictures were 9 MB.
- The line under the name says what the launcher is: a mod manager and offline launcher for PGA TOUR 2K.
- The project is now at github.com/xk0fe/pga2k-openfairway, and the launcher looks for updates and release notes
  there. The old address forwards, so 0.5.0 still finds this update.
- The installer stops before it changes anything while the OpenFairway launcher is open from a game it would update.
  Before, it could leave that game half installed.

### Mods
- **Offline Main Menu** 0.6.0 lays out 2K23's menu as well as 2K25's.
- **Offline Career** 0.3.0 runs in 2K23 too.
- **Skip Intro Videos** 1.1.0 runs in 2K23 too.
- Offline Main Menu, Offline Career and the Course Browser example need OpenFairway 0.6.0 (`ctx.Game`,
  `ctx.ModFile`). Offline Progression and The Climb stay 2K25 only.

### Docs
- [Adding a game of the series](docs/adding-a-game.md): the game list, the game's profile, finding its code with
  the type dump and the developer console, the mods, and what to check before it ships.
- The README lists what works in each game in its own table.

## 0.5.0 - 2026-11-24

### Launcher
- Redesigned: a turning 3D scan of a real golf hole in the middle (USGS lidar for the shape and trees, a USDA aerial
  photo for the colour, both public domain, see `THIRD-PARTY-NOTICES.md`), status on the left, Settings, Mods and
  Play on the right. Titles are upright; PLAY is a strip of mowed fairway, and the status turns green when all is
  well.
- Status card: the game's Steam build, the loader, the OpenFairway version and updates at a glance, under one state
  (Ready, Update, Starting, Loaded, Action needed, Not loaded).
- Play follows the start and confirms, from the platform's log, that OpenFairway loaded inside the game. The
  launcher then steps aside and comes back when the game closes, with how the session went.
- Problems say what happened and what to do, with Retry, Open logs and Copy details (a report for an issue, with your
  user folder left out).
- Updates from GitHub releases: download, SHA-256 check against the digest GitHub lists, install through the
  package's own installer, restart. Mods, settings and saves stay.
- What's new: the release notes of every version, from GitHub or, offline, the ones that came with the launcher.
- Settings: keep the launcher open while you play, update checks and pre-releases, the in-game mod manager key, launch
  options for the game, verify game files, detailed log, developer console, type dump.
- Mods: full details for each mod (what it needs, how it did last time); drop a mod's .zip or folder on the window
  to install it. A mod that is replaced keeps its saved data.
- Starting the launcher again brings back the open one. `--play` starts the game straight away.

### Platform
- `config.json` `"LogLevel": "detailed"` adds the in-game screens' step-by-step lines to the log; they are off by
  default now.
- `mod.json` can say what a mod needs: `openfairway`, the oldest platform version it works with (an older platform
  leaves it out and says why), and `requires`, the mods it needs (a warning when one is off).

### Mods
- **Offline Main Menu** 0.5.0: F8 opens and closes the mod manager anywhere in the game; the key is set in the
  launcher.
- **Offline Progression** declares that it needs Offline Career.

## 0.4.0 - 2026-11-23

First public release.

### Platform
- Offline boot into the game's own main menu, with only what works offline.
- Launcher redesigned: the fairway wordmark, a turning 3D golf hole, mod switches, one Play button.
- In-game mod manager in the game's own style.
- Custom screens: `IUiCanvas.Slide` (fills that glide between positions), `Clickable(..., onHover)`.
- Main menu banner can show a logo image (`MenuBanner.LogoPath`).
- Modding guide and API reference (`docs/index.html`), and four example mods (`examples/`).
- Developer tools find the game through Steam or `OPENFAIRWAY_GAME`.

### Mods
- **Offline Progression** (new): rounds played offline award club, ball and golfer XP, which 2K's servers would award
  online. Club levels unlock upgrades. 3x pace by default.
- **Offline Career** 0.2.0: MyCAREER and MyPLAYER offline; an action for equipment XP.
- **Offline Main Menu** 0.4.0: the OpenFairway banner and the new mod manager.
- **The Climb** 0.3.0: redesigned in the game's own look; broadcast-style leaderboard and scorecard; the test career
  no longer gives the real golfer XP.
- **Skip Intro Videos** 1.0.0.
