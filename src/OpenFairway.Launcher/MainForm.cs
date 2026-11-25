using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OpenFairway.Launcher
{
    /// <summary>
    /// The launcher window. Home is a dark stage: the turning 3D scan of a golf hole in the middle, the wordmark
    /// and the status card on the left, and the actions stacked on the right (Settings, Mods, Play), after the
    /// game's own tiles. Mods, Settings and What's new slide in as one panel with tabs. Everything is drawn here
    /// (no stock controls), so the window matches the game's look; clickable areas are recorded as they are
    /// drawn, and the turning hole is composited under a cached picture of the interface.
    /// </summary>
    internal sealed partial class MainForm : Form
    {
        private const int W = 1080, H = 640;
        private const float L0 = 48;
        private const float Rise = 14f, EnterTime = 0.45f;
        private const double EntrancesDone = 1.6;
        private const float TabTracking = 1.4f, TileTracking = 1.6f, PlayTracking = 1.5f, LabelTracking = 2f;
        private const double SweepFirst = 1.4, SweepEvery = 6.0, SweepTime = 1.5;

        private const float HoverTime = 0.16f, ToggleTime = 0.18f, OverlayTime = 0.2f, OverlayCloseTime = 0.16f;
        private const float TabSlideTime = 0.24f, ScrollTime = 0.26f, ExpandTime = 0.26f, PanelOpenTime = 0.34f, PanelCloseTime = 0.26f;
        private const double PageFadeTime = 0.32, PanelContentDelay = 0.12;
        private const float WheelScrollPixels = 72f;
        private const int WheelDelta = 120;

        private const double AmbientFrameTime = 0.012, AmbientFrameTimeBehindOthers = 0.032;
        private const int NoCompositionFallbackMs = 15;
        private const double SetupCheckEvery = 2.0, AutoPlayAfter = 1.2;
        private const double HoleFadeDelay = 0.05, HoleFadeTime = 1.1, HoleIn = HoleFadeDelay + HoleFadeTime;
        private const float PanelCoversHole = 0.98f;
        private const int UpdateLogLinesShown = 6;
        private const double LabelRevertMargin = 0.1;

        private static readonly RectangleF CardRect = new RectangleF(L0, 222, 396, 184);
        private static readonly RectangleF SettingsTile = new RectangleF(806, 352, 228, 54);
        private static readonly RectangleF ModsTile = new RectangleF(806, 414, 228, 66);
        private static readonly RectangleF PlayTile = new RectangleF(806, 488, 228, 96);
        private static readonly RectangleF CloseRect = new RectangleF(W - 46, 0, 46, 36);
        private static readonly RectangleF MinRect = new RectangleF(W - 92, 0, 46, 36);
        private static readonly Point HoleCentre = new Point(636, 300);   // = ORIGIN in tools/render-hole.py
        private const int Grid = 24;

        /// <summary>The install the launcher was started from; it keeps the choice of game.</summary>
        private readonly Install _home;

        /// <summary>OpenFairway in every supported game Steam has (one entry for most players).</summary>
        private List<Install> _installs;

        /// <summary>The game the launcher is showing and playing.</summary>
        private Install _install;

        /// <summary>The supported games as the last scan of the Steam libraries found them (Settings, GAMES).</summary>
        private List<GameScan> _scan = new List<GameScan>();

        private bool _addingGame;
        private const string PressAdd = "add:";
        private readonly StartOptions _options;
        private readonly bool _preview;
        private readonly float _scale;
        private LauncherSettings _settings;
        private CoreConfig _config;
        private List<ModInfo> _mods;

        private readonly ManualResetEventSlim _pacing = new ManualResetEventSlim(false);
        private Thread? _pacer;
        private volatile bool _tickQueued, _closing, _animating = true;
        private volatile float _ambientFrameTime = (float)AmbientFrameTime;
        private readonly Stopwatch _wall = new Stopwatch();
        private double _now, _lastTick, _lastRecheck, _lastHoleStep;
        private PointF? _pointer;   // drawn only when rendering frames

        private readonly Font _tab, _tile, _playFont, _cardTitle, _link, _noteVersion, _name, _headline, _body, _small, _line, _fine, _badge, _pill, _section;
        private readonly FontFamily _display;
        private GraphicsPath? _wordOpen, _wordFairway;
        private TextureBrush? _turfBrush;
        private Bitmap? _turfBitmap;
        private RectangleF _wordBounds;

        // Interaction: every clickable area is recorded while drawing, so hit tests match what is on screen.
        private sealed class HotRegion
        {
            public RectangleF Rect;
            public string Key = "";
            public Action? Click;
            public Cursor Cursor = Cursors.Hand;
        }

        private List<HotRegion> _regions = new List<HotRegion>();
        private readonly List<HotRegion> _building = new List<HotRegion>();
        private string _hover = "", _pressed = "";
        private readonly Dictionary<string, Tween> _fades = new Dictionary<string, Tween>();
        private readonly List<Tween> _tweens = new List<Tween>();

        // The interface is drawn into a cached layer; the turning hole goes under it.
        private Bitmap? _uiLayer;
        private bool _uiDirty = true;
        private Rectangle _holeArea;

        // Overlays.
        private sealed class Dialog
        {
            public string Title = "", Body = "", Confirm = "";
            public Action? OnConfirm;
        }

        private Dialog? _modal;
        private readonly Tween _modalFade, _dropFade;
        private readonly EventWaitHandle? _showSignal;

        public MainForm(string home, StartOptions options, EventWaitHandle? showSignal = null, bool preview = false, float scale = 1f)
        {
            _home = new Install(home);
            if (!preview) LauncherLog.Open(_home.LauncherLog);
            LauncherLog.Write($"Launcher {GitHub.AgentVersion} in {_home.Home}");
            _installs = Install.FindAll(_home);
            var chosen = options.Game ?? LauncherSettings.Load(_home.SettingsFile).Game;
            _install = _installs.FirstOrDefault(i => i.Edition.Id == chosen) ?? _home;
            if (_installs.Count > 1) LauncherLog.Write("Games: " + string.Join(", ", _installs.Select(i => i.Edition.Name)) + "; showing " + _install.Edition.Name);
            _scan = Install.Scan(_installs);
            _options = options;
            _preview = preview;
            _scale = scale;
            _showSignal = showSignal;
            Text = "OpenFairway";
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(W, H);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Paper;
            // Each paint writes every pixel it covers in one copy (MainForm.Compose.cs), so no back buffer is needed.
            DoubleBuffered = false;
            KeyPreview = true;
            AllowDrop = !preview;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            _display = Gfx.Family("Segoe UI Black", "Arial Black");
            // Titles are upright uppercase with a little letter spacing (Gfx.Title); only PLAY is set in Black.
            _tab = Gfx.Font("Segoe UI", 17, FontStyle.Bold, "Arial");
            _tile = Gfx.Font("Segoe UI", 15.5f, FontStyle.Bold, "Arial");
            _playFont = Gfx.Font("Segoe UI Black", 30, FontStyle.Regular, "Arial Black");
            _cardTitle = Gfx.Font("Segoe UI", 12, FontStyle.Bold, "Arial");
            _link = Gfx.Font("Segoe UI", 12.5f, FontStyle.Bold, "Arial");
            _noteVersion = Gfx.Font("Segoe UI", 19, FontStyle.Bold, "Arial");
            _name = Gfx.Font("Segoe UI Semibold", 15.5f, FontStyle.Regular, "Segoe UI");
            _headline = Gfx.Font("Segoe UI Semibold", 16.5f, FontStyle.Regular, "Segoe UI");
            _body = Gfx.Font("Segoe UI", 13, FontStyle.Regular, "Segoe UI");
            _small = Gfx.Font("Segoe UI", 12.5f, FontStyle.Regular, "Segoe UI");
            _line = Gfx.Font("Segoe UI", 12, FontStyle.Regular, "Segoe UI");
            _fine = Gfx.Font("Segoe UI", 11.5f, FontStyle.Regular, "Segoe UI");
            _badge = Gfx.Font("Segoe UI", 10.5f, FontStyle.Bold, "Arial");
            _pill = Gfx.Font("Segoe UI", 11, FontStyle.Bold, "Arial");
            _section = Gfx.Font("Segoe UI", 11, FontStyle.Bold, "Arial");

            _modalFade = NewTween(0);
            _dropFade = NewTween(0);
            _panel = NewTween(0);
            _tabX = NewTween(0);
            _tabW = NewTween(0);
            for (var i = 0; i < _scrollTweens.Length; i++) _scrollTweens[i] = NewTween(0);

            _settings = LauncherSettings.Load(_install.SettingsFile);
            _config = CoreConfig.Load(_install.ConfigFile);
            _mods = _install.LoadMods();
            foreach (var mod in _mods) Knob(mod);
            Recheck();
            _last = LogReport.Read(_install.GameLog);

            // If a previous launch was interrupted, make sure a plain Steam start is unmodified again.
            if (!_preview) try { if (!_checks.GameRunning && _checks.LoaderInstalled) _install.SetLoader(false); } catch (Exception) { }

            if (_options.Updated != null)
            {
                _notice = "Updated to OpenFairway " + _options.Updated;
                _openNewsAt = 0.9;
                LauncherLog.Write("Updated to " + _options.Updated);
            }
            if (_options.UpdateFailedLog != null)
            {
                var detail = "";
                try { detail = string.Join(" ", File.ReadAllLines(_options.UpdateFailedLog).Reverse().Take(UpdateLogLinesShown).Reverse()); } catch (Exception) { }
                ShowProblem(Problems.UpdateFailed("The installer stopped with an error", detail), () => _ = CheckForUpdates(thenInstall: true));
            }
            if (_preview) PreparePreview();
        }

        private Tween NewTween(float value)
        {
            var tween = new Tween(value);
            _tweens.Add(tween);
            return tween;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= WsMinimizeBox;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!_preview && !_options.Still && SceneData.Shared is { } scene)
            {
                _live = LiveScene.Start(scene, Handle, W, H);
                _live?.UploadSheet(_sheetPixels ??= Pixels(Sheet(), null));
            }
            try
            {
                var round = DwmCornerRound;
                DwmSetWindowAttribute(Handle, DwmWindowCornerPreference, ref round, sizeof(int));
                var border = ColorRef(Theme.Paper);
                DwmSetWindowAttribute(Handle, DwmBorderColor, ref border, sizeof(int));
            }
            catch (Exception) { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _wall.Restart();
            _lastTick = 0;
            StartPacer();
            if (_preview) return;
            _ = CheckForUpdates(thenInstall: _options.Press == "update");
            // Development: "add:<game id>" adds OpenFairway to that game as the Settings button would.
            if (_options.Press?.StartsWith(PressAdd, StringComparison.Ordinal) == true &&
                _scan.FirstOrDefault(s => s.Edition.Id == _options.Press.Substring(PressAdd.Length) && s.Install == null && s.GameDir != null) is { } toAdd)
            {
                OpenPage(Page.Settings);
                _ = AddGame(toAdd);
            }
            if (_showSignal != null)
                new Thread(() =>
                {
                    while (true)
                    {
                        _showSignal.WaitOne();
                        try { BeginInvoke(new Action(BringBack)); } catch (Exception) { return; }
                    }
                }) { IsBackground = true, Name = "show signal" }.Start();
        }

        /// <summary>Another start of the launcher asked this one to show itself.</summary>
        private void BringBack()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            StartPacer();
            MarkDirty();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CommitEditor(true);
            // Never leave the loader switched on for a plain Steam start.
            if (!_preview && _loaderOn) try { _install.SetLoader(false); } catch (Exception) { }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closing = true;
                _pacing.Set();
                _turfBrush?.Dispose();
                _turfBitmap?.Dispose();
                _wordOpen?.Dispose();
                _wordFairway?.Dispose();
                _uiLayer?.Dispose();
                _sheet?.Dispose();
                _still?.Dispose();
                _live?.Dispose();
                _display.Dispose();
                foreach (var font in new[] { _tab, _tile, _playFont, _cardTitle, _link, _noteVersion, _name, _headline, _body, _small, _line, _fine, _badge, _pill, _section }) font.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- offline rendering (stills and video frames) ---------------------------------------------------------

        /// <summary>Moves the clock past the entrance and any light sweep, so a still shows the resting window.</summary>
        public void Settle()
        {
            _now = 10.0;
            foreach (var tween in _tweens) tween.Snap(tween.Target);
            foreach (var tween in _fades.Values) tween.Snap(tween.Target);
            _scroll = _scrollTweens.Select(t => t.Target).ToArray();
        }

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

        /// <summary>A scripted walk through the window for the video: the entrance, MODS opened, one mod switched off
        /// and on again, back home, then resting on PLAY.</summary>
        public void Script(double t)
        {
            var tile = new PointF(ModsTile.X + ModsTile.Width * 0.6f, ModsTile.Y + ModsTile.Height * 0.55f);
            var row = Math.Min(2, Math.Max(0, _mods.Count - 1));
            var knob = SwitchCentre(row);
            var back = new PointF(W - PanelW + 30, 64);
            var play = new PointF(PlayTile.X + PlayTile.Width * 0.7f, PlayTile.Y + PlayTile.Height * 0.55f);
            var path = new List<(double T, PointF P)>
            {
                (0.0, new PointF(W + 60, H + 60)), (1.2, new PointF(W + 60, H + 60)), (1.9, tile), (2.25, tile),
                (2.9, knob), (4.3, knob), (4.9, back), (5.25, back), (6.0, play), (1e9, play),
            };
            var clicks = new[] { (T: 2.15, Key: "tile:mods"), (T: 3.25, Key: "modswitch:" + row), (T: 3.95, Key: "modswitch:" + row), (T: 5.15, Key: "page:back") };

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
            foreach (var click in clicks)
            {
                if (previous < click.T && t >= click.T) _pressed = click.Key;
                if (previous < click.T + 0.12 && t >= click.T + 0.12)
                {
                    _pressed = "";
                    Activate(click.Key);
                }
            }
            for (var i = 0; i < _scroll.Length; i++) _scroll[i] = _scrollTweens[i].Value(_now);
        }

        // ---- interaction -----------------------------------------------------------------------------------------

        private void Hot(RectangleF rect, string key, Action click, Cursor? cursor = null)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            _building.Add(new HotRegion { Rect = rect, Key = key, Click = click, Cursor = cursor ?? Cursors.Hand });
        }

        private string HitTest(Point p)
        {
            for (var i = _regions.Count - 1; i >= 0; i--)
                if (_regions[i].Rect.Contains(p)) return _regions[i].Key;
            return "";
        }

        private Tween Fade(string key)
        {
            if (!_fades.TryGetValue(key, out var tween)) _fades[key] = tween = new Tween(0);
            return tween;
        }

        private float HoverAmount(string key) => _fades.TryGetValue(key, out var tween) ? tween.Value(_now) : 0f;

        private bool Pressed(string key) => _pressed == key && _hover == key;

        private void SetHover(string key)
        {
            if (key == _hover) return;
            if (_hover.Length > 0) Fade(_hover).Set(0, _now, HoverTime);
            _hover = key;
            if (key.Length > 0) Fade(key).Set(1, _now, HoverTime);
            if (!_preview) Cursor = key.Length == 0 ? Cursors.Default : _regions.LastOrDefault(r => r.Key == key)?.Cursor ?? Cursors.Hand;
            MarkDirty();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetHover(HitTest(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHover("");
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var hit = HitTest(e.Location);
            if (_editor != null && hit != "setting:launch") CommitEditor(true);
            if (hit.Length == 0)
            {
                if (_modal != null) return;
                // Anywhere that is not a control moves the window, as a title bar would.
                ReleaseCapture();
                SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
                return;
            }
            _pressed = hit;
            MarkDirty();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var pressed = _pressed;
            _pressed = "";
            MarkDirty();
            if (e.Button == MouseButtons.Left && pressed.Length > 0 && pressed == HitTest(e.Location)) Activate(pressed);
        }

        private void Activate(string key)
        {
            var region = _regions.LastOrDefault(r => r.Key == key);
            region?.Click?.Invoke();
            MarkDirty();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_page == Page.None || _modal != null) return;
            CommitEditor(true);
            ScrollBy(-e.Delta / (float)WheelDelta * WheelScrollPixels);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // The launch options box handles its own keys (Esc there cancels the edit, not the page).
            if (_editor != null)
            {
                base.OnKeyDown(e);
                return;
            }
            if (_capturingKey)
            {
                CaptureKey(e.KeyCode);
                e.Handled = e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Escape)
            {
                if (_modal != null) CloseModal();
                else if (_gameMenuOpen) SetGameMenu(false);
                else if (_page != Page.None) OpenPage(Page.None);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            var files = e.Data?.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Any(ModInstaller.Accepts) && !_updating && _phase == Phase.Idle)
            {
                e.Effect = DragDropEffects.Copy;
                _dropFade.Set(1, _now, OverlayTime);
            }
            else e.Effect = DragDropEffects.None;
            MarkDirty();
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            _dropFade.Set(0, _now, OverlayTime);
            MarkDirty();
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            _dropFade.Set(0, _now, OverlayTime);
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) _ = InstallDropped(files.Where(ModInstaller.Accepts).ToArray());
            MarkDirty();
        }

        private void ShowModal(string title, string body, string confirm, Action onConfirm)
        {
            _modal = new Dialog { Title = title, Body = body, Confirm = confirm, OnConfirm = onConfirm };
            _modalFade.Set(1, _now, OverlayTime);
            MarkDirty();
        }

        private void CloseModal()
        {
            _modal = null;
            _modalFade.Set(0, _now, OverlayCloseTime);
            MarkDirty();
        }

        // ---- animation -------------------------------------------------------------------------------------------

        private bool SweepActive(double now) => now >= SweepFirst && (now - SweepFirst) % SweepEvery < SweepTime;

        private void MarkDirty()
        {
            _uiDirty = true;
            if (!_preview) Invalidate();
        }

        private void StartPacer()
        {
            _pacing.Set();
            if (_pacer != null) return;
            _pacer = new Thread(() =>
            {
                while (!_closing)
                {
                    _pacing.Wait();
                    if (_closing) return;
                    if (DwmFlush() != 0) Thread.Sleep(NoCompositionFallbackMs);
                    var waited = _wall.Elapsed.TotalSeconds - _lastTick;
                    if (_tickQueued || (!_animating && waited < _ambientFrameTime)) continue;
                    _tickQueued = true;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _tickQueued = false;
                            Tick();
                        }));
                    }
                    catch (ObjectDisposedException) { return; }
                    catch (InvalidOperationException) { return; }
                }
            }) { IsBackground = true, Name = "frame pacer" };
            _pacer.Start();
        }

        private void StopPacer() => _pacing.Reset();

        private void Tick()
        {
            var now = _wall.Elapsed.TotalSeconds;
            var previous = _lastTick;
            _lastTick = now;
            _now = now;

            if (_openNewsAt > 0 && now >= _openNewsAt)
            {
                _openNewsAt = 0;
                OpenPage(Page.News);
            }
            if (_options.Play && !_autoPlayed && now > AutoPlayAfter)
            {
                _autoPlayed = true;
                _ = Launch(true);
            }
            if (now - _lastRecheck > SetupCheckEvery && _phase == Phase.Idle && !_updating && !_rechecking)
            {
                _lastRecheck = now;
                _rechecking = true;
                Task.Run(() => ReadChecks()).ContinueWith(t =>
                {
                    _rechecking = false;
                    if (t.Status != TaskStatus.RanToCompletion || _phase != Phase.Idle) return;
                    var before = CheckSignature();
                    _checks = t.Result;
                    if (CheckSignature() != before) MarkDirty();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }

            var animating = now < EntrancesDone || SpinnerVisible()
                || _tweens.Any(t => t.Moving(now)) || _fades.Values.Any(t => t.Moving(now)) || _copiedUntil + LabelRevertMargin > now;
            for (var i = 0; i < _scroll.Length; i++)
            {
                var value = _scrollTweens[i].Value(now);
                if (Math.Abs(value - _scroll[i]) > 0.01f)
                {
                    _scroll[i] = value;
                    animating = true;
                    if (ClientRectangle.Contains(PointToClient(Cursor.Position))) SetHover(HitTest(PointToClient(Cursor.Position)));
                }
            }
            if (_live != null)
            {
                if (!Visible || WindowState == FormWindowState.Minimized) return;
                _ambientFrameTime = 0f;
                if (animating) _uiDirty = true;
                _animating = true;
                PresentLive();
                return;
            }
            if (animating || _uiDirty)
            {
                _animating = true;
                _uiDirty = true;
                Invalidate();
                return;
            }
            var interval = Form.ActiveForm == this ? AmbientFrameTime : AmbientFrameTimeBehindOthers;
            _ambientFrameTime = (float)interval;
            _animating = false;
            if (now - _lastHoleStep < interval) return;
            var sweep = SweepActive(now) || SweepActive(_lastHoleStep);
            var turning = false;   // without the live scene the hole is a still
            if (!sweep && !turning) return;
            _lastHoleStep = now;
            if (sweep) Invalidate(Rectangle.Ceiling(RectangleF.Inflate(_wordBounds, 4, 4)));
            if (turning)
            {
                var visible = _holeArea;
                var panelLeft = (int)(W - PanelW * _panel.Value(now));
                if (visible.Right > panelLeft) visible.Width = Math.Max(0, panelLeft - visible.X);
                if (visible.Width > 0) Invalidate(visible);
            }
        }

        /// <summary>How far an element's entrance has run (0 = not yet visible, 1 = in place).</summary>
        private float Entered(double delay) => Ease.Out((float)((_now - delay) / EnterTime));

        /// <summary>Draws a group that rises and fades in after <paramref name="delay"/>. The group is drawn into
        /// its own layer and faded as a whole, so it can sit over the turning hole.</summary>
        private void Reveal(Graphics g, double delay, RectangleF area, Action<Graphics> draw)
        {
            var e = Entered(delay);
            if (e <= 0f) return;
            if (e >= 1f)
            {
                draw(g);
                return;
            }
            var box = RectangleF.Inflate(area, 8, 8);
            var s = _scale;
            var w = Math.Max(1, (int)Math.Ceiling(box.Width * s));
            var h = Math.Max(1, (int)Math.Ceiling(box.Height * s));
            using (var layer = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var lg = Graphics.FromImage(layer))
                {
                    Gfx.Quality(lg);
                    lg.ScaleTransform(s, s);
                    lg.TranslateTransform(-box.X, -box.Y);
                    draw(lg);
                }
                using (var attributes = new ImageAttributes())
                {
                    attributes.SetColorMatrix(new ColorMatrix { Matrix33 = e });
                    var dy = Rise * (1f - e);
                    g.DrawImage(layer, new[] { new PointF(box.X, box.Y + dy), new PointF(box.Right, box.Y + dy), new PointF(box.X, box.Bottom + dy) },
                        new RectangleF(0, 0, w, h), GraphicsUnit.Pixel, attributes);
                }
            }
        }

        // ---- painting --------------------------------------------------------------------------------------------


        /// <summary>The whole window in one pass, at any scale (stills and video frames).</summary>
        public void PaintScene(Graphics g)
        {
            Gfx.Quality(g);
            DrawSheet(g, null);
            PaintBackdrop(g, null);
            PaintInterface(g);
            PaintSweep(g);
        }

        private void PaintInterface(Graphics g)
        {
            _building.Clear();
            PaintBrand(g);
            PaintCard(g);
            PaintHomeLinks(g);
            PaintTiles(g);
            PaintGameMenu(g);
            PaintPanel(g);
            PaintDrop(g);
            PaintModal(g);
            PaintWindowButtons(g);
            if (_pointer.HasValue) PaintPointer(g, _pointer.Value);
            // While a dialog is open, only the dialog (and the window buttons) can be clicked.
            _regions = _modal != null
                ? _building.Where(r => r.Key.StartsWith("modal:") || r.Key.StartsWith("window:")).ToList()
                : new List<HotRegion>(_building);
        }

        private LiveScene? _live;
        private Bitmap? _still;

        private static Rectangle HoleRect(SceneData scene) => new Rectangle(scene.OriginX, scene.OriginY, scene.FrameWidth, scene.FrameHeight);

        private float HoleFade => Ease.Out((float)((_now - HoleFadeDelay) / HoleFadeTime));

        /// <summary>The still of the hole: for stills of the window, and when the launcher cannot draw it live.</summary>
        private Bitmap? Still() => _still ??= SceneData.Shared?.StillImage();

        private void PaintBackdrop(Graphics g, Rectangle? clip)
        {
            var scene = SceneData.Shared;
            var frame = Still();
            if (scene == null || frame == null) return;
            var dest = HoleRect(scene);
            _holeArea = dest;
            var e = HoleFade;
            if (e <= 0f) return;
            var state = g.Save();
            if (_scale == 1f)
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
            }
            if (e >= 1f)
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                var part = clip.HasValue && _scale == 1f ? Rectangle.Intersect(dest, clip.Value) : dest;
                if (part.Width > 0 && part.Height > 0)
                    g.DrawImage(frame, part, part.X - dest.X, part.Y - dest.Y, part.Width, part.Height, GraphicsUnit.Pixel);
            }
            else
                using (var attributes = new ImageAttributes())
                {
                    attributes.SetColorMatrix(new ColorMatrix { Matrix33 = e });
                    g.DrawImage(frame, dest, 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel, attributes);
                }
            g.Restore(state);
        }

        private Bitmap? _sheet;

        /// <summary>The blueprint sheet the scene is drawn on: paper, a drafting grid, a border and a title block.
        /// The hole's frames carry the same paper and grid at their edges, pixel for pixel, so they join it without
        /// a seam (tools/render-hole.py, sheet).</summary>
        private Bitmap Sheet()
        {
            if (_sheet != null) return _sheet;
            var w = (int)Math.Round(W * _scale);
            var h = (int)Math.Round(H * _scale);
            var pixels = new int[w * h];
            int paper = Theme.Paper.ToArgb(), minor = Theme.GridMinor.ToArgb(), major = Theme.GridMajor.ToArgb();
            for (var y = 0; y < h; y++)
            {
                var ly = (int)(y / _scale);
                for (var x = 0; x < w; x++)
                {
                    var lx = (int)(x / _scale);
                    pixels[y * w + x] = lx % (Grid * 5) == 0 || ly % (Grid * 5) == 0 ? major : lx % Grid == 0 || ly % Grid == 0 ? minor : paper;
                }
            }
            var sheet = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = sheet.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            sheet.UnlockBits(data);

            // A border and a title block, outside the area the hole's frames cover.
            using (var g = Graphics.FromImage(sheet))
            {
                Gfx.Quality(g);
                g.ScaleTransform(_scale, _scale);
                using (var pen = new Pen(Theme.Alpha(Theme.Blueprint, 0.22f), 1f)) g.DrawRectangle(pen, 12.5f, 12.5f, W - 25, H - 25);
                using (var title = Gfx.Font("Bahnschrift SemiBold", 11.5f, FontStyle.Regular, "Segoe UI"))
                using (var credit = Gfx.Font("Bahnschrift", 9.5f, FontStyle.Regular, "Segoe UI"))
                {
                    const string hole = "HOLE 8  ·  PAR 4  ·  LIDAR SCAN";
                    const string osm = "MAP © OPENSTREETMAP CONTRIBUTORS";
                    var cx = HoleCentre.X;
                    var tw = Gfx.TitleWidth(hole, title, 1.6f);
                    Gfx.Title(g, hole, title, Theme.Alpha(Theme.Blueprint, 0.55f), cx - tw / 2, 588, 1.6f);
                    var ow = Gfx.TitleWidth(osm, credit, 1.2f);
                    Gfx.Title(g, osm, credit, Theme.Alpha(Theme.Blueprint, 0.38f), cx - ow / 2, 607, 1.2f);
                }
            }
            return _sheet = sheet;
        }

        private void DrawSheet(Graphics g, Rectangle? clip)
        {
            var state = g.Save();
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            if (clip.HasValue && _scale == 1f)
            {
                var part = Rectangle.Intersect(clip.Value, new Rectangle(0, 0, W, H));
                if (part.Width > 0 && part.Height > 0) g.DrawImage(Sheet(), part, part.X, part.Y, part.Width, part.Height, GraphicsUnit.Pixel);
            }
            else g.DrawImage(Sheet(), 0, 0, W, H);
            g.Restore(state);
        }

        private void BuildWordmark()
        {
            if (_wordOpen != null) return;
            const float em = 64f, width = 292f, top = 54f;
            var format = StringFormat.GenericTypographic;
            var open = new GraphicsPath();
            open.AddString("OPEN", _display, (int)FontStyle.Italic, em, PointF.Empty, format);
            var fairway = new GraphicsPath();
            fairway.AddString("FAIRWAY", _display, (int)FontStyle.Italic, em, PointF.Empty, format);

            // FAIRWAY sets the width; OPEN stacks above it with both left edges on the column edge.
            var fb = fairway.GetBounds();
            var fit = width / fb.Width;
            using (var m = new Matrix())
            {
                m.Scale(fit, fit);
                open.Transform(m);
                fairway.Transform(m);
            }
            var ob = open.GetBounds();
            fb = fairway.GetBounds();
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
            BuildWordmark();
            var open = _wordOpen!;
            var fairway = _wordFairway!;
            Reveal(g, 0.08, _wordBounds, lg =>
            {
                using (var white = new SolidBrush(Theme.Text)) lg.FillPath(white, open);
                lg.FillPath(_turfBrush!, fairway);
            });

            var tagline = new RectangleF(L0 + 1, _wordBounds.Bottom + 16, 380, 20);
            Reveal(g, 0.16, tagline, lg => Gfx.TextFit(lg, Tagline, _body, Theme.Muted, tagline));
        }

        /// <summary>What the launcher is, under its name. The series, not the games: which game it plays is beside PLAY.</summary>
        private const string Tagline = "Mod manager and offline launcher for PGA TOUR 2K";

        private static bool SameInstall(Install a, Install b) => string.Equals(a.Home, b.Home, StringComparison.OrdinalIgnoreCase);

        /// <summary>Looks through the Steam libraries again; OpenFairway set up meanwhile (by the installer) joins the
        /// games the launcher switches between.</summary>
        private void ScanGames()
        {
            var shown = _install;
            _installs = Install.FindAll(_home);
            _install = _installs.FirstOrDefault(i => SameInstall(i, shown)) ?? shown;
            _scan = Install.Scan(_installs);
            LauncherLog.Write("Scanned: " + string.Join(", ", _scan.Select(s => s.Edition.Short + (s.Install != null ? " set up" : s.GameDir != null ? " found" : " missing"))));
            _uiDirty = true;
            MarkDirty();
        }

        private void OfferAddGame(GameScan game) =>
            ShowModal("ADD " + game.Edition.Name.ToUpperInvariant() + "?",
                $"OpenFairway is set up next to {game.Edition.Name}, with the mods made for it, copied from this one. The game's own files stay as Steam installed them, and Uninstall OpenFairway.cmd removes it again.",
                "ADD", () => _ = AddGame(game));

        private async Task AddGame(GameScan game)
        {
            if (_addingGame || game.GameDir == null) return;
            _addingGame = true;
            _notice = "Adding OpenFairway to " + game.Edition.Name;
            MarkDirty();
            try
            {
                var source = _install;
                var gameDir = game.GameDir;
                var added = await Task.Run(() => source.AddTo(game.Edition, gameDir));
                LauncherLog.Write($"Added OpenFairway to {game.Edition.Name} in {gameDir}");
                ScanGames();
                SwitchGame(_installs.FirstOrDefault(i => SameInstall(i, added)) ?? added);
                _notice = "OpenFairway is set up in " + game.Edition.Name;
            }
            catch (InstallException e)
            {
                ShowProblem(Problems.AddGameFailed(game.Edition, e.Message), null);
            }
            catch (Exception e)
            {
                ShowProblem(Problems.AddGameFailed(game.Edition, "Windows did not let the launcher write to the game's folder: " + e.Message), null);
            }
            finally
            {
                _addingGame = false;
                MarkDirty();
            }
        }

        /// <summary>Shows another game's OpenFairway: its status, mods and settings. Remembered for the next start.</summary>
        private void SwitchGame(Install target)
        {
            if (target == _install || _updating || _phase != Phase.Idle) return;
            LauncherLog.Write("Switched to " + target.Edition.Name);
            _install = target;
            _settings = LauncherSettings.Load(_install.SettingsFile);
            _config = CoreConfig.Load(_install.ConfigFile);
            _mods = _install.LoadMods();
            foreach (var mod in _mods) Knob(mod);
            _problem = null;
            Recheck();
            _last = LogReport.Read(_install.GameLog);
            try
            {
                var remembered = _install == _home ? _settings : LauncherSettings.Load(_home.SettingsFile);
                remembered.Game = target.Edition.Id;
                remembered.Save();
            }
            catch (Exception e) { LauncherLog.Write("Could not remember the game: " + e.Message); }
            _uiDirty = true;
            Invalidate();
        }

        /// <summary>Now and then a band of sunlight passes over the grass of the wordmark. It is drawn over the cached
        /// interface, so it never makes the whole interface draw again.</summary>
        private void PaintSweep(Graphics g)
        {
            var fairway = _wordFairway;
            if (fairway == null || !SweepActive(_now) || Entered(0.08) < 1f || _modal != null || _dropFade.Value(_now) > 0.001f) return;
            var k = Ease.InOut((float)(((_now - SweepFirst) % SweepEvery) / SweepTime));
            var fb = fairway.GetBounds();
            const float band = 150f;
            var cx = fb.X - band + (fb.Width + band * 2) * k;
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
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

        private void PaintHomeLinks(Graphics g)
        {
            var area = new RectangleF(L0, 548, 404, 70);
            Reveal(g, 0.5, area, lg =>
            {
                var x = L0;
                x = LinkLabel(lg, x, 552, "WHAT'S NEW", "home:news", () => OpenPage(Page.News), external: false) + 30;
                LinkLabel(lg, x, 552, "GITHUB", "home:github", () => Install.OpenUrl(GitHub.RepoUrl), external: true);
                Gfx.TextFit(lg, "Unofficial. Not affiliated with 2K, HB Studios or the PGA TOUR.", _fine, Theme.Faint, new RectangleF(L0, 594, 404, 18));
            });
        }

        /// <summary>A heavy italic text button, as the game labels its own links. Returns its right edge.</summary>
        private float LinkLabel(Graphics g, float x, float y, string label, string key, Action click, bool external)
        {
            var hover = HoverAmount(key);
            var color = Theme.Mix(Theme.Muted, Theme.Text, hover);
            var w = Gfx.TitleWidth(label, _link, LabelTracking * 0.7f);
            Gfx.Title(g, label, _link, color, x, y + 1, LabelTracking * 0.7f);
            var right = x + w;
            if (external)
            {
                Gfx.External(g, right + 7, y + 5, 8.5f, color);
                right += 17;
            }
            Hot(new RectangleF(x - 6, y - 6, right - x + 12, 30), key, click);
            return right;
        }

        private void PaintTiles(Graphics g)
        {
            var on = _mods.Count(m => m.Enabled);
            Reveal(g, 0.30, GameRect, PaintGamePicker);
            Reveal(g, 0.36, SettingsTile, lg => Tile(lg, SettingsTile, "tile:settings", "SETTINGS", null,
                (ig, cx, cy, c) => Gfx.Gear(ig, cx, cy, 10.5f, c), () => OpenPage(Page.Settings)));
            Reveal(g, 0.42, ModsTile, lg => Tile(lg, ModsTile, "tile:mods", "MODS",
                _mods.Count == 0 ? "None installed yet" : $"{on} of {_mods.Count} on", (ig, cx, cy, c) => Gfx.Layers(ig, cx, cy, 11f, c), () => OpenPage(Page.Mods)));
            Reveal(g, 0.48, RectangleF.Union(PlayTile, new RectangleF(PlayTile.X - 20, PlayTile.Bottom, PlayTile.Width + 40, 30)), PaintPlay);
        }

        /// <summary>A slate tile on the scene, like the game's own menu tiles.</summary>
        private void Tile(Graphics g, RectangleF r, string key, string title, string? sub, Action<Graphics, float, float, Color> icon, Action click)
        {
            var hover = HoverAmount(key);
            if (Pressed(key)) r.Offset(0, 1);
            Gfx.FillVertical(g, r, 4, Theme.Alpha(Theme.Mix(Theme.TileTop, Theme.RaisedTop, hover), 0.94f), Theme.Alpha(Theme.Mix(Theme.TileBottom, Theme.RaisedBottom, hover), 0.94f));
            Gfx.Stroke(g, r, 4, Theme.Alpha(Theme.Line, 0.9f));
            var titleY = sub == null ? r.Y + (r.Height - 21) / 2 : r.Y + 13;
            Gfx.Title(g, title, _tile, Theme.Text, r.X + 18, titleY, TileTracking);
            if (sub != null) Gfx.TextFit(g, sub, _small, Theme.Muted, new RectangleF(r.X + 19, r.Y + 39, r.Width - 70, 18));
            icon(g, r.Right - 28, r.Y + r.Height / 2, Theme.Mix(Theme.Faint, Theme.Text, hover));
            Hot(r, key, click);
        }

        private void PaintDrop(Graphics g)
        {
            var e = _dropFade.Value(_now);
            if (e <= 0.001f) return;
            using (var scrim = new SolidBrush(Theme.Alpha(Theme.Ink, 0.9f * e))) g.FillRectangle(scrim, 0, 0, W, H);
            var r = new RectangleF(28, 28, W - 56, H - 56);
            using (var pen = new Pen(Theme.Alpha(Theme.Cream, 0.8f * e), 1.6f) { DashPattern = new[] { 5f, 4f } })
            using (var path = Gfx.Round(r, 6))
                g.DrawPath(pen, path);
            const string title = "DROP TO INSTALL";
            var tw = Gfx.TitleWidth(title, _playFont, PlayTracking);
            Gfx.Title(g, title, _playFont, Theme.Alpha(Theme.Text, e), (W - tw) / 2, H / 2f - 40, PlayTracking);
            const string sub = "A mod's .zip file or its folder. Mods you already have are replaced; their saved data stays.";
            var sw = Gfx.Width(sub, _body);
            Gfx.Text(g, sub, _body, Theme.Alpha(Theme.Muted, e), (W - sw) / 2, H / 2f + 12);
        }

        private void PaintModal(Graphics g)
        {
            var e = _modalFade.Value(_now);
            if (e <= 0.001f) return;
            using (var scrim = new SolidBrush(Theme.Alpha(Theme.Ink, 0.72f * e))) g.FillRectangle(scrim, 0, 0, W, H);
            var modal = _modal;
            if (modal == null) return;
            var r = new RectangleF((W - 460) / 2f, (H - 196) / 2f + 10 * (1 - e), 460, 196);
            Gfx.Fill(g, r, 4, Theme.Alpha(Theme.Strip, e));
            Gfx.Stroke(g, r, 4, Theme.Alpha(Theme.Line, e));
            Gfx.Title(g, modal.Title, _tab, Theme.Alpha(Theme.Text, e), r.X + 28, r.Y + 26, TabTracking);
            Gfx.Paragraph(g, modal.Body, _body, Theme.Alpha(Theme.Muted, e), r.X + 28, r.Y + 66, r.Width - 56, 19, 3);
            var x = r.Right - 28;
            var cancelW = Gfx.Width("CANCEL", _pill) + 28;
            x -= cancelW;
            PillButton(g, x, r.Bottom - 50, "CANCEL", "modal:cancel", CloseModal, primary: false, height: 30);
            var confirmW = Gfx.Width(modal.Confirm, _pill) + 28;
            x -= confirmW + 10;
            PillButton(g, x, r.Bottom - 50, modal.Confirm, "modal:ok", () =>
            {
                var action = modal.OnConfirm;
                CloseModal();
                action?.Invoke();
            }, primary: true, height: 30);
        }

        /// <summary>A small rounded button: cream when it is the one to press, an outline otherwise. Returns its width.</summary>
        private float PillButton(Graphics g, float x, float y, string label, string key, Action click, bool primary, float height = 26)
        {
            var hover = HoverAmount(key);
            var w = Gfx.Width(label, _pill) + 28;
            var r = new RectangleF(x, y, w, height);
            if (Pressed(key)) r.Offset(0, 1);
            if (primary) Gfx.Fill(g, r, 4, Theme.Mix(Theme.Cream, Color.White, hover * 0.6f));
            else
            {
                Gfx.Fill(g, r, 4, Theme.Alpha(Theme.RaisedTop, 0.55f * hover));
                Gfx.Stroke(g, r, 4, Theme.Mix(Theme.Line, Theme.Faint, hover));
            }
            Gfx.TextFit(g, label, _pill, primary ? Theme.Bg : Theme.Text, new RectangleF(r.X, r.Y + (height - 14) / 2, w, 16), StringAlignment.Center);
            Hot(r, key, click);
            return w;
        }

        private void PaintWindowButtons(Graphics g)
        {
            var minHover = HoverAmount("window:min");
            var closeHover = HoverAmount("window:close");
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
            Hot(MinRect, "window:min", () => { if (!_preview) WindowState = FormWindowState.Minimized; });
            Hot(CloseRect, "window:close", () => { if (!_preview) Close(); });
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

        private const int WsMinimizeBox = 0x00020000;
        private const int DwmWindowCornerPreference = 33, DwmBorderColor = 34, DwmCornerRound = 2;
        private const int WmNcLButtonDown = 0xA1, HtCaption = 2;

        private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
