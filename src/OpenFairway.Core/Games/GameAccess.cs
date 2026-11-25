using OpenFairway.API;
using OpenFairway.Core.Il2Cpp;

namespace OpenFairway.Core.Games;

/// <summary>
/// The front door of the game layer. The platform starts it once the game's runtime is up and hands mods what it
/// offers; everything behind it reads the game through the game's <see cref="GameProfile"/>, so the platform and
/// the mod loader never name a game class themselves.
/// </summary>
internal sealed class GameAccess
{
    public GameProfile Profile { get; }
    public GameUi Ui { get; }
    public GameInfo Info { get; }

    private GameAccess(GameProfile profile, GameUi ui, GameInfo info)
    {
        Profile = profile;
        Ui = ui;
        Info = info;
    }

    /// <summary>Must run on a thread attached to the runtime. From then on the game's own offline options are kept
    /// set on the main thread, and the menu rules mods register are applied there.</summary>
    public static GameAccess Start(GameRuntime runtime, MainThread mainThread, Config config, GameProfile profile, string build)
    {
        mainThread.Update += () =>
        {
            if (mainThread.Frame % OfflineSwitchFrames == 0) OfflineSwitches.Enforce(runtime, config, profile);
        };
        var info = new GameInfo { Id = profile.Edition.Id, Name = profile.Edition.Name, Build = build };
        return new GameAccess(profile, new GameUi(runtime, mainThread, profile), info);
    }

    /// <summary>The game reloads its options while it boots, so they are checked again every few frames.</summary>
    private const int OfflineSwitchFrames = 10;
}
