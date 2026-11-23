using System;
using System.Threading;
using OpenFairway.Core;

// Unity Doorstop looks for exactly `static void Doorstop.Entrypoint.Start()`.
namespace Doorstop
{
    public static class Entrypoint
    {
        public static void Start()
        {
            // Called before the game's runtime is initialised. Take the process offline now, then hand off.
            try
            {
                if (!Platform.Prepare()) return;
                new Thread(Platform.Run) { IsBackground = true, Name = "OpenFairway" }.Start();
            }
            catch (Exception e)
            {
                // Never take the game down because the platform failed to start; leave a note for diagnosis.
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openfairway-startup-error.txt"), e.ToString()); }
                catch (Exception) { }
            }
        }
    }
}
