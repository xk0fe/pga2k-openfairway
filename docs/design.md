# OpenFairway design language

One look for the launcher, the in-game screens (mod manager, The Climb) and anything we publish. It follows
PGA TOUR 2K25's own menus so our screens feel like part of the game, kept plain the way Gran Turismo 7 and
Forza Horizon 5 menus are plain.

## What the references taught

- PGA TOUR 2K25 (its own menus): deep navy background, slate tiles with a thin lighter border and a small
  radius, heavy italic uppercase titles, regular-weight body text, a cream fill on the selected tab, outlined
  keycaps for hints at the bottom left.
- Gran Turismo 7: flat dark cards with no borders or glow, pill buttons, generous empty space, one solid fill
  marks the selected card. Results are one huge line of type over the 3D scene.
- Forza Horizon 5 / FIFA 20: settings are text lists, not icon cards; the selected row is a solid inverted
  fill; a description panel on the right explains the selected row; small tag labels sit above panels.
- Grid Legends: results as TV lower thirds (broadcast graphics), which suits golf leaderboards.
- Mobbin (switch pattern): a switch sits at the right edge of a list row and acts at once; on-state is the
  filled track, off is a quiet grey track.

## Rules

- No glows, no breathing halos, no gradient icon squares, no coloured initials badges, no coloured dots on
  every chip, no rings around every number. One accent per screen, used for one thing.
- Lists before cards. A card only when it holds a picture.
- Hierarchy by size and weight, not by colour. Body text is regular weight, sentence case.
- Titles: in game, the game's own heading font, in its own heavy italic (the game's default style). Everywhere else
  (the launcher, the docs) titles are upright: uppercase Segoe UI Bold with about 1.5 px between letters, and Segoe
  UI Black only for the launcher's Play tile. No italic that is not the game's own.
- Corners: 4 px on tiles, rows, buttons and the launcher's Play tile (the game's tiles); full pill only for switches and
  status badges.
- Borders: 1 px, `line` colour, only on tiles that sit on a photo.

## Palette

Sampled from PGA TOUR 2K25's own menus (2560x1440 captures), so our screens sit next to the game's without a seam.

| token    | hex       | use |
|----------|-----------|-----|
| ink      | `#0A0A14` | deepest background, window edges |
| bg       | `#0F0F1D` | page background (the game's menu background) |
| strip    | `#191923` | subtitle strips, footers |
| tile     | `#3F4653` to `#242731` | tiles and rows: a vertical slate gradient, top to bottom |
| raised   | `#4A5160` to `#2E323D` | hovered tile |
| line     | `#454850` | 1 px tile borders, dividers |
| text     | `#F4F4F4` | primary text |
| muted    | `#AEB4C6` | secondary text |
| faint    | `#6E7486` | tertiary text, disabled |
| cream    | `#FDEDDA` | selection fill (the game's selected tab): selected row, tab, primary button; text on it is `bg` |
| red      | `#E0323F` | golf red: under-par scores, pending changes, "live" |
| gold     | `#E9B949` | trophies, wins, promotion, and an update that is ready |
| turf     | stripes `#84BE54` / `#56943C` | the mowed-grass texture: the wordmark, course art and the launcher's PLAY tile |
| green    | `#62B444` | all good: the Ready and Loaded badge, its bar and the ticks under it |
| paper    | `#0D2646` | the launcher's blueprint sheet |
| grid     | `#132E52` / `#1A385F` | its drafting grid, every 24 px, a stronger line every fifth |
| blueprint | `#CEE2FF` | blueprint ink: the drawn course, the sheet's border and title block |

The game renders its UI in linear colour space: translucent white shows as mid grey. Use opaque colours for
fills in game, and keep any translucent scrim dark.

## Status

The launcher's status card carries one badge and a 3 px bar in the same colour: green when all is well (Ready,
Loaded), gold for an update that is ready, red when something needs the player (Action needed, Not loaded), slate while
something is under way (Starting, Updating). Each check under it is a small green tick, or a red mark. PLAY is a tile
of the wordmark's mowed fairway with white lettering; UPDATE, a different action, is cream. A problem
always says what happened, then what to do, and offers the next step as a button.

## Blueprint

The launcher's scene is a drafting sheet: blueprint paper with a grid, a thin border and a title block. The turning
hole is a photographic scan in the middle that dissolves, along a ragged edge traced in ink, into a plan of the same
course drawn on the same ground: the real course becoming its drawing. The launcher draws the hole live over the sheet:
the photo covers it, the drawing is ink laid on it, so the grid runs through the drawing. The still (for machines
without OpenGL, and for screenshots) has the sheet's paper and grid baked in at the launcher's own pixels, so `paper`
and `grid` above, `HoleCentre` in the launcher and `ORIGIN` in `tools/render-hole.py` must change together.

## Wordmark

The logo keeps its slant, drawn after the game's own heavy italic headings. "OPEN" in `text`, "FAIRWAY" filled with mowed fairway stripes (`tools/make-wordmark.py`, Segoe UI Black
Italic). Never put the wordmark on a grass photo; it sits on navy or a dark scrim.

## Motion

Ease-out, no overshoot, 160-260 ms for state changes, 350-450 ms for entrances. Entrances rise 12-16 px and
fade in, staggered 40-60 ms. Selection fills slide between rows rather than jump. Switch knobs slide. The
only idle motion is a slow light sweep across the turf in the wordmark and the slow drift of photo backdrops.
