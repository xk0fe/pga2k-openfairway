using System;
using System.Threading;
using OpenFairway.Core.Il2Cpp;

namespace OpenFairway.Core.Games;

/// <summary>
/// Flips options the game itself ships with (its RuntimeOptions object) so that offline play behaves well:
/// boot straight into offline mode and keep the career save in a local file instead of the cloud.
/// These are the developer's own switches, changed in memory only. Nothing here grants content.
/// </summary>
internal static unsafe class OfflineSwitches
{
    private const int SaveFileLocationLocal = 1; // RuntimeOptions.ProTour.SaveFileLocation.Local

    private static IntPtr _options;
    private static int _corrections;

    /// <summary>Sets the switches if they are not already set. Cheap enough to call several times a second.</summary>
    public static void Enforce(GameRuntime runtime, Config config, GameProfile profile)
    {
        try
        {
            if (_options == IntPtr.Zero) _options = runtime.FindClass("HB.Golf", "", "RuntimeOptions");
            if (_options == IntPtr.Zero) return;
            var online = runtime.GetStaticField<IntPtr>(_options, "online");
            var proTour = runtime.GetStaticField<IntPtr>(_options, "proTour");
            if (online == IntPtr.Zero || proTour == IntPtr.Zero) return;

            var changed = false;
            if (config.ForceOfflineBoot && profile.HasOfflineBootOption && Get<byte>(runtime, online, "forceOfflineBoot") == 0)
            {
                Set<byte>(runtime, online, "forceOfflineBoot", 1);
                changed = true;
            }
            if (config.LocalCareerSave && Get<int>(runtime, proTour, "saveFileLocation") != SaveFileLocationLocal)
            {
                Set(runtime, proTour, "saveFileLocation", SaveFileLocationLocal);
                changed = true;
            }
            if (changed && ++_corrections <= 10)
                Log.Info($"Switches applied ({(profile.HasOfflineBootOption ? "forceOfflineBoot, " : "")}saveFileLocation=Local), time #{_corrections}; options object {proTour:X}");
        }
        catch (Exception e)
        {
            if (++_corrections <= 10) Log.Warn("Enforcing switches failed: " + e.Message);
        }
    }

    private static void Set<T>(GameRuntime runtime, IntPtr instance, string field, T value) where T : unmanaged =>
        runtime.SetField(instance, Native.object_get_class(instance), field, value);

    private static T Get<T>(GameRuntime runtime, IntPtr instance, string field) where T : unmanaged =>
        runtime.GetField<T>(instance, Native.object_get_class(instance), field);
}
