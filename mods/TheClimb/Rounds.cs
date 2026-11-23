using System;
using System.Collections.Generic;
using System.Linq;
using OpenFairway.API;

namespace OpenFairway.Mods.Climb;

/// <summary>A round the player finished in the game: the course and every completed hole's par and score.</summary>
public sealed record RoundResult(string Id, string Course, IReadOnlyList<(int Par, int Score)> Holes, int Greens, int Fairways)
{
    public int HolesPlayed => Holes.Count;
    public int Strokes => Holes.Sum(h => h.Score);
    public int Par => Holes.Sum(h => h.Par);
    public int ToPar => Strokes - Par;

    /// <summary>The same round cut down to its first holes (an 18-hole round entered in a 9-hole event).</summary>
    public RoundResult First(int holes) => HolesPlayed <= holes ? this : this with { Holes = Holes.Take(holes).ToList() };
}

/// <summary>
/// Reads the game's own live scorecards. A round the player finishes in the game shows up here with its course,
/// and each hole's par and score, which is all a tournament result needs.
/// </summary>
public sealed class RoundWatcher
{
    private readonly IGameRuntime _rt;
    private IntPtr _scorecards, _getScorecards, _user, _userId;
    private bool _unavailable;

    public RoundWatcher(IGameRuntime runtime) => _rt = runtime;

    private bool Resolve()
    {
        if (_unavailable) return false;
        if (_getScorecards != IntPtr.Zero) return true;
        _scorecards = _rt.FindClass("HB.Golf", "", "Scorecard");
        _user = _rt.FindClass("HB.Golf", "", "User");
        if (_scorecards != IntPtr.Zero) _getScorecards = _rt.FindMethod(_scorecards, "GetScorecards", 0);
        if (_user != IntPtr.Zero) _userId = _rt.FindMethod(_user, "Id", 0);
        if (_getScorecards == IntPtr.Zero || _userId == IntPtr.Zero) _unavailable = true;
        return !_unavailable;
    }

    /// <summary>Ids of every scorecard the game holds right now, finished or not.</summary>
    public List<string> CurrentIds()
    {
        var ids = new List<string>();
        foreach (var card in Cards()) if (Id(card) is { Length: > 0 } id) ids.Add(id);
        return ids;
    }

    /// <summary>The player's finished round that is not in <paramref name="known"/>, if there is one.</summary>
    public RoundResult? FindFinished(ICollection<string> known)
    {
        if (!Resolve()) return null;
        var me = _rt.ReadString(_rt.Invoke(_userId, IntPtr.Zero));
        foreach (var card in Cards())
        {
            var cardClass = _rt.GetClass(card);
            var id = Id(card);
            if (string.IsNullOrEmpty(id) || known.Contains(id)) continue;
            if (_rt.GetField<byte>(card, cardClass, "complete") == 0) continue;
            if (_rt.ReadString(_rt.GetField<IntPtr>(card, cardClass, "user")) != me) continue;

            var holes = new List<(int Par, int Score)>();
            int greens = 0, fairways = 0;
            foreach (var hole in _rt.ReadArray(_rt.GetField<IntPtr>(card, cardClass, "holes")))
            {
                if (hole == IntPtr.Zero) continue;
                var holeClass = _rt.GetClass(hole);
                var score = _rt.GetField<int>(hole, holeClass, "score");
                if (_rt.GetField<byte>(hole, holeClass, "complete") == 0 || score <= 0) continue;
                holes.Add((_rt.GetField<int>(hole, holeClass, "par"), score));
                if (_rt.GetField<byte>(hole, holeClass, "isGreenInRegulation") != 0) greens++;
                if (_rt.GetField<byte>(hole, holeClass, "isFairwayInRegulation") != 0) fairways++;
            }
            if (holes.Count == 0) continue;
            var course = _rt.ReadString(_rt.GetField<IntPtr>(card, cardClass, "courseName")) ?? "an unknown course";
            return new RoundResult(id, course, holes, greens, fairways);
        }
        return null;
    }

    private IntPtr[] Cards() => Resolve() ? _rt.ReadArray(_rt.Invoke(_getScorecards, IntPtr.Zero)) : Array.Empty<IntPtr>();

    private string? Id(IntPtr card) => card == IntPtr.Zero ? null : _rt.ReadString(_rt.GetField<IntPtr>(card, _rt.GetClass(card), "_id"));
}
