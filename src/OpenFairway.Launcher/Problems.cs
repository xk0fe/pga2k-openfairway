using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OpenFairway.Launcher
{
    internal enum Act { Retry, OpenLogs, CopyDetails, Releases, Dismiss }

    /// <summary>Something that stops the player, said plainly: what happened, then what to do about it.</summary>
    internal sealed class Problem
    {
        public string Id = "", Title = "", Body = "", Detail = "";
        public string Badge = "ACTION NEEDED";
        public Act[] Actions = { Act.Retry, Act.CopyDetails };
    }

    internal static class Problems
    {
        private const int GameLogLinesInReport = 40, LauncherLogLinesInReport = 25;

        public static Problem GameMissing(GameEdition game) => new Problem
        {
            Id = "game-missing", Title = "The game is not where it was",
            Body = game.ExeName + " is not in the game folder. If you moved or reinstalled the game, run Install OpenFairway.cmd again.",
        };

        public static Problem LoaderMissing() => new Problem
        {
            Id = "loader-missing", Title = "The loader is missing",
            Body = "version.dll is gone from the game folder, usually taken by antivirus. Restore it from Protection history, or run the installer again.",
        };

        public static Problem CoreMissing() => new Problem
        {
            Id = "core-missing", Title = "OpenFairway's files are incomplete",
            Body = "Part of OpenFairway or its .NET runtime is missing. Run Install OpenFairway.cmd again; your mods and settings stay.",
        };

        public static Problem OtherLoader() => new Problem
        {
            Id = "other-loader", Title = "Another mod loader is installed",
            Body = "winhttp.dll in the game folder belongs to another loader, and two cannot run together. Remove it, then press Retry.",
        };

        public static Problem AddGameFailed(GameEdition game, string why) => new Problem
        {
            Id = "add-game", Title = "OpenFairway was not added to " + game.Name,
            Body = why,
            Actions = new[] { Act.CopyDetails },
        };

        public static Problem GameRunning(GameEdition game) => new Problem
        {
            Id = "game-running", Title = "The game is already running",
            Body = $"Close {game.Name} first. If it is not on screen, end {game.ExeName} in Task Manager, then press Retry.",
            Actions = new[] { Act.Retry },
        };

        public static Problem ConfigLocked(Exception e) => new Problem
        {
            Id = "config-locked", Title = "Cannot switch the loader on",
            Body = "Windows did not let OpenFairway change doorstop_config.ini in the game folder. Check that it is not read-only, then press Retry.",
            Detail = e.Message,
        };

        public static Problem SteamFailed(Exception e) => new Problem
        {
            Id = "steam-failed", Title = "Steam did not respond",
            Body = "The game could not be started through Steam. Open Steam and sign in, then press Retry.",
            Detail = e.Message,
        };

        public static Problem NoStart() => new Problem
        {
            Id = "no-start", Title = "The game did not start",
            Body = "Steam did not start it within three minutes. Open Steam and sign in, then press Retry.",
            Actions = new[] { Act.Retry },
        };

        public static Problem NotLoaded() => new Problem
        {
            Id = "not-loaded", Badge = "NOT LOADED", Title = "OpenFairway did not load",
            Body = "The game started without it. Antivirus that blocks version.dll is the usual cause. Close the game, allow the file, then press Retry.",
            Actions = new[] { Act.Retry, Act.OpenLogs, Act.CopyDetails },
        };

        public static Problem OfflineFailed() => new Problem
        {
            Id = "offline-failed", Badge = "NOT LOADED", Title = "OpenFairway stayed off",
            Body = "It could not take the game offline, so it loaded nothing and the game runs as normal. Close the game, then press Retry.",
            Actions = new[] { Act.Retry, Act.OpenLogs, Act.CopyDetails },
        };

        public static Problem AttachFailed(string detail) => new Problem
        {
            Id = "attach-failed", Badge = "NOT LOADED", Title = "OpenFairway could not attach",
            Body = "A game update may have changed what it expects. Look for an OpenFairway update; if there is none, report it with the details.",
            Detail = detail,
            Actions = new[] { Act.Releases, Act.OpenLogs, Act.CopyDetails },
        };

        public static Problem ClosedWhileStarting() => new Problem
        {
            Id = "closed", Badge = "NOT LOADED", Title = "The game closed while starting",
            Body = "It closed before OpenFairway finished loading. Press Retry; if it keeps closing, switch mods off one at a time to find the cause.",
            Actions = new[] { Act.Retry, Act.OpenLogs, Act.CopyDetails },
        };

        public static Problem TooSlow() => new Problem
        {
            Id = "slow", Badge = "NOT LOADED", Title = "OpenFairway is taking too long",
            Body = "It started inside the game but has not finished loading after six minutes. The logs show where it stopped.",
            Actions = new[] { Act.OpenLogs, Act.CopyDetails, Act.Dismiss },
        };

        public static Problem UpdateFailed(string reason, string detail = "") => new Problem
        {
            Id = "update-failed", Title = "The update did not install",
            Body = reason.TrimEnd('.') + ". Your current version still works.",
            Detail = detail,
            Actions = new[] { Act.Retry, Act.Releases, Act.CopyDetails },
        };

        public static Problem InstallFailed(string reason) => new Problem
        {
            Id = "mod-install-failed", Title = "Could not install that mod",
            Body = reason,
            Actions = new[] { Act.Dismiss },
        };

        public static Problem SaveFailed(Exception e) => new Problem
        {
            Id = "save-failed", Title = "Could not save that",
            Body = "Windows did not let the launcher write to its folder. Check that the game folder is not read-only.",
            Detail = e.Message,
            Actions = new[] { Act.Dismiss, Act.CopyDetails },
        };

        public static string Label(Act act) => act switch
        {
            Act.Retry => "RETRY",
            Act.OpenLogs => "OPEN LOGS",
            Act.CopyDetails => "COPY DETAILS",
            Act.Releases => "GITHUB",
            _ => "OK",
        };

        /// <summary>The text "Copy details" puts on the clipboard, for an issue or a message. The player's own
        /// profile folder is replaced by %USERPROFILE%.</summary>
        public static string Report(Problem problem, Install install, IEnumerable<ModInfo> mods, LogReport? log)
        {
            var build = install.ReadGameBuild();
            var text = new StringBuilder();
            text.AppendLine("OpenFairway problem report");
            text.AppendLine($"Problem: {problem.Id} - {problem.Title}");
            if (problem.Detail.Length > 0) text.AppendLine("Detail: " + problem.Detail);
            text.AppendLine($"OpenFairway: {install.CoreVersion()} (launcher {GitHub.AgentVersion})");
            text.AppendLine($"Game: {install.Edition.Name}, Steam build {build?.BuildId ?? "unknown"}{(build?.UpdatePending == true ? " (update pending)" : "")}");
            text.AppendLine($"Windows: {Environment.OSVersion.Version}");
            text.AppendLine("Mods on: " + string.Join(", ", mods.Where(m => m.Enabled).Select(m => m.Folder + (m.Version.Length > 0 ? " " + m.Version : ""))));
            if (log != null)
            {
                text.AppendLine();
                text.AppendLine("Last lines of openfairway.log:");
                foreach (var line in log.Tail.Skip(Math.Max(0, log.Tail.Count - GameLogLinesInReport))) text.AppendLine(line);
            }
            text.AppendLine();
            text.AppendLine("Last lines of launcher.log:");
            foreach (var line in LauncherLog.Tail(LauncherLogLinesInReport)) text.AppendLine(line);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return profile.Length > 3 ? text.ToString().Replace(profile, "%USERPROFILE%") : text.ToString();
        }
    }
}
