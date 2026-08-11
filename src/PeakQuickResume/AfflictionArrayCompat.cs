using System;

namespace PEAKQuickResume
{
    /// <summary>
    /// Length-tolerant copy for <c>CharacterAfflictions.currentStatuses</c>, which is
    /// indexed by the <c>STATUSTYPE</c> enum and therefore changes length whenever the
    /// game adds a status. PEAK 2.0.a appended three:
    /// <code>
    /// ... Thorns, Spores, Web                          // 12 entries, to 1.65.a
    /// ... Thorns, Spores, Web, Arrow, Petrify, FlyTrap // 15 entries, 2.0.a
    /// </code>
    ///
    /// Both copy sites used to require <c>saved.Length == live.Length</c> before copying
    /// anything, which is safe but absolute: after 2.0.a every save written before it
    /// mismatched, so those runs came back with NO afflictions restored at all - silently,
    /// since a skipped copy looks exactly like a healthy player.
    ///
    /// Copying the overlapping prefix instead is correct as long as the enum only ever
    /// grows at the end, which is how PEAK has changed it so far (existing members keep
    /// their ordinals; a save's Injury is still index 0). Statuses the save predates are
    /// simply left at whatever the fresh character already has, which is the sane default
    /// - a 1.65.a save has nothing to say about Petrify. The reverse case (a save NEWER
    /// than the running game, i.e. after a downgrade) is handled by the same clamp, and
    /// the extra trailing values are dropped rather than overflowing.
    ///
    /// If the enum is ever REORDERED rather than appended to, this silently misaligns -
    /// but so would any positional format, and the previous exact-length guard would have
    /// happily copied a reordered array of the same length too, so this is not a
    /// regression in that scenario
    /// </summary>
    internal static class AfflictionArrayCompat
    {
        /// <summary>
        /// Copies as much of <paramref name="saved"/> into <paramref name="live"/> as the
        /// two have in common. Returns the number of entries copied (0 if either side is
        /// missing), so callers can log a partial restore if they care
        /// </summary>
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
