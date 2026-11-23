using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenFairway.API;

namespace OpenFairway.Mods.Climb;

/// <summary>
/// The Climb's screen: the frame every page shares (backdrop, top bar, navigation, toasts), the drawing helpers,
/// and the modal moments (title card, story, briefing, leaderboard, promotion, the week's ledger). UiPages.cs
/// draws the pages themselves. Everything is laid out on a 1920x1080 canvas.
/// </summary>
public sealed partial class TheClimb
{
    private enum Page { Hub, Season, Training, HomeBase, ProShop, Team, Career }

    private abstract record Modal;
    private sealed record SplashModal : Modal;
    private sealed record DialogModal(string Title, string Text, (string Label, Action Select)[] Choices, string Speaker = "") : Modal;
    private sealed record BriefingModal(PendingEvent Event) : Modal;
    private sealed record BoardModal(PendingEvent Event, List<int> PlayerRounds, EventOutcome? Outcome, bool Final) : Modal;
    private sealed record PromotionModal(int Tier) : Modal;
    private sealed record LedgerModal(int Week, List<(string Label, int Amount)> Lines, int Net, int Rested, List<string> Notes) : Modal;

    private sealed record EventOutcome(RoundResult Round, int Position, bool Tied, int Prize, int Cuts, int Points, int Fans, string Learned);

    private IUiScreen _screen = null!;
    private Page _page = Page.Hub;
    private bool _splashShown;
    private readonly LinkedList<Modal> _modals = new();
    private readonly List<(string Text, double Until)> _toasts = new();

    // Palette (docs/design.md, sampled from the game's own menus): slate tiles on the menu's dark background, the
    // game's cream for "do this" and the current selection, gold only for glory, red for under par and warnings.
    private const uint Bg = 0x0F0F1DFFu, Glass = 0x242731FFu, GlassSolid = 0x1A1D27FFu, GlassHi = 0x2E323DFFu, Line = 0x454850FFu, Disabled = 0x1C1F28FFu, Hairline = 0x454850FFu;
    private const uint TileTop = 0x3F4653FFu, Strip = 0x191923FFu;
    private const uint Ink = 0xF4F4F4FFu, Soft = 0xD2D6E0FFu, Dim = 0xAEB4C6FFu, Faint = 0x6E7486FFu;
    private const uint Cream = 0xFDEDDAFFu, CreamHi = 0xFFFFFFFFu, OnCream = 0x0F0F1DFFu, Gold = 0xE9B949FFu, Red = 0xE0323FFFu;

    /// <summary>The ladder reads in plain white until the very top, which is gold.</summary>
    private static readonly uint[] TierColors = { Soft, Soft, Soft, Soft, Soft, Gold };

    // Layout.
    private const float NavX = 40, NavW = 290, X0 = 372, Y0 = 136, FullW = 1508, BarH = 112;

    private string Art => Path.Combine(_ctx.ModDirectory, "assets", "hero.png");
    private static string Money(double value) => (value < 0 ? "-$" : "$") + Math.Abs(Math.Round(value)).ToString("N0");
    private static uint Alpha(uint color, uint alpha) => (color & 0xFFFFFF00u) | (alpha & 0xFF);
    private uint TierColor => TierColors[Math.Clamp(_state.Tier, 0, TierColors.Length - 1)];

    // ---- modals and toasts -------------------------------------------------------------------------------------

    private void Show(Modal modal)
    {
        _modals.AddLast(modal);
        _screen.Invalidate(true);
    }

    /// <summary>Shows a modal straight after the one in front (the answer to a story choice).</summary>
    private void ShowNext(Modal modal)
    {
        if (_modals.First != null) _modals.AddAfter(_modals.First, modal);
        else _modals.AddFirst(modal);
        _screen.Invalidate(true);
    }

    private void Tell(string title, string text) => Show(new DialogModal(title, text, new[] { ("OK", (Action)(() => { })) }));

    private void Dismiss(Action? then = null)
    {
        var current = _modals.First;
        then?.Invoke(); // may add the next modal after this one
        if (current?.List != null) _modals.Remove(current);
        _screen.Invalidate(true);
    }

    private void Toast(string text)
    {
        _toasts.Add((text, _ctx.MainThread.Time + 3.6));
        if (_toasts.Count > 3) _toasts.RemoveAt(0);
    }

    private void TickToasts()
    {
        if (_toasts.Count == 0) return;
        var now = _ctx.MainThread.Time;
        if (_toasts.RemoveAll(t => t.Until <= now) > 0 && _screen.IsOpen) _screen.Invalidate();
    }

    private void Back()
    {
        if (_modals.First != null)
        {
            // Escape (or B) answers anything with a single way out; a real choice has to be made.
            switch (_modals.First.Value)
            {
                case DialogModal { Choices.Length: 1 } dialog: Dismiss(dialog.Choices[0].Select); break;
                case DialogModal: break;
                default: Dismiss(); break;
            }
            return;
        }
        if (_page != Page.Hub) { Go(Page.Hub); return; }
        _screen.Close();
    }

    private void Go(Page page)
    {
        _page = page;
        _screen.Invalidate(true);
    }

    // ---- frame ----------------------------------------------------------------------------------------------

    private string WeekCourseId => _state.Pending?.CourseId ?? ThisWeek.Course?.Id ?? "";

    private void Draw(IUiCanvas c)
    {
        // Two moments own the whole screen.
        switch (_modals.First?.Value)
        {
            case SplashModal: DrawSplash(c); return;
            case PromotionModal promotion: DrawPromotion(c, promotion.Tier); return;
        }

        DrawBackground(c);
        DrawTopBar(c);
        DrawNavigation(c);
        switch (_page)
        {
            case Page.Hub: DrawHub(c); break;
            case Page.Season: DrawSeason(c); break;
            case Page.Training: DrawTraining(c); break;
            case Page.HomeBase: DrawHomeBase(c); break;
            case Page.ProShop: DrawProShop(c); break;
            case Page.Team: DrawTeam(c); break;
            case Page.Career: DrawCareer(c); break;
        }
        DrawToasts(c);
        if (_modals.First != null) DrawModal(c, _modals.First.Value);
    }

    private void DrawBackground(IUiCanvas c)
    {
        if (_page == Page.Hub)
        {
            // The clubhouse leaves the right of the screen to the game's own scene, where your golfer stands.
            c.Rect(0, 0, 1060, 1080, Bg);
            c.Backdrop(0, 0, 1060, 1080, Photo(WeekCourseId), null, 0x59606BFFu, 0.04f);
            c.Rect(0, 0, 1060, 1080, 0x0F0F1DD0u);
            c.Gradient(1060, 0, 560, 1080, Bg, UiGradient.Right);
            c.Gradient(1060, 0, 860, 1080, 0x0F0F1D80u, UiGradient.Right);
        }
        else
        {
            c.Rect(0, 0, 1920, 1080, Bg);
            c.Backdrop(0, 0, 1920, 1080, Photo(WeekCourseId), null, 0x6C7480FFu, 0.04f);
            c.Rect(0, 0, 1920, 1080, 0x0F0F1DC0u);
            c.Gradient(0, 0, 900, 1080, 0x0F0F1DE0u, UiGradient.Right);
        }
        c.Gradient(0, 0, 1920, 240, 0x0F0F1DF0u, UiGradient.Down);
        c.Gradient(0, 780, 1920, 300, 0x0F0F1DE0u, UiGradient.Up);
    }

    private UiImage? Photo(string courseId) => courseId.Length > 0 ? _ctx.Ui.Courses.Thumbnail(courseId) : null;

    private void DrawTopBar(IUiCanvas c)
    {
        // The screen's title, as the game titles its own screens.
        c.Label(NavX, 28, 330, 60, "THE CLIMB", 50, Ink, UiAlign.Left, true);

        // Where you are on the ladder.
        c.Label(X0, 30, 560, 26, $"LEVEL {_state.Tier + 1}  ·  {Tier.Name.ToUpperInvariant()}", 18, TierColor, UiAlign.Left, true);
        c.Label(X0, 58, 600, 30, $"Season {_state.Year}  ·  Week {_state.Week} of {Rules.WeeksPerYear}", 21, Dim);

        var x = 980f;
        Stat(c, x, 210, "FUNDS", "top.money", _state.Money, Money, _state.Money < 0 ? Red : Ink,
            $"Your money.\nEvery week: sponsors {Money(WeeklySponsors)}, wages {Money(-WeeklyWages)}, upkeep {Money(-WeeklyUpkeep)}.");
        Stat(c, x += 230, 170, "FANS", "top.fans", _state.Fans, v => Math.Round(v).ToString("N0"), Ink,
            "People who follow you. Results bring fans; sponsors count them.");
        Stat(c, x += 190, 150, "REPUTATION", "top.rep", _state.Reputation, v => Math.Round(v).ToString("N0"), Ink,
            "How you are regarded in the game. Opens better jobs and sponsors.");

        // Energy: the number with a thin bar under it, and form beside it.
        x += 170;
        c.Rect(x - 20, 30, 1, 56, Line);
        c.Label(x, 26, 120, 22, "ENERGY", 15, Dim, UiAlign.Left, true);
        c.Number(x, 46, 70, 40, "top.energyText", _state.Energy, v => Math.Round(v).ToString("0"), 32, _state.Energy < 30 ? Red : Ink);
        c.Meter(x, 88, 110, 4, "top.energy", _state.Energy / 100f, _state.Energy < 30 ? Red : Cream, 0x2E323DFFu);
        var form = _state.Form;
        c.Label(x + 64, 50, 120, 34, form > 0 ? "In form" : form < 0 ? "Out of form" : "Steady", 17, form < 0 ? Red : Dim);
        c.Hint(x, 14, 190, 86, "Energy: training, work and tournaments use it; rest restores it.\nBelow 30 your simulated rounds get worse.\n\nForm follows your recent results and helps or hurts simulated rounds.");

        // The week's days.
        x += 200;
        c.Rect(x - 20, 30, 1, 56, Line);
        c.Label(x, 26, 150, 22, "DAYS", 15, Dim, UiAlign.Left, true);
        for (var i = 0; i < Rules.ActionsPerWeek; i++)
            c.Panel(x + i * 32, 56, 24, 24, i < _state.ActionsLeft ? Cream : 0x2E323DFFu, 3);
        c.Hint(x, 14, 140, 86, $"Each week has {Rules.ActionsPerWeek} days to spend.\nA tournament takes {Rules.EventActions}; training, work, rest and fan days take 1.\nUnused days give energy back.");
    }

    private static void Stat(IUiCanvas c, float x, float w, string caption, string key, double value, Func<double, string> format, uint color, string tooltip)
    {
        c.Rect(x - 20, 30, 1, 56, Line);
        c.Label(x, 26, w, 22, caption, 15, Dim, UiAlign.Left, true);
        c.Number(x, 46, w, 44, key, value, format, 32, color);
        c.Hint(x, 14, w, 86, tooltip);
    }

    private void DrawNavigation(IUiCanvas c)
    {
        var items = new (string Label, Page Page)[]
        {
            ("CLUBHOUSE", Page.Hub), ("SEASON", Page.Season), ("TRAINING", Page.Training), ("HOME BASE", Page.HomeBase),
            ("PRO SHOP", Page.ProShop), ("TEAM & SPONSORS", Page.Team), ("CAREER", Page.Career),
        };
        var y = Y0;
        foreach (var (label, page) in items)
        {
            var selected = _page == page;
            // The selected page is a cream bar that slides from the page you came from, like the game's tabs.
            if (selected) c.Slide("climb.nav", NavX, y, NavW, 56, Cream, 4);
            else c.Clickable(NavX, y, NavW, 56, () => Go(page), 0x00000000u, 0x2E323DFFu, 4);
            c.Label(NavX + 22, y, NavW - 30, 56, label, 21, selected ? OnCream : Soft, UiAlign.Left, true);
            y += 62;
        }
        Keycap(c, NavX, 1000, "Esc", "Back to the game", () => _screen.Close());
    }

    /// <summary>A key hint in the game's style: the key in an outlined box, then what it does.</summary>
    private static void Keycap(IUiCanvas c, float x, float y, string key, string label, Action? onClick)
    {
        var kw = Math.Max(40, 16 + key.Length * 11);
        if (onClick != null) c.Clickable(x - 8, y - 8, kw + 30 + label.Length * 11 + 16, 56, onClick, 0x00000000u, 0x2E323DFFu, 4);
        c.Panel(x, y, kw, 40, 0x8A90A0FFu, 5);
        c.Panel(x + 2, y + 2, kw - 4, 36, 0x191923FFu, 4);
        c.Label(x, y, kw, 40, key, 17, Ink, UiAlign.Center);
        c.Label(x + kw + 14, y, 400, 40, label, 20, Ink);
    }

    private void DrawToasts(IUiCanvas c)
    {
        var y = BarH + 14;
        foreach (var (text, _) in _toasts)
        {
            var w = Math.Min(900, 80 + text.Length * 12.5f);
            var x = 1880 - w;
            using (c.Group(0f, -12))
            {
                c.Panel(x, y, w, 56, GlassHi, 4);
                c.Outline(x, y, w, 56, Line, 6);
                c.Rect(x, y, 4, 56, Cream);
                c.Label(x + 24, y, w - 40, 56, text, 20, Ink, UiAlign.Left, true);
            }
            y += 66;
        }
    }

    // ---- drawing helpers ---------------------------------------------------------------------------------------

    /// <summary>A tile in the game's style: a slate gradient with a hairline border. Other fills draw flat.</summary>
    private static void CardBox(IUiCanvas c, float x, float y, float w, float h, uint fill = Glass, float radius = 4)
    {
        c.Panel(x, y, w, h, fill, 4);
        if (fill == Glass) c.Gradient(x, y, w, Math.Min(h, 320), TileTop, UiGradient.Down);
        c.Outline(x, y, w, h, Line, 6);
    }

    private static void Caption(IUiCanvas c, float x, float y, float w, string text, uint color = Dim) => c.Label(x, y, w, 22, text, 16, color, UiAlign.Left, true);

    private static void PageTitle(IUiCanvas c, string title, string subtitle)
    {
        c.Label(X0, Y0 - 6, 1200, 58, title, 46, Ink, UiAlign.Left, true);
        // The game's subtitle strip.
        c.Gradient(X0 - 12, Y0 + 54, 1300, 40, Strip, UiGradient.Right);
        c.Label(X0, Y0 + 54, FullW, 40, subtitle, 20, Dim);
    }

    /// <summary>One of the game's icons, plain. Cream marks the one that matters; everything else is muted.</summary>
    private static void IconBadge(IUiCanvas c, float x, float y, float size, string icon, uint color) =>
        c.Icon(x + size * 0.15f, y + size * 0.15f, size * 0.7f, icon, color == Cream ? Ink : Dim);

    /// <summary>A button: cream when it is the thing to do, slate otherwise, and greyed with a reason when it cannot
    /// be used.</summary>
    private static void ActionButton(IUiCanvas c, float x, float y, float w, float h, string label, Action? onClick, bool primary = false, string? whyNot = null,
        string? tooltip = null)
    {
        if (onClick == null || whyNot != null)
        {
            c.Panel(x, y, w, h, Disabled, 4);
            c.Label(x, y, w, h, label, 20, Faint, UiAlign.Center, true);
            if (whyNot != null) c.Hint(x, y, w, h, whyNot);
        }
        else if (primary) c.Button(x, y, w, h, label, onClick, Cream, CreamHi, OnCream, 22, true, 4, tooltip);
        else c.Button(x, y, w, h, label, onClick, GlassHi, 0x4A5160FFu, Ink, 20, true, 4, tooltip);
    }

    /// <summary>A small tag: outlined, or filled when a colour is given (cream for now, gold for a championship).</summary>
    private static void Chip(IUiCanvas c, ref float x, float y, string text, uint fill = 0u, uint color = Soft)
    {
        var w = 28 + text.Length * 10.4f;
        if (fill == 0u) c.Outline(x, y, w, 34, Line, 6);
        else c.Panel(x, y, w, 34, fill, 4);
        c.Label(x, y, w, 34, text, 15, color, UiAlign.Center, true);
        x += w + 10;
    }

    private static void Fact(IUiCanvas c, float x, float y, string caption, string value, uint color = Ink, int size = 30)
    {
        c.Label(x, y, 260, 22, caption, 15, Dim, UiAlign.Left, true);
        c.Label(x, y + 22, 320, 44, value, size, color, UiAlign.Left, true);
    }

    private static void Pips(IUiCanvas c, float x, float y, int filled, int total, uint color, float width = 38)
    {
        for (var i = 0; i < total; i++)
            c.Panel(x + i * (width + 6), y, width, 8, i < filled ? color : 0x2E323DFFu, 2);
    }

    private void CoursePhoto(IUiCanvas c, float x, float y, float w, float h, string courseId, float radius, uint tint = 0xFFFFFFFFu)
    {
        var photo = Photo(courseId);
        if (photo != null) c.Image(x, y, w, h, photo, tint, Math.Min(radius, 4));
        else c.Picture(x, y, w, h, Art, tint, Math.Min(radius, 4));
    }

    // ---- modals ---------------------------------------------------------------------------------------------

    private void DrawModal(IUiCanvas c, Modal modal)
    {
        // A full-screen layer that takes every click, so nothing behind the dialog reacts.
        c.Clickable(0, 0, 1920, 1080, () => { }, 0x0A0A14E6u, 0x0A0A14E6u);
        using (c.Group(0f, 16))
        {
            switch (modal)
            {
                case DialogModal dialog: DrawDialog(c, dialog); break;
                case BriefingModal briefing: DrawBriefing(c, briefing.Event); break;
                case BoardModal board: DrawBoard(c, board); break;
                case LedgerModal ledger: DrawLedger(c, ledger); break;
            }
        }
    }

    private static int LineCount(string text, int perLine) => text.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / (double)perLine)));

    /// <summary>The title card when the mode opens: the week's course, your career in one line, and a way in.</summary>
    private void DrawSplash(IUiCanvas c)
    {
        c.Rect(0, 0, 1920, 1080, Bg);
        c.Backdrop(0, 0, 1920, 1080, Photo(WeekCourseId), Art, 0xE6EAEFFFu, 0.06f);
        c.Gradient(0, 0, 1500, 1080, 0x0F0F1DF2u, UiGradient.Right);
        c.Gradient(0, 0, 900, 1080, 0x0F0F1DC0u, UiGradient.Right);
        c.Gradient(0, 560, 1920, 520, Bg, UiGradient.Up);
        c.Gradient(0, 0, 1920, 200, 0x0F0F1DB0u, UiGradient.Down);

        using (c.TextShadow(0x00000099u, 2))
        {
            using (c.Group(0.05f, 24))
            {
                c.Label(176, 258, 900, 40, "A CAREER MODE FOR OPENFAIRWAY", 22, Dim, UiAlign.Left, true);
                c.Label(166, 280, 1500, 230, "THE CLIMB", 196, Ink, UiAlign.Left, true);
            }
            using (c.Group(0.2f, 18))
            {
                c.Label(176, 516, 1200, 40, $"LEVEL {_state.Tier + 1} OF {Rules.Tiers.Length}  ·  {Tier.Name.ToUpperInvariant()}", 24, TierColor, UiAlign.Left, true);
                c.Label(176, 554, 700, 36, $"Season {_state.Year}  ·  Week {_state.Week}", 23, Dim);
            }
        }

        // Your career at a glance: the rating in a thin ring, as the game shows a golfer's overall.
        using (c.Group(0.32f, 14))
        {
            const float y = 640;
            c.Ring(176, y, 120, 4, "splash.rating", _state.Rating() / 99f, Ink, 0x454850FFu);
            c.Number(176, y + 20, 120, 64, "splash.ratingText", _state.Rating(), v => Math.Round(v).ToString("0"), 48, Ink, UiAlign.Center);
            c.Label(176, y + 74, 120, 24, "RATING", 15, Dim, UiAlign.Center, true);
            var fx = 350f;
            foreach (var (caption, value, color) in new[]
                     {
                         ("FUNDS", Money(_state.Money), _state.Money < 0 ? Red : Ink), ("FANS", _state.Fans.ToString("N0"), Ink),
                         ("WINS", _state.Wins.ToString(), Ink), ("EARNED", Money(_state.CareerEarnings), Ink),
                     })
            {
                c.Rect(fx - 26, y + 22, 1, 84, Line);
                using (c.TextShadow())
                {
                    c.Label(fx, y + 24, 300, 24, caption, 16, Dim, UiAlign.Left, true);
                    c.Label(fx, y + 50, 320, 60, value, 42, color, UiAlign.Left, true);
                }
                fx += Math.Max(190, 70 + value.Length * 26);
            }
        }

        using (c.Group(0.44f, 12))
        {
            c.Button(176, 840, 400, 76, _state.EventsPlayed == 0 && _state.Week == 1 ? "BEGIN YOUR CAREER" : "CONTINUE", () => Dismiss(), Cream, CreamHi, OnCream, 28, true, 4);
            c.Button(596, 840, 240, 76, "BACK", () => _screen.Close(), GlassHi, 0x4A5160FFu, Ink, 24, true, 4);
            var next = _state.Pending != null ? $"Waiting for you: the final round of the {_state.Pending.Name}." : $"This week: {ThisWeek.Name}" + (ThisWeek.Course != null ? $" at {ThisWeek.Course.Name}." : ".");
            using (c.TextShadow())
                c.Label(178, 936, 1300, 34, next, 22, Soft);
        }
    }

    private void DrawDialog(IUiCanvas c, DialogModal dialog)
    {
        const float w = 1000, pad = 56;
        var textHeight = LineCount(dialog.Text, 80) * 29f + 8;
        var hasSpeaker = dialog.Speaker.Length > 0;
        var top = pad + (hasSpeaker ? 40 : 0);
        var buttons = dialog.Choices.Length * 74f;
        var h = top + 62 + 18 + textHeight + 36 + buttons + pad - 10;
        var x = (1920 - w) / 2;
        var y = Math.Max(110, (1080 - h) / 2);

        CardBox(c, x, y, w, h, GlassSolid);
        if (hasSpeaker) Caption(c, x + pad, y + pad - 4, 600, dialog.Speaker.ToUpperInvariant(), Gold);
        c.Label(x + pad, y + top, w - pad * 2, 62, dialog.Title, 42, Ink, UiAlign.Left, true);
        c.Label(x + pad, y + top + 78, w - pad * 2, textHeight, dialog.Text, 23, Soft, UiAlign.TopLeft);
        var by = y + top + 78 + textHeight + 36;
        for (var i = 0; i < dialog.Choices.Length; i++)
        {
            var choice = dialog.Choices[i];
            // A single way out is the thing to do; a real choice lists its answers as equals, like the game's popups.
            if (dialog.Choices.Length == 1)
                c.Button(x + pad, by, w - pad * 2, 62, choice.Label, () => Dismiss(choice.Select), Cream, CreamHi, OnCream, 23, true, 4);
            else
                c.Button(x + pad, by + i * 74, w - pad * 2, 62, choice.Label, () => Dismiss(choice.Select), GlassHi, 0x4A5160FFu, Ink, 21, true, 4);
        }
    }

    private void DrawBriefing(IUiCanvas c, PendingEvent e)
    {
        const float w = 1160, h = 640;
        var x = (1920 - w) / 2;
        var y = (1080 - h) / 2;
        CardBox(c, x, y, w, h, GlassSolid);
        CoursePhoto(c, x, y, w, 360, e.CourseId, 4);
        c.Gradient(x, y + 140, w, 222, GlassSolid, UiGradient.Up);
        c.Rect(x, y + 358, w, 26, GlassSolid);
        using (c.TextShadow(0x000000C0u, 2))
        {
            Caption(c, x + 56, y + 216, 600, e.Major ? "CHAMPIONSHIP WEEK  ·  DOUBLE POINTS" : "YOU ARE ENTERED", e.Major ? Gold : Dim);
            c.Label(x + 56, y + 240, w - 112, 76, e.Name.ToUpperInvariant(), 58, Ink, UiAlign.Left, true);
            if (e.CourseName.Length > 0) c.Label(x + 58, y + 316, w - 112, 34, e.CourseName, 23, Soft);
        }
        var cx = x + 56;
        Chip(c, ref cx, y + 382, $"{e.Holes} HOLES");
        Chip(c, ref cx, y + 382, "STROKE PLAY");
        Chip(c, ref cx, y + 382, $"FIELD {Rules.Tiers[e.Tier].Field}");
        c.Label(x + 56, y + 436, w - 112, 70,
            "One round decides it. The game opens the match on this course: press BEGIN ROUND, play it out, and come back to the main menu for the result.", 21, Dim, UiAlign.TopLeft);
        c.Button(x + 56, y + h - 112, 460, 70, "GO AND PLAY", () => Dismiss(GoAndPlay), Cream, CreamHi, OnCream, 25, true, 4);
        c.Button(x + 536, y + h - 112, 260, 70, "LATER", () => Dismiss(), GlassHi, 0x4A5160FFu, Ink, 22, true, 4);
    }

    /// <summary>The leaderboard, drawn like a golf broadcast: dark rows, red under-par totals, your line in cream.</summary>
    private void DrawBoard(IUiCanvas c, BoardModal modal)
    {
        var e = modal.Event;
        var tier = Rules.Tiers[e.Tier];
        var board = Sim.Leaderboard(e, tier, modal.PlayerRounds);
        var me = board.First(s => s.Player);
        var roundsShown = modal.PlayerRounds.Count;

        const float w = 1560, h = 900;
        var x = (1920 - w) / 2;
        var y = 96f;
        CardBox(c, x, y, w, h, GlassSolid);
        CoursePhoto(c, x, y, w, 190, e.CourseId, 4, 0xFFFFFFA0u);
        c.Gradient(x, y, w * 0.75f, 190, GlassSolid, UiGradient.Right);
        c.Gradient(x, y + 60, w, 132, GlassSolid, UiGradient.Up);
        c.Rect(x, y + 188, w, 14, GlassSolid);
        using (c.TextShadow())
        {
            Caption(c, x + 52, y + 44, 800, modal.Final ? "FINAL RESULT" : $"LEADERBOARD  ·  AFTER ROUND {roundsShown} OF {e.Rounds}", modal.Final ? Gold : Dim);
            c.Label(x + 50, y + 68, 1100, 70, e.Name.ToUpperInvariant(), 54, Ink, UiAlign.Left, true);
            c.Label(x + 52, y + 136, 1100, 30, (e.CourseName.Length > 0 ? e.CourseName + "  ·  " : "") + $"{tier.Name}  ·  field of {tier.Field}", 20, Soft);
        }

        // The table: the leaders, then your own line if you are not among them.
        const float tx = 52, rowH = 54;
        var tableW = modal.Final ? 880f : w - 104;
        var top = y + 216;
        const float colRound = 78f;
        var totalX = x + tx + tableW - 120;
        c.Label(x + tx + 18, top, 60, 30, "POS", 15, Faint, UiAlign.Left, true);
        c.Label(x + tx + 112, top, 300, 30, "PLAYER", 15, Faint, UiAlign.Left, true);
        for (var r = 0; r < roundsShown; r++)
            c.Label(totalX - (roundsShown - r) * colRound, top, colRound, 30, "R" + (r + 1), 15, Faint, UiAlign.Center, true);
        c.Label(totalX, top, 104, 30, "TOTAL", 15, Faint, UiAlign.Right, true);

        var rows = board.Take(8).ToList();
        if (!rows.Contains(me)) rows = board.Take(7).Append(me).ToList();
        var ry = top + 36;
        for (var i = 0; i < rows.Count; i++)
        {
            var s = rows[i];
            if (s.Player && i == rows.Count - 1 && board.IndexOf(s) > 7) ry += 14; // a gap before a line from further down
            using (c.Group(0.06f + i * 0.04f, 0))
            {
                var mine = s.Player;
                c.Panel(x + tx, ry, tableW, rowH - 4, mine ? Cream : i % 2 == 0 ? 0x242731FFu : 0x1F222BFFu, 2);
                // The position sits in its own darker box, as on a broadcast board.
                c.Panel(x + tx, ry, 84, rowH - 4, mine ? 0xE9D9C4FFu : 0x15171FFFu, 2);
                var place = Sim.Place(s);
                c.Label(x + tx, ry, 84, rowH - 4, place, 22, mine ? OnCream : s.Position == 1 ? Gold : Ink, UiAlign.Center, true);
                c.Label(x + tx + 112, ry, 440, rowH - 4, mine ? "YOU" : s.Name.ToUpperInvariant(), 21, mine ? OnCream : Ink, UiAlign.Left, true);
                if (s.Rival && !mine) c.Label(x + tx + 112 + s.Name.Length * 13f + 18, ry, 120, rowH - 4, "RIVAL", 13, Gold, UiAlign.Left, true);
                for (var r = 0; r < roundsShown; r++)
                    c.Label(totalX - (roundsShown - r) * colRound, ry, colRound, rowH - 4, Sim.ToPar(s.Rounds[r]), 20, mine ? 0x4A4550FFu : Dim, UiAlign.Center);
                c.Label(totalX, ry, 104, rowH - 4, Sim.ToPar(s.Total), 26, s.Total < 0 ? (mine ? 0xB0202BFFu : Red) : mine ? OnCream : Ink, UiAlign.Right, true);
            }
            ry += rowH;
        }

        if (!modal.Final || modal.Outcome == null)
        {
            var leader = board[0];
            var gap = me.Total - leader.Total;
            var story = me.Position == 1
                ? (me.Tied ? "You share the lead going into the final round." : "You lead going into the final round. Now finish it.")
                : $"You are {Sim.Place(me)}, {gap} behind {(leader.Player ? "the lead" : leader.Name)}. The final round is yours to play.";
            c.Label(x + 52, y + h - 190, w - 104, 40, story, 26, Ink, UiAlign.TopLeft, true);
            c.Label(x + 52, y + h - 148, w - 104, 30, $"{e.Holes} holes of stroke play. The game opens the match on the right course; press BEGIN ROUND.", 19, Dim, UiAlign.TopLeft);
            c.Button(x + 52, y + h - 104, 480, 68, "PLAY THE FINAL ROUND", () => Dismiss(GoAndPlay), Cream, CreamHi, OnCream, 25, true, 4);
            c.Button(x + 552, y + h - 104, 240, 68, "LATER", () => Dismiss(), GlassHi, 0x4A5160FFu, Ink, 21, true, 4);
            return;
        }

        // Final: what it was worth.
        var o = modal.Outcome;
        var px = x + tx + tableW + 36;
        var pw = w - tx - tableW - 36 - 52;
        using (c.Group(0.3f, 14))
        {
            CardBox(c, px, top, pw, 600);
            Caption(c, px + 30, top + 26, pw, "YOU FINISHED");
            c.Label(px + 28, top + 44, pw - 56, 120, (o.Tied ? "T" : "") + Sim.Ordinal(o.Position), 104, o.Position == 1 ? Gold : Ink, UiAlign.Left, true);
            c.Label(px + 30, top + 160, pw - 56, 28, $"of {tier.Field}  ·  {Sim.ToPar(me.Total)} overall", 21, Soft);

            var prizeText = o.Prize > 0 ? (tier.Professional ? Money(o.Prize) : $"{Money(o.Prize)} voucher") : "-";
            Fact(c, px + 30, top + 208, tier.Professional ? "PRIZE MONEY" : "PRIZE", prizeText, o.Prize > 0 ? Ink : Faint);
            Fact(c, px + 30 + (pw - 60) / 2, top + 208, "RANKING POINTS", "+" + o.Points);
            Fact(c, px + 30, top + 284, "NEW FANS", "+" + o.Fans.ToString("N0"));
            Fact(c, px + 30 + (pw - 60) / 2, top + 284, "YOUR ROUND", $"{o.Round.Strokes} ({Sim.ToPar(o.Round.ToPar)})");
            if (o.Cuts > 0) c.Label(px + 30, top + 350, pw - 60, 24, $"{Money(o.Cuts)} of the prize goes to your team.", 17, Faint);

            // The card as golfers mark it: a circle round a birdie (two for an eagle), a square round a bogey.
            Caption(c, px + 30, top + 388, pw, "YOUR CARD");
            var holes = o.Round.Holes;
            var cell = Math.Min(46f, (pw - 60) / 9f);
            for (var i = 0; i < holes.Count; i++)
            {
                var (par, score) = holes[i];
                var cx = px + 30 + (i % 9) * cell;
                var cy = top + 418 + (i / 9) * 52;
                var diff = score - par;
                var size = cell - 8;
                if (diff <= -1) c.Outline(cx, cy, size, size, Red, size / 2);
                if (diff <= -2) c.Outline(cx + 4, cy + 4, size - 8, size - 8, Red, (size - 8) / 2);
                if (diff == 1) c.Outline(cx, cy, size, size, Dim, 3);
                if (diff >= 2) c.Panel(cx, cy, size, size, 0x454850FFu, 3);
                c.Label(cx, cy, size, size, score.ToString(), 19, diff < 0 ? Red : Ink, UiAlign.Center, true);
            }
            c.Label(px + 30, top + 530, pw - 60, 60, $"Greens in regulation {o.Round.Greens}  ·  fairways {o.Round.Fairways}\nCompetition sharpened your {o.Learned.ToLowerInvariant()}.", 17, Dim, UiAlign.TopLeft);
        }
        c.Button(x + 52, y + h - 96, 420, 64, "CONTINUE", () => Dismiss(), Cream, CreamHi, OnCream, 25, true, 4);
    }

    /// <summary>A promotion owns the whole screen: the new level's name, and the road so far.</summary>
    private void DrawPromotion(IUiCanvas c, int tierIndex)
    {
        var tier = Rules.Tiers[tierIndex];
        var before = Rules.Tiers[tierIndex - 1];
        var pool = CoursePool(tierIndex);
        var photo = pool.Count > 0 ? Photo(pool[(tierIndex * 7 + _state.Year) % pool.Count].Id) : null;

        c.Clickable(0, 0, 1920, 1080, () => { }, Bg, Bg);
        c.Backdrop(0, 0, 1920, 1080, photo, Art, 0xB8BEC8FFu, 0.08f);
        c.Rect(0, 0, 1920, 1080, 0x0F0F1D90u);
        c.Gradient(0, 0, 1920, 600, 0x0F0F1DE0u, UiGradient.Down);
        c.Gradient(0, 420, 1920, 660, Bg, UiGradient.Up);

        using (c.TextShadow(0x000000A0u, 3))
        {
            using (c.Group(0.1f, 30))
            {
                c.Rect(600, 216, 200, 1, Gold);
                c.Rect(1120, 216, 200, 1, Gold);
                c.Label(0, 196, 1920, 40, "PROMOTED", 30, Gold, UiAlign.Center, true);
                c.Label(0, 236, 1920, 190, tier.Name.ToUpperInvariant(), tier.Name.Length > 14 ? 128 : 150, Ink, UiAlign.Center, true);
                c.Label(0, 418, 1920, 40, $"LEVEL {tierIndex + 1} OF {Rules.Tiers.Length}", 26, Dim, UiAlign.Center, true);
            }
            using (c.Group(0.3f, 20))
            {
                var turningPro = tier.Professional && !before.Professional;
                c.Label(360, 476, 1200, 80,
                    turningPro ? "You are turning professional. The day job is over: from here you live on what you win and who backs you."
                        : $"You have earned your place among better players. The {before.Name.ToLowerInvariant()} days are behind you.", 26, Soft, UiAlign.Center);
            }
        }

        // The road: six stops on one line, the ones behind you filled, this one in gold.
        using (c.Group(0.45f, 16))
        {
            const float rx = 460, rw = 1000, ry = 640;
            var step = rw / (Rules.Tiers.Length - 1);
            c.Rect(rx, ry - 1, rw, 2, 0x454850FFu);
            c.Rect(rx, ry - 1, step * tierIndex, 2, Cream);
            for (var i = 0; i < Rules.Tiers.Length; i++)
            {
                var nx = rx + i * step;
                var here = i == tierIndex;
                var reached = i < tierIndex;
                var size = here ? 26f : 16f;
                c.Panel(nx - size / 2, ry - size / 2, size, size, here ? Gold : reached ? Cream : Bg, size / 2);
                if (!reached && !here) c.Outline(nx - size / 2, ry - size / 2, size, size, 0x6E7486FFu, size / 2);
                using (c.TextShadow())
                    c.Label(nx - 110, ry + 26, 220, 26, Rules.Tiers[i].Name.ToUpperInvariant(), 15, here ? Gold : reached ? Ink : Faint, UiAlign.Center, true);
            }
        }

        using (c.Group(0.6f, 14))
        {
            var facts = new (string Caption, string Value)[]
            {
                ("EVENTS", $"{tier.Rounds} x {tier.Holes} holes"), ("FIELD", tier.Field.ToString()),
                (tier.Professional ? "PURSE" : "ENTRY", tier.Professional ? Money(tier.Purse) : Money(tier.EntryFee)),
                ("COURSES", tierIndex >= 4 ? "The famous ones" : tierIndex >= 2 ? "Tougher" : "Local"),
            };
            var fx = 460f;
            foreach (var (caption, value) in facts)
            {
                using (c.TextShadow())
                {
                    c.Label(fx, 738, 250, 24, caption, 16, Dim, UiAlign.Left, true);
                    c.Label(fx, 762, 300, 46, value, 32, Ink, UiAlign.Left, true);
                }
                fx += 260;
            }
        }

        using (c.Group(0.75f, 12))
        {
            c.Button(760, 880, 400, 80, "ONWARD", () => Dismiss(), Gold, 0xF5CD6BFFu, OnCream, 28, true, 4);
            if (tier.Rounds > 1)
                using (c.TextShadow())
                    c.Label(360, 978, 1200, 30, $"Events now run over {tier.Rounds} rounds: the first {(tier.Rounds == 2 ? "is" : tier.Rounds - 1 + " are")} simulated from your golfer, you play the last.", 19, Dim, UiAlign.Center);
        }
    }

    private void DrawLedger(IUiCanvas c, LedgerModal ledger)
    {
        const float w = 840;
        var h = 310 + ledger.Lines.Count * 50 + ledger.Notes.Count * 36 + (ledger.Lines.Count > 0 ? 76 : 0);
        var x = (1920 - w) / 2;
        var y = (1080 - h) / 2;
        CardBox(c, x, y, w, h, GlassSolid);
        Caption(c, x + 56, y + 52, 400, "WEEK COMPLETE");
        c.Label(x + 56, y + 76, w - 112, 60, $"WEEK {ledger.Week} IN THE BOOKS", 44, Ink, UiAlign.Left, true);
        var ly = y + 160;
        foreach (var (label, amount) in ledger.Lines)
        {
            c.Label(x + 56, ly, 500, 44, label, 23, Soft);
            c.Label(x + w - 356, ly, 300, 44, (amount > 0 ? "+" : "") + Money(amount), 26, amount >= 0 ? Ink : Red, UiAlign.Right, true);
            ly += 50;
        }
        if (ledger.Lines.Count > 0)
        {
            c.Rect(x + 56, ly + 6, w - 112, 1, Line);
            c.Label(x + 56, ly + 20, 500, 46, "Net this week", 23, Ink, UiAlign.Left, true);
            c.Label(x + w - 356, ly + 20, 300, 46, (ledger.Net > 0 ? "+" : "") + Money(ledger.Net), 32, ledger.Net >= 0 ? Ink : Red, UiAlign.Right, true);
            ly += 76;
        }
        c.Label(x + 56, ly, w - 112, 34, $"Energy +{ledger.Rested} from the days off.", 21, Dim);
        ly += 40;
        foreach (var note in ledger.Notes)
        {
            c.Label(x + 56, ly, w - 112, 32, note, 21, Gold);
            ly += 36;
        }
        c.Button(x + 56, y + h - 108, w - 112, 64, "NEXT WEEK", () => Dismiss(), Cream, CreamHi, OnCream, 25, true, 4);
    }
}
