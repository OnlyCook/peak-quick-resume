using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Widens the world-state search radii for Nadir only.
    ///
    /// Everywhere else the anchor is a campfire, players naturally end up clustered around
    /// it, and 30m is both generous and cheap. Nadir has no campfire: the anchor is the
    /// scoutmaster's soul pillar, and the biome is wide open, so a party's dropped gear can
    /// be spread much further out by the time somebody communes.
    ///
    /// Widening this is affordable specifically because Nadir ships with no natural loot -
    /// no luggage, no item spawners, no physics props. Anything found in range is something
    /// a player carried down and dropped, and a party can only carry so much, so the item
    /// count stays small no matter how large the radius is. (Luggage/spawner lookups are
    /// widened too even though there is nothing for them to find today, so a future update
    /// that does add them to Nadir is covered without another pass through here.)
    /// </summary>
    internal static class NadirSearchRadius
    {
        /// <summary>Radius used for every anchored world-state lookup while the checkpoint is Nadir's.</summary>
        public const float Radius = 80f;

        /// <summary>
        /// Capture-side: widens <paramref name="defaultRadius"/> when the run is currently in
        /// Nadir. Reads the live MapHandler, which is the same source OwnSaveCapture writes
        /// into the save's own <c>segment</c> field moments later.
        /// </summary>
        public static float ForCurrentSegment(float defaultRadius)
        {
            return Widen(defaultRadius, CurrentlyInNadir());
        }

        /// <summary>True while the run is in the Void biome. Fails closed on any error.</summary>
        public static bool CurrentlyInNadir()
        {
            try
            {
                MapHandler handler = MapHandler.Instance;
                return handler != null && handler.GetCurrentSegment() == Segment.Void;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Restore-side: widens <paramref name="defaultRadius"/> from the checkpoint's own
        /// saved segment rather than live game state, so the restore always searches the exact
        /// radius the capture used even if the segment jump didn't take.
        /// </summary>
        public static float ForSavedSegment(float defaultRadius, OwnSaveData data)
        {
            return Widen(defaultRadius, data != null && data.segment == Segment.Void);
        }

        // Max, not a plain override: AncientStatueRestore already searches wider than this,
        // and nothing here should ever make a search narrower than it was.
        private static float Widen(float defaultRadius, bool isNadir)
        {
            return isNadir ? Mathf.Max(defaultRadius, Radius) : defaultRadius;
        }
    }
}
