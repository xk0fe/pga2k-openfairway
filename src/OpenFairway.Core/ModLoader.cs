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
            File.WriteAllLines(_disabledFile, _installed.Where(m => !m.EnabledNextStart).Select(m => m.Folder));
        }
        catch (Exception e)
        {
            Log.Error("Could not save mod switches: " + e.Message);
        }
    }

    /// <summary>Loads every mods/&lt;name&gt;/&lt;name&gt;.dll that is switched on and calls OnLoad on each IFairwayMod in it.</summary>
    public void LoadAll(IGameRuntime runtime, IMainThread mainThread, IGameUi ui)
    {
        Directory.CreateDirectory(_modsDir);
        var disabled = File.Exists(_disabledFile)
            ? File.ReadAllLines(_disabledFile).Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        var toLoad = new List<(string Dir, string Dll)>();
        foreach (var dir in Directory.GetDirectories(_modsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetFileName(dir);
            var dll = Path.Combine(dir, folder + ".dll");
            if (!File.Exists(dll)) continue;
            var enabled = !disabled.Contains(folder);
            _installed.Add(ReadEntry(dir, folder, enabled));
            if (enabled) toLoad.Add((dir, dll));
            else Log.Info($"Mod {folder} is switched off.");
        }

        var platform = typeof(ModLoader).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var on = toLoad.Select(m => Path.GetFileName(m.Dir)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, dll) in toLoad)
        {
            var folder = Path.GetFileName(dir);
            var (minimum, requires) = ReadRequirements(dir);
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
                var assembly = Assembly.LoadFrom(dll);
                foreach (var type in assembly.GetTypes().Where(t => typeof(IFairwayMod).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }))
                {
                    var mod = (IFairwayMod)Activator.CreateInstance(type)!;
                    Log.Info($"Loading mod {mod.Id} ({Path.GetFileName(dll)})");
                    mod.OnLoad(new ModContext(mod.Id, runtime, mainThread, ui, this, this, Path.Combine(dir, "data")));
                }
            }
            catch (Exception e)
            {
                Log.Error($"Mod {Path.GetFileName(dir)} failed to load: {e}");
            }
        }
    }

    /// <summary>mod.json's optional "openfairway" (the oldest platform version the mod works with) and "requires"
    /// (folders of mods it needs).</summary>
    private static (string? Minimum, string[] Requires) ReadRequirements(string dir)
    {
        try
        {
            var manifest = Path.Combine(dir, "mod.json");
            if (!File.Exists(manifest)) return (null, []);
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = json.RootElement;
            var minimum = root.TryGetProperty("openfairway", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var requires = root.TryGetProperty("requires", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
                : [];
            return (minimum, requires);
        }
        catch (Exception)
        {
            return (null, []);
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

    private sealed class ModContext(string id, IGameRuntime runtime, IMainThread mainThread, IGameUi ui, IModManager mods, IActionRegistry actions, string dataDir) : IModContext
    {
        public ILog Log { get; } = new ModLog(id);
        public IGameRuntime Runtime { get; } = runtime;
        public IMainThread MainThread { get; } = mainThread;
        public IGameUi Ui { get; } = ui;
        public IModManager Mods { get; } = mods;
        public IActionRegistry Actions { get; } = actions;
        public string ModDirectory { get; } = Path.GetDirectoryName(dataDir)!;
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
