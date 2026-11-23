using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenFairway.API;

namespace OpenFairway.Mods;

/// <summary>
/// Gives back the progression the game's servers would award after a round, so playing offline still levels your
/// golfer and your clubs (each club level unlocks one of its upgrades). How fast is a setting, because offline you
/// are not racing anyone. Only experience earned by playing is touched: no VC, no store items, no level tokens,
/// Evo-Tools, fittings or pass rewards.
/// </summary>
public sealed class OfflineProgression : IFairwayMod
{
    // At the 1x rate, per hole played. Online the amount comes from 2K's servers and is not known to us; these
    // keep a club's first level about an 18-hole round away at 1x.
    private const int EquipmentXpPerHole = 45, GolferXpPerHole = 25;

    private IModContext _ctx = null!;
    private IGameRuntime _rt = null!;
    private Settings _settings = new();
    private readonly HashSet<string> _known = new();
    private string _knownPath = "";
    private bool _primed;
    private long _nextCheck;
    private string? _pendingNotice;

    public string Id => "openfairway.offlineprogression";

    private sealed class Settings
    {
        /// <summary>Multiplier on the experience a round gives: 1 is a normal pace, 3 the default.</summary>
        public double Rate { get; set; } = 3;

        /// <summary>Whether rounds also give your golfer (MyPLAYER) experience, not only your clubs and ball.</summary>
        public bool GolferXp { get; set; } = true;
    }

    public void OnLoad(IModContext ctx)
    {
        _ctx = ctx;
        _rt = ctx.Runtime;
        _settings = LoadSettings();
        _knownPath = Path.Combine(ctx.DataDirectory, "rounds.txt");
        if (File.Exists(_knownPath)) foreach (var id in File.ReadAllLines(_knownPath)) if (id.Length > 0) _known.Add(id);
        ctx.MainThread.Update += Tick;
        ctx.Log.Info($"Offline progression at {_settings.Rate:0.##}x.");
    }

    private Settings LoadSettings()
    {
        var path = Path.Combine(_ctx.DataDirectory, "settings.json");
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
                if (loaded != null)
                {
                    loaded.Rate = Math.Clamp(loaded.Rate, 0.25, 10);
                    return loaded;
                }
            }
            Directory.CreateDirectory(_ctx.DataDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(new Settings(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            _ctx.Log.Warn("Progression settings not read, using the defaults: " + e.Message);
        }
        return new Settings();
    }

    private void Tick()
    {
        // Twice a second is plenty: a round takes minutes.
        if (_ctx.MainThread.Frame < _nextCheck) return;
        _nextCheck = _ctx.MainThread.Frame + 30;

        if (_pendingNotice != null && _ctx.Ui.MainMenuReady)
        {
            _ctx.Ui.ShowNotice("ROUND COMPLETE", _pendingNotice);
            _pendingNotice = null;
        }

        var cards = Scorecards.Read(_rt);
        if (!_primed)
        {
            // Rounds already on the books when the game started were rewarded then (or before this mod).
            _primed = true;
            foreach (var card in cards.Where(c => c.Complete)) _known.Add(card.Id);
            return;
        }
        foreach (var card in cards)
        {
            if (!card.Complete || !card.Mine || card.HolesPlayed == 0 || !_known.Add(card.Id)) continue;
            Reward(card);
            try { File.AppendAllLines(_knownPath, new[] { card.Id }); } catch (IOException) { }
        }
    }

    private void Reward(Scorecards.Card card)
    {
        var equipment = (int)Math.Round(card.HolesPlayed * EquipmentXpPerHole * _settings.Rate);
        var golfer = _settings.GolferXp ? (int)Math.Round(card.HolesPlayed * GolferXpPerHole * _settings.Rate) : 0;
        var bag = _ctx.Actions.Invoke("player.addequipmentxp", equipment.ToString());
        var player = golfer > 0 && _ctx.Actions.Invoke("player.addxp", golfer.ToString());
        if (!bag && !player)
        {
            _ctx.Log.Warn("Round finished, but Offline Career is off, so there is no local golfer to reward.");
            return;
        }
        _ctx.Log.Info($"Round of {card.HolesPlayed} holes: equipment +{equipment} XP, golfer +{golfer} XP.");
        var parts = new List<string>();
        if (bag) parts.Add($"your clubs and ball +{equipment:N0} XP");
        if (player) parts.Add($"your golfer +{golfer:N0} XP");
        _pendingNotice = $"{card.HolesPlayed} holes played offline: {string.Join(", ", parts)}. Club levels unlock upgrades in MyPLAYER > GOLF BAG.";
    }
}

/// <summary>The game's live scorecards: which rounds exist, whose they are, and how many holes were finished.</summary>
internal static class Scorecards
{
    public sealed record Card(string Id, bool Complete, bool Mine, int HolesPlayed);

    private static IntPtr _class, _get, _user, _userId;

    public static List<Card> Read(IGameRuntime rt)
    {
        var result = new List<Card>();
        if (_get == IntPtr.Zero)
        {
            _class = rt.FindClass("HB.Golf", "", "Scorecard");
            _user = rt.FindClass("HB.Golf", "", "User");
            if (_class != IntPtr.Zero) _get = rt.FindMethod(_class, "GetScorecards", 0);
            if (_user != IntPtr.Zero) _userId = rt.FindMethod(_user, "Id", 0);
            if (_get == IntPtr.Zero || _userId == IntPtr.Zero) return result;
        }
        var me = rt.ReadString(rt.Invoke(_userId, IntPtr.Zero));
        foreach (var card in rt.ReadArray(rt.Invoke(_get, IntPtr.Zero)))
        {
            if (card == IntPtr.Zero) continue;
            var klass = rt.GetClass(card);
            var id = rt.ReadString(rt.GetField<IntPtr>(card, klass, "_id"));
            if (string.IsNullOrEmpty(id)) continue;
            var holes = 0;
            foreach (var hole in rt.ReadArray(rt.GetField<IntPtr>(card, klass, "holes")))
            {
                if (hole == IntPtr.Zero) continue;
                var holeClass = rt.GetClass(hole);
                if (rt.GetField<byte>(hole, holeClass, "complete") != 0 && rt.GetField<int>(hole, holeClass, "score") > 0) holes++;
            }
            result.Add(new Card(id, rt.GetField<byte>(card, klass, "complete") != 0, rt.ReadString(rt.GetField<IntPtr>(card, klass, "user")) == me, holes));
        }
        return result;
    }
}
