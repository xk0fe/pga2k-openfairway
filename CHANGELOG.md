# Changelog

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
