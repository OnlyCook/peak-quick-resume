using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Small helper around PEAK's own version string (<c>UnityEngine.Application.version</c>,
    /// e.g. "1.65.a" - confirmed directly against the game's own build, same string the
    /// vanilla top-left corner label shows). Used to detect a game update across mod
    /// sessions (see Plugin's launch-time check) and to flag individual archived saves
    /// as possibly stale after one (see SaveArchive/SavePicker)
    /// </summary>
    internal static class GameVersionCompat
    {
        /// <summary>The game version this session is actually running</summary>
        public static string Current => Application.version;

        /// <summary>
        /// Every save this mod wrote before this feature existed has no stored
        /// <c>gameVersion</c> at all. The mod only ever supported 1.64.a up to this
        /// point, so nearly all such saves were in fact written on 1.64.a - accepted
        /// as a flat fallback (not derived from file timestamps: a hardcoded real-world
        /// cutoff moment only ever matches THIS machine's own update time, not every
        /// player's - see the conversation this was decided in). A future mod update
        /// can drop this fallback once every save in the wild has a real stamp
        /// </summary>
        public const string FallbackVersion = "1.64.a";

        /// <summary>"1.64.a" -&gt; "v1.64.a", matching VersionString's own "v"-prefixed
        /// top-left corner label as closely as possible (that's the whole point - see
        /// SavePicker's use of this, the resemblance is what's supposed to ring a bell)</summary>
        public static string Display(string version) => "v" + version;

        /// <summary>
        /// True if <paramref name="version"/> is older than <paramref name="current"/> in
        /// the sense that matters here: the map pool was likely rotated since. Only
        /// major/minor ("1.64" vs "1.65") count - the trailing letter is a small
        /// hotfix/patch letter (e.g. "1.65.a" -&gt; "1.65.b") that, per experience, never
        /// rotates the map pool, so it's deliberately ignored here even though it's
        /// still parsed (kept available for anything that wants it later)
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

            // Unparseable (future format change) - "different" is still meaningful,
            // just without knowing direction. Treat as older/stale so a real change
            // isn't silently ignored; a version that then never advances again would
            // just keep showing the same (harmless) notice, not a growing problem
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
