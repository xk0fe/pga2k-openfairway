using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OpenFairway.Launcher
{
    /// <summary>The panel that slides in over the right of the scene: Mods, Settings and What's new, as tabs.</summary>
    internal sealed partial class MainForm
    {
        private enum Page { None, Mods, Settings, News }

        private const float PanelW = 620, Pad = 34, ViewTop = 128, ViewBottom = 594, ModRowH = 74;
        private static readonly Page[] Tabs = { Page.Mods, Page.Settings, Page.News };

        private Page _page = Page.None, _shown = Page.Mods;
        private readonly Tween _panel, _tabX, _tabW;
        private double _pageChangedAt = -10;
        private float[] _scroll = new float[4];
        private readonly Tween[] _scrollTweens = new Tween[4];
        private float _contentHeight;

        private readonly Dictionary<string, Tween> _knobs = new Dictionary<string, Tween>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Tween> _expanded = new Dictionary<string, Tween>(StringComparer.OrdinalIgnoreCase);

        private List<NoteEntry>? _notes;
        private bool _notesLoading, _notesOffline;

        private bool _capturingKey;
        private string? _keyHint;
        private TextBox? _editor;

        private float PanelX => W - PanelW * _panel.Value(_now);
        private RectangleF Viewport => new RectangleF(PanelX + Pad - 12, ViewTop, PanelW - 2 * Pad + 24, ViewBottom - ViewTop);

        private Tween Knob(ModInfo mod)
        {
            if (!_knobs.TryGetValue(mod.Folder, out var tween))
            {
                _knobs[mod.Folder] = tween = NewTween(mod.Enabled ? 1f : 0f);
            }
            return tween;
        }

        private Tween Expansion(ModInfo mod)
        {
            if (!_expanded.TryGetValue(mod.Folder, out var tween)) _expanded[mod.Folder] = tween = NewTween(0);
            return tween;
        }

        private void Expand(ModInfo mod, bool open)
        {
            var tween = Expansion(mod);
            tween.Set(open ? 1f : 0f, _now, 0.26f);
            if (_preview) tween.Snap(open ? 1f : 0f);
            MarkDirty();
        }

        private void OpenPage(Page page)
        {
            CommitEditor(true);
            _capturingKey = false;
            if (page == _page) return;
            var wasClosed = _page == Page.None;
            _page = page;
            if (page != Page.None)
            {
                _shown = page;
                _pageChangedAt = wasClosed ? _now + 0.12 : _now;
                var (x, w) = TabSpan(page);
                if (wasClosed)
                {
                    _tabX.Snap(x);
                    _tabW.Snap(w);
                }
                else
                {
                    _tabX.Set(x, _now, 0.24f);
                    _tabW.Set(w, _now, 0.24f);
                }
                if (page == Page.News && _notes == null) _ = LoadNotes();
            }
            _panel.Set(page == Page.None ? 0f : 1f, _now, page == Page.None ? 0.26f : 0.34f);
            if (_preview)
            {
                _panel.Snap(_panel.Target);
                _pageChangedAt = -10;
            }
            MarkDirty();
        }

        /// <summary>A tab's label position relative to the panel's left edge.</summary>
        private (float X, float W) TabSpan(Page page)
        {
            var x = 58f;
            foreach (var tab in Tabs)
            {
                var w = Gfx.TitleWidth(TabName(tab), _tab, TabTracking);
                if (tab == page) return (x, w);
                x += w + 28;
            }
            return (x, 0);
        }

        private static string TabName(Page page) => page == Page.Mods ? "MODS" : page == Page.Settings ? "SETTINGS" : "WHAT'S NEW";

        private void ScrollBy(float delta)
        {
            var i = (int)_shown;
            var max = Math.Max(0, _contentHeight - Viewport.Height + 12);
            var target = Theme.Clamp(_scrollTweens[i].Target + delta, 0, max);
            _scrollTweens[i].Set(target, _now, 0.26f);
            MarkDirty();
        }

        private void PaintPanel(Graphics g)
        {
            var open = _panel.Value(_now);
            if (open <= 0.001f) return;
            var px = PanelX;
            // A soft shadow where the panel meets the scene.
            using (var shadow = new LinearGradientBrush(new RectangleF(px - 48, 0, 49, H), Color.FromArgb(0, Theme.Ink), Theme.Alpha(Theme.Ink, 0.75f), 0f))
                g.FillRectangle(shadow, px - 48, 0, 48, H);
            using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, px, 0, PanelW, H);
            using (var edge = new Pen(Theme.Alpha(Theme.Line, 0.7f), 1f)) g.DrawLine(edge, px + 0.5f, 0, px + 0.5f, H);

            // Back, and the tabs.
            var backKey = "page:back";
            var backHover = HoverAmount(backKey);
            Gfx.Chevron(g, px + 30, 64, 15, 180, Theme.Mix(Theme.Muted, Theme.Text, backHover), 2.2f);
            Hot(new RectangleF(px + 12, 44, 38, 40), backKey, () => OpenPage(Page.None));
            foreach (var tab in Tabs)
            {
                var (x, w) = TabSpan(tab);
                var key = "tab:" + tab;
                var active = tab == _shown;
                var color = active ? Theme.Text : Theme.Mix(Theme.Faint, Theme.Muted, HoverAmount(key));
                Gfx.Title(g, TabName(tab), _tab, color, px + x, 52, TabTracking);
                var t = tab;
                Hot(new RectangleF(px + x - 8, 42, w + 16, 44), key, () => OpenPage(t));
            }
            Gfx.Fill(g, new RectangleF(px + _tabX.Value(_now), 82, _tabW.Value(_now), 3), 1.5f, Theme.Cream);

            // What this page is for, and its own extra at the top right.
            var x1 = px + PanelW - Pad;
            switch (_shown)
            {
                case Page.Mods:
                    var on = _mods.Count(m => m.Enabled);
                    var summary = _mods.Count == 0 ? "None installed" : $"{on} of {_mods.Count} on";
                    Gfx.Text(g, summary, _body, Theme.Muted, x1 - Gfx.Width(summary, _body), 56);
                    Gfx.TextFit(g, "Switch a mod on or off, or click it for details. Drop a mod's .zip on this window to install it.", _small, Theme.Faint,
                        new RectangleF(px + Pad, 99, PanelW - 2 * Pad, 18));
                    break;
                case Page.Settings:
                    Gfx.TextFit(g, "Changes are saved as you make them and apply the next time you play.", _small, Theme.Faint, new RectangleF(px + Pad, 99, PanelW - 2 * Pad, 18));
                    break;
                case Page.News:
                    var allKey = "news:all";
                    var allHover = HoverAmount(allKey);
                    const string all = "All releases";
                    var aw = Gfx.Width(all, _small);
                    var color = Theme.Mix(Theme.Muted, Theme.Text, allHover);
                    Gfx.Text(g, all, _small, color, x1 - aw - 14, 57);
                    Gfx.External(g, x1 - 8.5f, 61, 8.5f, color);
                    Hot(new RectangleF(x1 - aw - 20, 50, aw + 26, 28), allKey, () => Install.OpenUrl(GitHub.ReleasesUrl));
                    var note = _notesLoading ? "Reading the release notes from GitHub."
                        : !_notesOffline ? "What changed in each version, newest first."
                        : _settings.CheckUpdates ? "GitHub could not be reached, so these are the notes that came with this version."
                        : "Update checks are off, so these are the notes that came with this version.";
                    Gfx.TextFit(g, note, _small, Theme.Faint, new RectangleF(px + Pad, 99, PanelW - 2 * Pad, 18));
                    break;
            }
            using (var rule = new Pen(Theme.Line, 1f)) g.DrawLine(rule, px + Pad, ViewTop - 6.5f, x1, ViewTop - 6.5f);

            // The page itself, scrolled and clipped, fading in when the tab changes.
            var view = Viewport;
            var state = g.Save();
            g.SetClip(new RectangleF(view.X - 2, view.Y - 4, view.Width + 4, view.Height + 4));
            var enter = Ease.Out((float)((_now - _pageChangedAt) / 0.32));
            var dy = 10f * (1f - enter);
            var top = ViewTop + 4 - _scroll[(int)_shown] + dy;
            switch (_shown)
            {
                case Page.Mods: _contentHeight = ModsPage(g, px + Pad, top, PanelW - 2 * Pad); break;
                case Page.Settings: _contentHeight = SettingsPage(g, px + Pad, top, PanelW - 2 * Pad); break;
                case Page.News: _contentHeight = NewsPage(g, px + Pad, top, PanelW - 2 * Pad); break;
            }
            if (enter < 1f) using (var cover = new SolidBrush(Theme.Alpha(Theme.Bg, 1f - enter))) g.FillRectangle(cover, view);
            g.Restore(state);

            // Edges fade where the list continues, and a thin scroll bar.
            var max = Math.Max(0, _contentHeight - view.Height + 12);
            var scroll = _scroll[(int)_shown];
            if (max > 0)
            {
                if (scroll > 1)
                    using (var fade = new LinearGradientBrush(new RectangleF(view.X, view.Y - 1, view.Width, 26), Theme.Bg, Color.FromArgb(0, Theme.Bg), 90f))
                        g.FillRectangle(fade, view.X, view.Y, view.Width, 24);
                if (scroll < max - 1)
                    using (var fade = new LinearGradientBrush(new RectangleF(view.X, view.Bottom - 25, view.Width, 26), Color.FromArgb(0, Theme.Bg), Theme.Bg, 90f))
                        g.FillRectangle(fade, view.X, view.Bottom - 24, view.Width, 24);
                var thumbH = Math.Max(36f, view.Height * view.Height / (_contentHeight + 12));
                var thumbY = view.Y + (view.Height - thumbH) * Theme.Clamp(scroll / max, 0, 1);
                Gfx.Fill(g, new RectangleF(view.Right + 4, view.Y, 3, view.Height), 1.5f, Theme.Alpha(Theme.Line, 0.7f));
                Gfx.Fill(g, new RectangleF(view.Right + 4, thumbY, 3, thumbH), 1.5f, Theme.Faint);
            }

            // Footer.
            if (_shown == Page.Mods)
            {
                var key = "mods:folder";
                var hover = HoverAmount(key);
                const string label = "Open mods folder";
                var w = Gfx.Width(label, _small);
                var color = Theme.Mix(Theme.Muted, Theme.Text, hover);
                var lx = x1 - w;
                Gfx.Text(g, label, _small, color, lx, 609);
                Gfx.Folder(g, lx - 22, 617.5f, color);
                Hot(new RectangleF(lx - 28, 602, w + 32, 28), key, () =>
                {
                    if (_preview) return;
                    Directory.CreateDirectory(_install.ModsDir);
                    Install.OpenFolder(_install.ModsDir);
                });
            }
        }

        /// <summary>Clickable only where it shows inside the page.</summary>
        private void HotInView(RectangleF rect, string key, Action click, Cursor? cursor = null)
        {
            var clipped = RectangleF.Intersect(rect, Viewport);
            if (clipped.Height > 4) Hot(clipped, key, click, cursor);
        }

        // ---- MODS ----------------------------------------------------------------------------------------------------

        private PointF SwitchCentre(int index)
        {
            var y = ViewTop + 4 - _scroll[(int)Page.Mods];
            for (var i = 0; i < index && i < _mods.Count; i++) y += ModRowHeight(_mods[i], PanelW - 2 * Pad);
            return new PointF(W - Pad - 16 - 22, y + ModRowH / 2);
        }

        private sealed class Flag
        {
            public Glyph Glyph;
            public Color Color;
            public string Text = "";
            public bool Warn;
        }

        /// <summary>What the player should know about a mod: what it needs, and how it did last time.</summary>
        private List<Flag> ModFlags(ModInfo mod)
        {
            var flags = new List<Flag>();
            var installed = InstalledVersion;
            if (mod.Platform.Length > 0 && installed.Length > 0 && SemVer.Compare(mod.Platform, installed) > 0)
                flags.Add(new Flag { Glyph = Glyph.Alert, Color = Theme.Red, Warn = true, Text = $"Needs OpenFairway {mod.Platform} or newer (this is {installed}), so it is left out until you update." });
            foreach (var required in mod.Requires)
            {
                var other = _mods.FirstOrDefault(m => m.Folder.Equals(required, StringComparison.OrdinalIgnoreCase));
                if (other == null) flags.Add(new Flag { Glyph = Glyph.Alert, Color = Theme.Red, Warn = mod.Enabled, Text = $"Needs the {required} mod, which is not installed." });
                else if (!other.Enabled) flags.Add(new Flag { Glyph = Glyph.Alert, Color = Theme.Red, Warn = mod.Enabled, Text = $"Needs {other.Name}, which is off." });
                else flags.Add(new Flag { Glyph = Glyph.Check, Color = Theme.Green, Text = $"Works with {other.Name}, which is on." });
            }
            if (_last != null)
            {
                if (_last.Failed.TryGetValue(mod.Folder, out var error))
                    flags.Add(new Flag { Glyph = Glyph.Alert, Color = Theme.Red, Warn = true, Text = "Failed to load last time: " + error });
                else if (_last.LeftOut.TryGetValue(mod.Folder, out var why))
                    flags.Add(new Flag { Glyph = Glyph.Alert, Color = Theme.Gold, Text = "Left out last time: " + why });
                else if (_last.Loaded.Contains(mod.Folder, StringComparer.OrdinalIgnoreCase))
                    flags.Add(new Flag { Glyph = Glyph.Check, Color = Theme.Green, Text = "Loaded last time." });
                else
                    flags.Add(new Flag { Glyph = Glyph.Dot, Color = Theme.Faint, Text = "Off last time." });
            }
            return flags;
        }

        private float TextWidth(float width) => width - 16 - 44 - 28 - 16;

        private float ModDetailHeight(ModInfo mod, float width)
        {
            var tw = TextWidth(width);
            var description = Math.Max(1, Gfx.LineCount(mod.Description, _body, tw)) - 1;
            var flags = ModFlags(mod).Sum(f => Gfx.LineCount(f.Text, _small, tw - 18) * 18 + 4);
            return description * 19 + 12 + 20 + 8 + flags + 6 + 24 + 10;
        }

        private float ModRowHeight(ModInfo mod, float width) => ModRowH + Expansion(mod).Value(_now) * ModDetailHeight(mod, width);

        private float ModsPage(Graphics g, float x, float y, float width)
        {
            var start = y;
            if (_mods.Count == 0)
            {
                Gfx.Text(g, "No mods installed yet.", _name, Theme.Text, x, y + 14);
                Gfx.Paragraph(g, "Drop a mod's .zip on this window, or put mod folders in the mods folder.", _body, Theme.Muted, x, y + 42, width, 19);
                return 90;
            }
            using (var line = new Pen(Theme.Line, 1f))
            {
                for (var i = 0; i < _mods.Count; i++)
                {
                    var mod = _mods[i];
                    var h = ModRowHeight(mod, width);
                    if (y + h >= ViewTop - 10 && y <= ViewBottom + 10) ModRow(g, i, new RectangleF(x - 12, y, width + 24, h), width);
                    y += h;
                    if (i < _mods.Count - 1) g.DrawLine(line, x, y - 0.5f, x + width, y - 0.5f);
                }
            }
            return y - start;
        }

        private void ModRow(Graphics g, int index, RectangleF r, float width)
        {
            var mod = _mods[index];
            var rowKey = "mod:" + index;
            var switchKey = "modswitch:" + index;
            var hover = HoverAmount(rowKey);
            var switchHover = HoverAmount(switchKey);
            var open = Expansion(mod).Value(_now);
            var on = Knob(mod).Value(_now);
            var flags = ModFlags(mod);
            var warn = flags.Any(f => f.Warn);

            var fill = new RectangleF(r.X, r.Y + 4, r.Width, r.Height - 8);
            var lift = Math.Max(hover, open * 0.55f);
            if (lift > 0.001f)
                Gfx.FillVertical(g, fill, 4, Theme.Mix(Theme.Bg, Pressed(rowKey) ? Theme.TileTop : Theme.RaisedTop, lift * 0.85f), Theme.Mix(Theme.Bg, Theme.RaisedBottom, lift * 0.85f));

            var textX = r.X + 16;
            var switchX = r.Right - 16 - 44;
            var textW = TextWidth(width);
            var lit = Math.Max(on, hover * 0.6f);

            // Name, version, and a mark when something needs attention.
            var nameW = Math.Min(Gfx.Width(mod.Name, _name), textW - 80);
            Gfx.TextFit(g, mod.Name, _name, Theme.Mix(Theme.Muted, Theme.Text, lit), new RectangleF(textX, r.Y + 15, nameW + 2, 24));
            var vx = textX + nameW + 10;
            if (mod.Version.Length > 0)
            {
                Gfx.Text(g, "v" + mod.Version, _line, Theme.Faint, vx, r.Y + 19);
                vx += Gfx.Width("v" + mod.Version, _line) + 10;
            }
            if (warn) Gfx.Alert(g, vx + 6, r.Y + 26, 6.5f, Theme.Red, Theme.Text);
            Gfx.Chevron(g, switchX - 22, r.Y + 37, 9, 90 - 180 * open, Theme.Mix(Theme.Line, Theme.Muted, Math.Max(hover, open)), 1.6f);

            // The description: one line, or all of it when the row is open.
            var descColor = Theme.Mix(Theme.Faint, Theme.Muted, Math.Max(lit, open));
            if (open < 0.02f) Gfx.TextFit(g, mod.Description, _body, descColor, new RectangleF(textX, r.Y + 42, textW, 20));
            else
            {
                var state = g.Save();
                g.SetClip(RectangleF.Intersect(new RectangleF(r.X, r.Y, r.Width, r.Height - 4), g.ClipBounds));
                var y = r.Y + 42 + Gfx.Paragraph(g, mod.Description, _body, descColor, textX, r.Y + 42, textW, 19) + 12;
                var meta = string.Join("   ·   ", new[] { mod.Author.Length > 0 ? "by " + mod.Author : "", "mods\\" + mod.Folder }.Where(s => s.Length > 0));
                Gfx.TextFit(g, meta, _line, Theme.Alpha(Theme.Faint, open), new RectangleF(textX, y, textW, 18));
                y += 28;
                foreach (var flag in flags)
                {
                    PaintGlyph(g, flag.Glyph, textX + 6, y + 9, 5.5f, Theme.Alpha(flag.Color, open));
                    y += Gfx.Paragraph(g, flag.Text, _small, Theme.Alpha(flag.Warn ? Theme.Text : Theme.Muted, open), textX + 18, y, textW - 18, 18) + 4;
                }
                y += 6;
                var folderKey = "modfolder:" + index;
                var fh = HoverAmount(folderKey);
                var fc = Theme.Alpha(Theme.Mix(Theme.Muted, Theme.Text, fh), open);
                Gfx.Folder(g, textX + 1, y + 9.5f, fc);
                Gfx.Text(g, "Open its folder", _small, fc, textX + 22, y + 1);
                if (open > 0.9f) HotInView(new RectangleF(textX - 4, y - 3, Gfx.Width("Open its folder", _small) + 32, 24), folderKey, () => { if (!_preview) Install.OpenFolder(mod.Directory); });
                g.Restore(state);
            }

            PaintSwitch(g, new RectangleF(switchX, r.Y + 25, 44, 24), on, Math.Max(switchHover, hover * 0.4f));
            HotInView(new RectangleF(r.X, r.Y, switchX - 30 - r.X, ModRowH), rowKey, () => Expand(mod, Expansion(mod).Target < 0.5f));
            HotInView(new RectangleF(switchX - 30, r.Y, r.Right - (switchX - 30), ModRowH), switchKey, () => Toggle(mod));
        }

        private void Toggle(ModInfo mod)
        {
            mod.Enabled = !mod.Enabled;
            Knob(mod).Set(mod.Enabled ? 1f : 0f, _now, 0.18f);
            if (!_preview)
                try { _install.SaveDisabled(_mods); }
                catch (Exception e) { ShowProblem(Problems.SaveFailed(e), null); }
            MarkDirty();
        }

        private static void PaintSwitch(Graphics g, RectangleF r, float on, float hover)
        {
            var offTrack = Theme.Mix(Theme.Line, Theme.Hex(0x5A5E68), hover);
            Gfx.Fill(g, r, r.Height / 2, Theme.Mix(offTrack, Theme.Cream, on));
            var d = r.Height - 6;
            var knob = new RectangleF(r.X + 3 + on * (r.Width - 6 - d), r.Y + 3, d, d);
            using (var brush = new SolidBrush(Theme.Mix(Theme.Faint, Theme.Bg, on))) g.FillEllipse(brush, knob);
        }

        // ---- SETTINGS --------------------------------------------------------------------------------------------------

        private enum Control { Switch, Choice, KeyCap, Button, Text }

        private sealed class Setting
        {
            public string Section = "", Key = "", Name = "", Description = "";
            public Control Control;
            public bool On, Enabled = true;
            public string Value = "";
            public Action? Click;
        }

        private static readonly (string Value, string Label)[] DumpChoices = { ("auto", "When the game changes"), ("always", "Every start"), ("never", "Never") };

        private List<Setting> BuildSettings()
        {
            var menuKey = MenuKey.Available(_install);
            return new List<Setting>
            {
                new Setting
                {
                    Section = "LAUNCHER", Key = "keepopen", Name = "Keep the launcher open", Control = Control.Switch, On = _settings.KeepOpen,
                    Description = "It steps aside while you play and comes back when the game closes, with how the session went. Off closes it once the game is up.",
                    Click = () => Save(() => _settings.KeepOpen = !_settings.KeepOpen),
                },
                new Setting
                {
                    Section = "LAUNCHER", Key = "updates", Name = "Check for updates", Control = Control.Switch, On = _settings.CheckUpdates,
                    Description = "Asks GitHub for a newer OpenFairway when the launcher opens. Nothing about you is sent. Off keeps the version you have.",
                    Click = () => { Save(() => _settings.CheckUpdates = !_settings.CheckUpdates); _ = CheckForUpdates(); },
                },
                new Setting
                {
                    Section = "LAUNCHER", Key = "prerelease", Name = "Pre-release updates", Control = Control.Switch, On = _settings.PreReleases, Enabled = _settings.CheckUpdates,
                    Description = "Also offers test builds before they are released. Less tested.",
                    Click = () => { Save(() => _settings.PreReleases = !_settings.PreReleases); _ = CheckForUpdates(); },
                },
                new Setting
                {
                    Section = "IN THE GAME", Key = "menukey", Name = "Mod manager key", Control = Control.KeyCap, Value = menuKey ? MenuKey.Read(_install) : "", Enabled = menuKey,
                    Description = !menuKey ? "Needs the Offline Main Menu mod, which shows the mod manager."
                        : _capturingKey ? (_keyHint ?? "Press the key you want, or Esc to keep the one you have.")
                        : "Opens the mod manager anywhere in the game. Click, then press a key.",
                    Click = () => { _capturingKey = !_capturingKey; _keyHint = null; MarkDirty(); },
                },
                new Setting
                {
                    Section = "IN THE GAME", Key = "launch", Name = "Launch options", Control = Control.Text, Value = _settings.LaunchOptions,
                    Description = "Extra arguments for the game, given to it through Steam. For a window: -screen-fullscreen 0",
                },
                new Setting
                {
                    Section = "IN THE GAME", Key = "verify", Name = "Verify game files", Control = Control.Button, Value = "VERIFY", Enabled = _install.AppId.Length > 0,
                    Description = "Steam checks the game's own files and repairs any that differ. OpenFairway's files are left alone.",
                    Click = () => { if (!_preview) _install.VerifyGameFiles(); },
                },
                new Setting
                {
                    Section = "IN THE GAME", Key = "folder", Name = "Game folder", Control = Control.Button, Value = "OPEN",
                    Description = _install.GameDir,
                    Click = () => { if (!_preview) Install.OpenFolder(_install.GameDir); },
                },
                new Setting
                {
                    Section = "FOR MOD MAKERS", Key = "detail", Name = "Detailed log", Control = Control.Switch, On = _config.DetailedLog,
                    Description = "Also records each step the in-game screens take. Turn it on when you report a problem.",
                    Click = () => SaveConfig(() => _config.DetailedLog = !_config.DetailedLog),
                },
                new Setting
                {
                    Section = "FOR MOD MAKERS", Key = "console", Name = "Developer console", Control = Control.Switch, On = _config.DevConsole,
                    Description = "A local console for looking inside the running game (tools\\devcon.py). Leave it off to play.",
                    Click = () => SaveConfig(() => _config.DevConsole = !_config.DevConsole),
                },
                new Setting
                {
                    Section = "FOR MOD MAKERS", Key = "dump", Name = "Type dump", Control = Control.Choice,
                    Value = DumpChoices.FirstOrDefault(c => c.Value == _config.DumpTypes).Label ?? DumpChoices[0].Label,
                    Description = "Writes the game's type signatures to the dump folder for writing mods.",
                    Click = () => SaveConfig(() =>
                    {
                        var i = Array.FindIndex(DumpChoices, c => c.Value == _config.DumpTypes);
                        _config.DumpTypes = DumpChoices[(i + 1) % DumpChoices.Length].Value;
                    }),
                },
            };
        }

        private void Save(Action change)
        {
            change();
            if (_preview) return;
            try { _settings.Save(); }
            catch (Exception e) { ShowProblem(Problems.SaveFailed(e), null); }
            MarkDirty();
        }

        private void SaveConfig(Action change)
        {
            change();
            if (_preview) return;
            try { _config.Save(); }
            catch (Exception e) { ShowProblem(Problems.SaveFailed(e), null); }
            MarkDirty();
        }

        private float SettingsPage(Graphics g, float x, float y, float width)
        {
            var start = y;
            var section = "";
            using (var line = new Pen(Theme.Line, 1f))
            {
                foreach (var s in BuildSettings())
                {
                    if (s.Section != section)
                    {
                        if (section.Length > 0) y += 14;
                        section = s.Section;
                        Gfx.Text(g, section, _section, Theme.Faint, x, y + 8);
                        y += 30;
                        g.DrawLine(line, x, y - 0.5f, x + width, y - 0.5f);
                    }
                    var controlW = s.Control == Control.Switch ? 44f : s.Control == Control.KeyCap ? 92f : s.Control == Control.Choice ? 190f : s.Control == Control.Button ? 90f : 0f;
                    var textW = width - (controlW > 0 ? controlW + 28 : 0);
                    var lines = Gfx.LineCount(s.Description, _small, textW, 3);
                    var h = 42 + lines * 18 + (s.Control == Control.Text ? 42 : 0) + 12;
                    if (y + h >= ViewTop - 10 && y <= ViewBottom + 10) SettingRow(g, s, new RectangleF(x, y, width, h), controlW, textW);
                    y += h;
                    g.DrawLine(line, x, y - 0.5f, x + width, y - 0.5f);
                }
            }
            return y - start + 8;
        }

        private void SettingRow(Graphics g, Setting s, RectangleF r, float controlW, float textW)
        {
            var key = "setting:" + s.Key;
            var hover = s.Enabled ? HoverAmount(key) : 0f;
            var nameColor = s.Enabled ? Theme.Text : Theme.Faint;
            Gfx.Text(g, s.Name, _name, nameColor, r.X, r.Y + 13);
            var hint = s.Key == "menukey" && _capturingKey && _keyHint != null;
            Gfx.Paragraph(g, s.Description, _small, hint ? Theme.Red : s.Enabled ? Theme.Muted : Theme.Faint, r.X, r.Y + 40, textW, 18, 3);

            var cx = r.Right - controlW;
            var cy = r.Y + 14;
            switch (s.Control)
            {
                case Control.Switch:
                    PaintSwitch(g, new RectangleF(cx, cy + 2, 44, 24), s.On ? 1f : 0f, hover);
                    break;
                case Control.Button:
                {
                    var b = new RectangleF(r.Right - controlW, cy, controlW, 28);
                    if (Pressed(key)) b.Offset(0, 1);
                    Gfx.Fill(g, b, 4, Theme.Alpha(Theme.RaisedTop, 0.6f * hover));
                    Gfx.Stroke(g, b, 4, Theme.Mix(Theme.Line, Theme.Faint, hover));
                    Gfx.TextFit(g, s.Value, _pill, s.Enabled ? Theme.Text : Theme.Faint, new RectangleF(b.X, b.Y + 7, b.Width, 16), StringAlignment.Center);
                    break;
                }
                case Control.Choice:
                {
                    var color = Theme.Mix(Theme.Muted, Theme.Text, hover);
                    var w = Gfx.Width(s.Value, _body);
                    Gfx.Text(g, s.Value, _body, color, r.Right - 22 - w, cy + 4);
                    Gfx.Chevron(g, r.Right - 6, cy + 14, 10, 0, color, 1.8f);
                    break;
                }
                case Control.KeyCap:
                {
                    var capturing = _capturingKey;
                    var label = capturing ? "Press a key" : s.Value;
                    var w = Math.Max(52, Gfx.Width(label, _pill) + 26);
                    var b = new RectangleF(r.Right - w, cy, w, 28);
                    Gfx.Fill(g, b, 4, capturing ? Theme.Alpha(Theme.Cream, 0.12f) : Theme.Alpha(Theme.RaisedTop, 0.6f * hover));
                    Gfx.Stroke(g, b, 4, capturing ? Theme.Cream : Theme.Mix(Theme.Line, Theme.Faint, hover), capturing ? 1.4f : 1f);
                    Gfx.TextFit(g, label, _pill, s.Enabled ? Theme.Text : Theme.Faint, new RectangleF(b.X, b.Y + 7, b.Width, 16), StringAlignment.Center);
                    break;
                }
                case Control.Text:
                {
                    var field = new RectangleF(r.X, r.Y + 42 + Gfx.LineCount(s.Description, _small, textW, 3) * 18 + 6, r.Width, 32);
                    var editing = _editor != null;
                    Gfx.Fill(g, field, 4, Theme.Mix(Theme.Strip, Theme.TileBottom, Math.Max(hover, editing ? 1f : 0f)));
                    Gfx.Stroke(g, field, 4, editing ? Theme.Cream : Theme.Mix(Theme.Line, Theme.Faint, hover), editing ? 1.4f : 1f);
                    if (!editing)
                        Gfx.TextFit(g, s.Value.Length > 0 ? s.Value : "None", _body, s.Value.Length > 0 ? Theme.Text : Theme.Faint, new RectangleF(field.X + 12, field.Y + 8, field.Width - 24, 20));
                    HotInView(field, key, () => EditLaunchOptions(field), Cursors.IBeam);
                    _editorField = field;
                    return;
                }
            }
            if (s.Enabled && s.Click != null) HotInView(r, key, s.Click);
        }

        private RectangleF _editorField;

        private void CaptureKey(Keys key)
        {
            if (key == Keys.Escape)
            {
                _capturingKey = false;
                _keyHint = null;
            }
            else if (key == Keys.ShiftKey || key == Keys.ControlKey || key == Keys.Menu) return;
            else if (!KeyNames.Allowed(key))
                _keyHint = $"{KeyNames.Name(key)} is used by the game. Choose F1 to F11, Insert, Home, End, Page Up or Down, or a numpad key.";
            else
            {
                try
                {
                    MenuKey.Write(_install, key);
                    LauncherLog.Write("Mod manager key: " + KeyNames.Name(key));
                }
                catch (Exception e) { ShowProblem(Problems.SaveFailed(e), null); }
                _capturingKey = false;
                _keyHint = null;
            }
            MarkDirty();
        }

        private void EditLaunchOptions(RectangleF field)
        {
            if (_preview || _editor != null) return;
            var editor = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.TileBottom,
                ForeColor = Theme.Text,
                Font = _body,
                Text = _settings.LaunchOptions,
            };
            editor.SetBounds((int)field.X + 12, (int)(field.Y + (field.Height - 17) / 2), (int)field.Width - 24, 18);
            editor.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
                {
                    CommitEditor(e.KeyCode == Keys.Enter);
                    e.Handled = e.SuppressKeyPress = true;
                }
            };
            editor.LostFocus += (s, e) => CommitEditor(true);
            _editor = editor;
            Controls.Add(editor);
            editor.Focus();
            editor.SelectionStart = editor.Text.Length;
            MarkDirty();
        }

        private void CommitEditor(bool keep)
        {
            var editor = _editor;
            if (editor == null) return;
            _editor = null;
            if (keep && editor.Text.Trim() != _settings.LaunchOptions) Save(() => _settings.LaunchOptions = editor.Text.Trim());
            Controls.Remove(editor);
            editor.Dispose();
            MarkDirty();
        }

        // ---- WHAT'S NEW ------------------------------------------------------------------------------------------------

        private async Task LoadNotes()
        {
            if (_notesLoading) return;
            _notesLoading = true;
            MarkDirty();
            string text;
            var online = false;
            if (_settings.CheckUpdates)
            {
                try
                {
                    text = await GitHub.Changelog();
                    online = true;
                    if (_releases == null) _releases = await GitHub.Releases();
                }
                catch (Exception e)
                {
                    LauncherLog.Write("Release notes from GitHub failed: " + e.Message);
                    text = Notes.Embedded();
                }
            }
            else text = Notes.Embedded();
            if (!online) text = Notes.Embedded();
            _notes = Notes.Parse(text);
            _notesOffline = !online;
            _notesLoading = false;
            MarkDirty();
        }

        private float NewsPage(Graphics g, float x, float y, float width)
        {
            var start = y;
            if (_notes == null || _notesLoading)
            {
                Gfx.Spinner(g, x + 9, y + 22, 8, Theme.Muted, _now);
                Gfx.Text(g, "Reading the release notes", _body, Theme.Muted, x + 26, y + 13);
                return 60;
            }
            var installed = InstalledVersion;
            using (var line = new Pen(Theme.Line, 1f))
            {
                for (var i = 0; i < _notes.Count; i++)
                {
                    var entry = _notes[i];
                    y += i == 0 ? 6 : 22;
                    Gfx.Text(g, entry.Version, _noteVersion, Theme.Text, x - 1, y);
                    var bx = x + Gfx.Width(entry.Version, _noteVersion) + 12;
                    var release = _releases?.FirstOrDefault(r => r.Version == entry.Version);
                    if (installed.Length > 0 && entry.Version == installed) bx += VersionTag(g, bx, y + 7, "INSTALLED", Theme.Cream, outline: true) + 8;
                    else if (installed.Length > 0 && SemVer.Compare(entry.Version, installed) > 0) bx += VersionTag(g, bx, y + 7, "NEW", Theme.Gold, outline: false) + 8;
                    if (release?.PreRelease == true) VersionTag(g, bx, y + 7, "PRE-RELEASE", Theme.TileTop, outline: false);
                    var date = Notes.FormatDate(entry.Date);
                    Gfx.Text(g, date, _small, Theme.Faint, x + width - Gfx.Width(date, _small), y + 8);
                    y += 40;
                    foreach (var block in entry.Blocks)
                    {
                        switch (block.Kind)
                        {
                            case "h":
                                y += 8;
                                Gfx.Text(g, block.Text.ToUpperInvariant(), _section, Theme.Faint, x, y);
                                y += 22;
                                break;
                            case "li":
                                using (var dot = new SolidBrush(Theme.Faint)) g.FillEllipse(dot, x + 3, y + 7.5f, 4, 4);
                                y += Gfx.Paragraph(g, block.Text, _body, Theme.Muted, x + 16, y, width - 16, 19) + 6;
                                break;
                            default:
                                y += Gfx.Paragraph(g, block.Text, _body, Theme.Text, x, y, width, 19) + 8;
                                break;
                        }
                    }
                    if (i < _notes.Count - 1) g.DrawLine(line, x, y + 10.5f, x + width, y + 10.5f);
                    y += 10;
                }
            }
            return y - start + 10;
        }

        /// <summary>A small label beside a version: filled, or an outline. Returns its width.</summary>
        private float VersionTag(Graphics g, float x, float y, string text, Color color, bool outline)
        {
            var w = Gfx.Width(text, _badge) + 16;
            var r = new RectangleF(x, y, w, 19);
            if (outline) Gfx.Stroke(g, r, 9.5f, color, 1.2f);
            else Gfx.Fill(g, r, 9.5f, color);
            var ink = outline ? color : color == Theme.TileTop ? Theme.Text : Theme.Bg;
            Gfx.TextFit(g, text, _badge, ink, new RectangleF(r.X, r.Y + 2.5f, w, 16), StringAlignment.Center);
            return w;
        }
    }
}
