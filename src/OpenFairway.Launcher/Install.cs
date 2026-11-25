using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OpenFairway.Launcher
{
    internal sealed class ModInfo
    {
        public string Folder = "";
        public string Directory = "";
        public string Name = "";
        public string Version = "";
        public string Author = "";
        public string Description = "";
        /// <summary>mod.json "openfairway": the oldest OpenFairway the mod works with.</summary>
        public string Platform = "";
        /// <summary>mod.json "requires": folders of the mods it needs.</summary>
        public string[] Requires = new string[0];
        /// <summary>mod.json "games": the games it is made for (none listed means PGA TOUR 2K25, the first).</summary>
        public string[] Games = new string[0];
        public bool Enabled = true;
    }

    /// <summary>One supported game as a scan of the Steam libraries found it.</summary>
    internal sealed class GameScan
    {
        public GameEdition Edition = new GameEdition();

        /// <summary>Where Steam has the game; null if it is not installed.</summary>
        public string? GameDir;

        /// <summary>OpenFairway in that game; null if it is not set up there.</summary>
        public Install? Install;
    }

    internal sealed class GameBuild
    {
        public string BuildId = "";
        public bool UpdatePending;
    }

    /// <summary>Everything the launcher reads from and does to one game's folder. Each supported game Steam has gets
    /// its own copy of OpenFairway (its mods are made for that game); <see cref="FindAll"/> finds them all.</summary>
    internal sealed class Install
    {
        public readonly string Home;      // <game>\OpenFairway
        public readonly string GameDir;
        public readonly GameEdition Edition;

        public Install(string home)
        {
            Home = Path.GetFullPath(home).TrimEnd('\\', '/');
            GameDir = Path.GetDirectoryName(Home) ?? Home;
            Edition = GameEditions.InFolder(GameDir) ?? GameEditions.ById(GameEditions.FirstSupported) ?? new GameEdition
            {
                Id = GameEditions.FirstSupported, Name = "PGA TOUR 2K25", Short = "2K25", Executable = "PGATOUR2K25",
            };
        }

        public string ProcessName => Edition.Executable;

        /// <summary>The installer's copy of every bundled mod, for setting OpenFairway up in another game later.</summary>
        public string BundledModsDir => Path.Combine(Home, "bundled", "mods");

        /// <summary>The supported games, each with where Steam has it and whether OpenFairway is in it.</summary>
        public static List<GameScan> Scan(IEnumerable<Install> known)
        {
            var installs = known.ToList();
            var result = new List<GameScan>();
            var libraries = Steam.Libraries();
            foreach (var edition in GameEditions.All)
            {
                var install = installs.FirstOrDefault(i => i.Edition.Id == edition.Id);
                var gameDir = install?.GameDir ?? libraries
                    .Select(l => Path.Combine(l, "steamapps", "common", edition.InstallDir))
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, edition.ExeName)));
                if (install == null && gameDir != null && File.Exists(Path.Combine(gameDir, "OpenFairway", "core", "OpenFairway.Core.dll")))
                    install = new Install(Path.Combine(gameDir, "OpenFairway"));
                result.Add(new GameScan { Edition = edition, GameDir = gameDir, Install = install });
            }
            return result;
        }

        /// <summary>Sets OpenFairway up in another game from this install's own files, as the installer would: the
        /// platform, its .NET runtime, the launcher, the loader (switched off) and the bundled mods made for that game.
        /// The game's own files stay as Steam installed them, and the uninstaller removes exactly what is added.</summary>
        public Install AddTo(GameEdition edition, string gameDir)
        {
            var running = Process.GetProcessesByName(edition.Executable);
            foreach (var p in running) p.Dispose();
            if (running.Length > 0) throw new InstallException($"Close {edition.Name} first.");
            foreach (var other in new[] { "version.dll", "winhttp.dll" })
                if (File.Exists(Path.Combine(gameDir, other)))
                    throw new InstallException($"Another mod loader ({other}) is in {edition.Name}'s folder. Remove it first.");
            if (!File.Exists(LoaderDll) || !CoreInstalled)
                throw new InstallException($"OpenFairway in {Edition.Name} is incomplete, so it cannot be copied. Run Install OpenFairway.cmd instead.");

            var home = Path.Combine(gameDir, "OpenFairway");
            Directory.CreateDirectory(home);
            CopyFolder(Path.Combine(Home, "core"), Path.Combine(home, "core"));
            CopyFolder(Path.Combine(Home, "dotnet"), Path.Combine(home, "dotnet"));
            foreach (var file in LauncherFiles) File.Copy(Path.Combine(Home, file), Path.Combine(home, file), true);
            var installed = new List<string> { "version.dll", "doorstop_config.ini", @"OpenFairway\core", @"OpenFairway\dotnet", @"OpenFairway\appid.txt" };
            installed.AddRange(LauncherFiles.Select(f => @"OpenFairway\" + f));

            foreach (var (folder, source) in BundledMods())
            {
                if (!GameEditions.ModRunsIn(GameEditions.ModGames(source, ModGames(source)), edition)) continue;
                // A mod's data folder holds what it saved in this game; the other game starts its own. Of a mod made
                // for several games, only that game's own folder is copied, with the shared files.
                var copied = Path.Combine(home, "mods", folder);
                CopyFolder(source, copied, skip: name => name.Equals("data", StringComparison.OrdinalIgnoreCase) || GameEditions.IsOtherGamesFolder(name, edition));
                installed.AddRange(ModFiles(copied, folder, edition));
            }
            if (Directory.Exists(BundledModsDir))
            {
                CopyFolder(BundledModsDir, Path.Combine(home, "bundled", "mods"));
                installed.Add(@"OpenFairway\bundled");
            }

            File.WriteAllText(Path.Combine(home, "appid.txt"), edition.SteamAppId, Encoding.ASCII);
            File.WriteAllText(Path.Combine(gameDir, "doorstop_config.ini"), LoaderConfig, Encoding.ASCII);
            File.WriteAllLines(Path.Combine(home, "installed-files.txt"), installed, new UTF8Encoding(true));
            // The loader goes in last, once everything it starts is in place.
            File.Copy(LoaderDll, Path.Combine(gameDir, "version.dll"), true);
            return new Install(home);
        }

        private static readonly string[] LauncherFiles = { "OpenFairway.exe", "OpenFairway.exe.config" };

        /// <summary>What the uninstaller removes of a bundled mod (its saved data stays): its DLL for this game and, for a
        /// mod made for several games, the game's own folder and the shared one.</summary>
        private static IEnumerable<string> ModFiles(string modDir, string folder, GameEdition edition)
        {
            var prefix = @"OpenFairway\mods\" + folder + @"\";
            var dll = GameEditions.ModAssembly(modDir, folder, edition);
            if (dll != null) yield return prefix + dll.Substring(modDir.Length + 1);
            foreach (var part in new[] { edition.Id, GameEditions.SharedFolder })
                if (System.IO.Directory.Exists(Path.Combine(modDir, part))) yield return prefix + part;
        }

        /// <summary>The loader's settings, as the installer writes them: off until the launcher starts the game.</summary>
        private const string LoaderConfig =
            "[General]\r\nenabled=false\r\ntarget_assembly=OpenFairway\\core\\OpenFairway.Core.dll\r\nredirect_output_log=false\r\n" +
            "boot_config_override=\r\nignore_disable_switch=false\r\n\r\n[Il2Cpp]\r\ncoreclr_path=OpenFairway\\dotnet\\coreclr.dll\r\n" +
            "corlib_dir=OpenFairway\\dotnet\r\n";

        /// <summary>The bundled mods to copy: the installer's copy of them, or (from an older installer) the ones it
        /// installed into this game.</summary>
        private IEnumerable<(string Folder, string Source)> BundledMods()
        {
            if (System.IO.Directory.Exists(BundledModsDir))
                return System.IO.Directory.GetDirectories(BundledModsDir).Select(d => (Path.GetFileName(d), d));
            var manifest = Path.Combine(Home, "installed-files.txt");
            if (!File.Exists(manifest)) return Enumerable.Empty<(string, string)>();
            return File.ReadAllLines(manifest)
                .Select(l => Regex.Match(l.Trim(), @"^OpenFairway\\mods\\([^\\]+)\\.+\.dll$", RegexOptions.IgnoreCase))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(f => (f, Path.Combine(ModsDir, f)))
                .Where(x => System.IO.Directory.Exists(x.Item2));
        }

        private static string[] ModGames(string modDir)
        {
            try
            {
                var manifest = Path.Combine(modDir, "mod.json");
                return File.Exists(manifest) && Json.Parse(File.ReadAllText(manifest)) is Dictionary<string, object> json
                    ? Json.Arr(json.TryGetValue("games", out var g) ? g : null).OfType<string>().ToArray()
                    : new string[0];
            }
            catch (Exception) { return new string[0]; }
        }

        private static void CopyFolder(string source, string target, Func<string, bool>? skip = null)
        {
            System.IO.Directory.CreateDirectory(target);
            foreach (var file in System.IO.Directory.GetFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (var dir in System.IO.Directory.GetDirectories(source))
                if (skip == null || !skip(Path.GetFileName(dir)))
                    CopyFolder(dir, Path.Combine(target, Path.GetFileName(dir)));
        }

        /// <summary>This install and OpenFairway in every other supported game Steam has, in the order of the list of
        /// games (src/Shared/games.json).</summary>
        public static List<Install> FindAll(Install current)
        {
            var found = new List<Install> { current };
            foreach (var library in Steam.Libraries())
                foreach (var edition in GameEditions.All)
                {
                    var gameDir = Path.Combine(library, "steamapps", "common", edition.InstallDir);
                    var home = Path.Combine(gameDir, "OpenFairway");
                    if (found.Any(i => string.Equals(i.Home, Path.GetFullPath(home), StringComparison.OrdinalIgnoreCase))) continue;
                    if (File.Exists(Path.Combine(home, "core", "OpenFairway.Core.dll")) && File.Exists(Path.Combine(gameDir, edition.ExeName)))
                        found.Add(new Install(home));
                }
            var order = GameEditions.All.Select(e => e.Id).ToList();
            return found.OrderBy(i => order.IndexOf(i.Edition.Id) is var at && at >= 0 ? at : order.Count).ToList();
        }

        public string ModsDir => Path.Combine(Home, "mods");
        public string LogsDir => Path.Combine(Home, "logs");
        public string GameLog => Path.Combine(LogsDir, "openfairway.log");
        public string LauncherLog => Path.Combine(LogsDir, "launcher.log");
        public string ConfigFile => Path.Combine(Home, "config.json");
        public string SettingsFile => Path.Combine(Home, "launcher.json");
        public string Ini => Path.Combine(GameDir, "doorstop_config.ini");
        public string GameExe => Path.Combine(GameDir, Edition.ExeName);
        public string LoaderDll => Path.Combine(GameDir, "version.dll");
        public string CoreDll => Path.Combine(Home, "core", "OpenFairway.Core.dll");
        private string DisabledFile => Path.Combine(Home, "disabled-mods.txt");

        public string AppId
        {
            get
            {
                try
                {
                    var file = Path.Combine(Home, "appid.txt");
                    var written = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
                    return written.Length > 0 ? written : Edition.SteamAppId;
                }
                catch (Exception) { return Edition.SteamAppId; }
            }
        }

        public bool GameInstalled => File.Exists(GameExe);
        public bool CoreInstalled => File.Exists(CoreDll) && File.Exists(Path.Combine(Home, "dotnet", "coreclr.dll"));
        public bool LoaderInstalled => File.Exists(LoaderDll) && File.Exists(Ini);
        /// <summary>The installer never uses winhttp.dll, so one there belongs to another loader.</summary>
        public bool OtherLoader => File.Exists(Path.Combine(GameDir, "winhttp.dll"));

        public Process? GameProcess()
        {
            var found = Process.GetProcessesByName(ProcessName);
            for (var i = 1; i < found.Length; i++) found[i].Dispose();
            return found.Length > 0 ? found[0] : null;
        }

        public bool GameRunning
        {
            get
            {
                var found = Process.GetProcessesByName(ProcessName);
                foreach (var p in found) p.Dispose();
                return found.Length > 0;
            }
        }

        public string CoreVersion()
        {
            try { return AssemblyName.GetAssemblyName(CoreDll).Version?.ToString(3) ?? ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>The game's Steam build, from Steam's own manifest beside the game.</summary>
        public GameBuild? ReadGameBuild()
        {
            var appId = AppId;
            if (appId.Length == 0) return null;
            try
            {
                var steamapps = Path.GetDirectoryName(Path.GetDirectoryName(GameDir));
                if (steamapps == null) return null;
                var manifest = Path.Combine(steamapps, "appmanifest_" + appId + ".acf");
                if (!File.Exists(manifest)) return null;
                var text = File.ReadAllText(manifest);
                var build = Regex.Match(text, "\"buildid\"\\s+\"(\\d+)\"").Groups[1].Value;
                var flags = int.TryParse(Regex.Match(text, "\"StateFlags\"\\s+\"(\\d+)\"").Groups[1].Value, out var f) ? f : 4;
                // Steam's app state bits: 2 update required, 256 update running, 1024 update started.
                return new GameBuild { BuildId = build, UpdatePending = (flags & (2 | 256 | 1024)) != 0 };
            }
            catch (Exception) { return null; }
        }

        public List<ModInfo> LoadMods()
        {
            var disabled = new HashSet<string>(File.Exists(DisabledFile) ? File.ReadAllLines(DisabledFile).Select(l => l.Trim()) : new string[0], StringComparer.OrdinalIgnoreCase);
            var mods = new List<ModInfo>();
            if (!System.IO.Directory.Exists(ModsDir)) return mods;
            foreach (var dir in System.IO.Directory.GetDirectories(ModsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var folder = Path.GetFileName(dir);
                if (!GameEditions.IsModFolder(dir, folder)) continue;
                var mod = new ModInfo { Folder = folder, Directory = dir, Name = folder, Enabled = !disabled.Contains(folder) };
                try
                {
                    var manifest = Path.Combine(dir, "mod.json");
                    if (File.Exists(manifest) && Json.Parse(File.ReadAllText(manifest)) is Dictionary<string, object> json)
                    {
                        mod.Name = Json.Str(json, "name", folder);
                        mod.Version = Json.Str(json, "version");
                        mod.Author = Json.Str(json, "author");
                        mod.Description = Json.Str(json, "description");
                        mod.Platform = Json.Str(json, "openfairway");
                        mod.Requires = Json.Arr(json.TryGetValue("requires", out var r) ? r : null).OfType<string>().ToArray();
                        mod.Games = Json.Arr(json.TryGetValue("games", out var g) ? g : null).OfType<string>().ToArray();
                    }
                }
                catch (Exception) { /* a broken manifest should not hide the mod */ }
                // As in the game: a mod made for another game is not listed, it can be neither on nor off here.
                mod.Games = GameEditions.ModGames(dir, mod.Games);
                if (!GameEditions.ModRunsIn(mod.Games, Edition)) continue;
                mods.Add(mod);
            }
            return mods;
        }

        public void SaveDisabled(IEnumerable<ModInfo> mods) =>
            File.WriteAllLines(DisabledFile, mods.Where(m => !m.Enabled).Select(m => m.Folder).ToArray());

        public void SetLoader(bool on)
        {
            var lines = File.ReadAllLines(Ini).Select(l => l.StartsWith("enabled=") ? "enabled=" + (on ? "true" : "false") : l).ToArray();
            File.WriteAllLines(Ini, lines, Encoding.ASCII);
        }

        /// <summary>Starts the game through Steam. Launch options go through Steam's -applaunch, which passes them
        /// on to the game.</summary>
        public void StartGame(string options)
        {
            var appId = AppId;
            options = options.Trim();
            var steam = SteamExe();
            if (appId.Length > 0 && options.Length > 0 && steam != null)
                Process.Start(new ProcessStartInfo(steam, "-applaunch " + appId + " " + options) { UseShellExecute = false });
            else if (appId.Length > 0)
                Process.Start(new ProcessStartInfo("steam://rungameid/" + appId) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(GameExe, options) { WorkingDirectory = GameDir, UseShellExecute = true });
        }

        /// <summary>Asks Steam to check the game's files and repair any that differ (it leaves added files alone).</summary>
        public void VerifyGameFiles() => Process.Start(new ProcessStartInfo("steam://validate/" + AppId) { UseShellExecute = true });

        private static string? SteamExe() => Steam.Exe();

        public static void OpenFolder(string path, string? select = null)
        {
            if (select != null && File.Exists(select)) Process.Start("explorer.exe", "/select,\"" + select + "\"");
            else Process.Start("explorer.exe", "\"" + path + "\"");
        }

        public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    internal static class Steam
    {
        private static string? Value(string name)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    return key?.GetValue(name) as string;
            }
            catch (Exception) { return null; }
        }

        public static string? Exe() => Value("SteamExe") is string exe && File.Exists(exe) ? exe : null;

        /// <summary>Every Steam library folder: Steam's own and the ones listed in its libraryfolders.vdf.</summary>
        public static List<string> Libraries()
        {
            var libraries = new List<string>();
            var steam = Value("SteamPath");
            if (string.IsNullOrEmpty(steam)) return libraries;
            libraries.Add(Path.GetFullPath(steam));
            try
            {
                var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), LibraryPathPattern))
                        libraries.Add(Path.GetFullPath(m.Groups[1].Value.Replace(@"\\", @"\")));
            }
            catch (Exception) { /* Steam's own library is still searched */ }
            return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>A library's line in libraryfolders.vdf: "path" then the folder, with its backslashes doubled.</summary>
        private const string LibraryPathPattern = @"""path""\s+""(.+?)""";
    }

    /// <summary>The supported games (src/Shared/games.json), built into the launcher.</summary>
    internal static class GameCatalog
    {
        public static void Load()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(GameEditions.ResourceName))
            using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("The list of supported games is missing from the build.")))
            {
                var editions = Json.Arr(Json.Parse(reader.ReadToEnd())).OfType<Dictionary<string, object>>().Select(e => new GameEdition
                {
                    Id = Json.Str(e, "id"),
                    Name = Json.Str(e, "name"),
                    Short = Json.Str(e, "short"),
                    SteamAppId = Json.Str(e, "steamAppId"),
                    InstallDir = Json.Str(e, "installDir"),
                    Executable = Json.Str(e, "executable"),
                    OfficialHosts = Json.Arr(e.TryGetValue("officialHosts", out var hosts) ? hosts : null).OfType<string>().ToArray(),
                });
                GameEditions.Use(editions);
            }
        }
    }

    /// <summary>The launcher's own settings, in launcher.json beside it.</summary>
    internal sealed class LauncherSettings
    {
        public bool KeepOpen = true;
        public bool CheckUpdates = true;
        public bool PreReleases;
        public string LaunchOptions = "";

        /// <summary>The game picked last. Only the install the launcher was started from (its shortcut's) keeps it.</summary>
        public string Game = "";
        private string _path = "";

        public static LauncherSettings Load(string path)
        {
            var settings = new LauncherSettings { _path = path };
            try
            {
                if (File.Exists(path) && Json.Parse(File.ReadAllText(path)) is Dictionary<string, object> json)
                {
                    settings.KeepOpen = Json.Bool(json, "keepOpen", true);
                    settings.CheckUpdates = Json.Bool(json, "checkUpdates", true);
                    settings.PreReleases = Json.Bool(json, "preReleases", false);
                    settings.LaunchOptions = Json.Str(json, "launchOptions");
                    settings.Game = Json.Str(json, "game");
                }
            }
            catch (Exception) { /* defaults */ }
            return settings;
        }

        public void Save()
        {
            var json = new Dictionary<string, object>
            {
                ["keepOpen"] = KeepOpen,
                ["checkUpdates"] = CheckUpdates,
                ["preReleases"] = PreReleases,
                ["launchOptions"] = LaunchOptions,
            };
            if (Game.Length > 0) json["game"] = Game;
            File.WriteAllText(_path, Json.Write(json) + "\n", new UTF8Encoding(false));
        }
    }

    /// <summary>The platform's config.json, edited in place so keys the launcher does not know are kept.</summary>
    internal sealed class CoreConfig
    {
        private readonly string _path;
        private readonly Dictionary<string, object> _values;

        private CoreConfig(string path, Dictionary<string, object> values)
        {
            _path = path;
            _values = values;
        }

        public static CoreConfig Load(string path)
        {
            try
            {
                if (File.Exists(path) && Json.Parse(File.ReadAllText(path)) is Dictionary<string, object> json) return new CoreConfig(path, json);
            }
            catch (Exception) { /* Core keeps its defaults for a broken file; so does the launcher */ }
            return new CoreConfig(path, new Dictionary<string, object>());
        }

        public bool DevConsole { get => Json.Bool(_values, "DevConsole", false); set => _values["DevConsole"] = value; }
        public string DumpTypes { get => Json.Str(_values, "DumpTypes", "auto"); set => _values["DumpTypes"] = value; }
        public bool DetailedLog { get => Json.Str(_values, "LogLevel", "normal").Equals("detailed", StringComparison.OrdinalIgnoreCase); set => _values["LogLevel"] = value ? "detailed" : "normal"; }

        public void Save() => File.WriteAllText(_path, Json.Write(_values) + "\n", new UTF8Encoding(false));
    }

    /// <summary>The key that opens the in-game mod manager, kept by the Offline Main Menu mod in its data folder.</summary>
    internal static class MenuKey
    {
        public const string Mod = "OfflineMenu";
        public static bool Available(Install install) => Directory.Exists(Path.Combine(install.ModsDir, Mod));
        private static string File(Install install) => Path.Combine(install.ModsDir, Mod, "data", "settings.json");

        public static string Read(Install install)
        {
            try
            {
                var file = File(install);
                if (System.IO.File.Exists(file) && Json.Parse(System.IO.File.ReadAllText(file)) is Dictionary<string, object> json)
                    return Json.Str(json, "menuKey", "F8");
            }
            catch (Exception) { }
            return "F8";
        }

        public static void Write(Install install, Keys key)
        {
            var file = File(install);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var json = System.IO.File.Exists(file) && Json.Parse(System.IO.File.ReadAllText(file)) is Dictionary<string, object> existing
                ? existing
                : new Dictionary<string, object>();
            json["menuKey"] = KeyNames.Name(key);
            json["menuKeyCode"] = (int)key;
            System.IO.File.WriteAllText(file, Json.Write(json) + "\n", new UTF8Encoding(false));
        }
    }

    internal static class KeyNames
    {
        /// <summary>Keys the game does not use for play and that are on most keyboards. Letters and the arrows
        /// would also move the golfer; F12 takes a Steam screenshot.</summary>
        public static bool Allowed(Keys key) =>
            (key >= Keys.F1 && key <= Keys.F11) || (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            || key == Keys.Insert || key == Keys.Delete || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown
            || key == Keys.Pause || key == Keys.Scroll || key == Keys.Oemtilde
            || key == Keys.Multiply || key == Keys.Add || key == Keys.Subtract || key == Keys.Divide || key == Keys.Decimal;

        public static string Name(Keys key)
        {
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return "Num " + (key - Keys.NumPad0);
            switch (key)
            {
                case Keys.PageUp: return "Page Up";
                case Keys.PageDown: return "Page Down";
                case Keys.Scroll: return "Scroll Lock";
                case Keys.Oemtilde: return "`";
                case Keys.Multiply: return "Num *";
                case Keys.Add: return "Num +";
                case Keys.Subtract: return "Num -";
                case Keys.Divide: return "Num /";
                case Keys.Decimal: return "Num .";
                default: return key.ToString();
            }
        }
    }

    internal static class Json
    {
        private static JavaScriptSerializer Serializer => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

        public static object? Parse(string text) => Serializer.DeserializeObject(text.TrimStart('\uFEFF'));

        public static string Str(Dictionary<string, object>? json, string key, string fallback = "") =>
            json != null && json.TryGetValue(key, out var value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback : fallback;

        public static bool Bool(Dictionary<string, object>? json, string key, bool fallback) =>
            json != null && json.TryGetValue(key, out var value) && value is bool b ? b : fallback;

        public static long Long(Dictionary<string, object>? json, string key) =>
            json != null && json.TryGetValue(key, out var value) && value != null && long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var n) ? n : 0;

        public static IEnumerable<object> Arr(object? value) =>
            value is object[] array ? array : value is System.Collections.ArrayList list ? list.Cast<object>() : Enumerable.Empty<object>();

        /// <summary>Indented JSON, so the files stay easy to read and edit by hand.</summary>
        public static string Write(object? value, string pad = "")
        {
            var inner = pad + "  ";
            switch (value)
            {
                case null: return "null";
                case string s: return Serializer.Serialize(s);
                case bool b: return b ? "true" : "false";
                case IDictionary<string, object> map:
                    if (map.Count == 0) return "{}";
                    return "{\n" + string.Join(",\n", map.Select(kv => inner + Serializer.Serialize(kv.Key) + ": " + Write(kv.Value, inner))) + "\n" + pad + "}";
                case System.Collections.IEnumerable list:
                    var items = list.Cast<object?>().ToList();
                    if (items.Count == 0) return "[]";
                    if (items.All(i => !(i is IDictionary<string, object>) && !(i is System.Collections.IEnumerable) || i is string))
                        return "[" + string.Join(", ", items.Select(i => Write(i, inner))) + "]";
                    return "[\n" + string.Join(",\n", items.Select(i => inner + Write(i, inner))) + "\n" + pad + "]";
                case IFormattable number: return number.ToString(null, CultureInfo.InvariantCulture);
                default: return Serializer.Serialize(value.ToString());
            }
        }
    }
}
