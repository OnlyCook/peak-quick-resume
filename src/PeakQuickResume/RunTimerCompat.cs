using System;
using System.Reflection;
using BepInEx.Logging;

namespace PEAKQuickResume
{
    /// <summary>
    /// Compat shim around <c>RunManager</c>'s run clock, which changed shape in PEAK
    /// 2.0.a. Up to 1.65.a the clock was a plain public field:
    /// <code>public float timeSinceRunStarted;</code>
    /// 2.0.a made the field private and exposed a READ-ONLY property in its place:
    /// <code>
    /// private float timeSinceRunStarted;
    /// public float TimeSinceRunStarted { get { ... } }   // no setter
    /// </code>
    ///
    /// Reading is therefore just the property (see <see cref="Read"/>) - but restoring a
    /// saved run clock needs to WRITE it, and there is no public way to do that at all
    /// anymore, so <see cref="TryWrite"/> reflects the private backing field.
    ///
    /// Why this matters beyond "the field moved": because the old field was referenced
    /// directly, the mod's own methods that touched it died with a
    /// <c>FieldAccessException</c> the moment Mono JIT-compiled them - BEFORE any of
    /// their statements ran, so their internal try/catch never saw it. That took out the
    /// whole of SavePlayerOffline (campfire saves silently did nothing) rather than just
    /// the one line. Everything here is reflection-based and null-guarded precisely so a
    /// future rename degrades to "the run clock isn't restored" instead of taking a
    /// whole save or restore path down with it
    /// </summary>
    internal static class RunTimerCompat
    {
        private static readonly FieldInfo FTimeSinceRunStarted =
            typeof(RunManager).GetField("timeSinceRunStarted", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>
        /// Current run clock in seconds, or 0 if there's no RunManager. Uses the public
        /// 2.0.a property, which self-corrects for having already ticked this frame
        /// </summary>
        public static float Read(RunManager runManager) => runManager != null ? runManager.TimeSinceRunStarted : 0f;

        /// <summary>
        /// Writes the run clock back and re-syncs it to the room, mirroring what the
        /// pre-2.0.a code did (assign the field, then let <c>SyncTimeMaster</c> push it -
        /// that method no-ops on non-hosts on its own). Returns false if the private
        /// field is gone, in which case the caller should treat the run clock as simply
        /// not restorable rather than fail the surrounding restore
        /// </summary>
        public static bool TryWrite(RunManager runManager, float seconds, ManualLogSource log)
        {
            if (runManager == null) return false;
            if (FTimeSinceRunStarted == null)
            {
                log?.LogWarning("RunTimerCompat: RunManager.timeSinceRunStarted is gone - the run clock can't be restored on this game version.");
                return false;
            }

            FTimeSinceRunStarted.SetValue(runManager, seconds);
            typeof(RunManager).GetMethod("SyncTimeMaster", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(runManager, null);
            return true;
        }
    }
}
