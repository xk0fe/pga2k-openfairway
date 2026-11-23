using System;
using System.Collections.Generic;
using System.Linq;
using OpenFairway.API;

namespace OpenFairway.Mods.Climb;

/// <summary>The pages of The Climb's screen.</summary>
public sealed partial class TheClimb
{
    // ---- clubhouse ------------------------------------------------------------------------------------------

    private void DrawHub(IUiCanvas c)
    {
        const float w = 860;
        var week = ThisWeek;
        var pending = _state.Pending;

        // This week's event, on a photo of its course.
        using (c.Group(0f))
        {
            const float h = 372;
            var courseId = pending?.CourseId ?? week.Course?.Id ?? "";
            var courseName = pending != null ? pending.CourseName : week.Course?.Name ?? "";
            var name = pending?.Name ?? week.Name;
            var major = pending?.Major ?? week.Major;
                        CoursePhoto(c, X0, Y0, w, h, courseId, 22, 0xD6DAE0FFu);
            c.Gradient(X0, Y0, w * 0.85f, h, 0x0F0F1DF0u, UiGradient.Right);
            c.Gradient(X0, Y0 + h - 190, w, 190, 0x0F0F1DD0u, UiGradient.Up);
            c.Outline(X0, Y0, w, h, major ? Gold : Line, 6);

            using (c.TextShadow(0x000000C0u, 2))
            {
                var tagX = X0 + 40;
                Chip(c, ref tagX, Y0 + 32, major ? "CHAMPIONSHIP WEEK  ·  DOUBLE POINTS" : $"WEEK {_state.Week}  ·  THIS WEEK'S EVENT", major ? Gold : Cream, OnCream);
                c.Label(X0 + 38, Y0 + 76, w - 80, 76, name.ToUpperInvariant(), name.Length > 22 ? 48 : 60, Ink, UiAlign.Left, true);
                if (courseName.Length > 0)
                {
                    c.Icon(X0 + 40, Y0 + 158, 26, "Icon_Courses", Soft);
                    c.Label(X0 + 76, Y0 + 152, w - 116, 38, courseName, 24, Soft);
                }
            }
            var cx = X0 + 40;
            Chip(c, ref cx, Y0 + 206, $"{Tier.Holes} HOLES");
            Chip(c, ref cx, Y0 + 206, Tier.Rounds == 1 ? "1 ROUND" : $"{Tier.Rounds} ROUNDS");
            Chip(c, ref cx, Y0 + 206, $"FIELD {Tier.Field}");
            if (Tier.EntryFee > 0) Chip(c, ref cx, Y0 + 206, $"ENTRY {Money(Tier.EntryFee)}");
            if (Tier.Professional) Chip(c, ref cx, Y0 + 206, $"PURSE {Money(Tier.Purse * (major ? 2 : 1))}", 0u, Gold);

            var by = Y0 + h - 104;
            if (pending != null)
            {
                c.Button(X0 + 40, by, 350, 72, pending.Rounds > 1 ? "PLAY FINAL ROUND" : "GO AND PLAY", GoAndPlay, Cream, CreamHi, OnCream, 25, true, 4,
                    "Opens the game's match screen on this course. Press BEGIN ROUND there.");
                if (pending.Rounds > 1)
                    c.Button(X0 + 406, by, 230, 72, "LEADERBOARD", () => Show(new BoardModal(pending, pending.PlayerRounds.ToList(), null, false)), GlassHi, 0x4A5160FFu, Ink, 20, true, 4);
                c.Button(X0 + w - 200, by, 160, 72, "WITHDRAW", () => Show(new DialogModal("WITHDRAW?", $"You will lose your place in the {pending.Name} and the entry fee.",
                    new[] { ("WITHDRAW", (Action)Withdraw), ("STAY IN", () => { }) })), GlassHi, Red, Soft, 18, true, 4);
            }
            else if (PlayedThisWeek)
            {
                var last = _state.History.LastOrDefault();
                var text = last != null && last.Week == _state.Week && last.Year == _state.Year
                    ? $"PLAYED  ·  {Sim.Ordinal(last.Position).ToUpperInvariant()} OF {last.Field}  ·  {Sim.ToPar(last.ToPar)}" : "YOU WITHDREW";
                c.Panel(X0 + 40, by, 540, 72, 0x15171FE6u, 4);
                c.Outline(X0 + 40, by, 540, 72, Line, 6);
                c.Label(X0 + 40, by, 540, 72, text, 23, Ink, UiAlign.Center, true);
            }
            else
            {
                var whyNot = WhyNotEnter();
                ActionButton(c, X0 + 40, by, 350, 72, "ENTER EVENT", EnterEvent, true, whyNot,
                    Tier.Rounds > 1 ? $"Takes {Rules.EventActions} days. The first {(Tier.Rounds == 2 ? "round is" : Tier.Rounds - 1 + " rounds are")} simulated from your golfer; you play the last."
                        : $"Takes {Rules.EventActions} days. You play the round yourself.");
                using (c.TextShadow())
                {
                    c.Label(X0 + 414, by + 4, 420, 30, "A WIN IS WORTH", 15, Dim, UiAlign.Left, true);
                    c.Label(X0 + 414, by + 28, 440, 40, $"+{Rules.Points(1, Tier.Field, major)} pts" + (Tier.Professional
                        ? $"  ·  {Money(Rules.Prize(Tier, _state.Tier, 1, major))}" : $"  ·  {Money(Rules.Prize(Tier, _state.Tier, 1, major))} voucher"), 26, Gold, UiAlign.Left, true);
                }
            }
        }

        // How to spend the other days.
        var ay = Y0 + 390;
        var hasDay = _state.ActionsLeft > 0;
        var noDay = hasDay ? null : "No days left this week.";
        var cards = new List<(string Title, string Icon, uint Color, string Text, string Effect, string Button, Action Click, string? WhyNot)>
        {
            ("TRAIN", "SkillIcon", Dim, "Improve an attribute.", $"-{EnergyCost(25)} energy", "CHOOSE", () => Go(Page.Training), noDay),
        };
        if (!Tier.Professional) cards.Add(("WORK", "iconCoins", Gold, Job.Name + ".", $"+{Money(Job.Pay)}  ·  -{EnergyCost(20)} energy", "WORK A SHIFT", Work, noDay));
        else cards.Add(("FAN DAY", "Icon_Crowds", Gold, "Clinics and signings.", $"+{Money(Rules.AppearanceFee(_state.Tier))}  ·  +{PromoteFans:N0} fans", "MEET THE FANS", Promote, noDay));
        cards.Add(("REST", "Icon_Course_Sunny", Cream, "Recover energy.", $"+{RestGain} energy", "REST", Rest, noDay));
        var cw = (w - 20 * (cards.Count - 1)) / cards.Count;
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var x = X0 + i * (cw + 20);
            using (c.Group(0.08f + i * 0.05f))
            {
                CardBox(c, x, ay, cw, 196);
                IconBadge(c, x + cw - 72, ay + 18, 50, card.Icon, card.Color);
                c.Label(x + 26, ay + 18, cw - 110, 44, card.Title, 30, Ink, UiAlign.Left, true);
                c.Label(x + 26, ay + 70, cw - 40, 26, card.Text, 18, Dim);
                c.Label(x + 26, ay + 96, cw - 40, 28, card.Effect, 18, Soft, UiAlign.Left, true);
                ActionButton(c, x + 24, ay + 130, cw - 48, 48, card.Button, card.Click, false, card.WhyNot);
            }
        }

        // Goals and the ladder.
        var gy = ay + 214;
        using (c.Group(0.22f))
        {
            const float gw = 500;
            CardBox(c, X0, gy, gw, 226);
            var done = _state.Goals.Count;
            Caption(c, X0 + 26, gy + 22, 300, "GOALS");
            c.Label(X0 + gw - 186, gy + 22, 160, 22, $"{done} OF {Rules.Goals.Length}", 15, Faint, UiAlign.Right, true);
            c.Meter(X0 + 26, gy + 52, gw - 52, 4, "hub.goals", done / (float)Rules.Goals.Length, Cream, 0x2E323DFFu);
            var next = Rules.Goals.Where(g => !_state.Goals.Contains(g.Key)).Take(3).ToList();
            if (next.Count == 0) c.Label(X0 + 26, gy + 90, gw - 52, 60, "Every goal met. There is nothing left to prove.", 21, Gold);
            for (var i = 0; i < next.Count; i++)
            {
                var y = gy + 70 + i * 50;
                c.Panel(X0 + 26, y + 12, 22, 22, Dim, 3);
                c.Panel(X0 + 28, y + 14, 18, 18, 0x242731FFu, 2);
                c.Label(X0 + 62, y, gw - 190, 46, next[i].Text, 19, Ink);
                c.Label(X0 + gw - 136, y, 110, 46, "+" + Money(next[i].Reward), 20, Soft, UiAlign.Right, true);
            }

            var lx = X0 + gw + 20;
            var lw = w - gw - 20;
            CardBox(c, lx, gy, lw, 226);
            Caption(c, lx + 26, gy + 22, lw, "THE LADDER");
            if (Tier.PointsToPromote > 0)
            {
                var fraction = _state.TierPoints / (float)Tier.PointsToPromote;
                c.Label(lx + 26, gy + 52, 170, 70, $"{(int)Math.Round(fraction * 100)}%", 58, Ink, UiAlign.Left, true);
                c.Label(lx + 28, gy + 122, 170, 24, "TO PROMOTION", 14, Dim, UiAlign.Left, true);
                c.Meter(lx + 26, gy + 206, lw - 52, 4, "hub.ladder", fraction, Cream, 0x2E323DFFu);
                c.Label(lx + 192, gy + 70, lw - 210, 30, $"{_state.TierPoints} / {Tier.PointsToPromote}", 26, Ink, UiAlign.Left, true);
                c.Label(lx + 192, gy + 100, lw - 210, 24, "points", 17, Dim);
                c.Label(lx + 192, gy + 138, lw - 210, 22, "NEXT", 14, Faint, UiAlign.Left, true);
                c.Label(lx + 192, gy + 156, lw - 206, 44, Rules.Tiers[_state.Tier + 1].Name.ToUpperInvariant(), 17, TierColors[_state.Tier + 1], UiAlign.TopLeft, true);
            }
            else
            {
                c.Icon(lx + 30, gy + 66, 120, "HUD_TrophyCount_Icon", Gold);
                c.Label(lx + 170, gy + 90, lw - 190, 80, "The top of the game.", 24, Gold, UiAlign.TopLeft, true);
            }
            c.Hint(lx, gy, lw, 226, "Finishing well earns ranking points: 20 for a win, 12 for the top three, 6 for the top ten.\nChampionship weeks (every fifth) pay double.\nFill the bar to move up a level.");
        }

        // Ending the week.
        var ey = gy + 244;
        using (c.Group(0.28f))
        {
            var idle = _state.ActionsLeft > 0 && pending == null;
            using (c.TextShadow())
                c.Label(X0, ey, w - 330, 64, pending != null ? "Your round is waiting. The week ends after you have played it."
                    : idle ? $"{_state.ActionsLeft} {(_state.ActionsLeft == 1 ? "day" : "days")} left. Unused days give energy back." : "The week is spent.", 19, Soft);
            ActionButton(c, X0 + w - 310, ey, 310, 64, "END THE WEEK", EndWeek, !idle && pending == null, pending != null ? "Play your round first, or withdraw." : null);
        }

        // Your golfer stands on the right, in the game's own scene; the card sits at their feet.
        using (c.Group(0.34f))
        {
            const float px = 1530, py = 770, pw = 350, ph = 262;
            c.Panel(px, py, pw, ph, 0x15171FE6u, 4);
            c.Outline(px, py, pw, ph, Line, 6);
            var rating = _state.Rating();
            c.Ring(px + 26, py + 26, 112, 4, "hub.rating", rating / 99f, Ink, Line);
            c.Number(px + 26, py + 40, 112, 60, "hub.ratingText", rating, v => Math.Round(v).ToString("0"), 44, Ink, UiAlign.Center);
            c.Label(px + 26, py + 92, 112, 20, "RATING", 12, Dim, UiAlign.Center, true);
            var par = Rules.ParRating[_state.Tier];
            c.Label(px + 156, py + 36, pw - 176, 24, "THIS LEVEL ASKS", 14, Dim, UiAlign.Left, true);
            c.Label(px + 156, py + 58, pw - 176, 44, par.ToString(), 36, rating >= par ? Cream : Soft, UiAlign.Left, true);
            c.Label(px + 156, py + 100, pw - 176, 24, rating >= par ? "You are ready." : $"{par - rating} to go.", 17, rating >= par ? Cream : Dim);
            c.Rect(px + 26, py + 156, pw - 52, 1, Line);
            Fact(c, px + 26, py + 172, "WINS", _state.Wins.ToString(), Ink, 26);
            Fact(c, px + 120, py + 172, "EVENTS", _state.EventsPlayed.ToString(), Ink, 26);
            Fact(c, px + 220, py + 172, "EARNED", Money(_state.CareerEarnings), Ink, 26);
            c.Hint(px, py, pw, ph, "Your rating comes from your six attributes and the gear in your bag.\nIt decides how you score in simulated rounds, and sponsors look at it.\nEach level asks for more.");
        }
    }

    // ---- season ---------------------------------------------------------------------------------------------

    private void DrawSeason(IUiCanvas c)
    {
        PageTitle(c, "SEASON", $"The {Tier.Name} calendar. Every fifth week is a championship worth double points" + (Tier.Professional ? " and double money." : "."));
        const float cw = 362, ch = 300, gap = 20;
        for (var i = 0; i < 8; i++)
        {
            var weekNumber = _state.Week + i;
            var year = _state.Year;
            if (weekNumber > Rules.WeeksPerYear) { weekNumber -= Rules.WeeksPerYear; year++; }
            var e = EventFor(year, weekNumber, _state.Tier);
            var x = X0 + (i % 4) * (cw + gap);
            var y = Y0 + 110 + (i / 4) * (ch + gap);
            var accent = i == 0 ? Cream : e.Major ? Gold : 0u;
            using (c.Group(i * 0.04f))
            {
                c.Panel(x, y, cw, ch, GlassSolid, 4);
                CoursePhoto(c, x, y, cw, 186, e.Course?.Id ?? "", 20, i == 0 ? 0xFFFFFFFFu : 0xFFFFFFC8u);
                c.Gradient(x, y + 96, cw, 92, GlassSolid, UiGradient.Up);
                c.Rect(x, y + 184, cw, 20, GlassSolid);
                c.Outline(x, y, cw, ch, accent != 0 ? accent : Hairline, 6);
                var tx = x + 16;
                Chip(c, ref tx, y + 16, i == 0 ? "THIS WEEK" : $"WEEK {weekNumber}", i == 0 ? Cream : 0x15171FE6u, i == 0 ? OnCream : Ink);
                if (e.Major) Chip(c, ref tx, y + 16, "CHAMPIONSHIP", Gold, OnCream);
                using (c.TextShadow())
                    c.Label(x + 22, y + 190, cw - 44, 44, e.Name.ToUpperInvariant(), e.Name.Length > 18 ? 19 : 25, Ink, UiAlign.Left, true);
                c.Label(x + 22, y + 232, cw - 44, 28, e.Course?.Name ?? "Any course", 18, Soft);
                c.Label(x + 22, y + 262, cw - 44, 26, e.Course != null ? $"Par {(Tier.Holes == 9 ? e.Course.Pars.Take(9).Sum() : e.Course.Par)}  ·  {Tier.Holes} holes" + (year != _state.Year ? "  ·  next season" : "") : "", 16, Faint);
                if (e.Major) c.Icon(x + cw - 56, y + 236, 36, "HUD_TrophyCount_Icon", Gold);
            }
        }

        // This season so far.
        var ry = Y0 + 110 + 2 * (ch + gap);
        using (c.Group(0.3f))
        {
            var bh = 1036 - ry;
            CardBox(c, X0, ry, FullW, bh);
            Caption(c, X0 + 28, ry + 22, 400, $"SEASON {_state.Year} SO FAR");
            var season = _state.History.Where(h => h.Year == _state.Year).ToList();
            var fy = ry + 66;
            Fact(c, X0 + 28, fy, "EVENTS", season.Count.ToString());
            Fact(c, X0 + 168, fy, "WINS", season.Count(h => h.Position == 1).ToString(), Gold);
            Fact(c, X0 + 290, fy, "TOP TENS", season.Count(h => h.Position <= 10).ToString());
            Fact(c, X0 + 450, fy, "BEST", season.Count == 0 ? "-" : Sim.Ordinal(season.Min(h => h.Position)));
            Fact(c, X0 + 590, fy, "PRIZE MONEY", Money(_state.SeasonEarnings));
            c.Label(X0 + 588, ry + 22, 200, 22, $"WEEK {_state.Week} OF {Rules.WeeksPerYear}", 15, Dim, UiAlign.Right, true);
            c.Meter(X0 + 28, ry + 50, 760, 4, "season.progress", _state.Week / (float)Rules.WeeksPerYear, Cream, 0x2E323DFFu);
            var recent = season.AsEnumerable().Reverse().Take(3).ToList();
            for (var i = 0; i < recent.Count; i++)
            {
                var r = recent[i];
                var y = ry + 46 + i * 48;
                c.Panel(X0 + 840, y, 640, 42, 0x1F222BFFu, 2);
                c.Label(X0 + 860, y, 110, 42, $"Week {r.Week}", 17, Faint);
                c.Label(X0 + 970, y, 320, 42, r.Event, 19, Ink);
                c.Label(X0 + 1290, y, 80, 42, Sim.ToPar(r.ToPar), 19, r.ToPar < 0 ? Red : Dim, UiAlign.Right, true);
                c.Label(X0 + 1380, y, 84, 42, Sim.Ordinal(r.Position), 20, r.Position == 1 ? Gold : Ink, UiAlign.Right, true);
            }
            if (recent.Count == 0) c.Label(X0 + 860, ry + 80, 600, 36, "No results yet this season. Your first will be written here.", 19, Faint);
        }
    }

    // ---- training -------------------------------------------------------------------------------------------

    private void DrawTraining(IUiCanvas c)
    {
        PageTitle(c, "TRAINING", $"One day and {EnergyCost(25)} energy a session. Your home base and your coach decide how much a session gives.");
        const float w = 489, h = 400, gap = 20;
        var whyNot = _state.ActionsLeft <= 0 ? "No days left this week." : _state.Energy < EnergyCost(25) ? "Too tired. Rest first." : null;
        for (var i = 0; i < Rules.Attributes.Length; i++)
        {
            var attribute = Rules.Attributes[i];
            var x = X0 + (i % 3) * (w + gap);
            var y = Y0 + 110 + (i / 3) * (h + gap);
            var value = _state.Attribute(attribute.Key);
            var gain = TrainingGain(attribute.Key);
            using (c.Group(i * 0.045f))
            {
                CardBox(c, x, y, w, h);
                IconBadge(c, x + 28, y + 26, 54, attribute.Icon, Dim);
                c.Label(x + 98, y + 22, w - 130, 40, attribute.Name.ToUpperInvariant(), 28, Ink, UiAlign.Left, true);
                c.Label(x + 98, y + 58, w - 130, 28, attribute.Description, 17, Dim);

                c.Number(x + 28, y + 112, 180, 100, "train." + attribute.Key, value, v => Math.Round(v).ToString("0"), 88, Ink);
                c.Label(x + 30, y + 208, 180, 22, $"OF {Rules.MaxAttribute}", 14, Dim, UiAlign.Left, true);
                c.Meter(x + 28, y + 244, 180, 4, "train.r." + attribute.Key, value / (float)Rules.MaxAttribute, value >= 80 ? Gold : Cream, 0x2E323DFFu);

                c.Label(x + 228, y + 132, 240, 22, "A SESSION GIVES", 14, Dim, UiAlign.Left, true);
                c.Label(x + 228, y + 154, 240, 60, $"+{gain}", 52, Cream, UiAlign.Left, true);
                var facility = Rules.Facilities.FirstOrDefault(f => f.Attribute == attribute.Key || (attribute.Key == "fitness" && f.Item.Key == "gym"));
                c.Label(x + 228, y + 218, w - 250, 50, facility.Item != null
                    ? $"{facility.Item.Name}: level {_state.Facility(facility.Item.Key)}" + (_state.Coach ? "\nCoach +1" : "") : _state.Coach ? "Coach +1" : "", 16, Faint, UiAlign.TopLeft);
                ActionButton(c, x + 28, y + h - 86, w - 56, 60, value >= Rules.MaxAttribute ? "MASTERED" : "TRAIN", value >= Rules.MaxAttribute ? null : () => Train(attribute), false, whyNot);
            }
        }
    }

    // ---- home base ------------------------------------------------------------------------------------------

    private void DrawHomeBase(IUiCanvas c)
    {
        PageTitle(c, "HOME BASE", $"Where you practise. Each level makes training in that area give more, for a little upkeep (now {Money(WeeklyUpkeep)} a week).");
        const float w = 489, h = 400, gap = 20;
        for (var i = 0; i < Rules.Facilities.Length; i++)
        {
            var (item, _, baseCost) = Rules.Facilities[i];
            var x = X0 + (i % 3) * (w + gap);
            var y = Y0 + 110 + (i / 3) * (h + gap);
            var level = _state.Facility(item.Key);
            var color = level >= Rules.MaxFacilityLevel ? Gold : level > 0 ? Cream : Faint;
            using (c.Group(i * 0.045f))
            {
                CardBox(c, x, y, w, h);
                c.Icon(x + 34, y + 38, 72, item.Icon, level > 0 ? Ink : Dim);
                c.Label(x + 146, y + 30, w - 170, 40, item.Name.ToUpperInvariant(), 27, Ink, UiAlign.Left, true);
                c.Label(x + 146, y + 70, 200, 26, level == 0 ? "NOT BUILT" : level >= Rules.MaxFacilityLevel ? "FULLY BUILT" : $"LEVEL {level}", 16, color, UiAlign.Left, true);
                Pips(c, x + 146, y + 104, level, Rules.MaxFacilityLevel, color == Faint ? Cream : color, 64);
                c.Label(x + 28, y + 150, w - 56, 96, item.Description, 20, Soft, UiAlign.TopLeft);
                c.Label(x + 28, y + 258, w - 56, 26, level > 0 ? $"Upkeep {Money(Rules.FacilityUpkeep(level))} a week" : "No upkeep until it is built", 16, Faint);
                if (level >= Rules.MaxFacilityLevel) ActionButton(c, x + 28, y + h - 86, w - 56, 60, "FULLY BUILT", null);
                else
                {
                    var cost = Rules.FacilityCost(baseCost, level + 1);
                    ActionButton(c, x + 28, y + h - 86, w - 56, 60, $"{(level == 0 ? "BUILD" : "UPGRADE")}   {Money(cost)}", () => UpgradeFacility(item, baseCost), false,
                        _state.Money < cost ? $"You need {Money(cost - _state.Money)} more." : null);
                }
            }
        }
    }

    // ---- pro shop -------------------------------------------------------------------------------------------

    private static readonly uint[] GearColors = { 0x8A90A0FFu, 0xC08A5AFFu, 0xD2D6E0FFu, Gold };

    private void DrawProShop(IUiCanvas c)
    {
        PageTitle(c, "PRO SHOP", "What is in your bag. Every step up adds a point to your rating.");
        const float gap = 20;
        var w = (FullW - gap * 4) / 5;
        const float h = 520;
        for (var i = 0; i < Rules.Gear.Length; i++)
        {
            var item = Rules.Gear[i];
            var x = X0 + i * (w + gap);
            var y = Y0 + 110;
            var tier = _state.GearTier(item.Key);
            var color = GearColors[tier];
            using (c.Group(i * 0.045f))
            {
                CardBox(c, x, y, w, h);
                // A lit stand for the club.
                c.Panel(x + 20, y + 20, w - 40, 176, 0x15171FFFu, 2);
                c.Icon(x + w / 2 - 50, y + 58, 100, item.Icon, tier == 0 ? Dim : color);
                c.Label(x + 24, y + 212, w - 48, 40, item.Name.ToUpperInvariant(), 27, Ink, UiAlign.Left, true);
                Pips(c, x + 24, y + 260, tier, Rules.GearTiers.Length - 1, color, (w - 48 - 16) / 3);
                Caption(c, x + 24, y + 292, w, "IN THE BAG");
                c.Label(x + 24, y + 314, w - 48, 36, Rules.GearTiers[tier].ToUpperInvariant(), 22, color, UiAlign.Left, true);
                if (tier >= Rules.GearTiers.Length - 1) ActionButton(c, x + 24, y + h - 84, w - 48, 60, "BEST THERE IS", null);
                else
                {
                    var cost = Rules.GearCosts[tier + 1];
                    Caption(c, x + 24, y + 362, w, "NEXT", Faint);
                    c.Label(x + 24, y + 384, w - 48, 32, Rules.GearTiers[tier + 1], 20, Soft, UiAlign.Left, true);
                    ActionButton(c, x + 24, y + h - 84, w - 48, 60, $"BUY   {Money(cost)}", () => UpgradeGear(item), false, _state.Money < cost ? $"You need {Money(cost - _state.Money)} more." : null);
                }
            }
        }
        using (c.Group(0.28f))
        {
            var y = Y0 + 110 + h + gap;
            var bh = 1036 - y;
            CardBox(c, X0, y, FullW, bh);
            var rating = _state.Rating();
            c.Ring(X0 + 30, y + (bh - 150) / 2, 150, 4, "shop.ring", rating / 99f, Ink, Line);
            c.Number(X0 + 30, y + (bh - 150) / 2 + 30, 150, 70, "shop.rating", rating, v => Math.Round(v).ToString("0"), 54, Ink, UiAlign.Center);
            c.Label(X0 + 30, y + (bh - 150) / 2 + 92, 150, 22, "RATING", 13, Dim, UiAlign.Center, true);
            c.Label(X0 + 220, y + 40, 1200, 40, "YOUR GAME, ON PAPER", 26, Ink, UiAlign.Left, true);
            c.Label(X0 + 220, y + 82, 1200, 80,
                $"Attributes and bag together. A rating of {Rules.ParRating[_state.Tier]} keeps pace with the {Tier.Name} field in simulated rounds; " +
                "every ten points above or below is worth about a stroke a round.", 21, Soft, UiAlign.TopLeft);
        }
    }

    // ---- team and sponsors ----------------------------------------------------------------------------------

    private void DrawTeam(IUiCanvas c)
    {
        PageTitle(c, "TEAM & SPONSORS", $"The people around you and the names on your bag. Wages {Money(WeeklyWages)} a week, sponsors {Money(WeeklySponsors)} a week.");
        var people = new List<(string Title, string Icon, string Text, string Cost, bool Hired, Action Toggle, string? WhyNot)>
        {
            ("SWING COACH", "Icon_ChangeGolfer", "+1 to every training session.", $"{Money(Rules.CoachWage(_state.Tier))} a week", _state.Coach, () => Change(() => _state.Coach = !_state.Coach), null),
            ("CADDIE", "Icon_Tee", "Simulated rounds come out better, and reputation grows at every event.", $"{Money(Rules.CaddieWage(_state.Tier))} a week + {Rules.CaddieCutPercent}% of winnings", _state.Caddie, () => Change(() => _state.Caddie = !_state.Caddie), null),
            ("FITNESS TRAINER", "SkillIcon", "Training, work and events take a quarter less energy.", $"{Money(Rules.TrainerWage(_state.Tier))} a week", _state.Trainer, () => Change(() => _state.Trainer = !_state.Trainer), null),
            ("AGENT", "Icon_Mail", $"Sponsors pay {Rules.AgentSponsorBonusPercent}% more.", $"{Rules.AgentCutPercent}% of winnings", _state.Agent, () => Change(() => _state.Agent = !_state.Agent),
                Tier.Professional ? null : "Agents only take on professionals."),
        };
        const float gap = 20, h = 300;
        var w = (FullW - gap * 3) / 4;
        for (var i = 0; i < people.Count; i++)
        {
            var person = people[i];
            var x = X0 + i * (w + gap);
            var y = Y0 + 110;
            using (c.Group(i * 0.045f))
            {
                CardBox(c, x, y, w, h);
                if (person.Hired)
                {
                    c.Outline(x, y, w, h, Cream, 6);
                }
                IconBadge(c, x + 24, y + 24, 60, person.Icon, person.Hired ? Cream : Dim);
                c.Label(x + 100, y + 26, w - 120, 30, person.Title, 22, Ink, UiAlign.Left, true);
                c.Label(x + 100, y + 56, w - 120, 26, person.Hired ? "ON YOUR TEAM" : "NOT HIRED", 14, person.Hired ? Cream : Faint, UiAlign.Left, true);
                c.Label(x + 24, y + 104, w - 48, 72, person.Text, 18, Soft, UiAlign.TopLeft);
                c.Label(x + 24, y + 178, w - 48, 26, person.Cost, 17, Dim);
                if (person.Hired) c.Button(x + 24, y + h - 74, w - 48, 54, "LET GO", person.Toggle, GlassHi, Red, Ink, 20, true, 4);
                else ActionButton(c, x + 24, y + h - 74, w - 48, 54, "HIRE", person.Toggle, false, person.WhyNot);
            }
        }

        var by = Y0 + 110 + h + gap;
        var bh = 1036 - by;
        const float jw = 520;
        using (c.Group(0.2f))
        {
            CardBox(c, X0, by, jw, bh);
            if (Tier.Professional)
            {
                Caption(c, X0 + 26, by + 24, 300, "INCOME");
                c.Label(X0 + 26, by + 54, jw - 52, 40, "PROFESSIONAL", 30, Ink, UiAlign.Left, true);
                c.Label(X0 + 26, by + 104, jw - 52, 200, "No day job any more. You live on prize money and sponsors.\n\nFan days bring followers; followers, results and reputation bring bigger names to your bag.", 20, Soft, UiAlign.TopLeft);
            }
            else
            {
                Caption(c, X0 + 26, by + 24, 300, "DAY JOB");
                for (var i = 0; i < Rules.Jobs.Length; i++)
                {
                    var job = Rules.Jobs[i];
                    var y = by + 58 + i * 104;
                    var current = job.Key == _state.Job;
                    var open = _state.Reputation >= job.ReputationNeeded;
                    c.Panel(X0 + 24, y, jw - 48, 92, current ? GlassHi : 0x1F222BFFu, 2);
                    if (current) c.Outline(X0 + 24, y, jw - 48, 92, Cream, 6);
                    c.Label(X0 + 46, y + 12, 300, 34, job.Name.ToUpperInvariant(), 20, open ? Ink : Faint, UiAlign.Left, true);
                    c.Label(X0 + 46, y + 48, 300, 30, $"{Money(job.Pay)} a shift", 19, open ? Dim : Faint);
                    if (current) c.Label(X0 + jw - 226, y, 180, 92, "YOUR JOB", 18, Cream, UiAlign.Right, true);
                    else if (open) c.Button(X0 + jw - 206, y + 22, 160, 48, "TAKE IT", () => Change(() => _state.Job = job.Key), GlassHi, 0x4A5160FFu, Ink, 18, true, 4);
                    else c.Label(X0 + jw - 266, y, 220, 92, $"Reputation {job.ReputationNeeded}", 17, Faint, UiAlign.Right);
                }
            }
        }

        using (c.Group(0.26f))
        {
            var sx = X0 + jw + gap;
            var sw = FullW - jw - gap;
            CardBox(c, sx, by, sw, bh);
            Caption(c, sx + 26, by + 24, 600, $"SPONSORS  ·  {_state.Sponsors.Count} OF {Rules.MaxSponsors} PLACES ON THE BAG");
            var rowH = (bh - 66) / Rules.Sponsors.Length;
            for (var i = 0; i < Rules.Sponsors.Length; i++)
            {
                var sponsor = Rules.Sponsors[i];
                var y = by + 56 + i * rowH;
                var signed = _state.Sponsors.Contains(sponsor.Key);
                var whyNot = WhyNotSponsor(sponsor);
                var locked = whyNot != null && !signed;
                c.Panel(sx + 24, y, sw - 48, rowH - 8, signed ? GlassHi : 0x1F222BFFu, 2);
                if (signed) c.Outline(sx + 24, y, sw - 48, rowH - 8, Cream, 6);
                c.Label(sx + 48, y, 320, rowH - 8, sponsor.Name.ToUpperInvariant(), 20, locked ? Faint : Ink, UiAlign.Left, true);
                c.Label(sx + 370, y, 170, rowH - 8, $"{Money(sponsor.Weekly)} / week", 19, locked ? Faint : Soft, UiAlign.Left, true);
                if (signed) c.Button(sx + sw - 186, y + (rowH - 8 - 40) / 2, 140, 40, "DROP", () => ToggleSponsor(sponsor), GlassHi, Red, Ink, 17, true, 4);
                else if (locked)
                {
                    c.Icon(sx + sw - 68, y + (rowH - 8 - 24) / 2, 24, "lockedIcon", Faint);
                    c.Label(sx + 540, y, sw - 624, rowH - 8, "Needs: " + whyNot, 17, Faint, UiAlign.Right);
                }
                else c.Button(sx + sw - 186, y + (rowH - 8 - 40) / 2, 140, 40, "SIGN", () => ToggleSponsor(sponsor), GlassHi, 0x4A5160FFu, Ink, 18, true, 4, sponsor.Pitch);
            }
        }
    }

    // ---- career ---------------------------------------------------------------------------------------------

    private void DrawCareer(IUiCanvas c)
    {
        PageTitle(c, "CAREER", "From the Saturday medal to the World Tour.");

        // The road: six levels along one line, the ones behind you lit in their colours.
        using (c.Group(0f))
        {
            var y = Y0 + 110;
            const float h = 210;
            CardBox(c, X0, y, FullW, h);
            const float rx = X0 + 130, rw = FullW - 260;
            var ry = y + 84;
            var step = rw / (Rules.Tiers.Length - 1);
            c.Rect(rx, ry - 1, rw, 2, Line);
            if (_state.Tier > 0) c.Rect(rx, ry - 1, step * _state.Tier, 2, Cream);
            if (Tier.PointsToPromote > 0)
                c.Meter(rx + step * _state.Tier, ry - 1, step * Math.Clamp(_state.TierPoints / (float)Tier.PointsToPromote, 0f, 1f), 2, "career.leg", 1f, Cream, 0x00000000u);
            for (var i = 0; i < Rules.Tiers.Length; i++)
            {
                var nx = rx + i * step;
                var here = i == _state.Tier;
                var passed = i < _state.Tier;
                if (here)
                {
                    c.Panel(nx - 30, ry - 30, 60, 60, i == Rules.Tiers.Length - 1 ? Gold : Cream, 30);
                    c.Label(nx - 30, ry - 30, 60, 60, (i + 1).ToString(), 28, OnCream, UiAlign.Center, true);
                }
                else
                {
                    c.Panel(nx - 20, ry - 20, 40, 40, passed ? Soft : Bg, 20);
                    if (!passed) c.Outline(nx - 20, ry - 20, 40, 40, Faint, 20);
                    c.Label(nx - 20, ry - 20, 40, 40, (i + 1).ToString(), 18, passed ? OnCream : Faint, UiAlign.Center, true);
                }
                c.Label(nx - 120, ry + 48, 240, 28, Rules.Tiers[i].Name.ToUpperInvariant(), here ? 19 : 16, here ? Ink : passed ? Soft : Faint, UiAlign.Center, true);
                c.Label(nx - 120, ry + 76, 240, 24,
                    passed ? "DONE" : here ? (Tier.PointsToPromote > 0 ? $"{_state.TierPoints} / {Tier.PointsToPromote} PTS" : "THE TOP") : Rules.Tiers[i].Professional ? "PROFESSIONAL" : "AMATEUR",
                    13, here ? Ink : Faint, UiAlign.Center, true);
            }
        }

        var top = Y0 + 110 + 210 + 20;
        var bottom = 1036f;
        const float aw = 470;
        using (c.Group(0.1f))
        {
            CardBox(c, X0, top, aw, bottom - top);
            Caption(c, X0 + 26, top + 24, 300, "YOUR GAME");
            c.Number(X0 + aw - 150, top + 10, 124, 60, "career.rating", _state.Rating(), v => Math.Round(v).ToString("0"), 48, Ink, UiAlign.Right);
            var rowH = (bottom - top - 100) / Rules.Attributes.Length;
            for (var i = 0; i < Rules.Attributes.Length; i++)
            {
                var attribute = Rules.Attributes[i];
                var y = top + 86 + i * rowH;
                var value = _state.Attribute(attribute.Key);
                var color = value >= 80 ? Gold : Ink;
                c.Icon(X0 + 26, y + 2, 26, attribute.Icon, Dim);
                c.Label(X0 + 64, y - 4, 220, 34, attribute.Name, 20, Ink);
                c.Label(X0 + aw - 106, y - 4, 80, 34, value.ToString(), 24, color, UiAlign.Right, true);
                c.Meter(X0 + 26, y + 38, aw - 52, 4, "career.a." + attribute.Key, value / (float)Rules.MaxAttribute, value >= 80 ? Gold : Cream, 0x2E323DFFu);
            }
        }

        var rx2 = X0 + aw + 20;
        var rw2 = FullW - aw - 20;
        using (c.Group(0.16f))
        {
            CardBox(c, rx2, top, rw2, 150);
            var history = _state.History;
            var majors = history.Count(h => h.Major && h.Position == 1);
            c.Icon(rx2 + 40, top + 30, 90, "HUD_TrophyCount_Icon", majors > 0 ? Gold : Faint);
            Fact(c, rx2 + 170, top + 42, "EVENTS", _state.EventsPlayed.ToString());
            Fact(c, rx2 + 290, top + 42, "WINS", _state.Wins.ToString(), Gold);
            Fact(c, rx2 + 400, top + 42, "TOP TENS", history.Count(h => h.Position <= 10).ToString());
            Fact(c, rx2 + 540, top + 42, "CHAMPIONSHIPS", majors.ToString(), Gold);
            Fact(c, rx2 + 740, top + 42, "CAREER EARNINGS", Money(_state.CareerEarnings));
        }
        using (c.Group(0.19f))
        {
            var dy = top + 170;
            CardBox(c, rx2, dy, rw2, 88);
            Caption(c, rx2 + 28, dy + 20, 300, "FIELD STRENGTH");
            c.Label(rx2 + 28, dy + 44, 470, 30, "How good the fields are, from your next event.", 17, Dim);
            for (var i = 0; i < Rules.DifficultyNames.Length; i++)
            {
                var level = i;
                var bx = rx2 + rw2 - 28 - (3 - i) * 172 + 12;
                var tip = i == 0 ? "Fields score about three strokes a round worse. For a gentler climb." : i == 1 ? "The intended challenge." : "Fields score about two strokes a round better.";
                if (_state.Difficulty == i)
                {
                    c.Panel(bx, dy + 20, 160, 48, Cream, 4);
                    c.Label(bx, dy + 20, 160, 48, Rules.DifficultyNames[i].ToUpperInvariant(), 19, OnCream, UiAlign.Center, true);
                    c.Hint(bx, dy + 20, 160, 48, tip);
                }
                else c.Button(bx, dy + 20, 160, 48, Rules.DifficultyNames[i].ToUpperInvariant(), () => Change(() => _state.Difficulty = level), GlassHi, 0x4A5160FFu, Soft, 19, true, 4, tip);
            }
        }
        using (c.Group(0.22f))
        {
            var ty = top + 170 + 108;
            CardBox(c, rx2, ty, rw2, bottom - ty);
            Caption(c, rx2 + 28, ty + 24, 300, "RESULTS");
            var recent = _state.History.AsEnumerable().Reverse().Take((int)((bottom - ty - 70) / 46)).ToList();
            if (recent.Count == 0) c.Label(rx2 + 28, ty + 72, 700, 40, "No events played yet. Your first result will be written here.", 20, Faint);
            for (var i = 0; i < recent.Count; i++)
            {
                var r = recent[i];
                var y = ty + 58 + i * 46;
                c.Panel(rx2 + 16, y, rw2 - 32, 42, i % 2 == 0 ? 0x242731FFu : 0x1F222BFFu, 2);
                c.Label(rx2 + 52, y, 120, 42, $"S{r.Year}  W{r.Week}", 16, Faint);
                if (r.Major) c.Icon(rx2 + 158, y + 9, 24, "HUD_TrophyCount_Icon", r.Position == 1 ? Gold : Faint);
                c.Label(rx2 + 192, y, 300, 42, r.Event, 18, Ink);
                c.Label(rx2 + 496, y, 240, 42, r.Course, 16, Dim);
                c.Label(rx2 + 730, y, 64, 42, Sim.ToPar(r.ToPar), 18, r.ToPar < 0 ? Red : Dim, UiAlign.Right, true);
                c.Label(rx2 + 800, y, 76, 42, Sim.Ordinal(r.Position), 20, r.Position == 1 ? Gold : Ink, UiAlign.Right, true);
                c.Label(rx2 + rw2 - 160, y, 132, 42, r.Prize > 0 ? Money(r.Prize) : "", 17, Soft, UiAlign.Right);
            }
        }
    }
}
