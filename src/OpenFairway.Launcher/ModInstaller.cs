using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace OpenFairway.Launcher
{
    internal sealed class InstallException : Exception
    {
        public InstallException(string message) : base(message) { }
    }

    internal sealed class ModPackage
    {
        public string Folder = "", Name = "", Version = "", Source = "";

        /// <summary>mod.json "games": the games it is made for (none listed means the first, PGA TOUR 2K25).</summary>
        public string[] Games = new string[0];
    }

    /// <summary>
    /// Installs mods dropped on the launcher: a .zip or a folder holding one mod (a folder with its DLL and
    /// mod.json) or several. A mod that is already installed is replaced, but its data folder (its saves and
    /// settings) is kept.
    /// </summary>
    internal static class ModInstaller
    {
        private const long MaxZip = 512L * 1024 * 1024;

        public static bool Accepts(string path) =>
            Directory.Exists(path) || (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        /// <summary>Finds the mods in a drop. A zip is unpacked to a temporary folder, returned in <paramref name="temp"/>.</summary>
        public static List<ModPackage> Inspect(string path, out string? temp)
        {
            temp = null;
            string root;
            if (Directory.Exists(path)) root = path;
            else if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(path).Length > MaxZip) throw new InstallException("That zip is larger than any mod should be (over 512 MB).");
                temp = Path.Combine(Path.GetTempPath(), "OpenFairway-mod-" + Guid.NewGuid().ToString("N"));
                root = temp;
            }
            else throw new InstallException("Drop a mod's .zip file or its folder.");

            try
            {
                if (temp != null)
                {
                    try { ZipFile.ExtractToDirectory(path, temp); }
                    catch (InvalidDataException) { throw new InstallException("That zip is damaged or not a zip file."); }
                }
                var found = new List<ModPackage>();
                Search(root, 0, found);
                if (found.Count == 0)
                    throw new InstallException("There is no mod in it. A mod is a folder with its DLL and a mod.json, such as CourseBrowser\\CourseBrowser.dll.");
                return found;
            }
            catch (Exception)
            {
                // Nothing to install from it: do not leave the unpacked copy behind.
                Cleanup(temp);
                temp = null;
                throw;
            }
        }

        private static void Search(string dir, int depth, List<ModPackage> found)
        {
            if (File.Exists(Path.Combine(dir, "mod.json")))
            {
                var dlls = Directory.GetFiles(dir, "*.dll").Where(f => !IsApi(f)).ToList();
                var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
                // A mod for several games may keep its DLLs only in the games' own folders.
                var folder = GameEditions.IsModFolder(dir, name) ? name
                    : dlls.Count == 1 ? Path.GetFileNameWithoutExtension(dlls[0]) : null;
                if (folder != null)
                {
                    var package = new ModPackage { Folder = folder, Name = folder, Source = dir };
                    try
                    {
                        if (Json.Parse(File.ReadAllText(Path.Combine(dir, "mod.json"))) is Dictionary<string, object> json)
                        {
                            package.Name = Json.Str(json, "name", folder);
                            package.Version = Json.Str(json, "version");
                            package.Games = Json.Arr(json.TryGetValue("games", out var games) ? games : null).OfType<string>().ToArray();
                        }
                    }
                    catch (Exception) { /* the platform also loads mods with a broken manifest */ }
                    package.Games = GameEditions.ModGames(dir, package.Games);
                    found.Add(package);
                    return;
                }
            }
            if (depth >= 3) return;
            foreach (var sub in Directory.GetDirectories(dir)) Search(sub, depth + 1, found);
        }

        /// <summary>A copy of the API beside a mod would load a second copy of its types; the platform has its own.</summary>
        private static bool IsApi(string file) => Path.GetFileName(file).Equals("OpenFairway.API.dll", StringComparison.OrdinalIgnoreCase);

        /// <summary>Installs a mod into one game's mods folder. Of a mod made for several games, only what that game
        /// uses is copied: the shared files and the game's own folder, not the other games'.</summary>
        public static void Install(ModPackage package, string modsDir, GameEdition edition)
        {
            Directory.CreateDirectory(modsDir);
            var target = Path.Combine(modsDir, package.Folder);
            // Staged beside the target so the final move is a rename on the same drive. The leading dot keeps the
            // platform from ever treating it as a mod.
            var staging = Path.Combine(modsDir, "." + package.Folder + ".installing");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Copy(package.Source, staging, edition);

            // The mod's DLL must be named after its folder.
            var dlls = Directory.GetFiles(staging, "*.dll").Where(f => !IsApi(f)).ToList();
            if (!File.Exists(Path.Combine(staging, package.Folder + ".dll")) && dlls.Count == 1)
                File.Move(dlls[0], Path.Combine(staging, package.Folder + ".dll"));

            if (Directory.Exists(target))
            {
                var data = Path.Combine(target, "data");
                if (Directory.Exists(data)) Copy(data, Path.Combine(staging, "data"));   // the player's saves and settings win
                Directory.Delete(target, true);
            }
            Directory.Move(staging, target);
        }

        private static void Copy(string from, string to, GameEdition? edition = null)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
                if (!IsApi(file)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(from))
                if (edition == null || !GameEditions.IsOtherGamesFolder(Path.GetFileName(dir), edition))
                    Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
        }

        public static void Cleanup(string? temp)
        {
            if (temp == null) return;
            try { Directory.Delete(temp, true); } catch (Exception) { }
        }
    }
}
