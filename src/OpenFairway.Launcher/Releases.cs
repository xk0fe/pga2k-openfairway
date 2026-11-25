using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenFairway.Launcher
{
    internal sealed class ReleaseAsset
    {
        public string Name = "", Url = "", Sha256 = "";
        public long Size;
    }

    internal sealed class Release
    {
        public string Tag = "", Name = "", Url = "";
        public bool PreRelease;
        public DateTime Published;
        public ReleaseAsset? Package;
        public string Version => Tag.TrimStart('v', 'V');
    }

    /// <summary>The project on GitHub: releases for updates, CHANGELOG.md for What's new. Only GitHub is contacted,
    /// and nothing about the player is sent (the requests carry the launcher's version, nothing else).</summary>
    internal static class GitHub
    {
        public const string Repo = "xk0fe/pga2k-openfairway";
        public const string RepoUrl = "https://github.com/" + Repo;
        public const string ReleasesUrl = RepoUrl + "/releases";
        public const string IssuesUrl = RepoUrl + "/issues";

        public static readonly string AgentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
        private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20), DownloadTimeout = TimeSpan.FromMinutes(30);
        private const int ReleasesPerPage = 30;
        private static readonly HttpClient Api = Client(ApiTimeout);
        internal static readonly HttpClient Files = Client(DownloadTimeout);

        private static HttpClient Client(TimeSpan timeout)
        {
            var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenFairway-Launcher/" + AgentVersion);
            return client;
        }

        public static async Task<List<Release>> Releases()
        {
            var text = await Api.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page={ReleasesPerPage}").ConfigureAwait(false);
            var releases = new List<Release>();
            foreach (var item in Json.Arr(Json.Parse(text)).OfType<Dictionary<string, object>>())
            {
                if (Json.Bool(item, "draft", false)) continue;
                var release = new Release
                {
                    Tag = Json.Str(item, "tag_name"),
                    Name = Json.Str(item, "name"),
                    Url = Json.Str(item, "html_url"),
                    PreRelease = Json.Bool(item, "prerelease", false),
                    Published = DateTime.TryParse(Json.Str(item, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var when) ? when : DateTime.MinValue,
                };
                foreach (var asset in Json.Arr(item.TryGetValue("assets", out var a) ? a : null).OfType<Dictionary<string, object>>())
                {
                    var name = Json.Str(asset, "name");
                    if (!Regex.IsMatch(name, @"^OpenFairway-.+\.zip$", RegexOptions.IgnoreCase)) continue;
                    var digest = Json.Str(asset, "digest");
                    release.Package = new ReleaseAsset
                    {
                        Name = name,
                        Url = Json.Str(asset, "browser_download_url"),
                        Size = Json.Long(asset, "size"),
                        Sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest.Substring(7).ToLowerInvariant() : "",
                    };
                }
                if (release.Tag.Length > 0) releases.Add(release);
            }
            return releases;
        }

        public static Task<string> Changelog() => Api.GetStringAsync($"https://raw.githubusercontent.com/{Repo}/main/CHANGELOG.md");

        /// <summary>The newest release with a package that is newer than <paramref name="installed"/>.</summary>
        public static Release? Newer(IEnumerable<Release> releases, string installed, bool preReleases) =>
            releases.Where(r => r.Package != null && (preReleases || !r.PreRelease) && SemVer.Compare(r.Version, installed) > 0)
                .OrderByDescending(r => r.Version, SemVer.Comparer)
                .FirstOrDefault();
    }

    internal static class SemVer
    {
        public static readonly IComparer<string> Comparer = Comparer<string>.Create(Compare);

        /// <summary>Compares "1.2.3" and "1.2.3-beta.1" versions; a pre-release sorts before its release.</summary>
        public static int Compare(string a, string b)
        {
            Split(a, out var ca, out var pa);
            Split(b, out var cb, out var pb);
            for (var i = 0; i < Math.Max(ca.Length, cb.Length); i++)
            {
                var x = i < ca.Length ? ca[i] : 0;
                var y = i < cb.Length ? cb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            if (pa.Length == 0 || pb.Length == 0) return pb.Length.CompareTo(pa.Length);
            var xa = pa.Split('.');
            var xb = pb.Split('.');
            for (var i = 0; i < Math.Min(xa.Length, xb.Length); i++)
            {
                var both = int.TryParse(xa[i], out var na) & int.TryParse(xb[i], out var nb);
                var c = both ? na.CompareTo(nb) : string.CompareOrdinal(xa[i], xb[i]);
                if (c != 0) return c;
            }
            return xa.Length.CompareTo(xb.Length);
        }

        private static void Split(string version, out int[] core, out string pre)
        {
            version = version.Trim().TrimStart('v', 'V');
            var dash = version.IndexOf('-');
            pre = dash >= 0 ? version.Substring(dash + 1) : "";
            var main = dash >= 0 ? version.Substring(0, dash) : version;
            core = main.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        }
    }

    internal sealed class NoteBlock
    {
        public string Kind = "p";   // "h" a section heading, "li" a bullet, "p" a paragraph
        public string Text = "";
    }

    internal sealed class NoteEntry
    {
        public string Version = "", Date = "";
        public readonly List<NoteBlock> Blocks = new List<NoteBlock>();
    }

    /// <summary>CHANGELOG.md ("## 1.2.3 - 2026-11-23", "### Section", "- item") as entries to draw.</summary>
    internal static class Notes
    {
        public static List<NoteEntry> Parse(string markdown)
        {
            var entries = new List<NoteEntry>();
            NoteEntry? entry = null;
            NoteBlock? last = null;
            foreach (var raw in markdown.Replace("\r", "").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.StartsWith("## "))
                {
                    var head = line.Substring(3).Trim();
                    var parts = head.Split(new[] { " - ", " – " }, 2, StringSplitOptions.None);
                    // Notes for work not yet released are for developers, not players.
                    entry = parts[0].Trim().Equals("Unreleased", StringComparison.OrdinalIgnoreCase) ? null : new NoteEntry { Version = parts[0].Trim(), Date = parts.Length > 1 ? parts[1].Trim() : "" };
                    if (entry != null) entries.Add(entry);
                    last = null;
                    continue;
                }
                if (entry == null) continue;
                if (line.StartsWith("### "))
                {
                    entry.Blocks.Add(last = new NoteBlock { Kind = "h", Text = Clean(line.Substring(4)) });
                    last = null;
                }
                else if (line.StartsWith("- ") || line.StartsWith("* "))
                    entry.Blocks.Add(last = new NoteBlock { Kind = "li", Text = Clean(line.Substring(2)) });
                else if (line.Trim().Length == 0)
                    last = null;
                else if (last != null)
                    last.Text += " " + Clean(line.Trim());
                else
                    entry.Blocks.Add(last = new NoteBlock { Kind = "p", Text = Clean(line.Trim()) });
            }
            return entries;
        }

        private static string Clean(string text)
        {
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
            return text.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
        }

        public static string Embedded()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OpenFairway.Launcher.CHANGELOG.md"))
            {
                if (stream == null) return "";
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }

        public static string FormatDate(string iso) =>
            DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : iso;
    }

    internal sealed class UpdateException : Exception
    {
        public UpdateException(string message) : base(message) { }
    }

    /// <summary>
    /// Updates OpenFairway from a GitHub release: downloads the package, checks it against the SHA-256 GitHub
    /// publishes for it, unpacks it, and once the launcher has closed runs the package's own installer, which
    /// replaces only what the previous version installed (mods you added, settings and saves stay). The launcher
    /// then starts again.
    /// </summary>
    internal static class Updater
    {
        public static string WorkDir(string version) => Path.Combine(Path.GetTempPath(), "OpenFairway-update", version);

        public static async Task<string> Download(Release release, Action<long, long> progress, CancellationToken cancel)
        {
            var asset = release.Package ?? throw new UpdateException("This release has no package to install.");
            var dir = WorkDir(release.Version);
            Directory.CreateDirectory(dir);
            var zip = Path.Combine(dir, asset.Name);
            using (var response = await GitHub.Files.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub answered {(int)response.StatusCode} for the download.");
                var total = response.Content.Headers.ContentLength ?? asset.Size;
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = File.Create(zip))
                {
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancel).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read, cancel).ConfigureAwait(false);
                        done += read;
                        progress(done, total);
                    }
                }
            }
            if (asset.Sha256.Length > 0)
            {
                string actual;
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(zip))
                    actual = string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
                if (actual != asset.Sha256)
                {
                    File.Delete(zip);
                    throw new UpdateException("The download was damaged: it does not match the checksum GitHub lists for it.");
                }
            }
            return zip;
        }

        /// <summary>Unpacks the package and returns its installer (files\setup.ps1).</summary>
        public static string Unpack(string zip)
        {
            var dir = Path.Combine(Path.GetDirectoryName(zip)!, "package");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            ZipFile.ExtractToDirectory(zip, dir);   // refuses entries that would land outside the folder
            var setup = Directory.GetFiles(dir, "setup.ps1", SearchOption.AllDirectories)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), "files", StringComparison.OrdinalIgnoreCase));
            return setup ?? throw new UpdateException("The download is not an OpenFairway package (it has no files\\setup.ps1).");
        }

        /// <summary>Hands over to a small script that waits for this launcher to close, runs the installer quietly,
        /// and starts the launcher again with the result.</summary>
        /// <summary><paramref name="home"/> is the install the launcher runs from (started again afterwards);
        /// <paramref name="installs"/> are all the games OpenFairway is in, which are all updated.</summary>
        public static void Apply(Install home, IEnumerable<Install> installs, string setup, string version)
        {
            var dir = WorkDir(version);
            var script = Path.Combine(dir, "apply.ps1");
            var log = Path.Combine(dir, "install.log");
            File.WriteAllText(script, ApplyScript, new UTF8Encoding(true));
            var launcher = Path.Combine(home.Home, "OpenFairway.exe");
            // The installer takes several game folders as one argument, separated the way PATH is.
            var games = string.Join(";", installs.Select(i => i.GameDir));
            var arguments = string.Join(" ",
                "-NoProfile", "-ExecutionPolicy Bypass", "-WindowStyle Hidden", "-File", Quote(script),
                "-LauncherPid", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
                "-Setup", Quote(setup), "-GameDir", Quote(games), "-Launcher", Quote(launcher),
                "-Version", Quote(version), "-Log", Quote(log));
            Process.Start(new ProcessStartInfo("powershell.exe", arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private const string ApplyScript =
            "param([int]$LauncherPid, [string]$Setup, [string]$GameDir, [string]$Launcher, [string]$Version, [string]$Log)\r\n" +
            "# Written by the OpenFairway launcher: install the downloaded release once the launcher has closed.\r\n" +
            "try { Wait-Process -Id $LauncherPid -Timeout 60 -ErrorAction SilentlyContinue } catch { }\r\n" +
            "Start-Sleep -Milliseconds 400\r\n" +
            "& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Setup -GameDir $GameDir -Quiet -NoShortcuts *> $Log\r\n" +
            "$ok = ($LASTEXITCODE -eq 0)\r\n" +
            "if (Test-Path $Launcher) {\r\n" +
            "    if ($ok) { Start-Process -FilePath $Launcher -ArgumentList @('--updated', $Version) }\r\n" +
            "    else { Start-Process -FilePath $Launcher -ArgumentList @('--update-failed', ('\"' + $Log + '\"')) }\r\n" +
            "} else {\r\n" +
            "    Add-Type -AssemblyName System.Windows.Forms\r\n" +
            "    [void][System.Windows.Forms.MessageBox]::Show(\"The OpenFairway update did not finish. Run 'Install OpenFairway.cmd' from the downloaded package to repair it.\", 'OpenFairway', 'OK', 'Error')\r\n" +
            "    Start-Process explorer.exe (Split-Path (Split-Path $Setup -Parent) -Parent)\r\n" +
            "}\r\n";
    }
}
