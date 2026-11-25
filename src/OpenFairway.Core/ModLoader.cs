using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using OpenFairway.API;

namespace OpenFairway.Core;

/// <summary>Finds the installed mods, loads the ones that are switched on, and keeps the on/off switches.</summary>
internal sealed class ModLoader : IModManager, IActionRegistry
{
    private readonly Dictionary<string, Action> _actions = new();

    private readonly Dictionary<string, Action<string>> _argumentActions = new();

    public void Register(string name, Action action) => _actions[name] = action;

    public void Register(string name, Action<string> action) => _argumentActions[name] = action;

    public bool Invoke(string name)
    {
        if (!_actions.TryGetValue(name, out var action)) return false;
        action();
        return true;
    }

    public bool Invoke(string name, string argument)
    {
        if (!_argumentActions.TryGetValue(name, out var action)) return false;
        action(argument);
        return true;
    }

    private readonly string _modsDir, _disabledFile;
    private readonly List<ModEntry> _installed = new();

    /// <summary>Switched-off mods that are not listed in this game (made for another one, or no longer installed);
    /// kept in the switch file so saving does not switch them back on.</summary>
    private readonly HashSet<string> _otherDisabled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For the developer console.</summary>
    internal static ModLoader? Current;

    public ModLoader(string modsDir)
    {
        Current = this;
        _modsDir = modsDir;
        // The launcher and the in-game mods menu share this file: one mod folder name per line for every mod that is off.
        _disabledFile = Path.Combine(Path.GetDirectoryName(modsDir)!, "disabled-mods.txt");
    }

    public IReadOnlyList<ModEntry> Installed => _installed;

    public void SetEnabled(ModEntry mod, bool enabled)
    {
        mod.EnabledNextStart = enabled;
        try
        {
            File.WriteAllLines(_disabledFile, _installed.Where(m => !m.EnabledNextStart).Select(m => m.Folder).Concat(_otherDisabled));
        }
        catch (Exception e)
        {
            Log.Error("Could not save mod switches: " + e.Message);
        }
    }

    /// <summary>Loads every mods/&lt;name&gt;/&lt;name&gt;.dll that is switched on and calls OnLoad on each IFairwayMod in it.</summary>
    public void LoadAll(IGameRuntime runtime, IMainThread mainThread, IGameUi ui, GameInfo game)
    {
        Directory.CreateDirectory(_modsDir);
        var disabled = File.Exists(_disabledFile)
            ? File.ReadAllLines(_disabledFile).Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        var toLoad = new List<(string Dir, string Dll)>();
        var edition = Games.CurrentGame.Edition;
        foreach (var dir in Directory.GetDirectories(_modsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetFileName(dir);
            if (!GameEditions.IsModFolder(dir, folder)) continue;
            // Mods reach into the game's own code, so one made for another game would fail or worse. It is not
            // listed here at all: in this game it is neither on nor off.
            var games = GameEditions.ModGames(dir, ReadRequirements(dir).Games);
            if (edition != null && !GameEditions.ModRunsIn(games, edition))
            {
                Log.Info($"Mod {folder} left out: it is made for {string.Join(", ", games)}, not {edition.Name}.");
                continue;
            }
            // A mod for several games may keep a DLL per game, in a folder named after it.
            var dll = edition == null ? Path.Combine(dir, folder + ".dll") : GameEditions.ModAssembly(dir, folder, edition);
            if (dll == null)
            {
                Log.Warn($"Mod {folder} says it runs in {edition!.Name} but has no {folder}.dll for it; left out.");
                continue;
            }
            var enabled = !disabled.Contains(folder);
            _installed.Add(ReadEntry(dir, folder, enabled));
            if (enabled) toLoad.Add((dir, dll));
            else Log.Info($"Mod {folder} is switched off.");
        }
        _otherDisabled.UnionWith(disabled.Where(d => !_installed.Any(m => m.Folder.Equals(d, StringComparison.OrdinalIgnoreCase))));

        var platform = typeof(ModLoader).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var on = toLoad.Select(m => Path.GetFileName(m.Dir)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, dll) in toLoad)
        {
            var folder = Path.GetFileName(dir);
            var (minimum, requires, _) = ReadRequirements(dir);
            // A mod built for a newer platform would fail half-way through; leave it out and say why.
            if (minimum != null && Version.TryParse(minimum, out var needed) && needed > platform)
            {
                Log.Warn($"Mod {folder} left out: it needs OpenFairway {minimum} or newer (this is {platform.ToString(3)}).");
                continue;
            }
            foreach (var required in requires.Where(r => !on.Contains(r)))
                Log.Warn($"Mod {folder} needs {required}, which is off or not installed; it may not work.");
            try
            {
                LibraryFolders.Add(dir);
                var assembly = Assembly.LoadFrom(dll);
                foreach (var type in assembly.GetTypes().Where(t => typeof(IFairwayMod).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }))
                {
                    var mod = (IFairwayMod)Activator.CreateInstance(type)!;
                    Log.Info($"Loading mod {mod.Id} ({Path.GetFileName(dll)})");
                    mod.OnLoad(new ModContext(mod.Id, game, runtime, mainThread, ui, this, this, Path.Combine(dir, "data")));
                }
            }
            catch (Exception e)
            {
                Log.Error($"Mod {Path.GetFileName(dir)} failed to load: {e}");
            }
        }
    }

    /// <summary>mod.json's optional "openfairway" (the oldest platform version the mod works with), "requires"
    /// (folders of mods it needs) and "games" (the games it is made for).</summary>
    private static (string? Minimum, string[] Requires, string[] Games) ReadRequirements(string dir)
    {
        try
        {
            var manifest = Path.Combine(dir, "mod.json");
            if (!File.Exists(manifest)) return (null, [], []);
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = json.RootElement;
            var minimum = root.TryGetProperty("openfairway", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            string[] Strings(string key) => root.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
                : [];
            return (minimum, Strings("requires"), Strings("games"));
        }
        catch (Exception)
        {
            return (null, [], []);
        }
    }

    private static ModEntry ReadEntry(string dir, string folder, bool enabled)
    {
        string name = folder, description = "", version = "", author = "", icon = "";
        try
        {
            var manifest = Path.Combine(dir, "mod.json");
            if (File.Exists(manifest))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                string Get(string key, string fallback) =>
                    json.RootElement.TryGetProperty(key, out var value) && value.GetString() is { Length: > 0 } text ? text : fallback;
                name = Get("name", folder);
                description = Get("description", "");
                version = Get("version", "");
                author = Get("author", "");
                icon = Get("icon", "");
            }
        }
        catch (Exception) { /* a broken manifest should not hide the mod */ }
        return new ModEntry { Folder = folder, Name = name, Description = description, Version = version, Author = author, Icon = icon, Running = enabled, EnabledNextStart = enabled };
    }

    /// <summary>Mods whose libraries are looked for when the runtime cannot find one by itself: in the folder for the
    /// running game, then in the shared folder, then at the mod's root.</summary>
    private static readonly List<string> LibraryFolders = new();

    static ModLoader()
    {
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var dir in LibraryFolders.ToArray())
                foreach (var candidate in Candidates(dir, name.Name + ".dll"))
                    if (File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
            return null;
        };
    }

    /// <summary>Where a mod's file can be, best first: the running game's folder, the shared folder, the mod's root.</summary>
    private static IEnumerable<string> Candidates(string modDir, string relativePath)
    {
        if (Games.CurrentGame.Edition is { } edition) yield return Path.Combine(modDir, edition.Id, relativePath);
        yield return Path.Combine(modDir, GameEditions.SharedFolder, relativePath);
        yield return Path.Combine(modDir, relativePath);
    }

    private sealed class ModContext(string id, GameInfo game, IGameRuntime runtime, IMainThread mainThread, IGameUi ui, IModManager mods, IActionRegistry actions, string dataDir) : IModContext
    {
        public GameInfo Game { get; } = game;
        public ILog Log { get; } = new ModLog(id);
        public IGameRuntime Runtime { get; } = runtime;
        public IMainThread MainThread { get; } = mainThread;
        public IGameUi Ui { get; } = ui;
        public IModManager Mods { get; } = mods;
        public IActionRegistry Actions { get; } = actions;
        public string ModDirectory { get; } = Path.GetDirectoryName(dataDir)!;

        public string ModFile(string relativePath)
        {
            var candidates = Candidates(ModDirectory, relativePath).ToList();
            return candidates.FirstOrDefault(File.Exists) ?? candidates.FirstOrDefault(Directory.Exists) ?? candidates[^1];
        }
        public string PlatformVersion { get; } = typeof(ModLoader).Assembly.GetName().Version?.ToString(3) ?? "0";
        public string DataDirectory
        {
            get
            {
                Directory.CreateDirectory(dataDir);
                return dataDir;
            }
        }
    }
}
