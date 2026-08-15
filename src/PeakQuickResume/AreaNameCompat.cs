using BepInEx.Logging;
using Zorro.Core;

namespace PEAKQuickResume
{
    /// <summary>
    /// Resolves the area name for OwnSaveData.campfireName by asking
    /// MountainProgressHandler.progressPoints[(int)segment].title instead of maintaining
    /// a hardcoded Segment/biome name mapping, which fell out of date once PEAK 2.0.a
    /// added two new areas (GLOOM, THE CITADEL) sharing a biome with an existing one.
    /// title is already the localization key, so no extra mapping table is needed either.
    /// </summary>
    internal static class AreaNameCompat
    {
        /// <summary>Resolves the area name for segment, falling back to legacyName if the progress points aren't usable.</summary>
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
                log?.LogWarning($"AreaNameCompat: could not resolve the area name for {segment} ({e.Message}); using '{legacyName}'.");
                return legacyName;
            }
        }
    }
}
