using System;
using System.IO;
using OpenFairway.API;

namespace OpenFairway.Mods;

/// <summary>
/// Runs the game's own MyCAREER and MyPLAYER offline.
///
/// Things that stop the stock game from doing that, each answered with something the game already has:
///  - Per-user documents go to the server. The game sends any document whose id starts with one of its local
///    storage prefixes to local files instead, so the session's user id is given such a prefix.
///  - The career flow asks whether the game is online. That flag is set while these screens are in use (the
///    process itself stays cut off from the network by the platform).
///  - The player save lives in the publisher's cloud. A local one is used instead: the game's built-in default
///    save the first time, then whatever the player made of it, written with the game's own serializer to a
///    file in this mod's data folder.
/// Nothing here grants currency, items or passes: the local player starts as the game's default golfer.
/// </summary>
public sealed unsafe class OfflineCareer : IFairwayMod
{
    // "commonStorage" is the prefix of the game's general-purpose local document storage.
    private const string LocalUserId = "commonStorageOpenFairway";
    private const string SaveFileName = "golfer.json";
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(2);

    private IModContext _ctx = null!;
    private IGameRuntime _rt = null!;
    private IntPtr _user, _connection, _playerSave, _screens, _settings;
    private IntPtr _localSave;       // the player save this mod keeps for the session
    private bool _active, _broken;
    private string _lastSaved = "";
    private DateTime _nextSave = DateTime.UtcNow;
    private string _playerId = "";

    public string Id => "openfairway.offlinecareer";

    public void OnLoad(IModContext ctx)
    {
        _ctx = ctx;
        _rt = ctx.Runtime;
        ctx.Actions.Register("career.open", OpenCareer);
        ctx.Actions.Register("player.open", OpenPlayer);
        ctx.Actions.Register("player.addxp", AddXp);
        ctx.Actions.Register("player.addequipmentxp", AddEquipmentXp);
        ctx.MainThread.Update += Tick;
        ctx.MainThread.Quitting += () => SavePlayer("on exit");
    }

    private bool Resolve()
    {
        if (_broken) return false;
        if (_user != IntPtr.Zero) return true;
        _user = _rt.FindClass("HB.Golf", "", "User");
        _connection = _rt.FindClass("HB.Golf", "", "ConnectionManager");
        _playerSave = _rt.FindClass("HB.Golf", "", "MyPlayerSave");
        _screens = _rt.FindClass("HB.Golf", "MVC", "ScreenContainer");
        _settings = _rt.FindClass("HB.Golf", "", "ServerSettings");
        if (_user == IntPtr.Zero || _connection == IntPtr.Zero || _playerSave == IntPtr.Zero || _screens == IntPtr.Zero)
        {
            _broken = true;
            _ctx.Log.Warn("Career types not found; offline career is unavailable (game update?).");
            _user = IntPtr.Zero;
            return false;
        }
        return true;
    }

    // ---- entry points ---------------------------------------------------------------------------------------

    /// <summary>Opens the game's career, creating the local career on first use.</summary>
    private void OpenCareer() => Open("MyCAREER", () =>
    {
        // The game's own entry point, started the way the game starts it: through its screen container.
        const string show = "Show(System.Func<System.Action<MVC.BaseController>,System.Collections.IEnumerable>)";
        var showMethod = _rt.FindMethod(_screens, show);
        var careerHome = _rt.FindMethod(_rt.FindClass("HB.Golf", "MVC", "MainMenuPlayNow"), "CareerHome", 1);
        if (showMethod == IntPtr.Zero || careerHome == IntPtr.Zero) throw new InvalidOperationException("career entry point not found");
        _rt.Invoke(showMethod, IntPtr.Zero, _rt.CreateDelegateForMethod(_rt.GetParameterClass(showMethod, 0), careerHome));
    });

    /// <summary>Opens the game's MyPLAYER home: appearance, info, outfits, golf bag and progression for the local golfer.</summary>
    private void OpenPlayer() => Open("MyPLAYER", () =>
    {
        const string show = "Show(System.Func<MVC.BaseController>)";
        var showMethod = _rt.FindMethod(_screens, show);
        var editorClass = _rt.FindClass("HB.Golf", "MVC", "EditGolferAndEquipment");
        if (showMethod == IntPtr.Zero || editorClass == IntPtr.Zero) throw new InvalidOperationException("player editor not found");
        byte canManageSaves = 0;
        var editor = _rt.NewObject(editorClass, ".ctor(System.String,System.Boolean)", _rt.NewString(LocalUserId), (IntPtr)(&canManageSaves));
        _rt.Pin(editor);
        _rt.Invoke(showMethod, IntPtr.Zero, _rt.CreateFunc(_rt.GetParameterClass(showMethod, 0), () => editor));
    });

    /// <summary>Gives the local golfer experience through the game's own function, so levels and skill points follow
    /// the game's normal progression rules. Used by story mods to reward training.</summary>
    private void AddXp(string amount)
    {
        try
        {
            if (!int.TryParse(amount, out var xp) || xp <= 0 || !Resolve()) return;
            _active = true;
            Prepare();
            var add = _rt.FindMethod(_playerSave, "IncrementXp", 2);
            if (add == IntPtr.Zero) return;
            long activeGolfer = 0; // a Nullable<int> with no value: the active golfer
            _rt.Invoke(add, _localSave, (IntPtr)(&xp), (IntPtr)(&activeGolfer));
        }
        catch (Exception e)
        {
            _ctx.Log.Warn("XP not added: " + e.Message);
        }
    }

    /// <summary>Club and ball experience for the active golf bag, through the game's own equipment progression
    /// (the levels that unlock each club's upgrades). Online the game's servers award it after a round; offline
    /// nothing does, so Offline Progression calls this.</summary>
    private void AddEquipmentXp(string amount)
    {
        try
        {
            if (!int.TryParse(amount, out var xp) || xp <= 0 || !Resolve()) return;
            _active = true;
            Prepare();
            var add = _rt.FindMethod(_playerSave, "IncrementEquipmentXp", 2);
            if (add == IntPtr.Zero) return;
            byte fromGameplay = 1;
            _rt.Invoke(add, _localSave, (IntPtr)(&xp), (IntPtr)(&fromGameplay));
        }
        catch (Exception e)
        {
            _ctx.Log.Warn("Equipment XP not added: " + e.Message);
        }
    }

    private void Open(string what, Action open)
    {
        try
        {
            if (!Resolve())
            {
                _ctx.Ui.ShowNotice(what, "This does not support the current version of the game yet.");
                return;
            }
            _active = true;
            Prepare();
            open();
            _ctx.Log.Info(what + " opened.");
        }
        catch (Exception e)
        {
            _ctx.Log.Error($"Could not open {what}: {e}");
            _ctx.Ui.ShowNotice(what, "This could not be opened. Details are in the OpenFairway log.");
        }
    }

    // ---- keeping the offline conditions in place ---------------------------------------------------------

    private void Tick()
    {
        if (_broken) return;
        var frame = _ctx.MainThread.Frame;
        try
        {
            if (!_active || frame % 15 != 0) return;
            Prepare();
            if (DateTime.UtcNow >= _nextSave) SavePlayer("periodic");
        }
        catch (Exception e)
        {
            _broken = true;
            _ctx.Log.Error("Offline career stopped after an error: " + e);
        }
    }

    private void Prepare()
    {
        // 1. Local user id.
        var current = _rt.ReadString(_rt.GetStaticField<IntPtr>(_user, "golfUserIdCache"));
        if (current != LocalUserId) _rt.SetStaticReference(_user, "golfUserIdCache", _rt.NewString(LocalUserId));

        // 2. The career's own "are we online" gate.
        if (_rt.GetStaticField<byte>(_connection, "_gameOnline") == 0) _rt.SetStaticField<byte>(_connection, "_gameOnline", 1);

        // The season's official courses ship with the game; tell it to use those copies instead of asking the server.
        if (_settings != IntPtr.Zero)
        {
            var settings = _rt.GetStaticField<IntPtr>(_settings, "currentSettings");
            if (settings != IntPtr.Zero)
            {
                var settingsClass = _rt.GetClass(settings);
                foreach (var flag in new[] { "preferOfflineCourseMetadata", "preferOfflineCourseDescription", "preferOfflineCourseThumbnail" })
                    if (_rt.GetField<byte>(settings, settingsClass, flag) == 0) _rt.SetField<byte>(settings, settingsClass, flag, 1);
            }
        }

        // 3. The local player. Some game screens try the cloud, fail and clear the player; put ours back.
        if (_localSave == IntPtr.Zero) _localSave = LoadSavedPlayer();
        if (_localSave == IntPtr.Zero) _localSave = CreateDefaultSave();
        if (_rt.GetStaticField<IntPtr>(_playerSave, "_instance") != _localSave)
            _rt.SetStaticReference(_playerSave, "_instance", _localSave);
    }

    // ---- the local player save ----------------------------------------------------------------------------

    private IntPtr CreateDefaultSave()
    {
        var save = _rt.Invoke(_rt.FindMethod(_playerSave, "GetDummyStorage", 0), IntPtr.Zero);
        if (save == IntPtr.Zero) throw new InvalidOperationException("the game returned no default player save");
        var players = _rt.ReadArray(_rt.GetField<IntPtr>(save, _playerSave, "myPlayerData"));
        if (players.Length == 0 || players[0] == IntPtr.Zero) throw new InvalidOperationException("the default player save has no golfer");
        var golfer = players[0];
        // The default save gets a fresh golfer id every time; keep one id so the career stays attached to this golfer.
        if (_playerId.Length == 0) _playerId = LoadOrCreatePlayerId(_rt.ReadString(_rt.GetField<IntPtr>(golfer, _rt.GetClass(golfer), "myPlayerId")));
        _rt.SetReference(golfer, "myPlayerId", _rt.NewString(_playerId));
        _rt.Pin(save);
        _ctx.Log.Info("Local golfer created from the game's default (" + _playerId + ").");
        return save;
    }

    private string LoadOrCreatePlayerId(string? generated)
    {
        var file = Path.Combine(_ctx.DataDirectory, "golfer-id.txt");
        if (File.Exists(file))
        {
            var saved = File.ReadAllText(file).Trim();
            if (saved.Length > 0) return saved;
        }
        var id = string.IsNullOrEmpty(generated) ? Guid.NewGuid().ToString() : generated;
        File.WriteAllText(file, id);
        return id;
    }

    private string SavePath => Path.Combine(_ctx.DataDirectory, SaveFileName);

    /// <summary>Reads the golfer saved in an earlier session, using the game's own JSON reader.</summary>
    private IntPtr LoadSavedPlayer()
    {
        try
        {
            if (!File.Exists(SavePath)) return IntPtr.Zero;
            var json = File.ReadAllText(SavePath);
            var read = _rt.FindMethod(_rt.FindClass("Newtonsoft.Json", "Newtonsoft.Json", "JsonConvert"),
                "DeserializeObject(System.String,System.Type,Newtonsoft.Json.JsonSerializerSettings)");
            if (read == IntPtr.Zero) return IntPtr.Zero;
            var save = _rt.Invoke(read, IntPtr.Zero, _rt.NewString(json), _rt.GetTypeObject(_playerSave), IntPtr.Zero);
            if (save == IntPtr.Zero || _rt.GetClassName(_rt.GetClass(save)) != "MyPlayerSave") return IntPtr.Zero;
            var players = _rt.ReadArray(_rt.GetField<IntPtr>(save, _playerSave, "myPlayerData"));
            if (players.Length == 0 || players[0] == IntPtr.Zero) return IntPtr.Zero;
            _rt.Pin(save);
            _lastSaved = json;
            _ctx.Log.Info("Local golfer loaded from the previous session.");
            return save;
        }
        catch (Exception e)
        {
            _ctx.Log.Warn("Saved golfer could not be read; starting from the default. " + e.Message);
            return IntPtr.Zero;
        }
    }

    /// <summary>Writes the golfer with the game's own serializer. Skipped when nothing changed.</summary>
    private void SavePlayer(string reason)
    {
        _nextSave = DateTime.UtcNow + SaveInterval;
        if (_localSave == IntPtr.Zero) return;
        try
        {
            var write = _rt.FindMethod(_rt.FindClass("HB.Golf.Plugins", "", "Utilities"),
                "SerializeObj(System.Object,System.Boolean,System.Boolean,System.Boolean)");
            if (write == IntPtr.Zero) return;
            byte no = 0;
            var json = _rt.ReadString(_rt.Invoke(write, IntPtr.Zero, _localSave, (IntPtr)(&no), (IntPtr)(&no), (IntPtr)(&no)));
            if (string.IsNullOrEmpty(json) || json == _lastSaved) return;
            var temp = SavePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, SavePath, true);
            _lastSaved = json;
            _ctx.Log.Info($"Local golfer saved ({reason}).");
        }
        catch (Exception e)
        {
            _ctx.Log.Warn("Local golfer not saved: " + e.Message);
        }
    }
}
