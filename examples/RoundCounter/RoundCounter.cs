using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using OpenFairway.API;

namespace Examples;

/// <summary>Counts the rounds you finish and keeps the count between sessions. Shows a per-frame tick on the
/// game's main thread, reading the game's own objects through the runtime, saving data, and offering an action
/// other mods can call.</summary>
public sealed class RoundCounter : IFairwayMod
{
    private IModContext _ctx = null!;
    private readonly HashSet<string> _seen = new();
    private string _savePath = "";
    private int _rounds;
    private bool _primed;
    private long _nextCheck;
    private IntPtr _getScorecards;

    public string Id => "example.roundcounter";

    public void OnLoad(IModContext ctx)
    {
        _ctx = ctx;
        _savePath = Path.Combine(ctx.DataDirectory, "rounds.json");
        if (File.Exists(_savePath)) _rounds = JsonSerializer.Deserialize<int>(File.ReadAllText(_savePath));

        ctx.MainThread.Update += Tick;    // once per rendered frame, on the game's main thread
        ctx.MainThread.Quitting += Save;  // the last chance to save
        ctx.Actions.Register("roundcounter.show", () =>
            ctx.Ui.ShowNotice("ROUNDS", $"You have finished {_rounds} rounds with this mod on."));
    }

    private void Tick()
    {
        if (_ctx.MainThread.Frame < _nextCheck) return;
        _nextCheck = _ctx.MainThread.Frame + 60; // about once a second is plenty

        // The game's classes and methods are found by name. OpenFairway\dump\*.cs lists everything that exists.
        var rt = _ctx.Runtime;
        if (_getScorecards == IntPtr.Zero)
        {
            var scorecard = rt.FindClass("HB.Golf", "", "Scorecard");
            if (scorecard == IntPtr.Zero) return;
            _getScorecards = rt.FindMethod(scorecard, "GetScorecards", 0);
            if (_getScorecards == IntPtr.Zero) return;
        }

        foreach (var card in rt.ReadArray(rt.Invoke(_getScorecards, IntPtr.Zero)))
        {
            if (card == IntPtr.Zero) continue;
            var klass = rt.GetClass(card);
            var id = rt.ReadString(rt.GetField<IntPtr>(card, klass, "_id"));
            var complete = rt.GetField<byte>(card, klass, "complete") != 0;
            if (id == null || !complete || !_seen.Add(id) || !_primed) continue;
            _rounds++;
            _ctx.Log.Info($"Round finished: {_rounds} so far.");
            Save();
        }
        _primed = true; // rounds already finished when the game started are not new
    }

    private void Save() => File.WriteAllText(_savePath, JsonSerializer.Serialize(_rounds));
}
