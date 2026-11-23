using OpenFairway.API;

namespace OpenFairway.Mods;

/// <summary>
/// Reworks the main menu for offline play: only things that work offline are shown, the custom entries sit on
/// the HOME tab, the "Go Online" prompt is gone, the offline alert becomes an OpenFairway notice, and the MODS
/// tile opens an in-game mod manager.
/// </summary>
public sealed class OfflineMenu : IFairwayMod
{
    private const string OwnFolder = "OfflineMenu";
    private IModContext _ctx = null!;
    private ModsScreen _mods = null!;

    public string Id => "openfairway.offlinemenu";

    public void OnLoad(IModContext ctx)
    {
        _ctx = ctx;
        _mods = new ModsScreen(ctx, OwnFolder);
        ctx.Actions.Register("mods.open", () => _mods.Open());
        ctx.Actions.Register("ui.back", () => ctx.Ui.BackOut());
        var ui = ctx.Ui;

        ui.MainMenu.SetTabs(MenuTab.Home, MenuTab.Design);
        // HOME fills top-left, bottom-left, top-right, bottom-right.
        ui.MainMenu.SetTiles(MenuTab.Home,
            new MenuTile
            {
                Label = "THE CLIMB",
                Description = "Story career: from club amateur to the World Tour.",
                OnClick = () => OpenFeature("climb.open", "THE CLIMB", "The Climb"),
            },
            MenuTile.Game(GameTile.QuickPlay),
            new MenuTile
            {
                OnClick = () => OpenFeature("career.open", "MyCAREER", "Offline Career"),
            },
            new MenuTile
            {
                Label = "MyPLAYER",
                Description = "Create and edit your golfer.",
                OnClick = () => OpenFeature("player.open", "MyPLAYER", "Offline Career"),
            });
        ui.MainMenu.SetTiles(MenuTab.Design, MenuTile.Game(GameTile.NewCourse),
            new MenuTile { Existing = GameTile.UnpublishedCourses, Label = "MY COURSES" }, MenuTile.Game(GameTile.Training));
        ui.MainMenu.SetBanner(new MenuBanner
        {
            Title = "OPENFAIRWAY",
            Subtitle = "OFFLINE MODS  ·  v" + ctx.PlatformVersion,
            LogoPath = System.IO.Path.Combine(ctx.ModDirectory, "assets", "wordmark-banner.png"),
            ImagePath = System.IO.Path.Combine(ctx.ModDirectory, "assets", "banner.png"),
            CoursePhoto = true,
            CalloutLabel = "MODS",
            OnClick = () => _mods.Open(),
        });

        ui.ReplaceText("OFFLINE*Reconnect*", "OPENFAIRWAY - Offline mode");

        ui.ReplacePopup("NoConnectionMsg1", "OPENFAIRWAY",
            "You are playing with the OpenFairway mod. The game runs in offline mode: online features, XP and rewards are switched off.");
        ui.ReplacePopup("OfflineModeBootAlert", null, null);
    }

    /// <summary>These features live in other mods; say which one to switch on if it is off.</summary>
    private void OpenFeature(string action, string name, string mod)
    {
        if (!_ctx.Actions.Invoke(action))
            _ctx.Ui.ShowNotice(name, $"Switch on the {mod} mod (banner, MODS) to use {name}.");
    }
}
