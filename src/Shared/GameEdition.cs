using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenFairway
{
    /// <summary>One of the games OpenFairway runs in: its name, where Steam installs it and what its files are called.
    /// The list is src/Shared/games.json, which the installer and the developer tools read as well; the launcher and
    /// the platform both compile this file.</summary>
    public sealed class GameEdition
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Short { get; set; } = "";
        public string SteamAppId { get; set; } = "";
        public string InstallDir { get; set; } = "";
        public string Executable { get; set; } = "";

        /// <summary>The game's own servers, which must stop resolving once the process is isolated.</summary>
        public string[] OfficialHosts { get; set; } = new string[0];

        public string ExeName => Executable + ".exe";
        public string DataDir => Executable + "_Data";

        public override string ToString() => Name;
    }

    public static class GameEditions
    {
        /// <summary>Mods whose mod.json names no games were written before OpenFairway ran in more than one.</summary>
        public const string FirstSupported = "pga2k25";

        public const string ResourceName = "OpenFairway.games.json";

        public static IReadOnlyList<GameEdition> All { get; private set; } = new GameEdition[0];

        public static void Use(IEnumerable<GameEdition> editions) => All = editions.Where(e => e.Id.Length > 0 && e.Executable.Length > 0).ToList();

        public static GameEdition? ById(string? id) =>
            All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The edition whose executable this is (a path or a bare file name).</summary>
        public static GameEdition? ByExecutable(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var name = Path.GetFileNameWithoutExtension(path);
            return All.FirstOrDefault(e => string.Equals(e.Executable, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The edition installed in this folder, found by its executable.</summary>
        public static GameEdition? InFolder(string? gameDir) =>
            string.IsNullOrEmpty(gameDir) ? null : All.FirstOrDefault(e => File.Exists(Path.Combine(gameDir, e.ExeName)));

        /// <summary>Whether a mod made for <paramref name="games"/> (mod.json "games") runs in this edition.</summary>
        public static bool ModRunsIn(IReadOnlyCollection<string>? games, GameEdition edition) =>
            (games == null || games.Count == 0 ? new[] { FirstSupported } : games)
            .Any(g => string.Equals(g, edition.Id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The folder of a mod's files shared by every game it supports ("shared").</summary>
        public const string SharedFolder = "shared";

        /// <summary>
        /// The games a mod folder is made for: its mod.json "games" when it lists them, otherwise the games it has a
        /// folder for (a mod that supports several games keeps each game's own files in a folder named after the game,
        /// next to a "shared" folder), otherwise the first supported game.
        /// </summary>
        public static string[] ModGames(string modDir, IReadOnlyCollection<string>? listed)
        {
            if (listed != null && listed.Count > 0) return listed.ToArray();
            var own = All.Where(e => Directory.Exists(Path.Combine(modDir, e.Id))).Select(e => e.Id).ToArray();
            return own.Length > 0 ? own : new[] { FirstSupported };
        }

        /// <summary>A mod's DLL for one game: its own copy in the game's folder if it has one, else the one at the mod's
        /// root. Null if neither exists.</summary>
        public static string? ModAssembly(string modDir, string modName, GameEdition edition)
        {
            foreach (var candidate in new[] { Path.Combine(modDir, edition.Id, modName + ".dll"), Path.Combine(modDir, modName + ".dll") })
                if (File.Exists(candidate)) return candidate;
            return null;
        }

        /// <summary>A mod folder holds a DLL for some supported game, at its root or in a game's folder.</summary>
        public static bool IsModFolder(string modDir, string modName) =>
            File.Exists(Path.Combine(modDir, modName + ".dll")) || All.Any(e => File.Exists(Path.Combine(modDir, e.Id, modName + ".dll")));

        /// <summary>Folders of a mod that belong to other games, which need not be copied into this one.</summary>
        public static bool IsOtherGamesFolder(string folderName, GameEdition edition) =>
            !string.Equals(folderName, edition.Id, StringComparison.OrdinalIgnoreCase)
            && All.Any(e => string.Equals(folderName, e.Id, StringComparison.OrdinalIgnoreCase));
    }
}
