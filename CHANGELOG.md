# Changelog

## Unreleased

### Launcher
- The scene is a blueprint: the turning scan of the hole dissolves, along a ragged edge traced in ink, into a plan of
  the same course drawn on the same ground and turning with it (OpenStreetMap's fairways, greens, bunkers, water,
  paths and hole routings, the lidar's contour lines, a symbol for every tree), on a drafting sheet with a grid and a
  title block. It makes one slow turn every 40 seconds. Map data © OpenStreetMap contributors (ODbL).
- The hole is drawn live in 3D on the graphics card (OpenGL 3.3), a fresh picture every display refresh, so the turn
  is perfectly smooth on any display. Without OpenGL the launcher shows a still. The launcher is smaller for it: the
  scene it draws is 2.3 MB, where the turning pictures were 9 MB.

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
