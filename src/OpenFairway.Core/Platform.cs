using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using OpenFairway.Core.Game;
using OpenFairway.Core.Il2Cpp;

namespace OpenFairway.Core;

internal static unsafe class Platform
{
    public static string GameDir { get; private set; } = "";
    public static string HomeDir { get; private set; } = "";
    private static Config _config = new();

    /// <summary>
    /// Runs synchronously before the game's runtime starts, so the process is offline before the game can
    /// make its first request. Returns false if isolation failed, in which case the platform stays inactive.
    /// </summary>
    public static bool Prepare()
    {
        var exe = Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH");
        if (string.IsNullOrEmpty(exe)) exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        GameDir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        HomeDir = Path.Combine(GameDir, "OpenFairway");
        Log.Open(Path.Combine(HomeDir, "logs", "openfairway.log"));
        Log.Info($"OpenFairway {typeof(Platform).Assembly.GetName().Version} starting in {GameDir}");
        _config = Config.Load(Path.Combine(HomeDir, "config.json"));
        Log.Detailed = string.Equals(_config.LogLevel, "detailed", StringComparison.OrdinalIgnoreCase);

        if (!OfflineGuard.Enforce(_config.OfficialHosts))
        {
            Log.Error("The game could not be taken offline, so OpenFairway is inactive for this session.");
            return false;
        }
        Log.Info("Game process is offline (name resolution blocked in-process).");
        return true;
    }

    public static void Run()
    {
        try
        {
            if (!Native.Init())
            {
                Log.Error("GameAssembly.dll is not loaded or does not export the IL2CPP API; cannot continue.");
                return;
            }

            // get_corlib only reads a pointer, so it is safe to poll while the runtime is still starting.
            while (Native.get_corlib() == IntPtr.Zero) Thread.Sleep(50);
            WaitForGameWindow();
            Thread.Sleep(TimeSpan.FromSeconds(_config.StartupDelaySeconds));

            var runtime = new GameRuntime();
            using (GameRuntime.Attach())
            {
                Log.Info($"Attached to IL2CPP runtime: {runtime.AssemblyCount} assemblies loaded.");

                var dumpDir = Path.Combine(HomeDir, "dump");
                if (_config.DumpTypes == "always" || (_config.DumpTypes == "auto" && !TypeDumper.IsCurrent(dumpDir, GameDir)))
                {
                    var sw = Stopwatch.StartNew();
                    TypeDumper.Dump(dumpDir, GameDir);
                    Log.Info($"Type dump finished in {sw.Elapsed.TotalSeconds:F1}s -> {dumpDir}");
                }

                // The game's UI system registers on the same per-frame event during boot. Subscribing from this thread
                // at that moment is a race, so wait until the game's own handler is in place and things have settled.
                var canvas = runtime.FindClass("UnityEngine.UIModule", "UnityEngine", "Canvas");
                var waited = Stopwatch.StartNew();
                while (canvas != IntPtr.Zero && runtime.GetStaticField<IntPtr>(canvas, "willRenderCanvases") == IntPtr.Zero && waited.Elapsed.TotalSeconds < 120)
                    Thread.Sleep(100);
                Thread.Sleep(1500);

                var mainThread = MainThread.Install(runtime);
                if (mainThread == null) return;

                // From here on everything runs on the game's main thread; this thread ends after startup.
                var config = _config;
                mainThread.Update += () =>
                {
                    if (mainThread.Frame == 1) Log.Info("Main-thread scheduling is live.");
                    if (mainThread.Frame % 10 == 0) OfflineSwitches.Enforce(runtime, config);
                };
                var ui = new GameUi(runtime, mainThread);
                var mods = new ModLoader(Path.Combine(HomeDir, "mods"));
                mainThread.Post(() => mods.LoadAll(runtime, mainThread, ui));
                if (config.DevConsole)
                {
                    new DevConsole(runtime, mainThread).Start();
                    Log.Info("Developer console is on (local pipe only).");
                }
                Log.Info("Startup complete.");
            }
        }
        catch (Exception e)
        {
            Log.Error("Platform failed: " + e);
        }
    }

    private static void WaitForGameWindow()
    {
        using var self = Process.GetCurrentProcess();
        while (true)
        {
            self.Refresh();
            if (self.MainWindowHandle != IntPtr.Zero) return;
            Thread.Sleep(500);
        }
    }
}
