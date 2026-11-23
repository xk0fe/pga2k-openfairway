using System;
using System.IO;
using System.Linq;
using OpenFairway.API;

namespace OpenFairway.Mods.Climb;

/// <summary>Developer aids, registered only when a file named debug.txt is in the mod's data folder.</summary>
public sealed partial class TheClimb
{
    private IUiScreen? _testScreen;

    private void RegisterDebugTools(IModContext ctx)
    {
        if (!File.Exists(Path.Combine(ctx.DataDirectory, "debug.txt"))) return;

        // A page of every drawing primitive, to check the toolkit against the running game.
        ctx.Actions.Register("climb.uitest", () =>
        {
            _testScreen ??= ctx.Ui.CreateScreen();
            _testScreen.HideGameUi = true;
            _testScreen.OnBack = () => _testScreen.Close();
            _testScreen.Render = c =>
            {
                c.Gradient(0, 0, 1300, 1080, 0x0A0F16FFu, UiGradient.Right);
                c.Rect(0, 0, 700, 1080, 0x0A0F16FFu);
                using (c.Group(0.0f))
                {
                    c.Shadow(60, 60, 500, 260);
                    c.Panel(60, 60, 500, 260, 0x161E29FFu, 18);
                    c.Outline(60, 60, 500, 260, 0xFFFFFF30u, 18);
                    c.Label(90, 80, 400, 50, "PANEL + SHADOW", 34, 0xFFFFFFFFu, UiAlign.Left, true);
                    c.Number(90, 140, 300, 60, "test.money", 12345, v => $"${v:N0}", 44, 0xF3C24FFFu);
                    c.Meter(90, 230, 420, 16, "test.meter", 0.65f, 0x35C97DFFu, 0x263241FFu);
                    c.Icon(470, 80, 56, "Icon_Flag", 0x35C97DFFu);
                }
                using (c.Group(0.15f))
                {
                    var courses = ctx.Ui.Courses.All;
                    c.Label(60, 350, 600, 30, $"{courses.Count} courses", 22, 0xFFFFFFFFu);
                    var x = 60f;
                    foreach (var course in courses.Take(3))
                    {
                        c.Shadow(x, 400, 380, 240);
                        var photo = ctx.Ui.Courses.Thumbnail(course.Id);
                        if (photo != null) c.Image(x, 400, 380, 240, photo, 0xFFFFFFFFu, 18);
                        else c.Panel(x, 400, 380, 240, 0x402020FFu, 18);
                        c.Gradient(x, 520, 380, 120, 0x000000D0u, UiGradient.Up);
                        c.Label(x + 16, 590, 350, 40, $"{course.Name} · par {course.Par} · {course.Holes}h · d{course.Difficulty}", 18, 0xFFFFFFFFu);
                        x += 400;
                    }
                }
                using (c.Group(0.3f))
                {
                    c.Button(60, 700, 300, 70, "HOVER ME", () => { }, 0x35C97DFFu, 0x4BDB90FFu, 0x07160EFFu, 26, true, 12, "A tooltip.\nWith a second line that is a little longer.");
                    c.Button(380, 700, 300, 70, "CLOSE", () => _testScreen.Close(), 0x263241FFu, 0x202C3BFFu, 0xFFFFFFFFu);
                    var ix = 60f;
                    foreach (var icon in new[] { "iconCoins", "Icon_Stats", "Icon_Courses", "Icon_Event", "Icon_Leaderboards", "Icon_Profile", "Icon_Tee", "HUD_TrophyCount_Icon", "Icon_Settings", "lockedIcon", "Icon_Play", "iconHot", "Icon_Crowds", "Icon_Course_Sunny", "SkillIcon", "Icon_Mail" })
                    {
                        c.Icon(ix, 800, 48, icon, 0xFFFFFFFFu);
                        ix += 60;
                    }
                }
            };
            _testScreen.Open();
        });

        // Opens the game's match screen on a course: "index,holes" (holes 0 full, 1 front nine, 2 back nine).
        ctx.Actions.Register("climb.matchtest", arg =>
        {
            var parts = arg.Split(',').Select(int.Parse).ToArray();
            var course = ctx.Ui.Courses.All[parts[0]];
            ctx.Log.Info($"Match test: {course.Name} -> {ctx.Ui.Courses.OpenMatch(course.Id, (MatchHoles)parts[1])}");
        });

        // Sets parts of the test career: "tier=3,money=50000,week=4,energy=100,attr=60".
        ctx.Actions.Register("climb.set", arg =>
        {
            foreach (var pair in arg.Split(','))
            {
                var parts = pair.Split('=');
                var value = int.Parse(parts[1]);
                switch (parts[0])
                {
                    case "tier": _state.Tier = value; _state.TierPoints = 0; break;
                    case "money": _state.Money = value; break;
                    case "week": _state.Week = value; break;
                    case "energy": _state.Energy = value; break;
                    case "points": _state.TierPoints = value; break;
                    case "fans": _state.Fans = value; break;
                    case "rep": _state.Reputation = value; break;
                    case "days": _state.ActionsLeft = value; break;
                    case "difficulty": _state.Difficulty = value; break;
                    case "attr": foreach (var a in Rules.Attributes) _state.Attributes[a.Key] = value; break;
                }
            }
            _state.Pending = null;
            _state.Flags.RemoveAll(f => f.StartsWith("played_"));
            Changed();
        });

        // Feeds a made-up final round (strokes to par) to the event that is entered, without playing it.
        ctx.Actions.Register("climb.fakeround", arg =>
        {
            if (_state.Pending == null) return;
            var toPar = int.Parse(arg);
            var holes = Enumerable.Range(0, _state.Pending.Holes).Select(i => (Par: 4, Score: 4)).ToList();
            for (var i = 0; i < Math.Abs(toPar); i++)
            {
                var hole = holes[i % holes.Count];
                holes[i % holes.Count] = (hole.Par, hole.Score + Math.Sign(toPar));
            }
            _modals.Clear();
            _screen.Open();
            ResolveEvent(new RoundResult("fake-" + _random.Next(), _state.Pending.CourseName, holes, 6, 5));
            _screen.Invalidate(true);
        });

        // Shows one of the big moments without earning it: "splash", "promo,<tier>", "ledger", "story,<id>", "page,<n>".
        ctx.Actions.Register("climb.show", arg =>
        {
            var parts = arg.Split(',');
            if (!_screen.IsOpen) _screen.Open();
            _modals.Clear();
            switch (parts[0])
            {
                case "splash": Show(new SplashModal()); break;
                case "promo": Show(new PromotionModal(Math.Clamp(int.Parse(parts[1]), 1, Rules.Tiers.Length - 1))); break;
                case "ledger": Show(new LedgerModal(_state.Week, new() { ("Sponsors", 5450), ("Team wages", -1240), ("Home base upkeep", -135) }, 4075, 22, new() { "Vantage Golf renewed your contract." })); break;
                case "story":
                    var story = _story.FirstOrDefault(s => s.Id == parts[1]);
                    if (story != null) ShowStory(story);
                    break;
                case "page": _page = (Page)int.Parse(parts[1]); break;
            }
            _screen.Invalidate(true);
        });

        // Re-reads the test save from disk, for staging a career by editing career-test.json.
        ctx.Actions.Register("climb.reload", () =>
        {
            _state = CareerState.Load(SavePath);
            _modals.Clear();
            Changed();
        });

        ctx.Actions.Register("climb.courses", () =>
        {
            foreach (var course in ctx.Ui.Courses.All)
                ctx.Log.Info($"course {course.Id} | {course.Name} | par {course.Par} | {course.Holes} holes | real {course.IsRealCourse} | difficulty {course.Difficulty} | {course.LengthYards:F0} yd");
        });
    }
}
