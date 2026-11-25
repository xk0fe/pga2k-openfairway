using System;
using OpenFairway.API;

namespace OpenFairway.Mods;

/// <summary>
/// Skips the startup videos by fast-forwarding each one to its end, so the game's own boot sequence moves on
/// exactly as it would when a video finishes by itself. In a game whose video player cannot seek (PGA TOUR 2K23)
/// the boot screen takes its own step for the end of its videos instead.
/// </summary>
public sealed unsafe class SkipIntros : IFairwayMod
{

    private IModContext _ctx = null!;
    private IntPtr _bootScreenClass, _setTime, _getLength, _getIsPlaying, _getTime, _transition;
    private int _skipped;
    private bool _done;
    private IntPtr _titleClass, _legalClass;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public string Id => "openfairway.skipintros";

    /// <summary>The videos are passed only once one is playing, so the game has finished setting the screen up.</summary>
    private const double StartedSeconds = 0.2;

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
        // PGA TOUR 2K23 is built without the video player's seeking. There the boot screen is moved on with its own
        // step for when the videos are over.
        _getTime = rt.FindMethod(player, "get_time", 0);
        _transition = rt.FindMethod(_bootScreenClass, "Transition", 0);
        if (_getIsPlaying == IntPtr.Zero || ((_setTime == IntPtr.Zero || _getLength == IntPtr.Zero) && (_getTime == IntPtr.Zero || _transition == IntPtr.Zero)))
        {
            ctx.Log.Warn("The video player cannot be skipped in this game; only the logo screens are.");
            _getIsPlaying = IntPtr.Zero;
        }
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

        if (_getIsPlaying == IntPtr.Zero) return;
        var player = rt.GetField<IntPtr>(screens[0], _bootScreenClass, "videoPlayer");
        if (!rt.IsAlive(player)) return;
        var playing = *(byte*)(rt.Invoke(_getIsPlaying, player) + 16) != 0;
        if (!playing) return;
        if (_setTime == IntPtr.Zero || _getLength == IntPtr.Zero)
        {
            // Without seeking, once a video is under way, take the step the boot screen takes after its last one.
            if (*(double*)(rt.Invoke(_getTime, player) + 16) < StartedSeconds) return;
            rt.Invoke(_transition, screens[0]);
            _skipped++;
            return;
        }
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
