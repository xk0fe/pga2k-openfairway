using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenFairway.Core.Games;

/// <summary>Which game this process is, decided once from its executable before anything else runs.</summary>
internal static class CurrentGame
{
    public static GameEdition? Edition { get; private set; }

    /// <summary>What the game layer knows about this game's own code; null for a game it has no profile for.</summary>
    public static GameProfile? Profile { get; private set; }

    /// <summary>Loads the list of supported games and finds this process's among them; null if it is not one.</summary>
    public static GameEdition? Detect(string exePath)
    {
        using (var stream = typeof(CurrentGame).Assembly.GetManifestResourceStream(GameEditions.ResourceName)
                            ?? throw new InvalidOperationException("The list of supported games is missing from the build."))
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            GameEditions.Use(JsonSerializer.Deserialize<GameEdition[]>(stream, options) ?? []);
        }
        Edition = GameEditions.ByExecutable(exePath);
        Profile = Edition == null ? null : GameProfile.For(Edition);
        return Edition;
    }

    /// <summary>The game's build: its Unity build id when it has one, otherwise the size and date of its code.</summary>
    public static string Build(string gameDir)
    {
        if (Edition == null) return "unknown";
        var data = Path.Combine(gameDir, Edition.DataDir);
        var bootConfig = Path.Combine(data, "boot.config");
        if (File.Exists(bootConfig))
        {
            var guid = File.ReadAllLines(bootConfig).FirstOrDefault(l => l.StartsWith("build-guid="));
            if (guid != null) return guid["build-guid=".Length..].Trim();
        }
        var code = new FileInfo(Path.Combine(data, "GameAssembly.dll"));
        return code.Exists ? $"{code.Length}-{code.LastWriteTimeUtc:yyyyMMddHHmmss}" : "unknown";
    }
}
