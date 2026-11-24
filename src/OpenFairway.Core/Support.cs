using System;
using System.IO;
using System.Text.Json;
using OpenFairway.API;

namespace OpenFairway.Core;

internal static class Log
{
    private static StreamWriter? _writer;
    private static readonly object Gate = new();

    public static void Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    /// <summary>Set from config.json ("LogLevel": "detailed"); the launcher's Settings switch it.</summary>
    public static bool Detailed { get; set; }

    /// <summary>Step-by-step lines that only help when chasing a problem.</summary>
    public static void Detail(string m) { if (Detailed) Write("DEBUG", m); }

    public static void Info(string m) => Write("INFO ", m);
    public static void Warn(string m) => Write("WARN ", m);
    public static void Error(string m) => Write("ERROR", m);

    private static void Write(string level, string message)
    {
        lock (Gate) _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {level} {message}");
    }
}

internal sealed class ModLog(string id) : ILog
{
    public void Info(string message) => Log.Info($"[{id}] {message}");
    public void Warn(string message) => Log.Warn($"[{id}] {message}");
    public void Error(string message) => Log.Error($"[{id}] {message}");
}

internal sealed class Config
{
    /// <summary>Hosts the retail game talks to (from the game's own log); used to verify the process is isolated.</summary>
    public string[] OfficialHosts { get; set; } = ["tgc6pc.hb-studios.com"];

    /// <summary>Seconds to wait after the game's runtime has initialised before attaching to it.</summary>
    public int StartupDelaySeconds { get; set; } = 2;

    /// <summary>Use the game's own "boot offline" option so it does not sit retrying its servers.</summary>
    public bool ForceOfflineBoot { get; set; } = true;

    /// <summary>Use the game's own option to keep the career save in a local file rather than the cloud.</summary>
    public bool LocalCareerSave { get; set; } = true;

    /// <summary>Developer-only: local inspection console on a named pipe. Leave off for normal play.</summary>
    public bool DevConsole { get; set; }

    /// <summary>"auto" (when missing or the game build changed), "always" or "never".</summary>
    public string DumpTypes { get; set; } = "auto";

    /// <summary>"normal", or "detailed" to also log each step the in-game screens take.</summary>
    public string LogLevel { get; set; } = "normal";

    public static Config Load(string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        Config config;
        try
        {
            config = File.Exists(path) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(path), options) ?? new Config() : new Config();
        }
        catch (Exception e)
        {
            Log.Warn($"config.json is invalid ({e.Message}); using defaults.");
            return new Config();
        }
        if (!File.Exists(path)) File.WriteAllText(path, JsonSerializer.Serialize(config, options));
        return config;
    }
}
