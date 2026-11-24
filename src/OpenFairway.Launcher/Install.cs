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
        public bool Enabled = true;
    }

    internal sealed class GameBuild
    {
        public string BuildId = "";
        public bool UpdatePending;
    }

    /// <summary>Everything the launcher reads from and does to the game folder.</summary>
    internal sealed class Install
    {
        public const string ProcessName = "PGATOUR2K25";
        public readonly string Home;      // <game>\OpenFairway
        public readonly string GameDir;

        public Install(string home)
        {
            Home = Path.GetFullPath(home).TrimEnd('\\', '/');
            GameDir = Path.GetDirectoryName(Home) ?? Home;
        }

        public string ModsDir => Path.Combine(Home, "mods");
        public string LogsDir => Path.Combine(Home, "logs");
        public string GameLog => Path.Combine(LogsDir, "openfairway.log");
        public string LauncherLog => Path.Combine(LogsDir, "launcher.log");
        public string ConfigFile => Path.Combine(Home, "config.json");
        public string SettingsFile => Path.Combine(Home, "launcher.json");
        public string Ini => Path.Combine(GameDir, "doorstop_config.ini");
        public string GameExe => Path.Combine(GameDir, ProcessName + ".exe");
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
                    return File.Exists(file) ? File.ReadAllText(file).Trim() : "";
                }
                catch (Exception) { return ""; }
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
                if (!File.Exists(Path.Combine(dir, folder + ".dll"))) continue;
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
                    }
                }
                catch (Exception) { /* a broken manifest should not hide the mod */ }
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

        private static string? SteamExe()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    return key?.GetValue("SteamExe") is string exe && File.Exists(exe) ? exe : null;
            }
            catch (Exception) { return null; }
        }

        public static void OpenFolder(string path, string? select = null)
        {
            if (select != null && File.Exists(select)) Process.Start("explorer.exe", "/select,\"" + select + "\"");
            else Process.Start("explorer.exe", "\"" + path + "\"");
        }

        public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>The launcher's own settings, in launcher.json beside it.</summary>
    internal sealed class LauncherSettings
    {
        public bool KeepOpen = true;
        public bool CheckUpdates = true;
        public bool PreReleases;
        public string LaunchOptions = "";
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
