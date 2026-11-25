using System;
using System.Collections.Generic;
using OpenFairway.API;

namespace OpenFairway.Core.Games;

/// <summary>
/// What OpenFairway knows about one game's own code: where it keeps it, which of its options keep offline play
/// well-behaved, how its main menu is laid out and how it starts a match. Everything in this folder reads the game
/// through its profile, and nothing above this folder names a game class, so supporting another game of the same
/// family means writing another profile.
/// </summary>
internal sealed class GameProfile
{
    public required GameEdition Edition { get; init; }

    /// <summary>The game's own assemblies, by the names PGA TOUR 2K25 gives them. Mods and the platform ask for those
    /// names; a game that calls them something else is searched under its own names.</summary>
    public IReadOnlyDictionary<string, string> AssemblyNames { get; init; } = new Dictionary<string, string>();

    /// <summary>The game has its own "boot offline" option (RuntimeOptions.online.forceOfflineBoot).</summary>
    public bool HasOfflineBootOption { get; init; }

    public required MenuLayout Menu { get; init; }

    /// <summary>How the game's local match screen is given the holes to play.</summary>
    public required MatchHolesSetup MatchHoles { get; init; }

    /// <summary>Where the list of courses comes from.</summary>
    public required CourseSource Courses { get; init; }

    private static GameProfile Pga2K25(GameEdition edition) => new()
    {
        Edition = edition,
        HasOfflineBootOption = true,
        Menu = new MenuLayout
        {
            Style = MenuStyle.Tiles,
            TabCount = 4,
            Tabs = new() { [MenuTab.Home] = 0, [MenuTab.Play] = 1, [MenuTab.Compete] = 2, [MenuTab.Design] = 3 },
            GameTiles = new()
            {
                [GameTile.QuickPlay] = "QuickPlay_Upper",
                [GameTile.Training] = "Training_Upper",
                [GameTile.NewCourse] = "NewCourse_Upper",
                [GameTile.UnpublishedCourses] = "UnpublishedCourses",
                [GameTile.Career] = "CareerHome",
                [GameTile.Topgolf] = "TopgolfLocalMatch_Upper",
            },
            TextTileName = 13,   // Localized.MainMenuPlayNow.QuickPlay_Upper
            CareerTileName = 14, // Localized.MainMenuPlayNow.CareerHome
            BuilderPanel = 1,
            BuilderTakesOfflineLock = true,
        },
        MatchHoles = MatchHolesSetup.OverridesPassedIn,
        Courses = CourseSource.BundleNames,
    };

    private static GameProfile Pga2K23(GameEdition edition) => new()
    {
        Edition = edition,
        AssemblyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HB.Golf"] = "Assembly-CSharp",
            ["HB.Golf.Plugins"] = "Assembly-CSharp-firstpass",
        },
        Menu = new MenuLayout
        {
            Style = MenuStyle.Rows,
            TabCount = 6,
            // MyPLAYER, MyCAREER, COMPETITIVE, CASUAL, TOPGOLF, DESIGNER: the first tab is the one the menu opens on.
            Tabs = new() { [MenuTab.Home] = 0, [MenuTab.Play] = 3, [MenuTab.Compete] = 2, [MenuTab.Design] = 5 },
            GameTiles = new()
            {
                [GameTile.QuickPlay] = "LocalMatch_Upper",
                [GameTile.MyPlayer] = "MyPlayer",
                [GameTile.Topgolf] = "TopgolfLocalMatch_Upper",
                [GameTile.Training] = "Training_Upper",
                [GameTile.NewCourse] = "NewCourse_Upper",
                [GameTile.UnpublishedCourses] = "UnpublishedCourses",
            },
            TextTileName = 2,    // Localized.MainMenuPlayNow.LocalMatch_Upper
            CareerTileName = 2,
            BuilderPanel = 3,
            GoOnlineRows = ["GoOnline_Upper", "OnlineMatch_Upper"],
            TabNames = new() { [MenuTab.Home] = "HOME" },
        },
        MatchHoles = MatchHolesSetup.SetOnceShown,
        Courses = CourseSource.StaticStorageView,
    };

    /// <summary>The profile of a supported game; null for a game in the list that has none yet.</summary>
    public static GameProfile? For(GameEdition edition) => edition.Id switch
    {
        "pga2k25" => Pga2K25(edition),
        "pga2k23" => Pga2K23(edition),
        _ => null,
    };
}

internal enum MenuStyle
{
    /// <summary>PGA TOUR 2K25: four tabs of tiles, several with fixed artwork, under a banner panel.</summary>
    Tiles,

    /// <summary>PGA TOUR 2K23: a bar of tabs, each a short list of text rows under the tab's name in large outline
    /// letters.</summary>
    Rows,
}

internal enum MatchHolesSetup
{
    /// <summary>The match screen is created with the course overrides that hold the holes.</summary>
    OverridesPassedIn,

    /// <summary>The match screen resets its overrides while it prepares itself; the holes are set once it is shown.</summary>
    SetOnceShown,
}

internal enum CourseSource
{
    /// <summary>The game's data bundle names what it holds; the course records are read straight from it.</summary>
    BundleNames,

    /// <summary>The bundle cannot name its contents (PGA TOUR 2K23), so the game's own offline storage is asked for its
    /// course view, as the game's course picker does. The answer arrives a few frames later.</summary>
    StaticStorageView,
}

internal sealed class MenuLayout
{
    public required MenuStyle Style { get; init; }

    /// <summary>How many tabs the game's own menu has.</summary>
    public required int TabCount { get; init; }

    /// <summary>Where each of the API's tabs is in the game's own tab list.</summary>
    public required Dictionary<MenuTab, int> Tabs { get; init; }

    /// <summary>The game's own tiles that work offline, by their internal names.</summary>
    public required Dictionary<GameTile, string> GameTiles { get; init; }

    /// <summary>Localized.MainMenuPlayNow value a custom tile is built as: one that draws its name as text.</summary>
    public required int TextTileName { get; init; }

    /// <summary>The value a custom tile with no label of its own is built as (the career tile's artwork and name).</summary>
    public required int CareerTileName { get; init; }

    /// <summary>Which of the game's tab panels builds custom tiles (any panel can; this one's tiles look right).</summary>
    public required int BuilderPanel { get; init; }

    /// <summary>MainMenuPanel.Trigger takes a third argument: whether the tile locks itself while offline.</summary>
    public bool BuilderTakesOfflineLock { get; init; }

    /// <summary>Rows that only offer to go online, removed from every tab when a mod asks for that.</summary>
    public string[] GoOnlineRows { get; init; } = [];

    /// <summary>Names given to tabs whose own name no longer fits once a mod has taken them over.</summary>
    public Dictionary<MenuTab, string> TabNames { get; init; } = new();
}
