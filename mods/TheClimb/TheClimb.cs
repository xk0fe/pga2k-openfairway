using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenFairway.API;

namespace OpenFairway.Mods.Climb;

/// <summary>
/// THE CLIMB: a story career with management. You start as a club amateur with a day job and hand-me-down
/// clubs. Each week you choose how to spend your time, enter the week's tournament, and play its final round as
/// a real round in the game on the event's course. Earlier rounds are simulated from the golfer you have built.
/// What you earn goes into your home base, your bag and your team; results move you up six levels to the World Tour.
///
/// The mode is a layer on top of the game: it never changes a round. It asks the game to open a match on the
/// event's course, then reads the scorecard of the round you played.
///
/// This file holds the rules of play (what each action does). The Ui*.cs files draw the screens.
/// </summary>
public sealed partial class TheClimb : IFairwayMod
{
    private IModContext _ctx = null!;
    private CareerState _state = null!;
    private RoundWatcher _rounds = null!;
    private List<StoryEvent> _story = new();
    private RoundResult? _finished;
    private readonly Random _random = new();

    // With debug.txt present a separate save is used, so testing never touches the player's career.
    private bool DebugMode => File.Exists(Path.Combine(_ctx.DataDirectory, "debug.txt"));
    private string SavePath => Path.Combine(_ctx.DataDirectory, DebugMode ? "career-test.json" : "career.json");

    /// <summary>Gives the golfer you play with experience through the game's own system. The test career
    /// (debug.txt) never does, so testing leaves the real golfer alone.</summary>
    private bool GrantPlayerXp(int amount) => !DebugMode && _ctx.Actions.Invoke("player.addxp", amount.ToString());

    public string Id => "openfairway.theclimb";

    public void OnLoad(IModContext ctx)
    {
        _ctx = ctx;
        _state = CareerState.Load(SavePath);
        _rounds = new RoundWatcher(ctx.Runtime);
        _story = StoryEvent.Load(Path.Combine(ctx.ModDirectory, "assets", "story.json"), ctx.Log);
        _screen = ctx.Ui.CreateScreen();
        _screen.HideGameUi = true;
        _screen.Render = Draw;
        _screen.OnBack = Back;
        ctx.Actions.Register("climb.open", Open);
        RegisterDebugTools(ctx);
        ctx.MainThread.Update += Tick;
    }

    private TierInfo Tier => Rules.Tiers[_state.Tier];
    private JobInfo Job => Rules.Jobs.FirstOrDefault(j => j.Key == _state.Job) ?? Rules.Jobs[0];
    private string WeekFlag(string what) => $"{what}_{_state.Year}_{_state.Week}";
    private bool PlayedThisWeek => _state.Has(WeekFlag("played"));

    private void Save()
    {
        try { _state.Save(SavePath); }
        catch (Exception e) { _ctx.Log.Warn("Career not saved: " + e.Message); }
    }

    /// <summary>Saves, hands out any goals just completed, and redraws.</summary>
    private void Changed()
    {
        foreach (var goal in Rules.Goals)
        {
            if (_state.Goals.Contains(goal.Key) || !goal.Done(_state)) continue;
            _state.Goals.Add(goal.Key);
            _state.Money += goal.Reward;
            Toast($"GOAL COMPLETE   {goal.Text}   +${goal.Reward:N0}");
        }
        Save();
        _screen.Invalidate();
    }

    private void Open()
    {
        _page = Page.Hub;
        _modals.Clear();
        _screen.Open();
        if (!_splashShown)
        {
            // The title card plays once a session; after that the mode opens straight on the clubhouse.
            _splashShown = true;
            Show(new SplashModal());
        }
        if (!_state.Has("intro"))
        {
            _state.Set("intro");
            Save();
            Trigger("start");
        }
        _screen.Invalidate(true);
    }

    // ---- the calendar -----------------------------------------------------------------------------------------

    public sealed record WeekEvent(string Name, bool Major, CourseInfo? Course);

    /// <summary>The event of a given week at a given level. The same week always gives the same event and course.</summary>
    private WeekEvent EventFor(int year, int week, int tierIndex)
    {
        var tier = Rules.Tiers[tierIndex];
        var major = week % Rules.MajorEvery == 0;
        var name = major ? tier.Major : tier.Events[(week + year * 3) % tier.Events.Length];
        var pool = CoursePool(tierIndex);
        CourseInfo? course = null;
        if (pool.Count > 0)
        {
            var order = Enumerable.Range(0, pool.Count).ToArray();
            var shuffle = new Random(year * 1009 + tierIndex * 97 + 5);
            for (var i = order.Length - 1; i > 0; i--)
            {
                var j = shuffle.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            course = pool[order[(week - 1) % order.Length]];
        }
        return new WeekEvent(name, major, course);
    }

    private readonly Dictionary<int, List<CourseInfo>> _pools = new();

    /// <summary>Which of the game's courses a level visits: local, invented courses at first; the famous ones as a pro.</summary>
    private List<CourseInfo> CoursePool(int tierIndex)
    {
        if (_pools.TryGetValue(tierIndex, out var pool)) return pool;
        var all = _ctx.Ui.Courses.All.Where(c => c.Holes >= 18).ToList();
        pool = tierIndex switch
        {
            0 or 1 => all.Where(c => !c.IsRealCourse).ToList(),
            2 => all.Where(c => !c.IsRealCourse || c.Difficulty <= 2).ToList(),
            3 => all.Where(c => c.Difficulty <= 3).ToList(),
            _ => all.Where(c => c.IsRealCourse).ToList(),
        };
        if (pool.Count == 0) pool = all;
        if (all.Count > 0) _pools[tierIndex] = pool; // an empty list may only mean the game was not ready yet
        return pool;
    }

    private WeekEvent ThisWeek => EventFor(_state.Year, _state.Week, _state.Tier);

    // ---- watching for the round the player owes ------------------------------------------------------------

    private void Tick()
    {
        TickToasts();
        if (_state.Pending == null || _ctx.MainThread.Frame % 45 != 0) return;
        try
        {
            _finished ??= _rounds.FindFinished(_state.Pending.KnownScorecards);
            if (_finished == null || _screen.IsOpen) return;
            // After a round the game returns to its match screen; step back to the main menu from there.
            if (_ctx.Ui.IdleScreen == "LocalQuickplay") { _ctx.Ui.BackOut(); return; }
            if (_ctx.Ui.MainMenuReady)
            {
                var round = _finished;
                _finished = null;
                _page = Page.Hub;
                _modals.Clear();
                _screen.Open();
                ResolveEvent(round);
                _screen.Invalidate(true);
            }
        }
        catch (Exception e)
        {
            _ctx.Log.Error("Round check failed: " + e);
        }
    }

    // ---- playing --------------------------------------------------------------------------------------------

    private int EnergyCost(int basis) => Math.Max(5, (int)Math.Round(basis * (_state.Trainer ? 0.75 : 1.0)) - _state.Attribute("fitness") / 20);

    private string? WhyNotEnter()
    {
        if (PlayedThisWeek) return "You have already played this week's event.";
        if (_state.ActionsLeft < Rules.EventActions) return $"A tournament takes {Rules.EventActions} of the week's {Rules.ActionsPerWeek} days and you have {_state.ActionsLeft} left.";
        if (!Tier.Professional && _state.Money < Tier.EntryFee) return $"The entry fee is ${Tier.EntryFee:N0} and you have ${_state.Money:N0}.";
        if (_state.Energy < 15) return "You are too worn out to compete. Rest first.";
        return null;
    }

    private void EnterEvent()
    {
        if (WhyNotEnter() is { } reason) { Tell("NOT THIS WEEK", reason); return; }
        var week = ThisWeek;
        _state.Money -= Tier.EntryFee;
        _state.ActionsLeft -= Rules.EventActions;
        _state.Set(WeekFlag("played"));
        var pending = new PendingEvent
        {
            Name = week.Name, Major = week.Major, CourseId = week.Course?.Id ?? "", CourseName = week.Course?.Name ?? "",
            Holes = Tier.Holes, Rounds = Tier.Rounds, Tier = _state.Tier, Seed = _random.Next(1, int.MaxValue / 2000),
            FieldShift = Rules.FieldShift[Math.Clamp(_state.Difficulty, 0, 2)],
            KnownScorecards = _rounds.CurrentIds(),
        };
        // Every round but the last is played out from the golfer you have built.
        for (var round = 0; round < pending.Rounds - 1; round++) pending.PlayerRounds.Add(Sim.PlayerRound(_state, pending, round));
        _state.Pending = pending;
        Changed();
        if (_state.EventsPlayed == 0) Trigger("first_entry");
        if (pending.Rounds > 1) Show(new BoardModal(pending, pending.PlayerRounds.ToList(), null, Final: false));
        else Show(new BriefingModal(pending));
    }

    private void Withdraw()
    {
        _state.Pending = null;
        Changed();
    }

    /// <summary>Closes the dashboard and opens the game's match screen on the event's course.</summary>
    private void GoAndPlay()
    {
        var pending = _state.Pending;
        if (pending == null) return;
        _screen.Close();
        _ctx.MainThread.Post(() =>
        {
            try
            {
                var holes = pending.Holes <= 9 ? MatchHoles.FrontNine : MatchHoles.FullCourse;
                if (pending.CourseId.Length > 0 && _ctx.Ui.Courses.OpenMatch(pending.CourseId, holes)) return;
                if (!_ctx.Ui.MainMenu.Open(GameTile.QuickPlay))
                    _ctx.Ui.ShowNotice("THE CLIMB", "Open QUICK PLAY from the HOME tab and play your round there.");
            }
            catch (Exception e) { _ctx.Log.Error("Could not open the match: " + e); }
        });
    }

    private void ResolveEvent(RoundResult played)
    {
        var pending = _state.Pending!;
        if (played.HolesPlayed < pending.Holes)
        {
            pending.KnownScorecards.Add(played.Id);
            Save();
            Tell("ROUND TOO SHORT", $"That round was {played.HolesPlayed} holes; the {pending.Name} needs {pending.Holes}. It does not count, and you are still entered.");
            return;
        }
        var round = played.First(pending.Holes);
        var tierIndex = pending.Tier;
        var tier = Rules.Tiers[tierIndex];
        var rounds = pending.PlayerRounds.Append(round.ToPar).ToList();
        var board = Sim.Leaderboard(pending, tier, rounds);
        var me = board.First(s => s.Player);
        var position = me.Position;

        var prize = Rules.Prize(tier, tierIndex, position, pending.Major);
        var cuts = 0;
        if (tier.Professional && prize > 0)
        {
            if (_state.Caddie) cuts += prize * Rules.CaddieCutPercent / 100;
            if (_state.Agent) cuts += prize * Rules.AgentCutPercent / 100;
        }
        var points = Rules.Points(position, tier.Field, pending.Major);
        var fans = Rules.FansFor(position, tierIndex, pending.Major);

        _state.Money += prize - cuts;
        _state.CareerEarnings += prize;
        _state.SeasonEarnings += prize;
        _state.TierPoints += points;
        _state.Reputation += Math.Max(1, points / 2) + (_state.Caddie ? 1 : 0);
        _state.Fans += fans;
        _state.Energy = Math.Max(0, _state.Energy - EnergyCost(30));
        _state.Form = Math.Clamp(_state.Form + (position <= Math.Max(1, tier.Field / 10) ? 1 : position > tier.Field / 2 ? -1 : 0), -2, 2);
        _state.EventsPlayed++;
        if (position == 1) _state.Wins++;

        // Competition teaches: a little of what the round asked of you sticks.
        var learned = Rules.Attributes.OrderBy(_ => _random.Next()).Take(2).ToList();
        foreach (var attribute in learned)
            _state.Attributes[attribute.Key] = Math.Min(Rules.MaxAttribute, _state.Attribute(attribute.Key) + 1);
        GrantPlayerXp(60 + 40 * tierIndex);

        _state.History.Add(new ResultRecord
        {
            Year = _state.Year, Week = _state.Week, Tier = tierIndex, Event = pending.Name, Course = round.Course,
            ToPar = me.Total, Position = position, Field = tier.Field, Prize = prize, Major = pending.Major,
        });
        _state.Pending = null;
        _ctx.Log.Info($"{pending.Name}: {Sim.ToPar(me.Total)}, {Sim.Place(me)} of {tier.Field}.");

        var promoted = tier.PointsToPromote > 0 && _state.TierPoints >= tier.PointsToPromote && _state.Tier < Rules.Tiers.Length - 1;
        if (promoted)
        {
            _state.Tier++;
            _state.TierPoints = 0;
        }

        Show(new BoardModal(pending, rounds, new EventOutcome(round, position, me.Tied, prize, cuts, points, fans,
            string.Join(" and ", learned.Select(a => a.Name))), Final: true));
        if (_state.EventsPlayed == 1) Trigger("first_result");
        if (position == 1 && _state.Wins == 1) Trigger("first_win");
        if (promoted)
        {
            Show(new PromotionModal(_state.Tier));
            Trigger("tier_" + _state.Tier);
        }
        if (position == 1 && tierIndex == Rules.Tiers.Length - 1 && pending.Major) Trigger("champion");
        Changed();
    }

    // ---- the week's other activities ------------------------------------------------------------------------

    private int TrainingGain(string attribute)
    {
        var facility = Rules.Facilities.Where(f => f.Attribute == attribute).Select(f => _state.Facility(f.Item.Key)).DefaultIfEmpty(0).Max();
        if (attribute == "fitness") facility = Math.Max(facility, _state.Facility("gym"));
        return 2 + facility + (_state.Coach ? 1 : 0);
    }

    private bool SpendDay(int energy, string tiredMessage)
    {
        if (_state.ActionsLeft <= 0) { Tell("NO TIME LEFT", "Every day of this week is spoken for. End the week to move on."); return false; }
        if (_state.Energy < energy) { Tell("TOO TIRED", tiredMessage); return false; }
        _state.ActionsLeft--;
        _state.Energy -= energy;
        return true;
    }

    private void Train(NamedItem attribute)
    {
        if (_state.Attribute(attribute.Key) >= Rules.MaxAttribute) { Tell("NOTHING LEFT TO LEARN", $"Your {attribute.Name.ToLowerInvariant()} is as good as it gets."); return; }
        if (!SpendDay(EnergyCost(25), "You have nothing left in the tank. Rest before you train again.")) return;
        var gain = TrainingGain(attribute.Key);
        _state.Attributes[attribute.Key] = Math.Min(Rules.MaxAttribute, _state.Attribute(attribute.Key) + gain);
        _state.SessionsTrained++;
        // Training also feeds the golfer you actually play with, through the game's own experience system.
        var experience = 40 + 20 * _state.Facility("studio");
        var granted = GrantPlayerXp(experience);
        Toast($"{attribute.Name.ToUpperInvariant()} +{gain}" + (granted ? $"   MyPLAYER +{experience} XP" : ""));
        Changed();
    }

    private void Work()
    {
        if (!SpendDay(EnergyCost(20), "You are too tired for a shift. Rest first.")) return;
        _state.Money += Job.Pay;
        _state.ShiftsWorked++;
        Toast($"SHIFT DONE   +${Job.Pay}");
        Changed();
    }

    private int RestGain => 35 + 10 * _state.Facility("recovery");

    private void Rest()
    {
        if (!SpendDay(0, "")) return;
        _state.Energy = Math.Min(100, _state.Energy + RestGain);
        if (_state.Form < 0) _state.Form++;
        Toast($"RESTED   ENERGY +{RestGain}");
        Changed();
    }

    private int PromoteFans => 60 * (_state.Tier + 1) * (_state.Tier + 1);

    private void Promote()
    {
        if (!SpendDay(EnergyCost(10), "You are too tired to be charming. Rest first.")) return;
        var fee = Rules.AppearanceFee(_state.Tier);
        _state.Fans += PromoteFans;
        _state.Reputation += 2;
        _state.Money += fee;
        Toast($"A DAY WITH THE FANS   +{PromoteFans:N0} FANS" + (fee > 0 ? $"   +${fee:N0}" : "") + "   +2 REPUTATION");
        Changed();
    }

    // ---- ending the week ------------------------------------------------------------------------------------

    private int WeeklySponsors => _state.SponsorIncome() * (100 + (_state.Agent ? Rules.AgentSponsorBonusPercent : 0)) / 100;
    private int WeeklyWages => (_state.Coach ? Rules.CoachWage(_state.Tier) : 0) + (_state.Caddie ? Rules.CaddieWage(_state.Tier) : 0) + (_state.Trainer ? Rules.TrainerWage(_state.Tier) : 0);
    private int WeeklyUpkeep => Rules.Facilities.Sum(f => Rules.FacilityUpkeep(_state.Facility(f.Item.Key)));

    private void EndWeek()
    {
        if (_state.Pending != null) { Tell("YOU ARE ENTERED", $"Play your round in the {_state.Pending.Name} first, or withdraw from it."); return; }
        var lines = new List<(string Label, int Amount)>();
        if (WeeklySponsors > 0) lines.Add(("Sponsors", WeeklySponsors));
        if (WeeklyWages > 0) lines.Add(("Team wages", -WeeklyWages));
        if (WeeklyUpkeep > 0) lines.Add(("Home base upkeep", -WeeklyUpkeep));
        var net = lines.Sum(l => l.Amount);
        var rested = 10 + 12 * _state.ActionsLeft;
        var finishedWeek = _state.Week;
        _state.Money += net;
        _state.Energy = Math.Min(100, _state.Energy + rested);

        var notes = new List<string>();
        if (!PlayedThisWeek) notes.Add("You sat out this week's event.");
        if (_state.Money < 0 && WeeklyWages > 0)
        {
            _state.Coach = _state.Caddie = _state.Trainer = false;
            notes.Add("You could not make payroll: your team has moved on.");
        }

        _state.Week++;
        _state.ActionsLeft = Rules.ActionsPerWeek;
        var seasonOver = _state.Week > Rules.WeeksPerYear;
        Show(new LedgerModal(finishedWeek, lines, net, rested, notes));
        if (seasonOver)
        {
            var year = _state.Year;
            var season = _state.History.Where(h => h.Year == year).ToList();
            Show(new DialogModal($"SEASON {year} COMPLETE",
                $"Events played: {season.Count}\nWins: {season.Count(h => h.Position == 1)}\nTop tens: {season.Count(h => h.Position <= 10)}\n" +
                $"Prize money: ${_state.SeasonEarnings:N0}\n\nA new season begins. The calendar resets, the ladder does not.",
                new[] { ("ON TO NEXT SEASON", (Action)(() => { })) }, "SEASON REVIEW"));
            _state.Week = 1;
            _state.Year++;
            _state.SeasonEarnings = 0;
        }
        if (_state.Money < 0) Trigger("broke");
        if (_random.Next(100) < 65 && PickStory() is { } story) ShowStory(story);
        Changed();
    }

    // ---- story ----------------------------------------------------------------------------------------------

    private bool Eligible(StoryEvent e) =>
        (e.Tiers.Length == 0 || e.Tiers.Contains(_state.Tier)) && !_state.SeenEvents.Contains(e.Id) &&
        e.Requires.All(_state.Has) && !e.Forbids.Any(_state.Has);

    private StoryEvent? PickStory()
    {
        var eligible = _story.Where(e => e.Trigger.Length == 0 && Eligible(e)).ToList();
        return eligible.Count == 0 ? null : eligible[_random.Next(eligible.Count)];
    }

    /// <summary>Plays the story moments tied to a milestone, if they have not been seen.</summary>
    private void Trigger(string milestone)
    {
        foreach (var story in _story.Where(e => e.Trigger == milestone && Eligible(e)).ToList()) ShowStory(story);
    }

    private void ShowStory(StoryEvent story)
    {
        _state.SeenEvents.Add(story.Id);
        var choices = story.Choices.Select(choice => (choice.Label.ToUpperInvariant(), (Action)(() =>
        {
            var summary = ApplyEffects(choice.Effects);
            if (choice.Result.Length > 0)
                ShowNext(new DialogModal(story.Title, choice.Result + (summary.Length > 0 ? "\n\n" + summary : ""), new[] { ("CONTINUE", (Action)(() => { })) }, story.Speaker));
            Changed();
        }))).ToArray();
        if (choices.Length == 0) choices = new[] { ("CONTINUE", (Action)(() => { })) };
        Show(new DialogModal(story.Title, story.Text, choices, story.Speaker));
    }

    /// <summary>Applies a story choice and describes what changed, for the result dialog.</summary>
    private string ApplyEffects(Dictionary<string, JsonElement> effects)
    {
        var changes = new List<string>();
        foreach (var (key, value) in effects)
        {
            switch (key)
            {
                case "money": _state.Money += value.GetInt32(); changes.Add(Signed(value.GetInt32(), "$")); break;
                case "energy": _state.Energy = Math.Clamp(_state.Energy + value.GetInt32(), 0, 100); changes.Add($"Energy {Signed(value.GetInt32())}"); break;
                case "reputation": _state.Reputation = Math.Max(0, _state.Reputation + value.GetInt32()); changes.Add($"Reputation {Signed(value.GetInt32())}"); break;
                case "fans": _state.Fans = Math.Max(0, _state.Fans + value.GetInt32()); changes.Add($"Fans {Signed(value.GetInt32())}"); break;
                case "points": _state.TierPoints += value.GetInt32(); changes.Add($"Ranking points {Signed(value.GetInt32())}"); break;
                case "form": _state.Form = Math.Clamp(_state.Form + value.GetInt32(), -2, 2); changes.Add(value.GetInt32() > 0 ? "Form up" : "Form down"); break;
                case "flag": _state.Set(value.GetString() ?? ""); break;
                case "gear":
                    var gear = Rules.Gear.FirstOrDefault(g => g.Key == value.GetString());
                    if (gear != null && _state.GearTier(gear.Key) < Rules.GearTiers.Length - 1)
                    {
                        _state.Gear[gear.Key] = _state.GearTier(gear.Key) + 1;
                        changes.Add($"{gear.Name}: {Rules.GearTiers[_state.GearTier(gear.Key)]}");
                    }
                    break;
                default:
                    var attribute = Rules.Attributes.FirstOrDefault(a => a.Key == key);
                    if (attribute != null)
                    {
                        _state.Attributes[key] = Math.Clamp(_state.Attribute(key) + value.GetInt32(), 1, Rules.MaxAttribute);
                        changes.Add($"{attribute.Name} {Signed(value.GetInt32())}");
                    }
                    break;
            }
        }
        return string.Join("   ·   ", changes);
    }

    private static string Signed(int amount, string unit = "") => (amount < 0 ? "-" : "+") + unit + Math.Abs(amount).ToString("N0");

    // ---- management -----------------------------------------------------------------------------------------

    private void UpgradeFacility(NamedItem item, int baseCost)
    {
        var level = _state.Facility(item.Key);
        if (level >= Rules.MaxFacilityLevel) return;
        var cost = Rules.FacilityCost(baseCost, level + 1);
        if (_state.Money < cost) { Tell("NOT ENOUGH MONEY", $"That costs ${cost:N0} and you have ${_state.Money:N0}."); return; }
        _state.Money -= cost;
        _state.Facilities[item.Key] = level + 1;
        Toast($"{item.Name.ToUpperInvariant()}   LEVEL {level + 1}");
        Changed();
    }

    private void UpgradeGear(NamedItem item)
    {
        var tier = _state.GearTier(item.Key);
        if (tier >= Rules.GearTiers.Length - 1) return;
        var cost = Rules.GearCosts[tier + 1];
        if (_state.Money < cost) { Tell("NOT ENOUGH MONEY", $"That costs ${cost:N0} and you have ${_state.Money:N0}."); return; }
        _state.Money -= cost;
        _state.Gear[item.Key] = tier + 1;
        Toast($"NEW {item.Name.ToUpperInvariant()}   {Rules.GearTiers[tier + 1].ToUpperInvariant()}");
        Changed();
    }

    private string? WhyNotSponsor(SponsorInfo sponsor)
    {
        if (_state.Tier < sponsor.Tier) return $"Reach the {Rules.Tiers[sponsor.Tier].Name} level";
        if (_state.Reputation < sponsor.Reputation) return $"Reputation {sponsor.Reputation} (you have {_state.Reputation})";
        if (_state.Fans < sponsor.Fans) return $"{sponsor.Fans:N0} fans (you have {_state.Fans:N0})";
        if (_state.Rating() < sponsor.Rating) return $"Player rating {sponsor.Rating} (you have {_state.Rating()})";
        return null;
    }

    private void ToggleSponsor(SponsorInfo sponsor)
    {
        if (_state.Sponsors.Remove(sponsor.Key)) { Changed(); return; }
        if (WhyNotSponsor(sponsor) != null) return;
        if (_state.Sponsors.Count >= Rules.MaxSponsors) { Tell("NO ROOM ON THE BAG", $"You can carry {Rules.MaxSponsors} sponsors. Drop one first."); return; }
        _state.Sponsors.Add(sponsor.Key);
        Toast($"SIGNED   {sponsor.Name.ToUpperInvariant()}   +${sponsor.Weekly:N0} A WEEK");
        if (_state.Sponsors.Count == 1) Trigger("first_sponsor");
        Changed();
    }

    private void Change(Action change)
    {
        change();
        Changed();
    }
}

/// <summary>A story moment with choices, loaded from assets/story.json so it can be extended without code.</summary>
public sealed class StoryEvent
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "A MOMENT";
    public string Speaker { get; set; } = "";
    public string Trigger { get; set; } = "";
    public int[] Tiers { get; set; } = Array.Empty<int>();
    public string[] Requires { get; set; } = Array.Empty<string>();
    public string[] Forbids { get; set; } = Array.Empty<string>();
    public string Text { get; set; } = "";
    public List<StoryChoice> Choices { get; set; } = new();

    public static List<StoryEvent> Load(string path, ILog log)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return document.RootElement.GetProperty("events").Deserialize<List<StoryEvent>>(options) ?? new List<StoryEvent>();
        }
        catch (Exception e)
        {
            log.Warn("Story file not loaded: " + e.Message);
            return new List<StoryEvent>();
        }
    }
}

public sealed class StoryChoice
{
    public string Label { get; set; } = "";
    public string Result { get; set; } = "";
    public Dictionary<string, JsonElement> Effects { get; set; } = new();
}
