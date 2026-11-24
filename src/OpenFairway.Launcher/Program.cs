using System;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace OpenFairway.Launcher
{
    internal static class Program
    {
        private static readonly TimeSpan OldLauncherExitLimit = TimeSpan.FromSeconds(30);

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var home = Path.GetDirectoryName(Application.ExecutablePath)!;
            var options = StartOptions.Parse(args);

            var render = Array.IndexOf(args, "--render");
            if (render >= 0)
            {
                // Developer aid: draw the settled window to an image without showing it. An optional third argument
                // scales the picture. --page, --state, --expand, --drop, --modal and --capture pick what it shows.
                var scale = args.Length > render + 3 && float.TryParse(args[render + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 1f;
                using (var form = new MainForm(args[render + 2], options, preview: true, scale: scale))
                {
                    form.Settle();
                    using (var bitmap = form.Snapshot()) bitmap.Save(args[render + 1], ImageFormat.Png);
                }
                return;
            }

            var frames = Array.IndexOf(args, "--render-frames");
            if (frames >= 0)
            {
                // Renders the opening animation and a scripted walk through the window to numbered PNG frames,
                // on the window's own animation clock (for the video).
                // --render-frames <dir> <home> <seconds> <fps> [scale]
                var dir = args[frames + 1];
                var seconds = double.Parse(args[frames + 3], CultureInfo.InvariantCulture);
                var fps = double.Parse(args[frames + 4], CultureInfo.InvariantCulture);
                var scale = args.Length > frames + 5 ? float.Parse(args[frames + 5], CultureInfo.InvariantCulture) : 2f;
                Directory.CreateDirectory(dir);
                using (var form = new MainForm(args[frames + 2], options, preview: true, scale: scale))
                {
                    var count = (int)Math.Round(seconds * fps);
                    for (var i = 0; i < count; i++)
                    {
                        form.Script(i / fps);
                        using (var bitmap = form.Snapshot()) bitmap.Save(Path.Combine(dir, $"f{i:D6}.png"), ImageFormat.Png);
                    }
                }
                return;
            }

            // One launcher at a time: a second start brings the first one back (it hides while the game runs).
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\OpenFairway.Launcher.Show"))
            using (var single = new Mutex(true, @"Local\OpenFairway.Launcher", out var first))
            {
                if (!first)
                {
                    // An updated launcher starts while the old one is still closing: wait for it rather than give up.
                    if (options.Updated == null && options.UpdateFailedLog == null)
                    {
                        show.Set();
                        return;
                    }
                    try { single.WaitOne(OldLauncherExitLimit); } catch (AbandonedMutexException) { }
                }
                Application.Run(new MainForm(home, options, show));
            }
        }
    }

    /// <summary>Command-line options. The everyday ones: --play (start the game with mods straight away). The
    /// rest are for the updater and for development.</summary>
    internal sealed class StartOptions
    {
        public string? Updated, UpdateFailedLog, PretendVersion, Press, Page, State;
        public bool Play, Drop, Modal, Capture, Still;
        public int Expand = -1;

        public static StartOptions Parse(string[] args)
        {
            string? Value(string name)
            {
                var i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            return new StartOptions
            {
                Updated = Value("--updated"),
                UpdateFailedLog = Value("--update-failed"),
                PretendVersion = Value("--pretend-version"),   // development: behave as if this version were installed
                Press = Value("--press"),                      // development: "update" installs an available update
                Page = Value("--page"),
                State = Value("--state"),
                Expand = int.TryParse(Value("--expand"), out var e) ? e : -1,
                Play = Array.IndexOf(args, "--play") >= 0,
                Drop = Array.IndexOf(args, "--drop") >= 0,
                Modal = Array.IndexOf(args, "--modal") >= 0,
                Capture = Array.IndexOf(args, "--capture") >= 0,
                Still = Array.IndexOf(args, "--still") >= 0,         // development: the hole's still, as without OpenGL
            };
        }
    }
}
