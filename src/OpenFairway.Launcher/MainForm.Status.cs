using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OpenFairway.Launcher
{
    /// <summary>The status card and everything that feeds it: the checks, the launch and its confirmation, the
    /// update, and problems with what to do about them.</summary>
    internal sealed partial class MainForm
    {
        private sealed class Checks
        {
            public bool GameInstalled, CoreInstalled, LoaderInstalled, OtherLoader, GameRunning;
            public string CoreVersion = "";
            public GameBuild? Build;
        }

        private enum Phase { Idle, Starting, Loaded }
        private enum UpdateCheck { Off, Checking, UpToDate, Available, Failed }
        private enum Glyph { Check, Alert, Spinner, Dot, Download }

        private sealed class CardLine
        {
            public Glyph Glyph;
            public Color Color;
            public string Text = "";
        }

        private sealed class CardModel
        {
            public string Badge = "READY";
            public Color BadgeFill = Theme.Green, BadgeInk = Theme.Bg, Accent = Theme.Green;
            public Glyph Glyph = Glyph.Check;
            public Color GlyphColor = Theme.Green;
            public string Headline = "", Body = "";
            public List<CardLine> Lines = new List<CardLine>();
            public List<(string Label, string Key, Action Do, bool Primary)> Actions = new List<(string, string, Action, bool)>();
        }

        private static readonly TimeSpan SteamStartLimit = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan PlatformStartLimit = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan ModsLoadLimit = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan StartupLimit = TimeSpan.FromMinutes(6);
        private static readonly TimeSpan GameStartLimit = TimeSpan.FromMinutes(8);
        private static readonly TimeSpan LogClockSlack = TimeSpan.FromSeconds(2);
        private const int ProcessPollMs = 500, LogPollMs = 400, LoaderReadMs = 3000, InstallerHandoverMs = 500;
        private const int ShowResultMs = 2600, ShowResultBeforeClosingMs = 1800;
        private const double CopiedLabelSeconds = 2.2;
        private const int BodyLinesWithActions = 3, BodyLinesWithChecks = 2;

        private Checks _checks = new Checks();
        private Phase _phase = Phase.Idle;
        private bool _phaseWithMods, _loaderOn, _autoPlayed;
        private string _stage = "";
        private LogReport? _session, _last;
        private Problem? _problem;
        private Action? _retry;
        private string? _notice;
        private double _openNewsAt, _copiedUntil;

        private UpdateCheck _updateCheck = UpdateCheck.Off;
        private List<Release>? _releases;
        private Release? _available;
        private bool _updateDismissed, _updating;
        private float _progress = -1;
        private string _updateStage = "";

        private string InstalledVersion => _options.PretendVersion ?? _checks.CoreVersion;

        private bool _rechecking;

        private void Recheck() => _checks = ReadChecks();

        private Checks ReadChecks()
        {
            return new Checks
            {
                GameInstalled = _install.GameInstalled,
                CoreInstalled = _install.CoreInstalled,
                LoaderInstalled = _install.LoaderInstalled,
                OtherLoader = _install.OtherLoader,
                GameRunning = _install.GameRunning,
                CoreVersion = _install.CoreVersion(),
                Build = _install.ReadGameBuild(),
            };
        }

        private string CheckSignature() =>
            $"{_checks.GameInstalled}{_checks.CoreInstalled}{_checks.LoaderInstalled}{_checks.OtherLoader}{_checks.GameRunning}{_checks.CoreVersion}{_checks.Build?.BuildId}{_checks.Build?.UpdatePending}";

        /// <summary>A problem with the installation itself, which blocks playing with mods until it is fixed.</summary>
        private Problem? SetupProblem() =>
            !_checks.GameInstalled ? Problems.GameMissing()
            : _checks.OtherLoader ? Problems.OtherLoader()
            : !_checks.CoreInstalled ? Problems.CoreMissing()
            : !_checks.LoaderInstalled ? Problems.LoaderMissing()
            : null;

        private void ShowProblem(Problem problem, Action? retry)
        {
            _problem = problem;
            _retry = retry;
            LauncherLog.Write($"Problem {problem.Id}: {problem.Title}{(problem.Detail.Length > 0 ? " (" + problem.Detail + ")" : "")}");
            MarkDirty();
        }

        private void Do(Act act)
        {
            switch (act)
            {
                case Act.Retry:
                    var retry = _retry;
                    _problem = null;
                    _retry = null;
                    Recheck();
                    retry?.Invoke();
                    break;
                case Act.OpenLogs:
                    Directory.CreateDirectory(_install.LogsDir);
                    Install.OpenFolder(_install.LogsDir, _install.GameLog);
                    break;
                case Act.CopyDetails:
                    var problem = _problem ?? SetupProblem();
                    if (problem == null) break;
                    try
                    {
                        Clipboard.SetText(Problems.Report(problem, _install, _mods, LogReport.Read(_install.GameLog) ?? _session));
                        _copiedUntil = _now + CopiedLabelSeconds;
                    }
                    catch (Exception) { /* the clipboard can be busy; the button can be pressed again */ }
                    break;
                case Act.Releases:
                    Install.OpenUrl(GitHub.ReleasesUrl);
                    break;
                case Act.Dismiss:
                    _problem = null;
                    _retry = null;
                    break;
            }
            MarkDirty();
        }

        private bool SpinnerVisible() =>
            _updating || _phase == Phase.Starting || _updateCheck == UpdateCheck.Checking || (_page == Page.News && _notesLoading);

        // ---- the card ----------------------------------------------------------------------------------------------

        private List<CardLine> CheckLines()
        {
            var lines = new List<CardLine>();
            if (!_checks.GameInstalled) lines.Add(Line(Glyph.Alert, Theme.Red, "PGA TOUR 2K25 not found"));
            else if (_checks.Build?.UpdatePending == true) lines.Add(Line(Glyph.Alert, Theme.Gold, "Steam has a game update"));
            else lines.Add(Line(Glyph.Check, Theme.Green, _checks.Build?.BuildId is { Length: > 0 } id ? "Game build " + id : "PGA TOUR 2K25 found"));

            lines.Add(!_checks.CoreInstalled ? Line(Glyph.Alert, Theme.Red, "OpenFairway files missing") : Line(Glyph.Check, Theme.Green, "OpenFairway " + InstalledVersion));

            lines.Add(_checks.OtherLoader ? Line(Glyph.Alert, Theme.Red, "Another loader found")
                : !_checks.LoaderInstalled ? Line(Glyph.Alert, Theme.Red, "Loader missing")
                : Line(Glyph.Check, Theme.Green, "Loader installed"));

            switch (_updateCheck)
            {
                case UpdateCheck.Checking: lines.Add(Line(Glyph.Spinner, Theme.Faint, "Checking for updates")); break;
                case UpdateCheck.UpToDate: lines.Add(Line(Glyph.Check, Theme.Green, "Up to date")); break;
                case UpdateCheck.Available: lines.Add(Line(Glyph.Download, Theme.Gold, (_available?.Version ?? "") + " is ready")); break;
                case UpdateCheck.Failed: lines.Add(Line(Glyph.Dot, Theme.Faint, "Could not check for updates")); break;
                default: lines.Add(Line(Glyph.Dot, Theme.Faint, "Update checks are off")); break;
            }
            return lines;
        }

        private static CardLine Line(Glyph glyph, Color color, string text) => new CardLine { Glyph = glyph, Color = color, Text = text };

        private CardModel BuildCard()
        {
            var on = _mods.Count(m => m.Enabled);
            var model = new CardModel { Lines = CheckLines() };
            void Slate(CardModel m, string badge)
            {
                m.Badge = badge;
                m.BadgeFill = Theme.TileTop;
                m.BadgeInk = Theme.Text;
                m.Accent = Theme.Faint;
                m.Glyph = Glyph.Spinner;
                m.GlyphColor = Theme.Muted;
            }

            if (_updating)
            {
                Slate(model, "UPDATING");
                model.Headline = _updateStage;
                model.Body = "Keep this window open. The launcher restarts by itself when the new version is in place.";
                return model;
            }
            if (_phase == Phase.Starting)
            {
                Slate(model, "STARTING");
                model.Headline = _stage;
                model.Body = !_phaseWithMods ? "The normal game, without mods and online."
                    : _settings.KeepOpen ? "This takes a minute. The launcher steps aside while you play and comes back when you close the game."
                    : "This takes a minute. The launcher closes once the game is up.";
                return model;
            }
            var problem = _problem ?? SetupProblem();
            if (problem != null)
            {
                model.Badge = problem.Badge;
                model.BadgeFill = Theme.Red;
                model.BadgeInk = Theme.Text;
                model.Accent = Theme.Red;
                model.Glyph = Glyph.Alert;
                model.GlyphColor = Theme.Red;
                model.Headline = problem.Title;
                model.Body = problem.Body;
                model.Lines.Clear();
                var acts = problem.Actions;
                // Installation problems have no launch to retry; Retry checks again.
                if (_problem == null) _retry = null;
                foreach (var act in acts)
                {
                    var label = act == Act.CopyDetails && _copiedUntil > _now ? "COPIED" : Problems.Label(act);
                    var a = act;
                    model.Actions.Add((label, "card:" + act, () => Do(a), act == acts[0]));
                }
                return model;
            }
            if (_phase == Phase.Loaded && _session != null)
            {
                model.Badge = "LOADED";
                var failed = _session.Failed.Count;
                model.Headline = failed == 0 ? "Loaded into the game" : $"Loaded, but {(failed == 1 ? "1 mod" : failed + " mods")} failed";
                var names = string.Join(", ", _session.Failed.Keys.Select(f => _mods.FirstOrDefault(m => m.Folder.Equals(f, StringComparison.OrdinalIgnoreCase))?.Name ?? f));
                model.Body = failed == 0 ? $"Offline, with {(_session.Loaded.Count == 1 ? "1 mod" : _session.Loaded.Count + " mods")} running. Enjoy the round."
                    : $"{names} did not load; the rest are running. Mods shows why.";
                if (failed > 0)
                {
                    model.Glyph = Glyph.Alert;
                    model.GlyphColor = Theme.Red;
                }
                return model;
            }
            if (_phase == Phase.Loaded)
            {
                model.Badge = "STARTED";
                model.Headline = "The normal game is starting";
                model.Body = "Without mods and online. Use PLAY to play with mods next time.";
                return model;
            }
            if (_checks.GameRunning)
            {
                model.Badge = "RUNNING";
                model.BadgeFill = Theme.TileTop;
                model.BadgeInk = Theme.Text;
                model.Accent = Theme.Faint;
                model.Glyph = Glyph.Dot;
                model.GlyphColor = Theme.Muted;
                model.Headline = "The game is running";
                model.Body = "Close it to start again from here. Mod switches you change now apply the next time.";
                return model;
            }
            if (_updateCheck == UpdateCheck.Available && _available != null && !_updateDismissed)
            {
                model.Badge = "UPDATE";
                model.BadgeFill = Theme.Gold;
                model.Accent = Theme.Gold;
                model.Glyph = Glyph.Download;
                model.GlyphColor = Theme.Gold;
                model.Headline = $"OpenFairway {_available.Version} is ready";
                var size = _available.Package != null && _available.Package.Size > 0 ? $" ({_available.Package.Size / 1048576.0:0} MB)" : "";
                model.Body = $"Press UPDATE to install it{size}. Your mods, settings and saves stay as they are.";
                model.Lines.Clear();
                model.Actions.Add(("WHAT'S NEW", "card:news", () => OpenPage(Page.News), false));
                model.Actions.Add(("NOT NOW", "card:later", () => { _updateDismissed = true; MarkDirty(); }, false));
                return model;
            }
            model.Headline = _notice ?? "Ready to play";
            model.Body = $"Offline when modded. {on} of {_mods.Count} mods on." + (_last != null ? " " + _last.Summary(lastTime: true) : "");
            return model;
        }

        private void PaintCard(Graphics g)
        {
            var model = BuildCard();
            var r = CardRect;
            Reveal(g, 0.28, r, lg =>
            {
                Gfx.Fill(lg, r, 4, Theme.Alpha(Theme.Strip, 0.93f));
                Gfx.Stroke(lg, r, 4, Theme.Alpha(Theme.Line, 0.6f));
                Gfx.Fill(lg, new RectangleF(r.X, r.Y, 3, r.Height), 1.5f, model.Accent);
                var x0 = r.X + 24;
                var x1 = r.Right - 20;

                Gfx.Title(lg, "STATUS", _cardTitle, Theme.Muted, x0, r.Y + 20, LabelTracking);
                var bw = Gfx.Width(model.Badge, _badge) + 20;
                var badge = new RectangleF(x1 - bw, r.Y + 17, bw, 21);
                Gfx.Fill(lg, badge, 10.5f, model.BadgeFill);
                Gfx.TextFit(lg, model.Badge, _badge, model.BadgeInk, new RectangleF(badge.X, badge.Y + 3.5f, bw, 16), StringAlignment.Center);

                PaintGlyph(lg, model.Glyph, x0 + 8, r.Y + 64, 8.5f, model.GlyphColor);
                Gfx.TextFit(lg, model.Headline, _headline, Theme.Text, new RectangleF(x0 + 26, r.Y + 52, x1 - x0 - 26, 24));
                var bodyLines = model.Actions.Count > 0 ? BodyLinesWithActions : BodyLinesWithChecks;
                Gfx.Paragraph(lg, model.Body, _body, Theme.Muted, x0 + 26, r.Y + 79, x1 - x0 - 26, 18, bodyLines);

                if (model.Actions.Count > 0)
                {
                    var x = x0 + 26;
                    foreach (var (label, key, act, primary) in model.Actions)
                        x += PillButton(lg, x, r.Bottom - 39, label, key, act, primary) + 8;
                }
                else if (model.Lines.Count > 0)
                {
                    using (var line = new Pen(Theme.Alpha(Theme.Line, 0.8f), 1f)) lg.DrawLine(line, x0, r.Y + 127.5f, x1, r.Y + 127.5f);
                    var colW = new[] { 186f, x1 - x0 - 186f };
                    for (var i = 0; i < model.Lines.Count && i < 4; i++)
                    {
                        var item = model.Lines[i];
                        // Two columns: the game and the loader on the left, OpenFairway and updates on the right.
                        var col = i == 0 || i == 2 ? 0 : 1;
                        var row = i < 2 ? 0 : 1;
                        var x = x0 + (col == 0 ? 0 : colW[0]);
                        var y = r.Y + 140 + row * 21;
                        PaintGlyph(lg, item.Glyph, x + 6, y + 8, 5.5f, item.Color);
                        Gfx.TextFit(lg, item.Text, _line, Theme.Muted, new RectangleF(x + 18, y, colW[col] - 22, 18));
                    }
                }
            });
        }

        private void PaintGlyph(Graphics g, Glyph glyph, float cx, float cy, float r, Color color)
        {
            switch (glyph)
            {
                case Glyph.Check: Gfx.Check(g, cx, cy, r, color, Theme.Bg); break;
                case Glyph.Alert: Gfx.Alert(g, cx, cy, r, color, color == Theme.Red ? Theme.Text : Theme.Bg); break;
                case Glyph.Spinner: Gfx.Spinner(g, cx, cy, r * 0.82f, color, _now); break;
                case Glyph.Download: Gfx.Download(g, cx, cy, r * 0.95f, color, Math.Max(1.4f, r * 0.2f)); break;
                default:
                    using (var brush = new SolidBrush(color)) g.FillEllipse(brush, cx - r * 0.42f, cy - r * 0.42f, r * 0.84f, r * 0.84f);
                    break;
            }
        }

        // ---- the play tile -----------------------------------------------------------------------------------------

        private void PaintPlay(Graphics g)
        {
            var r = PlayTile;
            var key = "tile:play";
            var busy = _updating || _phase != Phase.Idle || _checks.GameRunning;
            var update = !busy && _updateCheck == UpdateCheck.Available && _available != null && !_updateDismissed && _problem == null;
            var hover = busy ? 0f : HoverAmount(key);
            if (!busy && Pressed(key)) r.Offset(0, 1);

            if (busy)
            {
                Gfx.FillVertical(g, r, 4, Theme.Alpha(Theme.TileTop, 0.94f), Theme.Alpha(Theme.TileBottom, 0.94f));
                Gfx.Stroke(g, r, 4, Theme.Alpha(Theme.Line, 0.9f));
                var label = _updating ? "UPDATING" : _phase == Phase.Starting ? "STARTING" : "PLAYING";
                Gfx.Title(g, label, _playFont, Theme.Muted, r.X + 20, r.Y + 14, PlayTracking);
                var sub = _updating ? _updateStage : _phase == Phase.Starting ? _stage : "The game is running";
                Gfx.TextFit(g, sub, _small, Theme.Faint, new RectangleF(r.X + 21, r.Bottom - 29, r.Width - 40, 18));
                if (_updating && _progress >= 0)
                {
                    var bar = new RectangleF(r.X + 1, r.Bottom - 5, (r.Width - 2) * _progress, 4);
                    Gfx.Fill(g, bar, 2, Theme.Cream);
                }
            }
            else if (update)
            {
                // An update is a different action from playing: the cream of the game's selected tab.
                Gfx.Fill(g, r, 4, Theme.Mix(Theme.Cream, Color.White, hover * 0.55f));
                Gfx.Title(g, "UPDATE", _playFont, Theme.Bg, r.X + 20, r.Y + 14, PlayTracking);
                var w = Gfx.TitleWidth("UPDATE", _playFont, PlayTracking);
                Gfx.Download(g, r.X + 20 + w + 20, r.Y + 33 + 3 * hover, 11, Theme.Bg, 2.4f);
                Gfx.TextFit(g, $"OpenFairway {_available!.Version}", _small, Theme.Alpha(Theme.Bg, 0.68f), new RectangleF(r.X + 21, r.Bottom - 29, r.Width - 40, 18));
                Hot(r, key, () => _ = RunUpdate());
            }
            else
            {
                // PLAY is a strip of mowed fairway, the same grass as FAIRWAY in the wordmark.
                using (var path = Gfx.Round(r, 4)) g.FillPath(PlayTurf(r), path);
                if (hover > 0.001f) Gfx.Fill(g, r, 4, Theme.Alpha(Color.White, 0.12f * hover));
                Gfx.Stroke(g, r, 4, Theme.Alpha(Color.White, 0.14f + 0.12f * hover));
                var shadow = Theme.Alpha(Theme.Ink, 0.35f);
                Gfx.Title(g, "PLAY", _playFont, shadow, r.X + 20, r.Y + 15.5f, PlayTracking);
                Gfx.Title(g, "PLAY", _playFont, Theme.Text, r.X + 20, r.Y + 14, PlayTracking);
                var w = Gfx.TitleWidth("PLAY", _playFont, PlayTracking);
                Gfx.PlayArrow(g, r.X + 20 + w + 14 + 4 * hover, r.Y + 35.5f, 18, shadow);
                Gfx.PlayArrow(g, r.X + 20 + w + 14 + 4 * hover, r.Y + 34, 18, Theme.Text);
                Gfx.TextFit(g, "With mods, offline", _small, Theme.Alpha(Theme.Text, 0.82f), new RectangleF(r.X + 21, r.Bottom - 29, r.Width - 40, 18));
                Hot(r, key, () => _ = Launch(true));
            }

            // The normal, unmodified game.
            var plainKey = "home:plain";
            var plainHover = busy ? 0f : HoverAmount(plainKey);
            const string first = "Play without mods";
            const string rest = "  ·  normal online game";
            var w1 = Gfx.Width(first, _small);
            var w2 = Gfx.Width(rest, _small);
            var px = r.X + (r.Width - (w1 + w2)) / 2;
            var py = PlayTile.Bottom + 9;
            Gfx.Text(g, first, _small, busy ? Theme.Faint : Theme.Mix(Theme.Muted, Theme.Text, plainHover), px, py);
            Gfx.Text(g, rest, _small, Theme.Faint, px + w1, py);
            if (plainHover > 0.01f)
                using (var pen = new Pen(Theme.Alpha(Theme.Text, 0.55f * plainHover), 1f))
                    g.DrawLine(pen, px, py + 18.5f, px + w1, py + 18.5f);
            if (!busy) Hot(new RectangleF(px - 4, py - 3, w1 + 8, 24), plainKey, () => _ = Launch(false));
        }

        private TextureBrush? _playTurf;

        /// <summary>The fairway for the play tile, made once at device resolution (like the wordmark's), a shade
        /// deeper so white lettering reads on it.</summary>
        private TextureBrush PlayTurf(RectangleF r)
        {
            if (_playTurf == null)
            {
                var bitmap = Turf.Make((int)Math.Ceiling(r.Width * _scale), (int)Math.Ceiling(r.Height * _scale), stripe: 30f * _scale, scale: _scale, darken: 0.16f);
                _playTurf = new TextureBrush(bitmap, System.Drawing.Drawing2D.WrapMode.Clamp);
                _playTurf.ScaleTransform(1f / _scale, 1f / _scale, System.Drawing.Drawing2D.MatrixOrder.Append);
            }
            _playTurf.ResetTransform();
            _playTurf.ScaleTransform(1f / _scale, 1f / _scale, System.Drawing.Drawing2D.MatrixOrder.Append);
            _playTurf.TranslateTransform(r.X, r.Y, System.Drawing.Drawing2D.MatrixOrder.Append);
            return _playTurf;
        }

        // ---- launching and confirming that OpenFairway loaded ------------------------------------------------------

        private void Stage(string text)
        {
            if (_stage == text) return;
            _stage = text;
            LauncherLog.Write("Stage: " + text);
            MarkDirty();
        }

        private async Task Launch(bool withMods)
        {
            if (_preview || _phase != Phase.Idle || _updating) return;
            CommitEditor(true);
            _problem = null;
            _notice = null;
            Recheck();
            if (_checks.GameRunning)
            {
                ShowProblem(Problems.GameRunning(), () => _ = Launch(withMods));
                return;
            }
            if (withMods && SetupProblem() is { } setup)
            {
                ShowProblem(setup, null);
                return;
            }

            _phase = Phase.Starting;
            _phaseWithMods = withMods;
            _session = null;
            var started = DateTime.Now;
            LauncherLog.Write(withMods ? "Play with mods" : "Play without mods");
            Stage("Starting the game through Steam");
            try
            {
                if (_checks.LoaderInstalled) _install.SetLoader(withMods);
                _loaderOn = withMods;
            }
            catch (Exception e)
            {
                Fail(Problems.ConfigLocked(e), () => _ = Launch(withMods));
                return;
            }
            try
            {
                _install.StartGame(_settings.LaunchOptions);
            }
            catch (Exception e)
            {
                Fail(Problems.SteamFailed(e), () => _ = Launch(withMods));
                return;
            }

            Process? game = null;
            var deadline = DateTime.UtcNow + SteamStartLimit;
            while ((game = _install.GameProcess()) == null)
            {
                if (DateTime.UtcNow > deadline)
                {
                    Fail(Problems.NoStart(), () => _ = Launch(withMods));
                    return;
                }
                await Task.Delay(ProcessPollMs);
            }
            var gameStarted = DateTime.UtcNow;

            if (!withMods)
            {
                await Task.Delay(LoaderReadMs);
                ResetLoader();
                _phase = Phase.Loaded;
                MarkDirty();
                AfterStart(game);
                return;
            }

            Stage("Waiting for OpenFairway");
            DateTime? loadedAt = null;
            var enabled = _mods.Count(m => m.Enabled);
            while (true)
            {
                await Task.Delay(LogPollMs);
                bool exited;
                try { exited = game.HasExited; } catch (Exception) { exited = true; }
                var report = LogReport.Read(_install.GameLog, started - LogClockSlack);
                if (report != null)
                {
                    _session = report;
                    // The loader has read its switch; a plain Steam start must be unmodified again.
                    ResetLoader();
                }
                if (exited)
                {
                    Fail(Problems.ClosedWhileStarting(), () => _ = Launch(true));
                    return;
                }
                if (report == null)
                {
                    if (DateTime.UtcNow - gameStarted > PlatformStartLimit)
                    {
                        Fail(Problems.NotLoaded(), () => _ = Launch(true));
                        return;
                    }
                    continue;
                }
                if (report.OfflineFailed)
                {
                    Fail(Problems.OfflineFailed(), () => _ = Launch(true));
                    return;
                }
                if (report.AttachFailed)
                {
                    Fail(Problems.AttachFailed(report.AttachError), null);
                    return;
                }
                var settled = report.Loaded.Count + report.Failed.Count + report.LeftOut.Count;
                if (!report.Offline) Stage("Taking the game offline");
                else if (!report.Attached) Stage("Offline. The game is still starting");
                else if (!report.StartupComplete || settled < enabled) Stage(enabled == 0 ? "Finishing" : $"Loading mods, {settled} of {enabled}");
                if (report.StartupComplete)
                {
                    loadedAt ??= DateTime.UtcNow;
                    if (settled >= enabled || DateTime.UtcNow - loadedAt.Value > ModsLoadLimit) break;
                }
                if (DateTime.Now - report.Written > StartupLimit || DateTime.UtcNow - gameStarted > GameStartLimit)
                {
                    Fail(Problems.TooSlow(), null);
                    return;
                }
            }

            LauncherLog.Write($"Loaded: {_session?.Loaded.Count} mods, {_session?.Failed.Count} failed");
            _phase = Phase.Loaded;
            MarkDirty();
            AfterStart(game);
        }

        private void ResetLoader()
        {
            if (!_loaderOn) return;
            try
            {
                _install.SetLoader(false);
                _loaderOn = false;
            }
            catch (Exception e) { LauncherLog.Write("Could not switch the loader off: " + e.Message); }
        }

        private void Fail(Problem problem, Action? retry)
        {
            ResetLoader();
            _phase = Phase.Idle;
            _stage = "";
            ShowProblem(problem, retry);
        }

        /// <summary>After a good start: show the result for a moment, then step aside (or close, if the player
        /// switched "Keep the launcher open" off). The launcher comes back when the game closes.</summary>
        private async void AfterStart(Process game)
        {
            await Task.Delay(_settings.KeepOpen ? ShowResultMs : ShowResultBeforeClosingMs);
            if (!_settings.KeepOpen)
            {
                Close();
                return;
            }
            LauncherLog.Write("Hidden while the game runs");
            Hide();
            StopPacer();
            await Task.Run(() =>
            {
                try { game.WaitForExit(); } catch (Exception) { }
            });
            game.Dispose();
            LauncherLog.Write("The game closed");
            _phase = Phase.Idle;
            _stage = "";
            _session = null;
            ReloadMods();
            _last = LogReport.Read(_install.GameLog);
            Recheck();
            BringBack();
        }

        private void ReloadMods()
        {
            _mods = _install.LoadMods();
            foreach (var mod in _mods) Knob(mod).Snap(mod.Enabled ? 1f : 0f);
            MarkDirty();
        }

        // ---- updates -----------------------------------------------------------------------------------------------

        private async Task CheckForUpdates(bool thenInstall = false)
        {
            if (!_settings.CheckUpdates)
            {
                _updateCheck = UpdateCheck.Off;
                MarkDirty();
                return;
            }
            _updateCheck = UpdateCheck.Checking;
            MarkDirty();
            try
            {
                _releases = await GitHub.Releases();
                _available = GitHub.Newer(_releases, InstalledVersion, _settings.PreReleases);
                _updateCheck = _available != null ? UpdateCheck.Available : UpdateCheck.UpToDate;
                LauncherLog.Write(_available != null ? $"Update available: {_available.Version}" : $"Up to date ({InstalledVersion})");
            }
            catch (Exception e)
            {
                _updateCheck = UpdateCheck.Failed;
                LauncherLog.Write("Update check failed: " + e.Message);
            }
            MarkDirty();
            if (thenInstall && _available != null) _ = RunUpdate();
        }

        private async Task RunUpdate()
        {
            var release = _available;
            if (release == null || _updating || _preview) return;
            Recheck();
            if (_checks.GameRunning)
            {
                ShowProblem(Problems.GameRunning(), () => _ = RunUpdate());
                return;
            }
            _problem = null;
            _updating = true;
            _progress = 0;
            _updateStage = "Downloading";
            LauncherLog.Write("Updating to " + release.Version);
            MarkDirty();
            try
            {
                var lastShown = -1;
                var zip = await Updater.Download(release, (done, total) =>
                {
                    var percent = total > 0 ? (int)(done * 100 / total) : -1;
                    if (percent == lastShown) return;
                    lastShown = percent;
                    BeginInvoke(new Action(() =>
                    {
                        _progress = total > 0 ? (float)done / total : -1;
                        _updateStage = total > 0 ? $"Downloading, {done / 1048576.0:0} of {total / 1048576.0:0} MB" : "Downloading";
                        MarkDirty();
                    }));
                }, CancellationToken.None);
                _updateStage = "Unpacking";
                _progress = -1;
                MarkDirty();
                var setup = await Task.Run(() => Updater.Unpack(zip));
                _updateStage = "Installing";
                MarkDirty();
                await Task.Delay(InstallerHandoverMs);
                Updater.Apply(_install, setup, release.Version);
                LauncherLog.Write("Handed over to the installer");
                Close();
            }
            catch (UpdateException e)
            {
                _updating = false;
                ShowProblem(Problems.UpdateFailed(e.Message), () => _ = RunUpdate());
            }
            catch (Exception e)
            {
                _updating = false;
                ShowProblem(Problems.UpdateFailed("The download did not finish", e.Message), () => _ = RunUpdate());
            }
        }

        // ---- installing dropped mods -------------------------------------------------------------------------------

        private async Task InstallDropped(string[] paths)
        {
            if (paths.Length == 0) return;
            Recheck();
            if (_checks.GameRunning)
            {
                ShowProblem(Problems.InstallFailed("Close the game first: it keeps its mods open while it runs."), null);
                return;
            }
            var packages = new List<ModPackage>();
            var temps = new List<string?>();
            try
            {
                foreach (var path in paths)
                {
                    var found = await Task.Run(() =>
                    {
                        var list = ModInstaller.Inspect(path, out var temp);
                        return (list, temp);
                    });
                    packages.AddRange(found.list);
                    temps.Add(found.temp);
                }
            }
            catch (InstallException e)
            {
                temps.ForEach(ModInstaller.Cleanup);
                ShowProblem(Problems.InstallFailed(e.Message), null);
                return;
            }
            catch (Exception e)
            {
                temps.ForEach(ModInstaller.Cleanup);
                ShowProblem(Problems.InstallFailed("It could not be read: " + e.Message), null);
                return;
            }

            var replacing = packages.Select(p => (Package: p, Installed: _mods.FirstOrDefault(m => m.Folder.Equals(p.Folder, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.Installed != null).ToList();
            async Task Go()
            {
                try
                {
                    await Task.Run(() => { foreach (var p in packages) ModInstaller.Install(p, _install.ModsDir); });
                    var names = packages.Select(p => p.Name + (p.Version.Length > 0 ? " " + p.Version : "")).ToList();
                    LauncherLog.Write("Installed " + string.Join(", ", names));
                    ReloadMods();
                    _notice = "Installed " + (names.Count == 1 ? names[0] : names.Count + " mods");
                    OpenPage(Page.Mods);
                    foreach (var p in packages)
                    {
                        var index = _mods.FindIndex(m => m.Folder.Equals(p.Folder, StringComparison.OrdinalIgnoreCase));
                        if (index >= 0) Expand(_mods[index], true);
                    }
                }
                catch (Exception e)
                {
                    ShowProblem(Problems.InstallFailed("Windows did not let the launcher write to the mods folder: " + e.Message), null);
                }
                finally
                {
                    temps.ForEach(ModInstaller.Cleanup);
                }
            }

            if (replacing.Count == 0)
            {
                await Go();
                return;
            }
            var first = replacing[0];
            var body = replacing.Count == 1
                ? $"{first.Installed!.Name} {first.Installed.Version} is installed. Replace it with {first.Package.Version}? Its saved data stays."
                : $"{string.Join(", ", replacing.Select(x => x.Installed!.Name))} are installed. Replace them? Their saved data stays.";
            ShowModal(replacing.Count == 1 ? "REPLACE THIS MOD?" : "REPLACE THESE MODS?", body.Replace("  ", " "), "REPLACE", () => _ = Go());
        }

        // ---- previews (stills for the README and for checking the design) --------------------------------------------

        private void PreparePreview()
        {
            _updateCheck = UpdateCheck.UpToDate;
            _notes = Notes.Parse(Notes.Embedded());
            _notesOffline = false;
            var fake = new Release { Tag = "v0.5.0", Package = new ReleaseAsset { Size = 39 * 1048576 } };
            switch (_options.State)
            {
                case "update":
                    _available = fake;
                    _updateCheck = UpdateCheck.Available;
                    break;
                case "updating":
                    _available = fake;
                    _updating = true;
                    _progress = 0.42f;
                    _updateStage = "Downloading, 16 of 39 MB";
                    break;
                case "starting":
                    _phase = Phase.Starting;
                    _phaseWithMods = true;
                    _stage = "Loading mods, 3 of 6";
                    break;
                case "loaded":
                    _phase = Phase.Loaded;
                    _session = _last ?? new LogReport();
                    break;
                case "notloaded": _problem = Problems.NotLoaded(); break;
                case "running": _checks.GameRunning = true; break;
                case "checking": _updateCheck = UpdateCheck.Checking; break;
                case "installed": _notice = "Installed Course Browser 1.0.0"; break;
            }
            if (_options.Page != null)
            {
                var page = _options.Page == "settings" ? Page.Settings : _options.Page == "news" ? Page.News : Page.Mods;
                OpenPage(page);
            }
            if (_options.Expand >= 0 && _options.Expand < _mods.Count) Expand(_mods[_options.Expand], true);
            if (_options.Drop) _dropFade.Snap(1);
            if (_options.Modal) ShowModal("REPLACE THIS MOD?", "Course Browser 1.0.0 is installed. Replace it with 1.1.0? Its saved data stays.", "REPLACE", () => { });
            if (_options.Capture) _capturingKey = true;
        }
    }
}
