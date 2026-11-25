using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace OpenFairway.Launcher
{
    /// <summary>
    /// The game picker beside PLAY: which game the launcher shows and plays, a list of the games OpenFairway is in
    /// and the ones Steam has that it can be added to, and a scan of the Steam libraries for more.
    /// </summary>
    internal sealed partial class MainForm
    {
        /// <summary>Just above SETTINGS, so the game sits with the buttons that act on it.</summary>
        private static readonly RectangleF GameRect = new RectangleF(806, 300, 228, 44);

        private const float GameMenuRowHeight = 38f, GameMenuGap = 4f, GameMenuPad = 4f, GameRadius = 4f;
        private const string GameMenuKey = "gamemenu:";

        private bool _gameMenuOpen;

        /// <summary>What the last scan from the menu found, shown in its scan row until the menu closes.</summary>
        private string? _scanNote;

        private bool GamePickerBusy => _updating || _phase != Phase.Idle || _addingGame;

        /// <summary>"A, B and C".</summary>
        private static string JoinAnd(IList<string> items) =>
            items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1];

        private void PaintGamePicker(Graphics g)
        {
            var r = GameRect;
            var key = GameMenuKey + "picker";
            var busy = GamePickerBusy;
            var hover = busy ? 0f : Math.Max(HoverAmount(key), _gameMenuOpen ? 1f : 0f);
            if (Pressed(key)) r.Offset(0, 1);
            Gfx.FillVertical(g, r, GameRadius, Theme.Alpha(Theme.Mix(Theme.TileTop, Theme.RaisedTop, hover), 0.94f), Theme.Alpha(Theme.Mix(Theme.TileBottom, Theme.RaisedBottom, hover), 0.94f));
            Gfx.Stroke(g, r, GameRadius, Theme.Alpha(Theme.Line, 0.9f));
            Gfx.Title(g, "GAME", _badge, Theme.Faint, r.X + 18, r.Y + 7, LabelTracking * 0.6f);
            Gfx.TextFit(g, _install.Edition.Name, _cardTitle, busy ? Theme.Muted : Theme.Text, new RectangleF(r.X + 18, r.Y + 21, r.Width - 60, 18));
            Gfx.Chevron(g, r.Right - 26, r.Y + r.Height / 2, 9, _gameMenuOpen ? -90 : 90, Theme.Mix(Theme.Faint, Theme.Text, hover), 1.8f);
            if (!busy) Hot(r, key, () => SetGameMenu(!_gameMenuOpen));
        }

        private void SetGameMenu(bool open)
        {
            _gameMenuOpen = open && _page == Page.None;
            if (!_gameMenuOpen) _scanNote = null;
            MarkDirty();
        }

        private sealed class GameMenuRow
        {
            public string Key = "", Label = "", Tag = "";
            public bool Current, Enabled = true;
            public Color TagColor;
            public Action? Click;
        }

        /// <summary>The games OpenFairway is in (pick one), the ones Steam has without it (add it), the supported ones
        /// that are not installed (for the record), and a scan for more.</summary>
        private List<GameMenuRow> GameMenuRows()
        {
            var rows = new List<GameMenuRow>();
            foreach (var game in _scan)
            {
                var install = game.Install == null ? null : _installs.FirstOrDefault(i => SameInstall(i, game.Install));
                if (install != null)
                {
                    var current = SameInstall(install, _install);
                    rows.Add(new GameMenuRow
                    {
                        Key = "game:" + game.Edition.Id, Label = game.Edition.Name, Current = current,
                        Click = () => { SetGameMenu(false); if (!current) SwitchGame(install); },
                    });
                }
                else if (game.GameDir != null)
                    rows.Add(new GameMenuRow
                    {
                        Key = "add:" + game.Edition.Id, Label = game.Edition.Name, Tag = "ADD", TagColor = Theme.Cream,
                        Click = () => { SetGameMenu(false); OfferAddGame(game); },
                    });
                else
                    rows.Add(new GameMenuRow { Key = "missing:" + game.Edition.Id, Label = game.Edition.Name, Tag = "NOT INSTALLED", TagColor = Theme.Faint, Enabled = false });
            }
            rows.Add(new GameMenuRow
            {
                Key = "scan", Label = _scanNote ?? "Scan Steam for games",
                Click = () =>
                {
                    var before = _scan.Count(s => s.GameDir != null);
                    ScanGames();
                    var found = _scan.Count(s => s.GameDir != null) - before;
                    _scanNote = found > 0 ? (found == 1 ? "Found 1 more game" : $"Found {found} more games") : "No other games found";
                    MarkDirty();
                },
            });
            return rows;
        }

        private void PaintGameMenu(Graphics g)
        {
            if (!_gameMenuOpen) return;
            // A click anywhere else closes the menu (the menu's own rows are drawn after, so they win).
            Hot(new RectangleF(0, 0, W, H), GameMenuKey + "outside", () => SetGameMenu(false), Cursors.Default);

            var rows = GameMenuRows();
            var panel = new RectangleF(GameRect.X, GameRect.Bottom + GameMenuGap, GameRect.Width, rows.Count * GameMenuRowHeight + GameMenuPad * 2);
            Gfx.Fill(g, new RectangleF(panel.X + 2, panel.Y + 4, panel.Width, panel.Height), GameRadius, Theme.Alpha(Theme.Ink, 0.5f));
            Gfx.Fill(g, panel, GameRadius, Theme.Bg);
            Gfx.Stroke(g, panel, GameRadius, Theme.Line);

            var y = panel.Y + GameMenuPad;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var key = GameMenuKey + row.Key;
                var r = new RectangleF(panel.X + GameMenuPad, y, panel.Width - GameMenuPad * 2, GameMenuRowHeight);
                if (i == rows.Count - 1)
                    using (var line = new Pen(Theme.Line, 1f)) g.DrawLine(line, panel.X, y - 0.5f, panel.Right, y - 0.5f);
                var hover = row.Enabled && row.Click != null ? HoverAmount(key) : 0f;
                if (hover > 0.001f) Gfx.Fill(g, r, GameRadius, Theme.Alpha(Theme.RaisedTop, 0.7f * hover));

                var color = !row.Enabled ? Theme.Faint : row.Key == "scan" ? Theme.Mix(Theme.Muted, Theme.Text, hover) : Theme.Text;
                var tagW = row.Tag.Length > 0 ? Gfx.TitleWidth(row.Tag, _badge, LabelTracking * 0.5f) + 12 : row.Current ? 24 : 0;
                Gfx.TextFit(g, row.Label, row.Key == "scan" ? _small : _line, color, new RectangleF(r.X + 12, r.Y + 10, r.Width - 24 - tagW, 18));
                if (row.Current) Gfx.Check(g, r.Right - 18, r.Y + r.Height / 2, 7.5f, Theme.Green, Theme.Bg);
                else if (row.Tag.Length > 0)
                    Gfx.Title(g, row.Tag, _badge, row.TagColor, r.Right - 10 - Gfx.TitleWidth(row.Tag, _badge, LabelTracking * 0.5f), r.Y + 12, LabelTracking * 0.5f);
                if (row.Enabled && row.Click != null) Hot(r, key, row.Click);
                y += GameMenuRowHeight;
            }
        }
    }
}
