using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenFairway.Launcher
{
    /// <summary>
    /// What one session of OpenFairway did, read from logs\openfairway.log. The platform writes that file afresh
    /// each time it starts inside the game, so it is both the proof that it loaded and the record of what happened.
    /// </summary>
    internal sealed class LogReport
    {
        public DateTime Written;
        public string Version = "";
        public bool Offline, OfflineFailed, Attached, AttachFailed, StartupComplete, Quit;
        public string AttachError = "";
        /// <summary>Mod DLL names (without .dll) that loaded.</summary>
        public readonly List<string> Loaded = new List<string>();
        /// <summary>Mod folder to the first line of the error that stopped it loading.</summary>
        public readonly Dictionary<string, string> Failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Mod folder to why the platform left it out.</summary>
        public readonly Dictionary<string, string> LeftOut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Tail = new List<string>();

        private static readonly Regex Starting = new Regex(@"^OpenFairway (\S+) starting in ");
        private static readonly Regex LoadingMod = new Regex(@"^Loading mod \S+ \((.+?)\.dll\)");
        private static readonly Regex FailedMod = new Regex(@"^Mod (.+?) failed to load: (.*)$");
        private static readonly Regex LeftOutMod = new Regex(@"^Mod (.+?) left out: (.*)$");

        /// <summary>The report of the session in the log, or null if there is none (or it is older than
        /// <paramref name="notBefore"/>).</summary>
        public static LogReport? Read(string path, DateTime? notBefore = null)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var written = File.GetLastWriteTime(path);
                if (notBefore.HasValue && written < notBefore.Value) return null;
                string text;
                // The game holds the file open for writing while it runs.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) text = reader.ReadToEnd();

                var report = new LogReport { Written = written };
                var started = false;
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    report.Tail.Add(line);
                    if (report.Tail.Count > 80) report.Tail.RemoveAt(0);
                    // "[12:34:56.789] LEVEL message"; lines that do not start like that continue the line above.
                    if (line.Length < 22 || line[0] != '[' || line[13] != ']') continue;
                    var level = line.Substring(15, 5).Trim();
                    var message = line.Substring(21);
                    var m = Starting.Match(message);
                    if (m.Success) { started = true; report.Version = m.Groups[1].Value; continue; }
                    if (message.StartsWith("Game process is offline")) report.Offline = true;
                    else if (message.Contains("could not be taken offline")) report.OfflineFailed = true;
                    else if (message.StartsWith("Attached to IL2CPP runtime")) report.Attached = true;
                    else if (message.StartsWith("Startup complete")) report.StartupComplete = true;
                    else if (message.StartsWith("Game is quitting")) report.Quit = true;
                    else if (message.StartsWith("GameAssembly.dll is not loaded") || message.StartsWith("Platform failed"))
                    {
                        report.AttachFailed = true;
                        report.AttachError = Shorten(message);
                    }
                    else if ((m = LoadingMod.Match(message)).Success) report.Loaded.Add(m.Groups[1].Value);
                    else if ((m = FailedMod.Match(message)).Success) report.Failed[m.Groups[1].Value] = Shorten(m.Groups[2].Value);
                    else if ((m = LeftOutMod.Match(message)).Success) report.LeftOut[m.Groups[1].Value] = m.Groups[2].Value;
                    if (level == "ERROR") report.Errors.Add(message);
                }
                return started ? report : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>"System.IO.FileNotFoundException: Could not load ..." becomes "FileNotFoundException: Could not load ...".</summary>
        private static string Shorten(string message)
        {
            message = Regex.Replace(message, @"\b(System|Microsoft)(\.\w+)*\.(?=\w+Exception)", "");
            return message.Length > 180 ? message.Substring(0, 177) + "..." : message;
        }

        /// <summary>One sentence for the status card about how this session went.</summary>
        public string Summary(bool lastTime)
        {
            var when = lastTime ? "Last time " : "";
            if (OfflineFailed) return when + "OpenFairway stayed off: it could not take the game offline.";
            if (AttachFailed) return when + "OpenFairway could not attach to the game.";
            if (Failed.Count > 0)
                return when + (Failed.Count == 1 ? "1 mod failed to load." : Failed.Count + " mods failed to load.");
            if (!StartupComplete) return when + "OpenFairway did not finish starting.";
            var mods = Loaded.Count == 1 ? "1 mod" : Loaded.Count + " mods";
            return Errors.Count == 0 ? when + mods + " loaded with no errors." : when + mods + " loaded; the log has " + (Errors.Count == 1 ? "1 error." : Errors.Count + " errors.");
        }
    }

    /// <summary>The launcher's own log (logs\launcher.log), written afresh each start, for problem reports.</summary>
    internal static class LauncherLog
    {
        private static string? _path;
        private static readonly object Gate = new object();

        public static void Open(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "");
                _path = path;
            }
            catch (Exception) { _path = null; }
        }

        public static void Write(string message)
        {
            if (_path == null) return;
            lock (Gate)
            {
                try { File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n"); }
                catch (Exception) { }
            }
        }

        public static IEnumerable<string> Tail(int lines)
        {
            try { return _path != null && File.Exists(_path) ? File.ReadAllLines(_path).Reverse().Take(lines).Reverse() : Enumerable.Empty<string>(); }
            catch (Exception) { return Enumerable.Empty<string>(); }
        }
    }
}
