using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Helper around PEAK's version string (Application.version, e.g. "1.65.a"). Used to
    /// detect a game update across mod sessions (see Plugin's launch-time check) and to
    /// flag archived saves as possibly stale after one (see SaveArchive/SavePicker).
    /// </summary>
    internal static class GameVersionCompat
    {
        /// <summary>The game version this session is actually running</summary>
        public static string Current => Application.version;

        /// <summary>Shown in place of a real version for a save with no stored gameVersion at all.</summary>
        public const string NoVersionDisplay = "v?.??.?";

        /// <summary>
        /// The one safe guess for a missing gameVersion: saves written at
        /// ArchiveNativeSettingsVersion - 1 only exist from a ~3 hour window during the
        /// 1.65.a update. Any other missing version could genuinely be anything, so
        /// stays unguessed (NoVersionDisplay) instead.
        /// </summary>
        public const string LegacySettingsVersion6GameVersion = "1.64.a";

        public static string Display(string version) => "v" + version;

        /// <summary>
        /// True if version is older than current in the sense that matters here: the map
        /// pool was likely rotated since. Only major/minor counts; the trailing hotfix
        /// letter never rotates the map pool, so it's ignored here (though still parsed).
        /// </summary>
        public static bool IsOlderThan(string version, string current)
        {
            if (string.IsNullOrEmpty(version)) return true;
            if (version == current) return false;

            if (TryParse(version, out int vMajor, out int vMinor, out char _)
                && TryParse(current, out int cMajor, out int cMinor, out char _))
            {
                if (vMajor != cMajor) return vMajor < cMajor;
                return vMinor < cMinor;
            }

            // Unparseable: treat as older/stale so a real change isn't silently ignored.
            return true;
        }

        private static bool TryParse(string version, out int major, out int minor, out char letter)
        {
            major = 0; minor = 0; letter = '\0';
            if (string.IsNullOrEmpty(version)) return false;

            string[] parts = version.Split('.');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], out major)) return false;
            if (!int.TryParse(parts[1], out minor)) return false;
            if (parts[2].Length != 1) return false;
            letter = parts[2][0];
            return true;
        }
    }
}
