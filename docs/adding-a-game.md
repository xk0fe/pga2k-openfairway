# Adding a game of the series

OpenFairway runs in PGA TOUR 2K25 and PGA TOUR 2K23. Both come from HB Studios and share most of their code, and the
other games of the series are built the same way. Adding one is mostly two things: an entry in the game list, and a
profile in the game layer that says where this game differs. The installer, the launcher, the mod loader and the
developer tools then pick it up from the list by themselves.

This guide is for contributors adding a game. Writing mods is covered in the
[modding guide](https://xk0fe.github.io/pga2k-openfairway/).

## Before you start

- **The game must be a Unity IL2CPP build.** Look for `GameAssembly.dll` and a `<Exe>_Data` folder next to the
  game's exe. The platform reaches the game only through the `il2cpp_*` functions that `GameAssembly.dll` exports. A
  Mono build (`<Exe>_Data\Managed\Assembly-CSharp.dll` and no `GameAssembly.dll`) would need a different engine layer.
- **It must be on Steam.** The installer and the launcher find games through the Steam libraries, and Play starts the
  game through Steam.
- **The [ground rules](../README.md#ground-rules) apply in full.** Some games keep parts of their code encrypted on
  disk (2K25 does). That doesn't matter: OpenFairway learns a game's code from the running game's runtime, never from
  its files. Don't decrypt, unpack or patch anything, and never unlock what the game sells: currency, store items,
  passes, DLC.

## 1. Add it to the game list

`src/Shared/games.json` is the one list of supported games. The platform and the launcher compile it in, the
installer ships a copy, and the developer tools read it. Its order is the order the launcher and the installer show
the games in.

```json
{
  "id": "pga2k27",
  "name": "PGA TOUR 2K27",
  "short": "2K27",
  "steamAppId": "<the game's Steam app id>",
  "installDir": "PGA TOUR 2K27",
  "executable": "PGATOUR2K27",
  "officialHosts": []
}
```

| Field | What it is |
|---|---|
| `id` | The name mods use for the game, in `mod.json` `"games"` and in a mod's per-game folders. Never change it once released. |
| `name`, `short` | The full name, and a short one for where space is tight. |
| `steamAppId` | The game's Steam app id, from its store page URL or `steamapps\appmanifest_<id>.acf`. Play starts the game with it. |
| `installDir` | The game's folder under `steamapps\common`. |
| `executable` | The game's exe without `.exe`. The platform uses it to tell which game it is running in, and `<executable>_Data` is the game's data folder. |
| `officialHosts` | The game's own servers. Once the offline guard has blocked name resolution in the game process, it checks that these no longer resolve, and stays inactive if one still does. |

Leave `officialHosts` empty for the first start. The guard blocks name resolution whether or not the list is empty;
the list only lets it check that the block worked. Fill it in during step 3, before the game ships.

## 2. Write its profile

Everything that knows a game's own code lives in `src/OpenFairway.Core/Games`, the game layer. Nothing above that
folder names a game class. What differs between the games is held in one profile per game, in `GameProfile.cs`.
Add a profile and register it in `For`:

```csharp
private static GameProfile Pga2K27(GameEdition edition) => new()
{
    Edition = edition,
    // ...
};

public static GameProfile? For(GameEdition edition) => edition.Id switch
{
    "pga2k25" => Pga2K25(edition),
    "pga2k23" => Pga2K23(edition),
    "pga2k27" => Pga2K27(edition),
    _ => null,
};
```

Without a profile, the platform logs "is not a game OpenFairway supports" and stays inactive. So start by copying
the profile of the game closest to the new one; step 3 corrects it. What each part means:

| Property | What to set |
|---|---|
| `AssemblyNames` | Mods and the platform ask for the game's classes by 2K25's assembly names, `HB.Golf` and `HB.Golf.Plugins`. If the game calls its assemblies something else, map the names here. 2K23 maps them to `Assembly-CSharp` and `Assembly-CSharp-firstpass`. |
| `HasOfflineBootOption` | Whether `RuntimeOptions.Online` has `forceOfflineBoot`, the game's own switch for booting offline. The career save is moved to a local file through `RuntimeOptions.ProTour.saveFileLocation` in every game. |
| `Menu.Style` | `Tiles` for a menu of artwork tiles under a banner panel (2K25). `Rows` for tabs that each hold a short list of text rows (2K23). |
| `Menu.TabCount`, `Menu.Tabs` | How many tabs the game's menu has, and where the API's Home, Play, Compete and Design tabs sit among them. |
| `Menu.GameTiles` | The internal names of the game's own tiles or rows that work offline, such as `QuickPlay_Upper` or `TopgolfLocalMatch_Upper`. |
| `Menu.TextTileName`, `Menu.CareerTileName` | Values of the game's `Localized.MainMenuPlayNow` enum that custom tiles are built as: one that draws its name as text, and the career tile's artwork. |
| `Menu.BuilderPanel`, `Menu.BuilderTakesOfflineLock` | Which of the game's tab panels builds the custom tiles, and whether its `MainMenuPanel.Trigger` takes a third argument (2K25's does: whether the tile locks itself while offline). |
| `Menu.GoOnlineRows`, `Menu.TabNames` | Rows that only offer to go online, removed when a mod asks; names for tabs whose own name no longer fits once a mod has taken them over. |
| `MatchHoles` | `OverridesPassedIn` if the local match screen keeps the course overrides it is created with (2K25). `SetOnceShown` if it resets them while it prepares, so the holes are set once it shows (2K23). |
| `Courses` | `BundleNames` if the game's course bundle can list its contents (`AssetBundle.GetAllAssetNames`, 2K25). `StaticStorageView` to ask the game's offline storage for its course view, as its course picker does (2K23). |

If the game differs in a way no property covers, add a property or an enum value to the profile and branch on that
in the game layer, never on the game's id. That way the next game only has to set it.

## 3. Start it and fill in the profile

1. Build and install: run `tools\package.ps1`, then `dist\OpenFairway\Install OpenFairway.cmd`. The installer sets
   OpenFairway up in every supported game Steam has, the new one included. Close the OpenFairway launcher first.
2. Turn on the developer console with the launcher's Settings switch, or `"DevConsole": true` in
   `<game>\OpenFairway\config.json`.
3. Run `tools\dev-cycle.ps1 -GameId pga2k27`. It copies the build in `build\` and the game's mods into the game,
   starts it through Steam with the loader on, and puts it in a small window. After each change, build the platform
   again (`tools\package.ps1` builds everything into `build\`) and run it again.

`<game>\OpenFairway\logs\openfairway.log` shows how far it got:

| Log line | Means |
|---|---|
| `Game: PGA TOUR 2K27, build ...` | The game was recognised and has a profile. |
| `Game process is offline (name resolution blocked in-process).` | The offline guard is in place. |
| `Attached to IL2CPP runtime: N assemblies loaded.` | The platform found the game's runtime. |
| `Type dump finished in ...` | The type dump is written. |
| `Main-thread scheduling is live.` | Mods can run; the mod lines follow. |
| `Switches applied (...)` | The game's own offline options were found and set. |

The **type dump** in `<game>\OpenFairway\dump\` has one `.cs` file per assembly of the running game: classes,
fields with their offsets, and method signatures, without code. The file names show what to put in
`AssemblyNames`. Search the dump for the names the other profiles use (`RuntimeOptions`, `MainMenuPanel`,
`Localized.MainMenuPlayNow`, `Endpoint`) to find the new game's counterparts. The dump is for your own use and is
never committed.

The **developer console** reads and calls into the running game over a local pipe:

```
python tools\devcon.py "statics @HB.Golf|-|Endpoint"
```

A class is written `@assembly|namespace|Name`, with `-` for an empty namespace and `*` for any assembly. `find`,
`fields`, `statics`, `methods`, `tree`, `call` and `invoke` cover most of the work. `tools/dev-lib.sh` and
`tools/dev-models.sh` build helpers on them for bash: the current screen (`screen`), its clickable items (`items`),
the main menu's tiles (`hometiles`). Set `OF_GAME_NAME` to the game's name so `errs` and `loglines` read its
`Player.log`. Only pass `call` and `invoke` pointers that `find` or `fields` printed: anything else crashes the game.

The game's **servers** for `officialHosts` are in its own `Endpoint` class, in the static `awsServer`. In 2K25 that
is `https://tgc6pc.hb-studios.com`, in 2K23 `https://tgc5pc.hb-studios.com`. Add the host names, without
`https://`. Older server names the game may still use (2K23 also lists `tgc4pc`) can go in too.

Then go down the profile and check each value against the dump and the running game: the menu's tabs and tile names
(`hometiles`, `items`), the `Localized.MainMenuPlayNow` values, the `Trigger` parameters, whether the match screen
keeps its holes, and where the course list comes from.

## 4. Make the mods run in it

A mod loads only in the games its `mod.json` `"games"` lists, and the installer and the launcher copy only those
mods into a game. Add the new id to each mod once you have tried it there. The game list in the launcher, its
Settings and the installer need no change.

Where the games differ, the bundled mods ask `ctx.Game.Id`. Offline Main Menu lays out the menu per game (rows in
2K23, tiles otherwise), and Offline Career knows which game keeps its own golfer and which screen holds the career.
Give the new game its case there. A mod that needs a separate build per game keeps it in a folder named after the
game, next to a `shared` folder; see "One mod for both games" in the modding guide.

## 5. Check it before it ships

- The installer finds the game, and the uninstaller removes exactly what it added.
- The launcher lists it under GAME and in Settings, shows Ready, and Play starts it and confirms that OpenFairway
  loaded. Play without mods starts the unmodified game.
- The log shows the offline guard in place, with `officialHosts` filled in.
- In the game: the offline main menu shows only what works offline, the mod manager opens with F8, the intros are
  skipped, and a local match starts on the course and holes chosen. MyPLAYER and MyCAREER work if the menu offers
  them, and no online error pop-ups appear.
- Nothing sold by the game is unlocked. Its developer switches for passes, DLC or store items stay untouched.

## 6. Say so in the docs

- The games table and the mods table in the [README](../README.md).
- [CHANGELOG.md](../CHANGELOG.md).
- The list of ids in the `GameInfo.Id` comment in `src/OpenFairway.API/Api.cs`.
- The "Two games" section of the modding guide, in `tools/make-docs.py`; run it to regenerate `docs/index.html`.
- The game list in the `installer/setup.ps1` header and in `installer/README.txt`.
