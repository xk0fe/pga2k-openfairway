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
- Titles: heavy italic uppercase (in game: the game's heading font; on Windows: Segoe UI Black Italic).
- Corners: 4 px on tiles, rows and in-game buttons (the game's tiles); full pill only for switches and the launcher's Play button.
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
| gold     | `#E9B949` | trophies, wins, promotion only |
| turf     | stripes `#84BE54` / `#56943C` | only as the mowed-grass texture (wordmark, course art); never a flat UI colour |

The game renders its UI in linear colour space: translucent white shows as mid grey. Use opaque colours for
fills in game, and keep any translucent scrim dark.

## Wordmark

"OPEN" in `text`, "FAIRWAY" filled with mowed fairway stripes (`tools/make-wordmark.py`, Segoe UI Black
Italic). Never put the wordmark on a grass photo; it sits on navy or a dark scrim.

## Motion

Ease-out, no overshoot, 160-260 ms for state changes, 350-450 ms for entrances. Entrances rise 12-16 px and
fade in, staggered 40-60 ms. Selection fills slide between rows rather than jump. Switch knobs slide. The
only idle motion is a slow light sweep across the turf in the wordmark and the slow drift of photo backdrops.
