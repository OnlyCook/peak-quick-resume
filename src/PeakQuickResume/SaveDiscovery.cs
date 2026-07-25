using System;
using System.Globalization;
using System.IO;
using BepInEx.Logging;

namespace PEAKQuickResume
{
    /// <summary>
    /// Describes which run a save on disk belongs to: either a normal difficulty
    /// (identified by its ascent index) or a custom run (the boarding-pass "Custom"
    /// toggle, which is stored in a single ascent-less file)
    /// </summary>
    public struct SaveTarget
    {
        /// <summary>True if this is a custom run (peak_save_CustomRun_*)</summary>
        public bool IsCustom;

        /// <summary>Ascent index for a normal run. Meaningless (0) when <see cref="IsCustom"/> </summary>
        public int Ascent;

        public static SaveTarget Normal(int ascent) => new SaveTarget { IsCustom = false, Ascent = ascent };
        public static SaveTarget Custom() => new SaveTarget { IsCustom = true, Ascent = 0 };

        /// <summary>True if both describe the same run bucket (same ascent, or both custom)</summary>
        public bool SameRunAs(SaveTarget other) =>
            IsCustom == other.IsCustom && (IsCustom || Ascent == other.Ascent);

        public override string ToString() => IsCustom ? "custom run" : $"ascent {Ascent}";
    }

    /// <summary>
    /// Finds which run to resume when the game doesn't tell us, specifically at the
    /// Airport, where <c>Ascents.currentAscent</c> is just the boarding-pass default
    /// (0), not the difficulty of the save you want
    ///
    /// Reads our own save store (<see cref="OwnSavePaths.ArchiveDir"/>) and picks the
    /// most recent save event, matching the user's rule: "choose the latest". Recency
    /// comes from the event stamp baked into each filename, NOT from the file's
    /// modification time - so hand-editing a save can't reorder the store or make an
    /// old checkpoint look like the newest one
    /// </summary>
    public static class SaveDiscovery
    {
        private const string CustomToken = "CustomRun";

        /// <summary>
        /// Newest save event on disk for the current network mode, a normal ascent OR a
        /// custom run, whichever event stamp is highest. Returns false if no
        /// recognizable saves are found
        /// </summary>
        public static bool TryGetLatestSave(ManualLogSource log, bool offlineMode, out SaveTarget target)
        {
            target = SaveTarget.Normal(0);
            try
            {
                string dir = OwnSavePaths.ArchiveDir(offlineMode);
                if (!Directory.Exists(dir))
                {
                    log?.LogInfo($"[savescan] Save directory does not exist yet: {dir}");
                    return false;
                }

                DateTime best = DateTime.MinValue;
                string bestFile = null;
                bool found = false;

                foreach (string file in Directory.GetFiles(dir, "peak_save_*.json"))
                {
                    if (!OwnSavePaths.TrySplit(file, out string stem, out string stamp)) continue;
                    if (!TryParseStem(stem, offlineMode, out SaveTarget t)) continue;

                    // Same fallback SaveArchive.ReadAll uses: a stamp that isn't a
                    // parseable timestamp still identifies its event, it just can't order
                    // the scan, so fall back to the file's own write time for that
                    if (!DateTime.TryParseExact(stamp, OwnSavePaths.StampFormat, CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out DateTime when))
                    {
                        try { when = File.GetLastWriteTimeUtc(file); }
                        catch { continue; }
                    }

                    if (found && when <= best) continue;

                    best = when;
                    target = t;
                    bestFile = Path.GetFileName(file);
                    found = true;
                }

                if (found)
                    log?.LogInfo($"[savescan] Latest {(offlineMode ? "offline" : "coop")} save: {target} ('{bestFile}').");
                else
                    log?.LogInfo($"[savescan] No recognizable {(offlineMode ? "offline" : "coop")} saves found in {dir}.");

                return found;
            }
            catch (Exception e)
            {
                log?.LogError($"[savescan] TryGetLatestSave failed: {e}");
                return false;
            }
        }

        /// <summary>
        /// Parse a save-file stem (no extension, no <c>__{stamp}</c> suffix), e.g.
        /// "peak_save_2_offline" or "peak_save_CustomRun_76561..." into a
        /// <see cref="SaveTarget"/>. Shared with <see cref="SaveArchive"/>
        /// </summary>
        public static bool TryParseStem(string stem, bool offlineMode, out SaveTarget target)
        {
            target = SaveTarget.Normal(0);
            const string prefix = "peak_save_";
            if (string.IsNullOrEmpty(stem) || !stem.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string rest = stem.Substring(prefix.Length); // "-1_offline" | "-1_<userId>" | "CustomRun_..."

            string token; // the ascent/CustomRun part, before the trailing mode/userId
            if (offlineMode)
            {
                const string suffix = "_offline";
                if (!rest.EndsWith(suffix, StringComparison.Ordinal)) return false; // e.g. legacy "peak_save_offline"
                token = rest.Substring(0, rest.Length - suffix.Length); // "-1" or "CustomRun"
            }
            else
            {
                // coop: token is everything before the first underscore of the userId
                int us = rest.IndexOf('_');
                if (us <= 0) return false;
                token = rest.Substring(0, us);
            }

            if (token == CustomToken)
            {
                target = SaveTarget.Custom();
                return true;
            }
            if (int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int ascent))
            {
                target = SaveTarget.Normal(ascent);
                return true;
            }
            return false;
        }
    }
}
