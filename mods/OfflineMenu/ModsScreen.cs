using System;
using System.IO;
using System.Linq;
using OpenFairway.API;

namespace OpenFairway.Mods;

/// <summary>
/// The in-game mod manager, laid out like the game's own MyPLAYER screen: a list on the left over a dark scrim,
/// details for the selected mod beside it, and the menu's 3D scene on the right. Switches take effect on the
/// next start (mods cannot be loaded or unloaded mid-session).
/// </summary>
internal sealed class ModsScreen
{
    // docs/design.md
    private const uint Bg = 0x0F0F1DFFu, TileTop = 0x3F4653FFu, TileBottom = 0x242731FFu, TileHover = 0x2E323DFFu, Line = 0x454850FFu,
        Text = 0xF4F4F4FFu, Muted = 0xAEB4C6FFu, Faint = 0x6E7486FFu, Cream = 0xFDEDDAFFu, CreamDim = 0xD8CBBBFFu, Red = 0xE0323FFFu;

    private const float ListX = 56, ListY = 236, ListW = 600, RowH = 92, RowGap = 10, DetailX = 704, DetailW = 560;

    private readonly IModContext _ctx;
    private readonly string _ownFolder;
    private readonly IUiScreen _screen;
    private readonly string _wordmark;
    private string _selected = "";

    public ModsScreen(IModContext ctx, string ownFolder)
    {
        _ctx = ctx;
        _ownFolder = ownFolder;
        _wordmark = Path.Combine(ctx.ModDirectory, "assets", "wordmark.png");
        _screen = ctx.Ui.CreateScreen();
        _screen.HideGameUi = true;
        _screen.Render = Draw;
        _screen.OnBack = () => _screen.Close();
    }

    public void Open()
    {
        _screen.Open();
        _screen.Invalidate(true);
    }

    private void Draw(IUiCanvas c)
    {
        var mods = _ctx.Mods.Installed.OrderBy(m => m.Folder == _ownFolder ? 1 : 0).ThenBy(m => m.Name).ToList();
        if (mods.Count > 0 && mods.All(m => m.Folder != _selected)) _selected = mods[0].Folder;
        var pending = mods.Any(m => m.EnabledNextStart != m.Running);

        // The game's own look: dark on the left, the 3D menu scene showing through on the right.
        c.Rect(0, 0, 820, 1080, 0x0F0F1DF2u);
        c.Gradient(820, 0, 640, 1080, 0x0F0F1DF2u, UiGradient.Right);
        c.Gradient(0, 0, 1920, 200, 0x0A0A14C0u, UiGradient.Down);

        using (c.Group(0f, 12))
        {
            c.Picture(ListX, 44, 469, 44, _wordmark);
            c.Label(ListX + 2, 104, 400, 34, "MODS", 30, Text, UiAlign.Left, true);
            // The game's subtitle strip under the screen title.
            c.Gradient(0, 160, 1000, 50, 0x191923FFu, UiGradient.Right);
            c.Label(ListX, 160, 900, 50, "Switch mods on or off. The game picks up changes the next time it starts.", 21, Muted);
        }

        for (var i = 0; i < mods.Count; i++)
        {
            using (c.Group(0.05f + i * 0.05f, 14))
                Row(c, mods[i], ListY + i * (RowH + RowGap));
        }

        var selected = mods.FirstOrDefault(m => m.Folder == _selected);
        if (selected != null)
            using (c.Group(0.12f, 14))
                Details(c, selected, mods.Count);

        using (c.Group(0.25f, 0))
        {
            Keycap(c, 56, 1010, "Esc", "Back", () => _screen.Close());
            Keycap(c, 230, 1010, "Space", "Switch on / off", null);
            if (pending)
                c.Label(560, 1000, 900, 50, "Restart the game to apply your changes.", 21, Cream);
        }
    }

    private void Row(IUiCanvas c, ModEntry mod, float y)
    {
        var own = mod.Folder == _ownFolder;
        var on = mod.EnabledNextStart;
        var isSelected = mod.Folder == _selected;

        // A tile in the game's style: slate with a hairline border. The selection is a cream fill that slides.
        c.Clickable(ListX, y, ListW, RowH, () => Toggle(mod), TileBottom, TileHover, 4, null, () => Select(mod));
        c.Gradient(ListX, y, ListW, RowH, TileTop, UiGradient.Down);
        c.Outline(ListX, y, ListW, RowH, Line, 6);
        if (isSelected) c.Slide("mods.selection", ListX, y, ListW, RowH, Cream, 4);

        var ink = isSelected ? Bg : Text;
        var sub = isSelected ? 0x5A5560FFu : Muted;
        c.Label(ListX + 28, y + 16, ListW - 180, 36, mod.Name.ToUpperInvariant(), 26, ink);
        c.Label(ListX + 28, y + 52, ListW - 180, 26, StateText(mod), 18, StateColor(mod, isSelected, sub));

        // The switch: a cream track with a navy knob when on; on the cream selection the colours swap.
        const float sw = 72, sh = 38;
        var sx = ListX + ListW - sw - 28;
        var sy = y + (RowH - sh) / 2;
        uint track, knob;
        if (own) (track, knob) = isSelected ? (0xD9D2C5FFu, 0xFFFFFFFFu) : (Line, Faint);
        else if (on) (track, knob) = isSelected ? (Bg, Cream) : (Cream, Bg);
        else (track, knob) = isSelected ? (CreamDim, 0xFFFFFFFFu) : (Line, Faint);
        c.Panel(sx, sy, sw, sh, track, sh / 2);
        var kx = on ? sx + sw - sh + 4 : sx + 4;
        c.Slide("mods.knob." + mod.Folder, kx, sy + 4, sh - 8, sh - 8, knob, (sh - 8) / 2);
    }

    private void Details(IUiCanvas c, ModEntry mod, int count)
    {
        var own = mod.Folder == _ownFolder;
        var y = ListY;
        c.Label(DetailX, y - 6, DetailW, 60, mod.Name.ToUpperInvariant(), 44, Text, UiAlign.Left, true);
        var meta = string.Join("   ·   ", new[] { mod.Version.Length > 0 ? "Version " + mod.Version : "", mod.Author }.Where(s => s.Length > 0));
        c.Label(DetailX, y + 56, DetailW, 30, meta, 19, Faint);
        c.Rect(DetailX, y + 104, DetailW, 1, Line);
        c.Label(DetailX, y + 128, DetailW, 300, mod.Description, 23, Muted, UiAlign.TopLeft);

        // Where it stands now and after the next start.
        var by = y + 450;
        c.Rect(DetailX, by, DetailW, 1, Line);
        Fact(c, DetailX, by + 24, "NOW", own || mod.Running ? "Running" : "Off");
        Fact(c, DetailX + 190, by + 24, "NEXT START", own || mod.EnabledNextStart ? "On" : "Off");
        Fact(c, DetailX + 380, by + 24, "INSTALLED", count == 1 ? "1 mod" : count + " mods");
        if (own)
            c.Label(DetailX, by + 120, DetailW, 60, "This mod draws the menu you are using. Switch it off in the OpenFairway launcher.", 19, Faint, UiAlign.TopLeft);
    }

    private static void Fact(IUiCanvas c, float x, float y, string label, string value)
    {
        c.Label(x, y, 180, 24, label, 15, Faint, UiAlign.Left, true);
        c.Label(x, y + 26, 180, 40, value, 28, Text);
    }

    /// <summary>A key hint in the game's style: the key in an outlined box, then what it does.</summary>
    private static void Keycap(IUiCanvas c, float x, float y, string key, string label, Action? onClick)
    {
        var kw = Math.Max(40, 16 + key.Length * 11);
        if (onClick != null) c.Clickable(x - 8, y - 8, kw + 30 + label.Length * 11 + 16, 56, onClick, 0x00000000u, 0x2E323DFFu, 4);
        c.Panel(x, y, kw, 40, 0x8A90A0FFu, 5);
        c.Panel(x + 2, y + 2, kw - 4, 36, 0x191923FFu, 4);
        c.Label(x, y, kw, 40, key, 17, Text, UiAlign.Center);
        c.Label(x + kw + 14, y, 400, 40, label, 21, Text);
    }

    private string StateText(ModEntry mod) =>
        mod.Folder == _ownFolder ? "Always on"
        : mod.Running && mod.EnabledNextStart ? "On"
        : !mod.Running && !mod.EnabledNextStart ? "Off"
        : mod.EnabledNextStart ? "On after restart" : "Off after restart";

    private static uint StateColor(ModEntry mod, bool selected, uint normal) =>
        mod.Running != mod.EnabledNextStart ? (selected ? 0xB0202BFFu : Red) : normal;

    private void Select(ModEntry mod)
    {
        if (_selected == mod.Folder) return;
        _selected = mod.Folder;
        _screen.Invalidate();
    }

    private void Toggle(ModEntry mod)
    {
        _selected = mod.Folder;
        if (mod.Folder == _ownFolder)
            _ctx.Ui.ShowNotice("MODS", "This mod draws the menu you are using. Switch it off in the OpenFairway launcher.");
        else
            _ctx.Mods.SetEnabled(mod, !mod.EnabledNextStart);
        _screen.Invalidate();
    }
}
