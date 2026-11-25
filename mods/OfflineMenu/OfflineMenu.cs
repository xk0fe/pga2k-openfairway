using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    private int _menuKey = DefaultMenuKey;
    private bool _menuKeyWasDown;

    /// <summary>F8. The launcher's Settings change it (data\settings.json: "menuKey", "menuKeyCode").</summary>
    private const int DefaultMenuKey = 0x77;

    public string Id => "openfairway.offlinemenu";

    public void OnLoad(IModContext ctx)
    {
        _ctx = ctx;
        _mods = new ModsScreen(ctx, OwnFolder);
        ctx.Actions.Register("mods.open", () => _mods.Open());
        _menuKey = ReadMenuKey(ctx.DataDirectory);
        if (_menuKey != 0) ctx.MainThread.Update += PollMenuKey;
        ctx.Actions.Register("ui.back", () => ctx.Ui.BackOut());
        var ui = ctx.Ui;
        if (ctx.Game.Id == Pga2K23) LayOutRows(ui);
        else LayOutTiles(ui);

        ui.ReplaceText("OFFLINE*Reconnect*", "OPENFAIRWAY - Offline mode");

        ui.ReplacePopup("NoConnectionMsg1", "OPENFAIRWAY",
            "You are playing with the OpenFairway mod. The game runs in offline mode: online features, XP and rewards are switched off.");
        ui.ReplacePopup("OfflineModeBootAlert", null, null);
    }

    private const string Pga2K23 = "pga2k23";

    private string Asset(string name) => _ctx.ModFile(Path.Combine("assets", name));

    /// <summary>PGA TOUR 2K23: tabs of rows, four to a tab. HOME (the tab the menu opens on) has the golfer, the career
    /// and the mod manager under the wordmark; CASUAL has every way to play a round offline.</summary>
    private void LayOutRows(IGameUi ui)
    {
        ui.MainMenu.SetTabs(MenuTab.Home, MenuTab.Play, MenuTab.Design);
        ui.MainMenu.SetTiles(MenuTab.Home,
            MenuTile.Game(GameTile.MyPlayer),
            new MenuTile
            {
                Label = "MyCAREER",
                Description = "Your PGA TOUR career, from Q-School up, saved on this PC.",
                OnClick = () => OpenFeature("career.open", "MyCAREER", "Offline Career"),
            },
            new MenuTile
            {
                Label = "MODS",
                Description = "Switch mods on and off. Its key opens it anywhere in the game.",
                OnClick = () => _mods.Open(),
            });
        ui.MainMenu.SetTiles(MenuTab.Play,
            MenuTile.Game(GameTile.QuickPlay),
            new MenuTile { Existing = GameTile.Topgolf, Label = "TOPGOLF", Description = "Ten shots each at Topgolf, solo or with up to three guests." },
            MenuTile.Game(GameTile.Training));
        ui.MainMenu.SetTiles(MenuTab.Design, MenuTile.Game(GameTile.NewCourse),
            new MenuTile { Existing = GameTile.UnpublishedCourses, Label = "MY COURSES" });
        ui.MainMenu.SetBanner(new MenuBanner { Title = "OPENFAIRWAY", LogoPath = Asset("wordmark-banner.png") });
    }

    /// <summary>PGA TOUR 2K25: tiles under a banner.</summary>
    private void LayOutTiles(IGameUi ui)
    {
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
            Subtitle = "OFFLINE MODS  ·  v" + _ctx.PlatformVersion,
            LogoPath = Asset("wordmark-banner.png"),
            ImagePath = Asset("banner.png"),
            CoursePhoto = true,
            CalloutLabel = "MODS",
            OnClick = () => _mods.Open(),
        });
    }

    private static int ReadMenuKey(string dataDir)
    {
        try
        {
            var file = Path.Combine(dataDir, "settings.json");
            if (!File.Exists(file)) return DefaultMenuKey;
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            return json.RootElement.TryGetProperty("menuKeyCode", out var code) && code.TryGetInt32(out var value) && value is >= 0 and < 256
                ? value
                : DefaultMenuKey;
        }
        catch (Exception)
        {
            return DefaultMenuKey;
        }
    }

    /// <summary>The mod manager key, anywhere in the game. Only while the game's window is in front, so the key
    /// does nothing when it is pressed in another program.</summary>
    private void PollMenuKey()
    {
        var down = (GetAsyncKeyState(_menuKey) & 0x8000) != 0 && GameHasFocus();
        if (down && !_menuKeyWasDown)
        {
            var wasOpen = _mods.IsOpen;
            _mods.Toggle();
            // Before the game's menus exist (the title screen) there is nothing to draw the screen with yet.
            _ctx.Log.Info(wasOpen ? "Mod manager closed with its key."
                : _mods.IsOpen ? "Mod manager opened with its key."
                : "Mod manager key pressed before the game's menus were ready; nothing opened.");
        }
        _menuKeyWasDown = down;
    }

    private static bool GameHasFocus()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /// <summary>These features live in other mods; say which one to switch on if it is off.</summary>
    private void OpenFeature(string action, string name, string mod)
    {
        if (!_ctx.Actions.Invoke(action))
            _ctx.Ui.ShowNotice(name, $"Switch on the {mod} mod (banner, MODS) to use {name}.");
    }
}
