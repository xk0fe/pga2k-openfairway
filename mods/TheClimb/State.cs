using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenFairway.Mods.Climb;

/// <summary>Everything about one career: saved as plain JSON in the mod's data folder.</summary>
public sealed class CareerState
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public int Year { get; set; } = 1;
    public int Week { get; set; } = 1;
    public int ActionsLeft { get; set; } = Rules.ActionsPerWeek;
    public int Money { get; set; } = 250;
    public int Energy { get; set; } = 80;
    public int Form { get; set; }
    public int Reputation { get; set; }
    public int Fans { get; set; }
    public int Tier { get; set; }
    public int TierPoints { get; set; }
    public string Job { get; set; } = "range";
    public bool Coach { get; set; }
    public bool Caddie { get; set; }
    public bool Agent { get; set; }
    public bool Trainer { get; set; }
    public List<string> Sponsors { get; set; } = new();
    public Dictionary<string, int> Attributes { get; set; } = new();
    public Dictionary<string, int> Facilities { get; set; } = new();
    public Dictionary<string, int> Gear { get; set; } = new();
    public List<string> Flags { get; set; } = new();
    public List<string> SeenEvents { get; set; } = new();
    public List<string> Goals { get; set; } = new();
    public List<ResultRecord> History { get; set; } = new();
    public PendingEvent? Pending { get; set; }
    public int Wins { get; set; }
    public int EventsPlayed { get; set; }
    public int CareerEarnings { get; set; }
    public int SeasonEarnings { get; set; }
    /// <summary>How strong the fields are: 0 relaxed, 1 standard, 2 tough.</summary>
    public int Difficulty { get; set; } = 1;

    public int ShiftsWorked { get; set; }
    public int SessionsTrained { get; set; }

    public int Attribute(string key) => Attributes.TryGetValue(key, out var v) ? v : Rules.StartingAttribute;
    public int Facility(string key) => Facilities.TryGetValue(key, out var v) ? v : 0;
    public int GearTier(string key) => Gear.TryGetValue(key, out var v) ? v : 0;
    public bool Has(string flag) => Flags.Contains(flag);
    public void Set(string flag) { if (!Flags.Contains(flag)) Flags.Add(flag); }

    /// <summary>One number for how good the golfer is on paper: attributes plus what is in the bag (17 to 99).</summary>
    public int Rating()
    {
        var attributes = Rules.Attributes.Average(a => Attribute(a.Key));
        var gear = Rules.Gear.Sum(g => GearTier(g.Key));
        return (int)Math.Round(attributes * 0.85 + gear);
    }

    public int SponsorIncome() => Sponsors.Sum(key => Rules.Sponsors.FirstOrDefault(s => s.Key == key)?.Weekly ?? 0);

    public static CareerState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<CareerState>(File.ReadAllText(path)) ?? new CareerState();
                if (state.Version < CurrentVersion)
                {
                    // Saves from the first version: an entered event cannot be carried over, so it is refunded.
                    if (state.Pending != null) state.Money += Rules.Tiers[Math.Clamp(state.Tier, 0, Rules.Tiers.Length - 1)].EntryFee;
                    state.Pending = null;
                    state.Flags.RemoveAll(f => f.StartsWith("played_") || f == "intro"); // the opening scene is new
                    state.ActionsLeft = Rules.ActionsPerWeek;
                    state.Version = CurrentVersion;
                }
                state.Tier = Math.Clamp(state.Tier, 0, Rules.Tiers.Length - 1);
                return state;
            }
        }
        catch (Exception) { /* a damaged save starts a new career rather than breaking the mod */ }
        return new CareerState();
    }

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}

/// <summary>The tournament the player has entered. Earlier rounds are simulated; the last one is played in the game.</summary>
public sealed class PendingEvent
{
    public string Name { get; set; } = "";
    public string CourseId { get; set; } = "";
    public string CourseName { get; set; } = "";
    public int Holes { get; set; }
    public int Rounds { get; set; } = 1;
    public int Tier { get; set; }
    public int Seed { get; set; }
    public bool Major { get; set; }

    /// <summary>Strokes per 18 holes added to the field's average for this event (the field strength chosen when it was entered).</summary>
    public double FieldShift { get; set; }

    /// <summary>The player's simulated rounds before the one they play, as strokes to par.</summary>
    public List<int> PlayerRounds { get; set; } = new();

    public List<string> KnownScorecards { get; set; } = new();
}

public sealed class ResultRecord
{
    public int Year { get; set; }
    public int Week { get; set; }
    public int Tier { get; set; }
    public string Event { get; set; } = "";
    public string Course { get; set; } = "";
    public int ToPar { get; set; }
    public int Position { get; set; }
    public int Field { get; set; }
    public int Prize { get; set; }
    public bool Major { get; set; }
}

public sealed record TierInfo(string Name, int Holes, int Rounds, int Field, double FieldMeanPer18, int EntryFee, int Purse, int PointsToPromote,
    bool Professional, string Major, string[] Events, string[] Rivals);

public sealed record NamedItem(string Key, string Name, string Description)
{
    /// <summary>Name of one of the game's interface icons that suits this item.</summary>
    public string Icon { get; init; } = "";
}

public sealed record JobInfo(string Key, string Name, int Pay, int ReputationNeeded);

public sealed record SponsorInfo(string Key, string Name, string Pitch, int Weekly, int Tier, int Reputation, int Fans, int Rating);

public sealed record GoalInfo(string Key, string Text, int Reward, Func<CareerState, bool> Done);

/// <summary>The numbers of the game. Tune here.</summary>
public static class Rules
{
    public const int ActionsPerWeek = 3;
    public const int EventActions = 2;
    public const int WeeksPerYear = 40;
    public const int MajorEvery = 5;
    public const int StartingAttribute = 20;
    public const int MaxAttribute = 99;
    public const int MaxFacilityLevel = 3;
    public const int MaxSponsors = 3;

    public static readonly TierInfo[] Tiers =
    {
        new("Club Amateur", 9, 1, 16, 6.0, 10, 0, 60, false, "Club Championship",
            new[] { "Saturday Medal", "Captain's Prize", "Winter League", "Members' Cup", "Monthly Stableford" },
            new[] { "Gus Aldridge", "Ronnie Pike", "Bill Tanner" }),
        new("Regional Amateur", 9, 2, 32, 3.5, 30, 0, 70, false, "Regional Championship",
            new[] { "County Open", "Coastal Amateur", "District Trophy", "Valley Invitational" },
            new[] { "Dane Kessler", "Owen Marsh", "Felix Brandt" }),
        new("National Amateur", 18, 2, 60, 0.5, 60, 0, 110, false, "National Amateur Championship",
            new[] { "Amateur Strokeplay", "Federation Trophy", "National Qualifier", "Lakeland Amateur" },
            new[] { "Dane Kessler", "Tomas Reyes", "Kofi Mensah" }),
        new("Mini Tour", 18, 2, 80, -2.5, 250, 20000, 160, true, "Mini Tour Championship",
            new[] { "Sunbelt Classic", "Lakeside Pro Open", "Monday Qualifier Series", "Pinewood Invitational" },
            new[] { "Dane Kessler", "Luca Moretti", "Ansel Whitlock" }),
        new("Challenger Tour", 18, 3, 120, -4.5, 400, 150000, 240, true, "Challenger Tour Finals",
            new[] { "Challenger Open", "Harbour Invitational", "Mountain Classic", "Riverside Masters" },
            new[] { "Dane Kessler", "Hiro Tanaka", "Viktor Lindqvist" }),
        new("World Tour", 18, 4, 140, -6.5, 0, 1500000, 0, true, "World Tour Championship",
            new[] { "World Tour Open", "Champions Invitational", "The Continental", "Pacific Classic" },
            new[] { "Dane Kessler", "Rafael Santoro", "Callum Reid" }),
    };

    public static readonly NamedItem[] Attributes =
    {
        new("power", "Power", "Distance off the tee.") { Icon = "icon_replay_longestDrive" },
        new("accuracy", "Accuracy", "Finding fairways and greens.") { Icon = "Icon_AimAdjust" },
        new("short", "Short Game", "Chips, pitches and bunkers.") { Icon = "icon_replay_chipGreen" },
        new("putting", "Putting", "Reading and holing putts.") { Icon = "icon_replay_putt" },
        new("fitness", "Fitness", "Events and training take less out of you.") { Icon = "SkillIcon" },
        new("mental", "Mental Game", "Steadier scores under pressure.") { Icon = "Icon_Challenge_Detail" },
    };

    /// <summary>Home base. Each level raises what training in its area gives.</summary>
    public static readonly (NamedItem Item, string Attribute, int BaseCost)[] Facilities =
    {
        (new("net", "Practice Net", "Hit balls at home. Accuracy training gives more.") { Icon = "Icon_AimAdjust" }, "accuracy", 300),
        (new("green", "Putting Green", "A strip of turf, then a real green. Putting training gives more.") { Icon = "icon_replay_putt" }, "putting", 350),
        (new("chipping", "Short Game Area", "A bunker and a chipping green. Short game training gives more.") { Icon = "icon_replay_chipGreen" }, "short", 450),
        (new("gym", "Home Gym", "Weights and mobility. Power and fitness training give more.") { Icon = "SkillIcon" }, "power", 500),
        (new("recovery", "Recovery Room", "Sleep, ice bath, massage. Resting gives more energy back.") { Icon = "Icon_Course_Sunny" }, "", 600),
        (new("studio", "Swing Studio", "Launch monitor and video. Mental training gives more, and every session earns your golfer more experience.") { Icon = "Icon_Camera_View" }, "mental", 900),
    };

    public static readonly NamedItem[] Gear =
    {
        new("driver", "Driver", "") { Icon = "icon_replay_drive" },
        new("irons", "Irons", "") { Icon = "icon_replay_approachShot" },
        new("wedges", "Wedges", "") { Icon = "icon_replay_chipGreen" },
        new("putter", "Putter", "") { Icon = "icon_replay_putt" },
        new("ball", "Golf Balls", "") { Icon = "blankCircleIcon" },
    };

    public static readonly string[] GearTiers = { "Hand-me-down", "Budget", "Mid-range", "Tour-fitted" };
    public static readonly int[] GearCosts = { 0, 150, 700, 3000 };

    public static readonly JobInfo[] Jobs =
    {
        new("range", "Range ball picker", 90, 0),
        new("shop", "Pro shop assistant", 140, 10),
        new("coach", "Junior coaching assistant", 210, 30),
    };

    public static readonly SponsorInfo[] Sponsors =
    {
        new("garage", "Harlow's Garage", "A logo on the bag, entry fees covered.", 80, 0, 8, 0, 0),
        new("dental", "Pinecrest Dental", "The county's brightest smile backs the county's brightest prospect.", 160, 1, 20, 0, 0),
        new("apparel", "Fairline Apparel", "Shirts and caps, plus a monthly retainer.", 450, 2, 0, 2500, 0),
        new("vantage", "Vantage Golf", "A full equipment contract with a stipend.", 1200, 3, 0, 0, 45),
        new("northbank", "Northbank Financial", "A serious name on the collar.", 5000, 4, 0, 30000, 0),
        new("meridian", "Meridian Air", "The headline sponsor: you fly at the front.", 20000, 5, 150, 0, 0),
    };

    // Staff wages grow with the level you play at.
    private static readonly double[] WageScale = { 1, 1, 1.5, 3, 6, 12 };
    public static int CoachWage(int tier) => (int)(80 * WageScale[tier]);
    public static int CaddieWage(int tier) => (int)(60 * WageScale[tier]);
    public static int TrainerWage(int tier) => (int)(110 * WageScale[tier]);
    public const int AgentCutPercent = 10, CaddieCutPercent = 8, AgentSponsorBonusPercent = 30;

    public static int FacilityCost(int baseCost, int nextLevel) => baseCost * nextLevel * nextLevel * (nextLevel == 3 ? 3 : 1);
    public static int FacilityUpkeep(int level) => level * 15;

    /// <summary>What a finishing position earns. Amateurs win pro shop vouchers, professionals a share of the purse.</summary>
    public static int Prize(TierInfo tier, int tierIndex, int position, bool major)
    {
        int prize;
        if (!tier.Professional)
        {
            int[] win = { 150, 400, 900 }, podium = { 60, 150, 350 }, top10 = { 0, 50, 120 };
            prize = position == 1 ? win[tierIndex] : position <= 3 ? podium[tierIndex] : position <= 10 ? top10[tierIndex] : 0;
        }
        else
        {
            if (position > tier.Field * 0.65) return 0; // out of the money
            var share = position switch { 1 => 0.18, 2 => 0.108, 3 => 0.068, <= 5 => 0.044, <= 10 => 0.027, <= 25 => 0.011, _ => 0.004 };
            prize = (int)(tier.Purse * share);
        }
        return major ? prize * 2 : prize;
    }

    public static int Points(int position, int field, bool major)
    {
        var points = position switch { 1 => 20, <= 3 => 12, <= 10 => 6, _ => position <= field / 2 ? 3 : 1 };
        return major ? points * 2 : points;
    }

    public static int FansFor(int position, int tier, bool major)
    {
        var fans = (position == 1 ? 400 : position <= 3 ? 150 : position <= 10 ? 40 : 5) * (tier + 1) * (tier + 1);
        return major ? fans * 2 : fans;
    }

    /// <summary>The player rating at which a simulated round matches the average of each level's field.</summary>
    public static readonly int[] ParRating = { 20, 32, 45, 58, 70, 82 };

    /// <summary>Field strength settings: strokes per 18 holes added to every field's average.</summary>
    public static readonly double[] FieldShift = { 3.0, 0.0, -2.0 };
    public static readonly string[] DifficultyNames = { "Relaxed", "Standard", "Tough" };

    /// <summary>What a professional is paid for a day with the fans (nothing for amateurs).</summary>
    public static int AppearanceFee(int tier) => tier < 3 ? 0 : 150 * (tier - 1) * (tier - 1);

    /// <summary>
    /// The player's expected strokes to par over 18 holes in a simulated round at a level: the field's average,
    /// better or worse by 1.2 strokes for every ten rating points above or below what the level asks for.
    /// </summary>
    public static double ExpectedToPar(CareerState s, int tier)
    {
        var expected = Tiers[tier].FieldMeanPer18 + FieldShift[Math.Clamp(s.Difficulty, 0, 2)] + Math.Clamp((ParRating[tier] - s.Rating()) * 0.12, -3.5, 5.0);
        if (s.Caddie) expected -= 0.7;
        expected -= s.Form * 0.6;
        if (s.Energy < 15) expected += 4;
        else if (s.Energy < 30) expected += 2;
        return expected;
    }

    public static readonly GoalInfo[] Goals =
    {
        new("first_event", "Play your first event", 50, s => s.EventsPlayed >= 1),
        new("first_shift", "Work a shift", 30, s => s.ShiftsWorked >= 1 || s.Tier >= 3),
        new("first_training", "Complete a training session", 30, s => s.SessionsTrained >= 1),
        new("first_facility", "Build your first facility", 100, s => s.Facilities.Values.Any(l => l >= 1)),
        new("first_gear", "Replace a hand-me-down club", 80, s => s.Gear.Values.Any(t => t >= 1)),
        new("top5", "Finish in the top five", 100, s => s.History.Any(h => h.Position <= 5)),
        new("first_win", "Win an event", 200, s => s.Wins >= 1),
        new("regional", "Reach the Regional Amateur level", 300, s => s.Tier >= 1),
        new("first_sponsor", "Sign your first sponsor", 150, s => s.Sponsors.Count >= 1),
        new("coach", "Hire a swing coach", 150, s => s.Coach),
        new("rating35", "Reach a player rating of 35", 300, s => s.Rating() >= 35),
        new("national", "Reach the National Amateur level", 600, s => s.Tier >= 2),
        new("major_amateur", "Win a championship (a week 5 event)", 800, s => s.History.Any(h => h.Major && h.Position == 1)),
        new("pro", "Turn professional", 1500, s => s.Tier >= 3),
        new("earn10k", "Earn $10,000 in prize money", 2000, s => s.CareerEarnings >= 10000),
        new("fans10k", "Reach 10,000 fans", 2500, s => s.Fans >= 10000),
        new("facility_max", "Fully build a facility", 3000, s => s.Facilities.Values.Any(l => l >= MaxFacilityLevel)),
        new("challenger", "Reach the Challenger Tour", 8000, s => s.Tier >= 4),
        new("rating70", "Reach a player rating of 70", 12000, s => s.Rating() >= 70),
        new("world", "Reach the World Tour", 40000, s => s.Tier >= 5),
        new("world_win", "Win on the World Tour", 150000, s => s.History.Any(h => h.Tier == 5 && h.Position == 1)),
        new("champion", "Win the World Tour Championship", 500000, s => s.History.Any(h => h.Tier == 5 && h.Major && h.Position == 1)),
    };
}
