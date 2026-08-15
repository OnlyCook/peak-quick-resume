using System;
using System.Reflection;
using BepInEx.Logging;

namespace PEAKQuickResume
{
    /// <summary>
    /// Compat shim around <c>RunManager</c>'s run clock: PEAK 2.0.a made the field private
    /// and read-only via a property, so writing it needs reflection on the backing field.
    /// Reflection-based and null-guarded throughout so a future rename degrades to "the run
    /// clock isn't restored" instead of a <c>FieldAccessException</c> at JIT time taking down
    /// a whole save/restore path (as a direct field reference previously did).
    /// </summary>
    internal static class RunTimerCompat
    {
        private static readonly FieldInfo FTimeSinceRunStarted =
            typeof(RunManager).GetField("timeSinceRunStarted", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>Current run clock in seconds, or 0 if there's no RunManager.</summary>
        public static float Read(RunManager runManager) => runManager != null ? runManager.TimeSinceRunStarted : 0f;

        /// <summary>Writes the run clock and re-syncs it via SyncTimeMaster (no-ops on non-hosts).</summary>
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
