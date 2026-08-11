using BepInEx.Logging;
using Zorro.Core;

namespace PEAKQuickResume
{
    /// <summary>
    /// Works out the name of the area a save was taken in, i.e. what goes into
    /// <c>OwnSaveData.campfireName</c> and ends up as the biome column in the save picker.
    ///
    /// WHY THIS EXISTS: the old approach was to use the <c>Segment</c> enum name and then
    /// special-case the two known cave variants by hand:
    /// <code>
    /// if (biome == Roots &amp;&amp; segment == Tropics) name = "Roots";
    /// else if (biome == Mesa &amp;&amp; segment == Alpine) name = "Mesa";
    /// </code>
    /// That is a hardcoded list, so PEAK 2.0.a's two new areas - GLOOM and THE CITADEL -
    /// fell through to the base segment names and were saved as "Caldera" and "TheKiln".
    ///
    /// The game itself has an exact answer, so ask it instead of maintaining a list.
    /// <c>MountainProgressHandler.InitProgressPoints</c> rebuilds its progress-point array
    /// from the biomes actually present in this run:
    /// <code>
    /// foreach (biome in MapHandler.biomes)
    ///     list.AddRange(progressPoints.Where(p => p.biome == biome));
    /// list.Add(progressPoints.Last());   // PEAK
    /// </code>
    /// and from then on indexes it BY SEGMENT (<c>SetSegmentComplete(int segment)</c> and
    /// <c>JumpToSegment(int segment)</c> both do <c>progressPoints[segment]</c>). So
    /// <c>progressPoints[(int)segment].title</c> IS that segment's area name, variants
    /// included, with no mapping table on our side.
    ///
    /// Note one biome can supply SEVERAL consecutive areas, which is exactly how the new
    /// pair works: the Swamp biome contributes both GLOOM and THE CITADEL, the same way
    /// Volcano contributes both CALDERA and THE KILN. That is also why a plain
    /// "resolve the current biome and use its name" fix would NOT have been enough - both
    /// new areas share one <c>BiomeType</c>, so the biome alone can't tell them apart.
    ///
    /// BONUS: <c>title</c> is already the localization key (<c>ProgressPoint.localizedTitle
    /// =&gt; LocalizedText.GetText(title)</c>), so names produced here localize without
    /// needing an entry in <see cref="SaveArchive"/>'s legacy mapping table
    /// </summary>
    internal static class AreaNameCompat
    {
        /// <summary>
        /// The area name for <paramref name="segment"/>, preferring the game's own
        /// progress-point title. Falls back to <paramref name="legacyName"/> (the caller's
        /// segment/biome-derived name) whenever the progress points aren't usable - not
        /// yet initialized, a segment past the end of the array, or a blank title - so a
        /// save is never left with an empty area
        /// </summary>
        public static string ResolveAreaName(Segment segment, string legacyName, ManualLogSource log)
        {
            try
            {
                var handler = Singleton<MountainProgressHandler>.Instance;
                var points = handler != null ? handler.progressPoints : null;
                if (points == null) return legacyName;

                int index = (int)segment;
                if (index < 0 || index >= points.Length) return legacyName;

                string title = points[index]?.title;
                if (string.IsNullOrEmpty(title)) return legacyName;

                if (title != legacyName)
                    log.Trace($"AreaNameCompat: segment {segment} resolved to area '{title}' (legacy naming would have said '{legacyName}').");

                return title;
            }
            catch (System.Exception e)
            {
                // Never worth failing a save over a display name
                log?.LogWarning($"AreaNameCompat: could not resolve the area name for {segment} ({e.Message}); using '{legacyName}'.");
                return legacyName;
            }
        }
    }
}
