using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OpenFairway.Launcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var home = Path.GetDirectoryName(Application.ExecutablePath)!;

            var render = Array.IndexOf(args, "--render");
            if (render >= 0)
            {
                // Developer aid: draw the settled window to an image without showing it. The window is owner-drawn,
                // so the scene is painted straight into the bitmap (DrawToBitmap would miss it). An optional third
                // argument scales the picture (for sharp stills in videos).
                var scale = args.Length > render + 3 ? float.Parse(args[render + 3], CultureInfo.InvariantCulture) : 1f;
                using (var form = new MainForm(args[render + 2], preview: true, scale))
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
                // on the window's own animation clock (for the hype video).
                // --render-frames <dir> <home> <seconds> <fps> [scale]
                var dir = args[frames + 1];
                var seconds = double.Parse(args[frames + 3], CultureInfo.InvariantCulture);
                var fps = double.Parse(args[frames + 4], CultureInfo.InvariantCulture);
                var scale = args.Length > frames + 5 ? float.Parse(args[frames + 5], CultureInfo.InvariantCulture) : 2f;
                Directory.CreateDirectory(dir);
                using (var form = new MainForm(args[frames + 2], preview: true, scale))
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

            Application.Run(new MainForm(home));
        }
    }

    internal sealed class ModInfo
    {
        public string Folder = "";
        public string Name = "";
        public string Version = "";
        public string Author = "";
        public string Description = "";
        public bool Enabled = true;
    }

    /// <summary>Everything the launcher does to the game folder: read mods, remember which are off, start the game.</summary>
    internal sealed class Install
    {
        private const string ProcessName = "PGATOUR2K25";
        public readonly string Home;      // <game>\OpenFairway
        public readonly string GameDir;
        private string Ini => Path.Combine(GameDir, "doorstop_config.ini");
        private string DisabledFile => Path.Combine(Home, "disabled-mods.txt");
        public string ModsDir => Path.Combine(Home, "mods");

        public Install(string home)
        {
            Home = home;
            GameDir = Path.GetDirectoryName(home.TrimEnd('\\'))!;
        }

        public bool GameRunning => Process.GetProcessesByName(ProcessName).Length > 0;

        public List<ModInfo> LoadMods()
        {
            var disabled = new HashSet<string>(File.Exists(DisabledFile) ? File.ReadAllLines(DisabledFile).Select(l => l.Trim()) : new string[0], StringComparer.OrdinalIgnoreCase);
            var mods = new List<ModInfo>();
            if (!Directory.Exists(ModsDir)) return mods;
            foreach (var dir in Directory.GetDirectories(ModsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var folder = Path.GetFileName(dir);
                if (!File.Exists(Path.Combine(dir, folder + ".dll"))) continue;
                var mod = new ModInfo { Folder = folder, Name = folder, Enabled = !disabled.Contains(folder) };
                try
                {
                    var manifest = Path.Combine(dir, "mod.json");
                    if (File.Exists(manifest))
                    {
                        var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifest));
                        mod.Name = Get(json, "name", folder);
                        mod.Version = Get(json, "version", "");
                        mod.Author = Get(json, "author", "");
                        mod.Description = Get(json, "description", "");
                    }
                }
                catch (Exception) { /* a broken manifest should not hide the mod */ }
                mods.Add(mod);
            }
            return mods;
        }

        private static string Get(Dictionary<string, object> json, string key, string fallback) =>
            json.TryGetValue(key, out var value) && value != null ? value.ToString() : fallback;

        public void SaveDisabled(IEnumerable<ModInfo> mods) =>
            File.WriteAllLines(DisabledFile, mods.Where(m => !m.Enabled).Select(m => m.Folder).ToArray());

        public void SetLoader(bool on)
        {
            var lines = File.ReadAllLines(Ini).Select(l => l.StartsWith("enabled=") ? "enabled=" + (on ? "true" : "false") : l).ToArray();
            File.WriteAllLines(Ini, lines, System.Text.Encoding.ASCII);
        }

        private void StartGame()
        {
            var appIdFile = Path.Combine(Home, "appid.txt");
            var appId = File.Exists(appIdFile) ? File.ReadAllText(appIdFile).Trim() : "";
            if (appId.Length > 0) Process.Start(new ProcessStartInfo("steam://rungameid/" + appId) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(Path.Combine(GameDir, ProcessName + ".exe")) { WorkingDirectory = GameDir, UseShellExecute = true });
        }

        /// <summary>Starts the game through Steam. With mods, the loader is on for this one launch only.</summary>
        public async Task Play(bool withMods, Action<string> status)
        {
            if (GameRunning) throw new InvalidOperationException("The game is already running. Close it first.");
            SetLoader(withMods);
            try
            {
                status("Starting the game through Steam...");
                StartGame();
                var deadline = DateTime.UtcNow.AddMinutes(3);
                while (!GameRunning)
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("The game did not start. Make sure Steam is running and try again.");
                    await Task.Delay(500);
                }
                status(withMods ? "Game started with mods (offline)." : "Game started without mods.");
                await Task.Delay(TimeSpan.FromSeconds(20)); // the loader reads its switch at process start
            }
            finally
            {
                SetLoader(false); // a plain Steam launch is always unmodified
            }
        }
    }

    /// <summary>The OpenFairway palette (docs/design.md), shared in spirit with the in-game screens.</summary>
    internal static class Theme
    {
        // Sampled from PGA TOUR 2K25's own menus (docs/design.md).
        public static readonly Color Ink = Hex(0x0A0A14), Bg = Hex(0x0F0F1D), Strip = Hex(0x191923), Line = Hex(0x454850);
        public static readonly Color TileTop = Hex(0x3F4653), TileBottom = Hex(0x242731), RaisedTop = Hex(0x4A5160), RaisedBottom = Hex(0x2E323D);
        public static readonly Color Text = Hex(0xF4F4F4), Muted = Hex(0xAEB4C6), Faint = Hex(0x6E7486);
        public static readonly Color Cream = Hex(0xFDEDDA), Red = Hex(0xE0323F), Gold = Hex(0xE9B949);
        public static readonly Color TurfLight = Hex(0x84BE54), TurfDark = Hex(0x56943C);

        public static Color Hex(int rgb, int alpha = 255) => Color.FromArgb(alpha, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        public static Color Alpha(Color c, float alpha) => Color.FromArgb(Clamp((int)Math.Round(alpha * 255), 0, 255), c);

        public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }

    internal static class Ease
    {
        public static float Out(float t) { t = Theme.Clamp(t, 0f, 1f); var u = 1f - t; return 1f - u * u * u; }
        public static float InOut(float t) { t = Theme.Clamp(t, 0f, 1f); return t < 0.5f ? 4f * t * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 3) / 2f; }
    }

    /// <summary>A value that glides to a new target over a fixed time with an ease-out curve, on the window's clock.</summary>
    internal sealed class Tween
    {
        private float _from, _to;
        private double _start;
        private float _duration = 0.0001f;

        public Tween(float value) { _from = _to = value; }

        public float Target => _to;

        public float Value(double now) => _from + (_to - _from) * Ease.Out((float)((now - _start) / _duration));

        public bool Moving(double now) => now - _start < _duration && _from != _to;

        public void Set(float target, double now, float duration)
        {
            if (target == _to) return;
            _from = Value(now);
            _to = target;
            _start = now;
            _duration = duration;
        }
    }

    internal static class Gfx
    {
        private static readonly StringFormat Typographic = MakeTypographic();

        private static StringFormat MakeTypographic()
        {
            var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return format;
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color color)
        {
            using (var brush = new SolidBrush(color)) using (var path = Round(r, radius)) g.FillPath(brush, path);
        }

        /// <summary>A rounded box with a top-to-bottom colour ramp (the game's slate tiles).</summary>
        public static void FillVertical(Graphics g, RectangleF r, float radius, Color top, Color bottom)
        {
            using (var brush = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), top, bottom, 90f))
            using (var path = Round(r, radius)) g.FillPath(brush, path);
        }

        public static float Width(Graphics g, string text, Font font) => g.MeasureString(text, font, PointF.Empty, Typographic).Width;

        /// <summary>One line in a box, cut with an ellipsis if it does not fit.</summary>
        public static void TextFit(Graphics g, string text, Font font, Color color, RectangleF box, StringAlignment align = StringAlignment.Near)
        {
            using (var format = (StringFormat)Typographic.Clone())
            using (var brush = new SolidBrush(color))
            {
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.Alignment = align;
                g.DrawString(text, font, brush, box, format);
            }
        }

        public static void Text(Graphics g, string text, Font font, Color color, float x, float y)
        {
            using (var brush = new SolidBrush(color)) g.DrawString(text, font, brush, x, y, Typographic);
        }

        public static Font Font(string family, float px, FontStyle style, string fallback)
        {
            try
            {
                using (new FontFamily(family)) { }
                return new Font(family, px, style, GraphicsUnit.Pixel);
            }
            catch (ArgumentException)
            {
                return new Font(fallback, px, style, GraphicsUnit.Pixel);
            }
        }

        public static FontFamily Family(string name, string fallback)
        {
            try { return new FontFamily(name); }
            catch (ArgumentException) { return new FontFamily(fallback); }
        }
    }

    /// <summary>A mowed fairway seen from above: broad diagonal stripes alternating light and dark, with grass grain
    /// running along them and sunlight falling from the top left. Made in code, matching tools/make-wordmark.py.</summary>
    internal static class Turf
    {
        public static Bitmap Make(int width, int height, float stripe, float scale, float angleDeg = -28f, float darken = 0f)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var pixels = new int[width * height];
            var a = angleDeg * Math.PI / 180.0;
            var cos = Math.Cos(a);
            var sin = Math.Sin(a);
            var grainAcross = Math.Max(1.0, 1.1 * scale);
            var grainAlong = Math.Max(2.0, 5.0 * scale);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var u = x * cos + y * sin;
                    var v = -x * sin + y * cos;
                    var k = u / stripe;
                    var band = (int)Math.Floor(k) & 1;
                    var edge = Math.Abs((k - Math.Floor(k)) - 0.5) * 2.0;
                    var baseColor = band == 0 ? Theme.TurfLight : Theme.TurfDark;
                    var shade = 0.96 + 0.04 * edge;
                    shade *= 1.0 + 0.075 * Noise((int)Math.Floor(u / grainAcross), (int)Math.Floor(v / grainAlong));
                    shade *= 1.12 - 0.28 * y / height - 0.06 * x / width;
                    shade *= 1.0 - darken;
                    var r = Byte(baseColor.R * shade);
                    var gg = Byte(baseColor.G * shade);
                    var b = Byte(baseColor.B * shade);
                    pixels[y * width + x] = (255 << 24) | (r << 16) | (gg << 8) | b;
                }
            }
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            bitmap.UnlockBits(data);
            return bitmap;
        }

        private static int Byte(double v) => v < 0 ? 0 : v > 255 ? 255 : (int)v;

        /// <summary>Repeatable noise in [-1, 1] for a cell.</summary>
        private static double Noise(int x, int y)
        {
            unchecked
            {
                var h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFF) / 32767.5 - 1.0;
            }
        }
    }

    internal sealed class MainForm : Form
    {
        // Layout, in window pixels (1000 x 640). The left column is the brand and the play button, the right side the mods.
        private const int W = 1000, H = 640, Split = 400;
        private const float L0 = 40, L1 = 360;            // left column content edges
        private const float R0 = 448, R1 = 952;           // right side content edges
        private const float ListTop = 132, ListBottom = 568, RowH = 86, RowPad = 16;
        private const float Rise = 14f, EnterTime = 0.42f;
        private const double SweepFirst = 1.25, SweepEvery = 6.0, SweepTime = 1.5;

        private static readonly RectangleF PlayRect = new RectangleF(L0, 490, L1 - L0, 56);
        private static readonly RectangleF CloseRect = new RectangleF(W - 46, 0, 46, 36);
        private static readonly RectangleF MinRect = new RectangleF(W - 92, 0, 46, 36);
        private static readonly RectangleF Viewport = new RectangleF(R0 - RowPad, ListTop, R1 - R0 + 2 * RowPad, ListBottom - ListTop);

        private enum Hit { None, Minimise, Close, Row, Play, Plain, Folder, Drag }
        private enum StatusKind { Info, Busy, Error }

        private readonly Install _install;
        private readonly bool _preview;
        private readonly float _scale;
        private readonly List<ModInfo> _mods;
        private readonly Dictionary<ModInfo, Tween> _knob = new Dictionary<ModInfo, Tween>();
        private readonly Dictionary<(Hit, int), Tween> _hoverFade = new Dictionary<(Hit, int), Tween>();
        private readonly Timer _timer = new Timer { Interval = 15 };
        private readonly Stopwatch _wall = new Stopwatch();
        private readonly string _version;
        private readonly Font _heading, _play, _name, _version12, _body, _small, _fine;
        private readonly FontFamily _display;
        private GraphicsPath? _wordOpen, _wordFairway;
        private TextureBrush? _turfBrush;
        private Bitmap? _turfBitmap;
        private RectangleF _wordBounds;
        private string? _statusText;
        private StatusKind _statusKind = StatusKind.Info;
        private bool _launching, _launchingWithMods;
        private (Hit Kind, int Index) _hover = (Hit.None, -1), _pressed = (Hit.None, -1);
        private float _scroll, _scrollTarget;
        private double _now, _lastTick;
        private PointF? _pointer;   // drawn only when rendering frames
        private readonly Tween _scrollTween = new Tween(0);

        public MainForm(string home, bool preview = false, float scale = 1f)
        {
            _install = new Install(home);
            _preview = preview;
            _scale = scale;
            Text = "OpenFairway";
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(W, H);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            _display = Gfx.Family("Segoe UI Black", "Arial Black");
            _heading = Gfx.Font("Segoe UI Black", 30, FontStyle.Italic, "Arial Black");
            _play = Gfx.Font("Segoe UI Black", 21, FontStyle.Italic, "Arial Black");
            _name = Gfx.Font("Segoe UI Semibold", 15.5f, FontStyle.Regular, "Segoe UI");
            _version12 = Gfx.Font("Segoe UI", 12, FontStyle.Regular, "Segoe UI");
            _body = Gfx.Font("Segoe UI", 13, FontStyle.Regular, "Segoe UI");
            _small = Gfx.Font("Segoe UI", 12.5f, FontStyle.Regular, "Segoe UI");
            _fine = Gfx.Font("Segoe UI", 11.5f, FontStyle.Regular, "Segoe UI");

            _version = CoreVersion(home);
            _mods = _install.LoadMods();
            foreach (var mod in _mods) _knob[mod] = new Tween(mod.Enabled ? 1f : 0f);
            _timer.Tick += (s, e) => Tick();

            // If a previous launch was interrupted, make sure a plain Steam start is unmodified again.
            if (!_preview) try { if (!_install.GameRunning) _install.SetLoader(false); } catch (Exception) { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00020000; // WS_MINIMIZEBOX: lets the taskbar button minimise and restore a borderless window
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                var round = 2;                    // DWMWCP_ROUND (Windows 11; older systems ignore it)
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
                var border = 0x00362A1F;          // a frame colour (COLORREF) that disappears into the dark window
                DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
            }
            catch (Exception) { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _wall.Restart();
            _lastTick = 0;
            _timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _turfBrush?.Dispose();
                _turfBitmap?.Dispose();
                _wordOpen?.Dispose();
                _wordFairway?.Dispose();
                _display.Dispose();
                foreach (var font in new[] { _heading, _play, _name, _version12, _body, _small, _fine }) font.Dispose();
            }
            base.Dispose(disposing);
        }

        private static string CoreVersion(string home)
        {
            try { return AssemblyName.GetAssemblyName(Path.Combine(home, "core", "OpenFairway.Core.dll")).Version?.ToString(3) ?? ""; }
            catch (Exception) { return ""; }
        }

        // ---- offline rendering (stills and video frames) ---------------------------------------------------------

        /// <summary>Moves the clock past the entrance and any light sweep, so a still shows the resting window.</summary>
        public void Settle() => _now = 10.0;

        public Bitmap Snapshot()
        {
            var bitmap = new Bitmap((int)Math.Round(W * _scale), (int)Math.Round(H * _scale), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.ScaleTransform(_scale, _scale);
                PaintScene(g);
            }
            return bitmap;
        }

        /// <summary>A scripted walk through the window for the video: the entrance, the pointer moving down the mods,
        /// one mod switched off and on again, then resting on PLAY.</summary>
        public void Script(double t)
        {
            var rows = Enumerable.Range(0, Math.Max(1, _mods.Count)).Select(i => RowCenter(i)).ToArray();
            var toggle = Math.Min(2, _mods.Count - 1);
            var path = new List<(double T, PointF P)>
            {
                (0.0, new PointF(1060, 720)),
                (1.05, new PointF(1060, 720)),
                (1.6, new PointF(742, rows[0] + 4)),
                (1.9, new PointF(742, rows[0] + 4)),
            };
            var time = 1.9;
            for (var i = 1; i < rows.Length; i++)
            {
                time += 0.34;
                path.Add((time, new PointF(742 + 6 * i, rows[i] + 4)));
            }
            if (toggle >= 0)
            {
                time += 0.4;
                path.Add((time, new PointF(760, rows[toggle] + 2)));
            }
            var click1 = time + 0.25;
            var click2 = click1 + 0.75;
            path.Add((click2 + 0.35, new PointF(760, rows[Math.Max(0, toggle)] + 2)));
            path.Add((click2 + 1.05, new PointF(PlayRect.X + PlayRect.Width * 0.74f, PlayRect.Y + PlayRect.Height * 0.62f)));
            path.Add((1e9, new PointF(PlayRect.X + PlayRect.Width * 0.74f, PlayRect.Y + PlayRect.Height * 0.62f)));

            var p = path[0].P;
            for (var i = 0; i + 1 < path.Count; i++)
            {
                if (t < path[i].T || t > path[i + 1].T) continue;
                var k = Ease.InOut((float)((t - path[i].T) / (path[i + 1].T - path[i].T)));
                p = new PointF(path[i].P.X + (path[i + 1].P.X - path[i].P.X) * k, path[i].P.Y + (path[i + 1].P.Y - path[i].P.Y) * k);
                break;
            }

            var previous = _now;
            _now = t;
            _pointer = p;
            SetHover(HitTest(Point.Round(p)));
            foreach (var click in new[] { click1, click2 })
            {
                if (toggle < 0) break;
                if (previous < click && t >= click) _pressed = (Hit.Row, toggle);
                if (previous < click + 0.12 && t >= click + 0.12)
                {
                    _pressed = (Hit.None, -1);
                    Activate((Hit.Row, toggle));
                }
            }
        }

        private float RowCenter(int index) => ListTop + index * RowH + RowH / 2 - _scroll;

        // ---- layout and input ------------------------------------------------------------------------------------

        private float ContentHeight => _mods.Count * RowH;
        private float MaxScroll => Math.Max(0, ContentHeight - Viewport.Height);
        private bool Scrolls => MaxScroll > 0;

        private RectangleF RowRect(int index) =>
            new RectangleF(Viewport.X, ListTop + index * RowH - _scroll, Viewport.Width - (Scrolls ? 12 : 0), RowH);

        private RectangleF PlainRect() => new RectangleF(L0 + 40, 556, L1 - L0 - 80, 26);
        private RectangleF FolderRect => new RectangleF(R1 - 150, 594, 150, 26);

        private (Hit Kind, int Index) HitTest(Point p)
        {
            if (CloseRect.Contains(p)) return (Hit.Close, -1);
            if (MinRect.Contains(p)) return (Hit.Minimise, -1);
            if (Viewport.Contains(p))
            {
                for (var i = 0; i < _mods.Count; i++)
                    if (RowRect(i).Contains(p)) return (Hit.Row, i);
                return (Hit.None, -1);
            }
            if (PlayRect.Contains(p)) return (Hit.Play, -1);
            if (PlainRect().Contains(p)) return (Hit.Plain, -1);
            if (FolderRect.Contains(p)) return (Hit.Folder, -1);
            if (p.Y < 40 || p.X < Split) return (Hit.Drag, -1);
            return (Hit.None, -1);
        }

        private Tween Fade((Hit, int) key)
        {
            if (!_hoverFade.TryGetValue(key, out var tween)) _hoverFade[key] = tween = new Tween(0);
            return tween;
        }

        private float HoverAmount(Hit kind, int index = -1) =>
            _hoverFade.TryGetValue((kind, index), out var tween) ? tween.Value(_now) : 0f;

        private void SetHover((Hit Kind, int Index) hit)
        {
            if (hit == _hover) return;
            if (_hover.Kind != Hit.None) Fade(_hover).Set(0, _now, 0.16f);
            _hover = hit;
            if (hit.Kind != Hit.None && hit.Kind != Hit.Drag) Fade(hit).Set(1, _now, 0.16f);
            if (!_preview) Cursor = hit.Kind is Hit.None or Hit.Drag ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetHover(HitTest(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHover((Hit.None, -1));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var hit = HitTest(e.Location);
            if (hit.Kind == Hit.Drag)
            {
                // Hand the drag to Windows, as a title bar would.
                ReleaseCapture();
                SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
                return;
            }
            _pressed = hit;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var pressed = _pressed;
            _pressed = (Hit.None, -1);
            Invalidate();
            if (e.Button == MouseButtons.Left && pressed.Kind != Hit.None && pressed == HitTest(e.Location)) Activate(pressed);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Scrolls) return;
            _scrollTarget = Theme.Clamp(_scrollTarget - e.Delta / 120f * RowH, 0, MaxScroll);
            _scrollTween.Set(_scrollTarget, _now, 0.26f);
        }

        private void Activate((Hit Kind, int Index) hit)
        {
            switch (hit.Kind)
            {
                case Hit.Close: Close(); break;
                case Hit.Minimise: WindowState = FormWindowState.Minimized; break;
                case Hit.Row:
                    var mod = _mods[hit.Index];
                    mod.Enabled = !mod.Enabled;
                    _knob[mod].Set(mod.Enabled ? 1f : 0f, _now, 0.18f);
                    if (!_preview)
                        try { _install.SaveDisabled(_mods); } catch (Exception ex) { SetStatus(ex.Message, StatusKind.Error); }
                    break;
                case Hit.Play: if (!_preview) _ = Launch(true); break;
                case Hit.Plain: if (!_preview) _ = Launch(false); break;
                case Hit.Folder:
                    if (_preview) break;
                    Directory.CreateDirectory(_install.ModsDir);
                    Process.Start("explorer.exe", "\"" + _install.ModsDir + "\"");
                    break;
            }
            Invalidate();
        }

        private void SetStatus(string text, StatusKind kind)
        {
            _statusText = text;
            _statusKind = kind;
            Invalidate();
        }

        private async Task Launch(bool withMods)
        {
            if (_launching) return;
            _launching = true;
            _launchingWithMods = withMods;
            Invalidate();
            try
            {
                await _install.Play(withMods, text => SetStatus(text, StatusKind.Busy));
                Close();
            }
            catch (Exception e)
            {
                _launching = false;
                SetStatus(e.Message, StatusKind.Error);
            }
        }

        // ---- animation -------------------------------------------------------------------------------------------

        private bool SweepActive(double now) => now >= SweepFirst && (now - SweepFirst) % SweepEvery < SweepTime;

        private void Tick()
        {
            var now = _wall.Elapsed.TotalSeconds;
            var previous = _lastTick;
            _lastTick = now;
            _now = now;
            var busy = now < 1.6 || _launching || SweepActive(now) || SweepActive(previous)
                || _knob.Values.Any(t => t.Moving(now)) || _hoverFade.Values.Any(t => t.Moving(now)) || _scrollTween.Moving(now);
            if (_scrollTween.Moving(now) || Math.Abs(_scroll - _scrollTween.Target) > 0.01f)
            {
                _scroll = _scrollTween.Value(now);
                if (ClientRectangle.Contains(PointToClient(Cursor.Position))) SetHover(HitTest(PointToClient(Cursor.Position)));
            }
            if (busy) Invalidate();
            else if (Turntable.Shared is { } hole && _holeArea.Width > 0 && hole.Index(now) != hole.Index(previous))
                Invalidate(Rectangle.Round(RectangleF.Inflate(_holeArea, 2, 2))); // only the turning hole changed
        }

        /// <summary>How far an element's entrance has run (0 = not yet visible, 1 = in place).</summary>
        private float Entered(double delay) => Ease.Out((float)((_now - delay) / EnterTime));

        /// <summary>Draws a group that rises and fades in after <paramref name="delay"/>. The fade is done by covering
        /// the group's area with its flat background colour, so groups must not overlap.</summary>
        private void Reveal(Graphics g, double delay, RectangleF area, Color background, Action draw)
        {
            var e = Entered(delay);
            if (e <= 0f) return;
            var state = g.Save();
            var dy = Rise * (1f - e);
            g.TranslateTransform(0, dy);
            draw();
            if (e < 1f) using (var cover = new SolidBrush(Theme.Alpha(background, 1f - e))) g.FillRectangle(cover, RectangleF.Inflate(area, 2, 2));
            g.Restore(state);
        }

        // ---- painting --------------------------------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e) => PaintScene(e.Graphics);

        public void PaintScene(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Theme.Bg);
            using (var ink = new SolidBrush(Theme.Ink)) g.FillRectangle(ink, 0, 0, Split, H);

            PaintBrand(g);
            PaintPlay(g);
            PaintHeader(g);
            PaintRows(g);
            PaintFooter(g);
            PaintWindowButtons(g);
            if (_pointer.HasValue) PaintPointer(g, _pointer.Value);
        }

        private void BuildWordmark(Graphics g)
        {
            if (_wordOpen != null) return;
            const float em = 66f;
            var format = StringFormat.GenericTypographic;
            var open = new GraphicsPath();
            open.AddString("OPEN", _display, (int)FontStyle.Italic, em, PointF.Empty, format);
            var fairway = new GraphicsPath();
            fairway.AddString("FAIRWAY", _display, (int)FontStyle.Italic, em, PointF.Empty, format);

            // Fit FAIRWAY to the column width, and stack OPEN above it with both left edges on the column edge.
            var fb = fairway.GetBounds();
            var fit = Math.Min(1f, (L1 - L0) / fb.Width);
            using (var m = new Matrix())
            {
                m.Scale(fit, fit);
                open.Transform(m);
                fairway.Transform(m);
            }
            var ob = open.GetBounds();
            fb = fairway.GetBounds();
            const float top = 66f;
            var lineGap = fb.Height * 0.16f;
            using (var m = new Matrix()) { m.Translate(L0 - ob.X, top - ob.Y); open.Transform(m); }
            ob = open.GetBounds();
            using (var m = new Matrix()) { m.Translate(L0 - fb.X, ob.Bottom + lineGap - fb.Y); fairway.Transform(m); }
            fb = fairway.GetBounds();
            _wordOpen = open;
            _wordFairway = fairway;
            _wordBounds = RectangleF.Union(ob, fb);

            // The turf is made at device resolution so it stays crisp when the scene is scaled for stills.
            var px = new RectangleF(fb.X - 4, fb.Y - 4, fb.Width + 8, fb.Height + 8);
            _turfBitmap = Turf.Make((int)Math.Ceiling(px.Width * _scale), (int)Math.Ceiling(px.Height * _scale), stripe: em * fit * 0.62f * _scale, scale: _scale);
            _turfBrush = new TextureBrush(_turfBitmap, WrapMode.Clamp);
            _turfBrush.ScaleTransform(1f / _scale, 1f / _scale, MatrixOrder.Append);
            _turfBrush.TranslateTransform(px.X, px.Y, MatrixOrder.Append);
        }

        private void PaintBrand(Graphics g)
        {
            BuildWordmark(g);
            var open = _wordOpen!;
            var fairway = _wordFairway!;
            var area = _wordBounds;

            Reveal(g, 0.08, area, Theme.Ink, () =>
            {
                using (var white = new SolidBrush(Theme.Text)) g.FillPath(white, open);
                g.FillPath(_turfBrush!, fairway);

                // Now and then a band of sunlight passes over the grass.
                if (SweepActive(_now))
                {
                    var k = Ease.InOut((float)(((_now - SweepFirst) % SweepEvery) / SweepTime));
                    var fb = fairway.GetBounds();
                    var band = 150f;
                    var cx = fb.X - band + (fb.Width + band * 2) * k;
                    var state = g.Save();
                    g.SetClip(fairway, CombineMode.Intersect);
                    var r = new RectangleF(cx - band / 2, fb.Y - 10, band, fb.Height + 20);
                    using (var light = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), Color.Transparent, Color.Transparent, 20f))
                    {
                        light.InterpolationColors = new ColorBlend
                        {
                            Colors = new[] { Color.FromArgb(0, 255, 250, 225), Color.FromArgb(62, 255, 250, 225), Color.FromArgb(0, 255, 250, 225) },
                            Positions = new[] { 0f, 0.5f, 1f },
                        };
                        g.FillRectangle(light, r);
                    }
                    g.Restore(state);
                }
            });

            var tagline = new RectangleF(L0, area.Bottom + 20, L1 - L0, 22);
            Reveal(g, 0.16, tagline, Theme.Ink, () =>
                Gfx.TextFit(g, "Offline mods for PGA TOUR 2K25", _body, Theme.Muted, new RectangleF(tagline.X + 1, tagline.Y, tagline.Width, tagline.Height)));

            var art = new RectangleF(L0, tagline.Bottom + 46, L1 - L0, PlayRect.Y - 38 - (tagline.Bottom + 46));
            if (art.Height < 90) return;
            // A golf hole turning slowly: a 3D tile rendered by tools/render-hole.py and played back frame by frame.
            // Its frames are drawn on the column's ink, so it sits in the column without an edge.
            var canvas = new RectangleF(0, tagline.Bottom + 6, Split, PlayRect.Y - 10 - (tagline.Bottom + 6));
            var hole = Turntable.Shared;
            if (hole == null) return;
            var fit = Math.Min(canvas.Width / hole.Width, canvas.Height / hole.Height);
            var dest = new RectangleF(canvas.X + (canvas.Width - hole.Width * fit) / 2, canvas.Y + (canvas.Height - hole.Height * fit) / 2,
                hole.Width * fit, hole.Height * fit);
            _holeArea = dest;
            Reveal(g, 0.26, dest, Theme.Ink, () => g.DrawImage(hole.Frame(_now), dest));
        }

        private RectangleF _holeArea;

        /// <summary>The turning hole's frames, embedded in the launcher (Assets/hole.bin: "OFH1", count, width,
        /// height, then each frame as a length and a JPEG). Only the frame on screen is decoded.</summary>
        private sealed class Turntable
        {
            private const double FramesPerSecond = 24;
            private readonly byte[][] _frames;
            private Bitmap? _bitmap;
            private int _shown = -1;

            public int Width { get; }
            public int Height { get; }

            private Turntable(byte[][] frames, int width, int height)
            {
                _frames = frames;
                Width = width;
                Height = height;
            }

            private static Turntable? _shared;
            private static bool _tried;

            public static Turntable? Shared
            {
                get
                {
                    if (_tried) return _shared;
                    _tried = true;
                    try
                    {
                        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OpenFairway.Launcher.hole.bin");
                        if (stream == null) return null;
                        using var reader = new BinaryReader(stream);
                        if (new string(reader.ReadChars(4)) != "OFH1") return null;
                        var count = reader.ReadInt32();
                        var width = reader.ReadInt32();
                        var height = reader.ReadInt32();
                        var frames = new byte[count][];
                        for (var i = 0; i < count; i++) frames[i] = reader.ReadBytes(reader.ReadInt32());
                        _shared = new Turntable(frames, width, height);
                    }
                    catch (Exception)
                    {
                        _shared = null; // the launcher works without its picture
                    }
                    return _shared;
                }
            }

            public int Index(double seconds) => (int)(Math.Max(0, seconds) * FramesPerSecond) % _frames.Length;

            public Bitmap Frame(double seconds)
            {
                var index = Index(seconds);
                if (index != _shown || _bitmap == null)
                {
                    using var stream = new MemoryStream(_frames[index]);
                    using var image = Image.FromStream(stream);
                    _bitmap?.Dispose();
                    _bitmap = new Bitmap(image);
                    _shown = index;
                }
                return _bitmap;
            }
        }

        private void PaintPlay(Graphics g)
        {
            var hover = _launching ? 0f : HoverAmount(Hit.Play);
            var pressed = _pressed.Kind == Hit.Play && _hover.Kind == Hit.Play;
            var area = new RectangleF(PlayRect.X, PlayRect.Y, PlayRect.Width, PlayRect.Height);
            Reveal(g, 0.42, area, Theme.Ink, () =>
            {
                var r = PlayRect;
                if (pressed) r.Offset(0, 1);
                var fill = _launching ? Theme.TileBottom : Theme.Mix(Theme.Cream, Color.White, hover);
                Gfx.Fill(g, r, r.Height / 2, fill);
                var ink = _launching ? Theme.Muted : Theme.Bg;
                var label = _launching ? (_launchingWithMods ? "STARTING" : "PLAY") : "PLAY";
                var textW = Gfx.Width(g, label, _play);
                const float arrowW = 13f, gap = 14f;
                var showArrow = !_launching;
                var total = textW + (showArrow ? gap + arrowW : 0);
                var x = r.X + (r.Width - total) / 2;
                var cy = r.Y + r.Height / 2;
                Gfx.Text(g, label, _play, ink, x, cy - _play.GetHeight(g) / 2 + 0.5f);
                if (showArrow)
                {
                    var ax = x + textW + gap + 4f * hover;
                    using (var brush = new SolidBrush(ink))
                        g.FillPolygon(brush, new[] { new PointF(ax + 2.5f, cy - 8), new PointF(ax + arrowW + 1.5f, cy), new PointF(ax, cy + 8) });
                }
            });

            var plain = PlainRect();
            var plainHover = _launching ? 0f : HoverAmount(Hit.Plain);
            Reveal(g, 0.48, plain, Theme.Ink, () =>
            {
                var label = _launching && !_launchingWithMods ? "Starting the normal game..." : "Play without mods";
                var tail = _launching ? "" : "  ·  normal online game";
                var w1 = Gfx.Width(g, label, _small);
                var w2 = tail.Length > 0 ? Gfx.Width(g, tail, _small) : 0;
                var x = plain.X + (plain.Width - (w1 + w2)) / 2;
                var color = Theme.Mix(Theme.Muted, Theme.Text, plainHover);
                Gfx.Text(g, label, _small, color, x, plain.Y + 4);
                if (w2 > 0) Gfx.Text(g, tail, _small, Theme.Faint, x + w1, plain.Y + 4);
                if (plainHover > 0.01f)
                    using (var pen = new Pen(Theme.Alpha(Theme.Text, 0.55f * plainHover), 1f))
                        g.DrawLine(pen, x, plain.Y + 22.5f, x + w1, plain.Y + 22.5f);
            });

            var statusArea = new RectangleF(L0, 596, L1 - L0, 22);
            Reveal(g, 0.54, statusArea, Theme.Ink, () =>
            {
                string text;
                Color color;
                if (_statusText != null)
                {
                    text = _statusText;
                    color = _statusKind == StatusKind.Error ? Theme.Red : Theme.Muted;
                }
                else
                {
                    var on = _mods.Count(m => m.Enabled);
                    text = string.Join("   ·   ", new[] { "Offline when modded", $"{on} of {_mods.Count} mods on", _version.Length > 0 ? "Core " + _version : "" }.Where(s => s.Length > 0));
                    color = Theme.Faint;
                }
                Gfx.TextFit(g, text, _fine, color, new RectangleF(statusArea.X, statusArea.Y + 2, statusArea.Width, statusArea.Height), StringAlignment.Center);
            });
        }

        private void PaintHeader(Graphics g)
        {
            var area = new RectangleF(R0, 44, R1 - R0, 72);
            Reveal(g, 0.14, area, Theme.Bg, () =>
            {
                Gfx.Text(g, "MODS", _heading, Theme.Text, R0 - 1, 44);
                var on = _mods.Count(m => m.Enabled);
                var summary = _mods.Count == 0 ? "None installed" : $"{on} of {_mods.Count} on";
                var w = Gfx.Width(g, summary, _body);
                Gfx.Text(g, summary, _body, Theme.Muted, R1 - w, 60);
                Gfx.TextFit(g, "Click a mod to switch it on or off. Changes apply the next time you play.", _small, Theme.Faint, new RectangleF(R0, 92, R1 - R0, 20));
            });
        }

        private void PaintRows(Graphics g)
        {
            var state = g.Save();
            g.SetClip(new RectangleF(Viewport.X - 2, Viewport.Y - 1, Viewport.Width + 4, Viewport.Height + 2));

            if (_mods.Count == 0)
            {
                var r = new RectangleF(R0, ListTop + 12, R1 - R0, RowH);
                Reveal(g, 0.22, r, Theme.Bg, () =>
                {
                    Gfx.Text(g, "No mods installed yet.", _name, Theme.Text, r.X, r.Y + 6);
                    Gfx.Text(g, "Put mod folders in the mods folder, then open the launcher again.", _body, Theme.Muted, r.X, r.Y + 32);
                });
            }

            using (var line = new Pen(Theme.Line, 1f))
            {
                // A rule above the list; each row draws the one beneath it.
                Reveal(g, 0.18, new RectangleF(R0, ListTop - 2, R1 - R0, 4), Theme.Bg, () => g.DrawLine(line, R0, ListTop - 0.5f, R1, ListTop - 0.5f));
                for (var i = 0; i < _mods.Count; i++)
                {
                    var r = RowRect(i);
                    if (r.Bottom < Viewport.Top - 2 || r.Top > Viewport.Bottom + 2) continue;
                    var index = i;
                    Reveal(g, 0.2 + 0.055 * i, r, Theme.Bg, () => PaintRow(g, index, r, line));
                }
            }
            g.Restore(state);

            if (!Scrolls) return;
            if (_scroll > 1)
                using (var fade = new LinearGradientBrush(new RectangleF(Viewport.X, ListTop - 1, Viewport.Width, 30), Theme.Bg, Color.FromArgb(0, Theme.Bg), 90f))
                    g.FillRectangle(fade, Viewport.X, ListTop, Viewport.Width, 28);
            if (_scroll < MaxScroll - 1)
                using (var fade = new LinearGradientBrush(new RectangleF(Viewport.X, ListBottom - 29, Viewport.Width, 30), Color.FromArgb(0, Theme.Bg), Theme.Bg, 90f))
                    g.FillRectangle(fade, Viewport.X, ListBottom - 28, Viewport.Width, 28);
            var thumbH = Math.Max(36f, Viewport.Height * Viewport.Height / ContentHeight);
            var thumbY = ListTop + (Viewport.Height - thumbH) * (_scroll / MaxScroll);
            Gfx.Fill(g, new RectangleF(Viewport.Right - 3, ListTop, 3, Viewport.Height), 1.5f, Theme.Line);
            Gfx.Fill(g, new RectangleF(Viewport.Right - 3, thumbY, 3, thumbH), 1.5f, Theme.Faint);
        }

        private void PaintRow(Graphics g, int index, RectangleF r, Pen line)
        {
            var mod = _mods[index];
            var hover = HoverAmount(Hit.Row, index);
            var pressed = _pressed.Kind == Hit.Row && _pressed.Index == index && _hover == _pressed;
            var on = _knob[mod].Value(_now);

            var fillRect = new RectangleF(r.X, r.Y + 4, r.Width, r.Height - 8);
            if (hover > 0.001f)
                Gfx.FillVertical(g, fillRect, 4, Theme.Mix(Theme.Bg, pressed ? Theme.TileTop : Theme.RaisedTop, hover), Theme.Mix(Theme.Bg, pressed ? Theme.TileBottom : Theme.RaisedBottom, hover));
            if (index < _mods.Count - 1) g.DrawLine(line, R0, r.Bottom - 0.5f, R1, r.Bottom - 0.5f);

            var textX = r.X + RowPad;
            var switchW = 44f;
            var switchX = r.Right - RowPad - switchW;
            var textRight = switchX - 28;

            var lit = Math.Max(on, hover * 0.7f);
            var nameColor = Theme.Mix(Theme.Muted, Theme.Text, lit);
            var nameW = Math.Min(Gfx.Width(g, mod.Name, _name), textRight - textX - 60);
            Gfx.TextFit(g, mod.Name, _name, nameColor, new RectangleF(textX, r.Y + 18, nameW + 2, 24));
            if (mod.Version.Length > 0)
                Gfx.TextFit(g, "v" + mod.Version, _version12, Theme.Faint, new RectangleF(textX + nameW + 10, r.Y + 21.5f, textRight - (textX + nameW + 10), 20));
            Gfx.TextFit(g, mod.Description, _body, Theme.Mix(Theme.Faint, Theme.Muted, lit), new RectangleF(textX, r.Y + 46, textRight - textX, 20));

            PaintSwitch(g, new RectangleF(switchX, r.Y + (r.Height - 24) / 2, switchW, 24), on, hover);
        }

        private static void PaintSwitch(Graphics g, RectangleF r, float on, float hover)
        {
            var offTrack = Theme.Mix(Theme.Line, Theme.Hex(0x5A5E68), hover);
            Gfx.Fill(g, r, r.Height / 2, Theme.Mix(offTrack, Theme.Cream, on));
            var d = r.Height - 6;
            var knob = new RectangleF(r.X + 3 + on * (r.Width - 6 - d), r.Y + 3, d, d);
            using (var brush = new SolidBrush(Theme.Mix(Theme.Faint, Theme.Bg, on))) g.FillEllipse(brush, knob);
        }

        private void PaintFooter(Graphics g)
        {
            var area = new RectangleF(R0, 594, R1 - R0, 26);
            var hover = HoverAmount(Hit.Folder);
            Reveal(g, 0.56, area, Theme.Bg, () =>
            {
                Gfx.TextFit(g, "Unofficial. Not affiliated with 2K, HB Studios or the PGA TOUR.", _fine, Theme.Faint,
                    new RectangleF(R0, 598, FolderRect.X - R0 - 16, 20));

                var label = "Open mods folder";
                var w = Gfx.Width(g, label, _small);
                var color = Theme.Mix(Theme.Muted, Theme.Text, hover);
                var x = FolderRect.Right - w;
                Gfx.Text(g, label, _small, color, x, 597);
                // A small folder outline before the label.
                var fx = x - 22;
                var fy = 605.5f;
                using (var pen = new Pen(color, 1.3f) { LineJoin = LineJoin.Round })
                using (var folder = new GraphicsPath())
                {
                    folder.AddLines(new[] { new PointF(fx, fy + 8), new PointF(fx, fy - 2), new PointF(fx + 5, fy - 2), new PointF(fx + 6.5f, fy), new PointF(fx + 14, fy), new PointF(fx + 14, fy + 8) });
                    folder.CloseFigure();
                    g.DrawPath(pen, folder);
                }
            });
        }

        private void PaintWindowButtons(Graphics g)
        {
            var minHover = HoverAmount(Hit.Minimise);
            var closeHover = HoverAmount(Hit.Close);
            if (minHover > 0.01f) using (var light = new SolidBrush(Theme.Alpha(Theme.TileBottom, minHover))) g.FillRectangle(light, MinRect);
            if (closeHover > 0.01f) using (var red = new SolidBrush(Theme.Alpha(Theme.Red, closeHover))) g.FillRectangle(red, CloseRect);
            using (var pen = new Pen(Theme.Mix(Theme.Faint, Theme.Text, minHover), 1.2f))
            {
                var cx = MinRect.X + MinRect.Width / 2;
                var cy = MinRect.Y + MinRect.Height / 2;
                g.DrawLine(pen, cx - 5.5f, cy, cx + 5.5f, cy);
            }
            using (var pen = new Pen(Theme.Mix(Theme.Faint, Theme.Text, closeHover), 1.2f))
            {
                var cx = CloseRect.X + CloseRect.Width / 2;
                var cy = CloseRect.Y + CloseRect.Height / 2;
                g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(pen, cx - 5, cy + 5, cx + 5, cy - 5);
            }
        }

        /// <summary>The mouse pointer, for rendered frames only (a real window shows the system pointer).</summary>
        private static void PaintPointer(Graphics g, PointF p)
        {
            if (p.X > W + 4 || p.Y > H + 4) return;
            var shape = new[] { new PointF(0, 0), new PointF(0, 17), new PointF(4.2f, 13.2f), new PointF(7.2f, 20f), new PointF(9.8f, 18.9f), new PointF(6.9f, 12.3f), new PointF(12.2f, 12.3f) };
            var points = shape.Select(s => new PointF(p.X + s.X, p.Y + s.Y)).ToArray();
            using (var fill = new SolidBrush(Color.White)) g.FillPolygon(fill, points);
            using (var pen = new Pen(Color.Black, 1.1f) { LineJoin = LineJoin.Round }) g.DrawPolygon(pen, points);
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
