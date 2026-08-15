using System;

namespace PEAKQuickResume
{
    /// <summary>
    /// Length-tolerant copy for CharacterAfflictions.currentStatuses, indexed by the
    /// STATUSTYPE enum which grows whenever the game adds a status (e.g. PEAK 2.0.a
    /// appended three). An exact-length guard would silently restore no afflictions at
    /// all for any save older than the enum's current length; copying the overlapping
    /// prefix instead handles both older saves (missing statuses keep their fresh
    /// default) and newer-than-game saves (extra trailing values dropped). Assumes the
    /// enum only ever grows at the end, not reorders.
    /// </summary>
    internal static class AfflictionArrayCompat
    {
        /// <summary>Copies the overlapping prefix of saved into live. Returns entries copied (0 if either is missing).</summary>
        public static int CopyOverlap(float[] saved, float[] live)
        {
            if (saved == null || live == null) return 0;

            int count = Math.Min(saved.Length, live.Length);
            if (count <= 0) return 0;

            Array.Copy(saved, live, count);
            return count;
        }
    }
}
