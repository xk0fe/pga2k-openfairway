using System;
using System.Diagnostics;
using System.IO;
using OpenFairway.Core.Il2Cpp;

namespace OpenFairway.Core.Game;

/// <summary>
/// The clock every on-screen animation runs on. It is real time, except while the game is being recorded frame by
/// frame: then it advances exactly one frame's worth per rendered frame, so animations play at their true speed in
/// the recording however long each frame takes to capture.
/// </summary>
internal static class AnimationClock
{
    private static readonly Stopwatch Wall = Stopwatch.StartNew();
    private static double _offset;
    private static Func<double>? _source;

    public static double Now => _source?.Invoke() ?? Wall.Elapsed.TotalSeconds + _offset;

    public static void Follow(Func<double>? source)
    {
        var last = Now;
        _source = source;
        // Hand back to the wall clock without ever going backwards, or running animations would jump.
        if (source == null) _offset = Math.Max(_offset, last - Wall.Elapsed.TotalSeconds);
    }
}

/// <summary>
/// Developer aid for making videos of the game: captures every rendered frame to a numbered PNG while the engine's
/// capture frame rate holds game time to a fixed step per frame, so the frames play back smoothly at that rate even
/// though capturing slows the game down. Only reachable from the developer console.
/// </summary>
internal sealed unsafe class Recorder
{
    private readonly GameRuntime _rt;
    private IntPtr _capture, _setCaptureDeltaTime, _getFrameCount;
    private string _dir = "";
    private int _fps, _superSize, _startFrame, _lastFrame, _count;
    private double _startTime;

    public bool Active { get; private set; }

    public Recorder(GameRuntime runtime, MainThread mainThread)
    {
        _rt = runtime;
        mainThread.Update += Tick;
        mainThread.Quitting += () => { if (Active) Stop(); };
    }

    private bool Resolve()
    {
        if (_capture != IntPtr.Zero) return true;
        var time = _rt.FindClass("UnityEngine.CoreModule", "UnityEngine", "Time");
        var screenCapture = _rt.FindClass("UnityEngine.ScreenCaptureModule", "UnityEngine", "ScreenCapture");
        if (time == IntPtr.Zero || screenCapture == IntPtr.Zero) return false;
        // The game's build keeps only these overloads: a fixed delta time, and the capture call with a stereo mode.
        _setCaptureDeltaTime = _rt.FindMethod(time, "set_captureDeltaTime", 1);
        _getFrameCount = _rt.FindMethod(time, "get_frameCount", 0);
        _capture = _rt.FindMethod(screenCapture, "CaptureScreenshot(System.String,System.Int32,UnityEngine.ScreenCapture.StereoScreenCaptureMode)");
        return _capture != IntPtr.Zero && _setCaptureDeltaTime != IntPtr.Zero && _getFrameCount != IntPtr.Zero;
    }

    private int FrameCount => *(int*)(_rt.Invoke(_getFrameCount, IntPtr.Zero) + 16);

    private void SetCaptureFramerate(int fps)
    {
        var delta = fps > 0 ? 1f / fps : 0f;
        _rt.Invoke(_setCaptureDeltaTime, IntPtr.Zero, (IntPtr)(&delta));
    }

    public string Start(string dir, int fps, int superSize)
    {
        if (Active) return "already recording to " + _dir;
        if (!Resolve()) return "ERROR engine capture functions not found";
        Directory.CreateDirectory(dir);
        _dir = dir;
        _fps = Math.Clamp(fps, 10, 120);
        _superSize = Math.Clamp(superSize, 1, 4);
        _count = 0;
        _startFrame = _lastFrame = FrameCount;
        _startTime = AnimationClock.Now;
        SetCaptureFramerate(_fps);
        AnimationClock.Follow(() => _startTime + (FrameCount - _startFrame) / (double)_fps);
        Active = true;
        Log.Info($"Recording frames to {dir} at {_fps} fps (super size {_superSize}).");
        return "recording";
    }

    public string Stop()
    {
        if (!Active) return "not recording";
        Active = false;
        SetCaptureFramerate(0);
        AnimationClock.Follow(null);
        Log.Info($"Recording stopped: {_count} frames in {_dir}.");
        return $"stopped, {_count} frames";
    }

    public string Status() => Active ? $"recording, {_count} frames so far" : "not recording";

    private void Tick()
    {
        if (!Active) return;
        var frame = FrameCount;
        if (frame == _lastFrame) return; // the hook can fire more than once in a frame
        _lastFrame = frame;
        int size = _superSize, leftEye = 1;
        _rt.Invoke(_capture, IntPtr.Zero, _rt.NewString(Path.Combine(_dir, $"f{_count++:D6}.png")), (IntPtr)(&size), (IntPtr)(&leftEye));
    }
}
