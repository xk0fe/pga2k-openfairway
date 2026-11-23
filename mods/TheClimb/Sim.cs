using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenFairway.Mods.Climb;

/// <summary>One line of a tournament leaderboard.</summary>
public sealed class Standing
{
    public string Name = "";
    public bool Player, Rival;
    public int[] Rounds = Array.Empty<int>(); // strokes to par, round by round
    public int Total => Rounds.Sum();
    public int Position;
    public bool Tied;
}

/// <summary>
/// The simulated side of a tournament: a named field whose scores depend only on the event's seed, so the same
/// leaderboard can be rebuilt at any time without being stored.
/// </summary>
public static class Sim
{
    private static readonly string[] First =
    {
        "Aaron", "Ben", "Carl", "Dev", "Eli", "Finn", "Grant", "Hugo", "Ivan", "Jack", "Kai", "Leo", "Max", "Noah", "Otis", "Pete", "Quinn", "Reid",
        "Sam", "Theo", "Umar", "Vince", "Wes", "Xavi", "Yusuf", "Zane", "Arlo", "Bram", "Cole", "Dario", "Emil", "Flynn", "Gabe", "Henrik", "Idris",
        "Jonas", "Kenji", "Lars", "Mateo", "Nico",
    };

    private static readonly string[] Last =
    {
        "Abbott", "Barlow", "Castell", "Doyle", "Eastman", "Fenwick", "Garrow", "Hale", "Ingram", "Jessop", "Keane", "Lowry", "Maddox", "Nash",
        "Orton", "Penrose", "Quill", "Rourke", "Stroud", "Thorne", "Underhill", "Voss", "Wyatt", "Yardley", "Zeller", "Ashby", "Brook", "Calder",
        "Dunmore", "Ellery", "Fairbank", "Glover", "Hartley", "Ives", "Jarvis", "Kimble", "Latham", "Mercer", "Norwood", "Oakes", "Pryor", "Radley",
        "Sefton", "Tolland", "Upton", "Vane", "Whitby", "Yates", "Okoro", "Sato", "Varga", "Lindgren", "Moreau", "Navarro", "Petrov", "Rossi",
        "Schmidt", "Tran", "Walker", "Zhou",
    };

    /// <summary>Scores of everyone else in the field for the first <paramref name="rounds"/> rounds.</summary>
    public static List<Standing> Field(PendingEvent e, TierInfo tier, int rounds)
    {
        var field = new List<Standing>();
        var names = new HashSet<string>(tier.Rivals);
        var scale = e.Holes / 18.0;
        for (var i = 0; i < tier.Field - 1; i++)
        {
            var random = new Random(unchecked(e.Seed * 31 + i * 7919));
            var rival = i < tier.Rivals.Length;
            string name;
            if (rival) name = tier.Rivals[i];
            else
            {
                do name = $"{First[random.Next(First.Length)]} {Last[random.Next(Last.Length)]}";
                while (!names.Add(name));
            }
            // Each player has a level for the week; rivals are the class of the field.
            var level = tier.FieldMeanPer18 + e.FieldShift + Gaussian(random) * 1.4 - (rival ? 1.8 : 0);
            var scores = new int[rounds];
            for (var r = 0; r < rounds; r++)
            {
                var roundRandom = new Random(unchecked(e.Seed * 131 + i * 104729 + r * 15485863));
                scores[r] = (int)Math.Round(level * scale + Gaussian(roundRandom) * 2.3 * Math.Sqrt(scale));
            }
            field.Add(new Standing { Name = name, Rival = rival, Rounds = scores });
        }
        return field;
    }

    /// <summary>One simulated round for the player: strokes to par.</summary>
    public static int PlayerRound(CareerState state, PendingEvent e, int round)
    {
        var random = new Random(unchecked(e.Seed * 977 + round * 6151 + 17));
        var scale = e.Holes / 18.0;
        var spread = 2.8 - state.Attribute("mental") / 70.0; // a steadier head, a tighter range of scores
        return (int)Math.Round(Rules.ExpectedToPar(state, e.Tier) * scale + Gaussian(random) * spread * Math.Sqrt(scale));
    }

    /// <summary>The leaderboard after the given player rounds, best first, with positions and ties filled in.</summary>
    public static List<Standing> Leaderboard(PendingEvent e, TierInfo tier, IReadOnlyList<int> playerRounds)
    {
        var board = Field(e, tier, playerRounds.Count);
        board.Add(new Standing { Name = "YOU", Player = true, Rounds = playerRounds.ToArray() });
        // Ties are broken in the player's favour only for the order on screen; the position is shared.
        board = board.OrderBy(s => s.Total).ThenByDescending(s => s.Player).ToList();
        for (var i = 0; i < board.Count; i++)
        {
            var total = board[i].Total;
            board[i].Position = board.Count(s => s.Total < total) + 1;
            board[i].Tied = board.Count(s => s.Total == total) > 1;
        }
        return board;
    }

    public static string ToPar(int toPar) => toPar == 0 ? "E" : toPar > 0 ? $"+{toPar}" : toPar.ToString();

    public static string Place(Standing s) => (s.Tied ? "T" : "") + s.Position;

    public static string Ordinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    private static double Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
