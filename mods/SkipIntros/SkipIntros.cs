using System;
using OpenFairway.API;

namespace OpenFairway.Mods;

/// <summary>
/// Skips the startup videos by fast-forwarding each one to its end, so the game's own boot sequence moves on
/// exactly as it would when a video finishes by itself.
/// </summary>
public sealed unsafe class SkipIntros : IFairwayMod
{

    private IModContext _ctx = null!;
    private IntPtr _bootScreenClass, _setTime, _getLength, _getIsPlaying;
    private int _skipped;
    private bool _done;
    private IntPtr _titleClass, _legalClass;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public string Id => "openfairway.skipintros";

    public void OnLoad(IModContext ctx)
    {
        _ctx = ctx;
        var rt = ctx.Runtime;
        _bootScreenClass = rt.FindClass("HB.Golf", "", "BootScreen");
        _titleClass = rt.FindClass("HB.Golf", "", "BootScreenTitle");
        _legalClass = rt.FindClass("HB.Golf", "", "BootScreenLegalText");
        var player = rt.FindClass("UnityEngine.VideoModule", "UnityEngine.Video", "VideoPlayer");
        if (_bootScreenClass == IntPtr.Zero || player == IntPtr.Zero)
        {
            ctx.Log.Warn("Boot screen types not found; nothing to skip (game update?).");
            return;
        }
        _setTime = rt.FindMethod(player, "set_time", 1);
        _getLength = rt.FindMethod(player, "get_length", 0);
        _getIsPlaying = rt.FindMethod(player, "get_isPlaying", 0);
        ctx.MainThread.Update += OnUpdate;
    }

    private void OnUpdate()
    {
        // Frame counts are useless here: the boot sequence runs at an uncapped frame rate.
        if (_done || _clock.ElapsedMilliseconds < 100) return;
        _clock.Restart();

        var rt = _ctx.Runtime;
        var screens = rt.FindObjectsOfType(_bootScreenClass);
        if (screens.Length == 0)
        {
            // After the videos the game shows timed logo and notice screens; run their timers out.
            if (_legalClass != IntPtr.Zero)
            {
                foreach (var legal in rt.FindObjectsOfType(_legalClass))
                {
                    var countdown = rt.GetField<IntPtr>(legal, _legalClass, "countdown");
                    if (countdown == IntPtr.Zero) continue;
                    var countdownClass = rt.GetClass(countdown);
                    // The timer counts down from waitTime to zero.
                    if (rt.GetField<float>(countdown, countdownClass, "currentTime") > 0.05f)
                    {
                        rt.SetField(countdown, countdownClass, "currentTime", 0.05f);
                        _skipped++;
                    }
                }
            }
            // Everything is over once the title screen exists.
            if (_titleClass != IntPtr.Zero && rt.FindObjectsOfType(_titleClass).Length > 0) Finish();
            return;
        }

        var player = rt.GetField<IntPtr>(screens[0], _bootScreenClass, "videoPlayer");
        if (!rt.IsAlive(player)) return;
        var playing = *(byte*)(rt.Invoke(_getIsPlaying, player) + 16) != 0;
        if (!playing) return;
        var length = *(double*)(rt.Invoke(_getLength, player) + 16);
        if (length <= 0.5) return;

        var end = length - 0.05;
        rt.Invoke(_setTime, player, (IntPtr)(&end));
        _skipped++;
    }

    private void Finish()
    {
        _done = true;
        _ctx.MainThread.Update -= OnUpdate;
        _ctx.Log.Info($"Done ({_skipped} skip requests).");
    }
}
