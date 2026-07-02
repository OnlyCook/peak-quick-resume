using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;

namespace PEAKQuickResume
{
    /// <summary>
    /// Describes which run a save on disk belongs to: either a normal difficulty
    /// (identified by its ascent index) or a custom run (the boarding-pass "Custom"
    /// toggle, which the checkpoint mod stores in a single ascent-less file)
    /// </summary>
    public struct SaveTarget
    {
        /// <summary>True if this is a custom run (peak_save_CustomRun_*)</summary>
        public bool IsCustom;

        /// <summary>Ascent index for a normal run. Meaningless (0) when <see cref="IsCustom"/> </summary>
        public int Ascent;

        public static SaveTarget Normal(int ascent) => new SaveTarget { IsCustom = false, Ascent = ascent };
        public static SaveTarget Custom() => new SaveTarget { IsCustom = true, Ascent = 0 };

        public override string ToString() => IsCustom ? "custom run" : $"ascent {Ascent}";
    }

    /// <summary>
    /// Finds which run to resume when the game doesn't tell us, specifically at the
    /// Airport, where <c>Ascents.currentAscent</c> is just the boarding-pass default
    /// (0), not the difficulty of the save you want
    ///
    /// We read the checkpoint mod's own save files (same folder it writes) and pick
    /// the most-recently-modified one, matching the user's rule: "choose the latest"
    ///
    /// File layout (checkpoint save 0.4.7), under <c>BepInEx/plugins/Checkpoint_Save</c>:
    ///   offline normal : peak_save_{ascent}_offline.json
    ///   offline custom : peak_save_CustomRun_offline.json
    ///   coop normal    : Coop/peak_save_{ascent}_{userId}.json
    ///   coop custom    : Coop/peak_save_CustomRun_{userId}.json
    /// </summary>
    public static class SaveDiscovery
    {
        private const string CustomToken = "CustomRun";

        private static string BaseDir => Path.Combine(Paths.PluginPath, "Checkpoint_Save");
        private static string CoopDir => Path.Combine(BaseDir, "Coop");

        /// <summary>
        /// Newest save on disk for the current network mode, a normal ascent OR a
        /// custom run, whichever file was modified most recently. Returns false if
        /// no recognizable saves are found
        /// </summary>
        public static bool TryGetLatestSave(ManualLogSource log, bool offlineMode, out SaveTarget target)
        {
            target = SaveTarget.Normal(0);
            try
            {
                string dir = offlineMode ? BaseDir : CoopDir;
                if (!Directory.Exists(dir))
                {
                    log.LogInfo($"[savescan] Save directory does not exist yet: {dir}");
                    return false;
                }

                DateTime best = DateTime.MinValue;
                bool found = false;
                string bestFile = null;

                foreach (string file in Directory.GetFiles(dir, "peak_save_*.json"))
                {
                    if (!TryParseTarget(Path.GetFileName(file), offlineMode, out SaveTarget t))
                        continue;

                    DateTime mt = File.GetLastWriteTimeUtc(file);
                    if (mt > best)
                    {
                        best = mt;
                        target = t;
                        bestFile = Path.GetFileName(file);
                        found = true;
                    }
                }

                if (found)
                    log.LogInfo($"[savescan] Latest {(offlineMode ? "offline" : "coop")} save: {target} "
                        + $"('{bestFile}', modified {best:u}).");
                else
                    log.LogInfo($"[savescan] No recognizable {(offlineMode ? "offline" : "coop")} saves found in {dir}.");

                return found;
            }
            catch (Exception e)
            {
                log.LogError($"[savescan] TryGetLatestSave failed: {e}");
                return false;
            }
        }

        // "peak_save_-1_offline.json"          (offline normal) -> ascent -1
        // "peak_save_CustomRun_offline.json"   (offline custom) -> custom
        // "peak_save_-1_76561198...id.json"    (coop normal)    -> ascent -1
        // "peak_save_CustomRun_76561198...json"(coop custom)    -> custom
        private static bool TryParseTarget(string fileName, bool offlineMode, out SaveTarget target)
            => TryParseStem(Path.GetFileNameWithoutExtension(fileName), offlineMode, out target);

        /// <summary>
        /// Parse a checkpoint-mod canonical file stem (no extension), e.g.
        /// "peak_save_2_offline" or "peak_save_CustomRun_76561..." into a
        /// <see cref="SaveTarget"/>. Shared with <see cref="SaveArchive"/>
        /// </summary>
        public static bool TryParseStem(string stem, bool offlineMode, out SaveTarget target)
        {
            target = SaveTarget.Normal(0);
            const string prefix = "peak_save_";
            if (string.IsNullOrEmpty(stem) || !stem.StartsWith(prefix)) return false;
            string rest = stem.Substring(prefix.Length); // "-1_offline" | "-1_<userId>" | "CustomRun_..."

            string token; // the ascent/CustomRun part, before the trailing mode/userId
            if (offlineMode)
            {
                const string suffix = "_offline";
                if (!rest.EndsWith(suffix)) return false; // e.g. legacy "peak_save_offline"
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
            if (int.TryParse(token, out int ascent))
            {
                target = SaveTarget.Normal(ascent);
                return true;
            }
            return false;
        }
    }
}
