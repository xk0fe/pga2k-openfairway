using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OpenFairway.API;

namespace OpenFairway.Core.Il2Cpp;

/// <summary>
/// Runs platform and mod code on the game's own main thread, once per frame.
///
/// This is done without patching any code: a delegate object is created in the game's runtime and subscribed to
/// the engine event that fires before UI rendering every frame (Canvas.willRenderCanvases), and another to
/// Application.quitting. Because all recurring work then happens on the game's thread, nothing of ours touches
/// the runtime while the game shuts down.
/// </summary>
internal sealed unsafe class MainThread : IMainThread
{
    // System.Delegate field offsets (verified against the runtime's own type information at startup).
    private const int MethodPtr = 0x10, InvokeImpl = 0x18, Method = 0x28, MethodCode = 0x40;

    private static readonly ConcurrentQueue<Action> Posted = new();
    private static MainThread? _instance;
    private static bool _quitting;
    private static int _errors;

    public event Action? Update;
    public event Action? Quitting;
    public long Frame { get; private set; }
    public double Time => Game.AnimationClock.Now;

    public void Post(Action action) => Posted.Enqueue(action);

    /// <summary>Must be called from a thread that is attached to the runtime.</summary>
    public static MainThread? Install(GameRuntime runtime)
    {
        var delegateClass = runtime.FindClass("mscorlib", "System", "Delegate");
        if (!OffsetIs(delegateClass, "method_ptr", MethodPtr) || !OffsetIs(delegateClass, "invoke_impl", InvokeImpl) ||
            !OffsetIs(delegateClass, "method", Method) || !OffsetIs(delegateClass, "method_code", MethodCode))
        {
            Log.Error("Delegate layout differs from what this version expects; main-thread scheduling is disabled.");
            return null;
        }

        // Any real method record will do for the delegate's bookkeeping; this one has an empty body in the game.
        var placeholder = runtime.FindMethod(runtime.FindClass("HB.Golf", "", "ConnectionManager"), "GoOnline", 0);
        if (placeholder == IntPtr.Zero) placeholder = runtime.FindMethod(runtime.FindClass("mscorlib", "System", "Object"), "Finalize", 0);
        var combine = runtime.FindMethod(delegateClass, "Combine", 2);
        var canvas = runtime.FindClass("UnityEngine.UIModule", "UnityEngine", "Canvas");
        var application = runtime.FindClass("UnityEngine.CoreModule", "UnityEngine", "Application");
        if (placeholder == IntPtr.Zero || combine == IntPtr.Zero || canvas == IntPtr.Zero || application == IntPtr.Zero)
        {
            Log.Error("Engine types for main-thread scheduling were not found.");
            return null;
        }

        _instance = new MainThread();
        Subscribe(runtime, canvas, "willRenderCanvases", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnFrame, placeholder, combine);
        Subscribe(runtime, application, "quitting", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnQuitting, placeholder, combine);
        return _instance;
    }

    private static void Subscribe(GameRuntime runtime, IntPtr klass, string eventField, IntPtr callback, IntPtr placeholder, IntPtr combine)
    {
        var field = runtime.FindField(klass, eventField);
        var handlerClass = Native.class_from_type(Native.field_get_type(field));
        var handler = Native.object_new(handlerClass);
        *(IntPtr*)(handler + MethodPtr) = callback;
        *(IntPtr*)(handler + InvokeImpl) = callback;
        *(IntPtr*)(handler + Method) = placeholder;
        *(IntPtr*)(handler + MethodCode) = handler;

        IntPtr existing = IntPtr.Zero;
        Native.field_static_get_value(field, &existing);
        var combined = existing == IntPtr.Zero ? handler : runtime.Invoke(combine, IntPtr.Zero, existing, handler);
        // For reference-type fields the runtime takes the object pointer itself as the value.
        Native.field_static_set_value(field, (void*)combined);
    }

    private static bool OffsetIs(IntPtr klass, string field, int expected)
    {
        var f = Native.class_get_field_from_name_managed(klass, field);
        return f != IntPtr.Zero && (int)Native.field_get_offset(f) == expected;
    }

    [UnmanagedCallersOnly]
    private static void OnFrame(IntPtr self, IntPtr method)
    {
        if (_quitting || _instance == null) return;
        try
        {
            _instance.Frame++;
            while (Posted.TryDequeue(out var action)) action();
            _instance.Update?.Invoke();
        }
        catch (Exception e)
        {
            if (++_errors <= 20) Log.Error("Main-thread callback failed: " + e);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnQuitting(IntPtr self, IntPtr method)
    {
        try { _instance?.Quitting?.Invoke(); }
        catch (Exception e) { Log.Error("Quit handler failed: " + e); }
        _quitting = true;
        Log.Info("Game is quitting; OpenFairway stopped.");
    }
}
