using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using Newtonsoft.Json;
using Photon.Pun;
using Zorro.Core;

namespace PEAKQuickResume
{
    /// <summary>
    /// One archived checkpoint save the player can pick from the F7 menu
    /// Backed by a copy of a checkpoint-mod save file living in our own folder
    /// </summary>
    public class ArchivedSave
    {
        public string FilePath; // full path to the archived .json in our folder
        public bool Offline; // category: offline vs coop
        public SaveTarget Target; // difficulty / custom-run this save belongs to
        public DateTime SortTime; // parsed from the archive filename (source mtime)

        // Display metadata (read from the save's JSON; best-effort)
        public string SaveDate = "";
        public string CampfireName = "";
        public float Playtime;
        public string BiomesSummary = "";
        // Everyone who played this run (co-op). The checkpoint mod stores playerNames
        // alphabetically, NOT host-first, so we show the whole list.
        public string Players = "";

        public string DifficultyLabel => SaveArchive.DifficultyLabel(Target);
    }

    /// <summary>
    /// Keeps a growing archive of every checkpoint save, so the player can browse and
    /// load ANY past checkpoint (not just the single latest one the checkpoint mod
    /// keeps). We never modify the checkpoint mod or its files: on each save we copy
    /// its file into <c>BepInEx/plugins/QuickResume/Archive</c>; to load an older one
    /// we copy it back over the mod's canonical file and then run the normal resume
    ///
    /// This deliberately sidesteps the checkpoint mod's own cleanup, it deletes files
    /// matching <c>peak_save_{ascent}_*</c> in its folders on save/load, by keeping our
    /// copies in a separate directory it never touches
    ///
    /// Archived saves are split by category into <c>Archive/Offline</c> and
    /// <c>Archive/Coop</c>. Filename: <c>{canonicalStem}__{yyyyMMdd_HHmmss_fff}.json</c>,
    /// e.g. <c>peak_save_2_offline__20260702_140311_204.json</c>. The stem carries the
    /// difficulty/custom + category; the timestamp is the source file's write time and
    /// drives both sort order and idempotent de-duplication
    /// </summary>
    public static class SaveArchive
    {
        private const string TsFormat = "yyyyMMdd_HHmmss_fff";
        private const string Sep = "__";

        private static bool _migrated;

        private static string CanonicalBase => Path.Combine(Paths.PluginPath, "Checkpoint_Save");
        private static string CanonicalCoop => Path.Combine(CanonicalBase, "Coop");
        private static string ArchiveRoot => Path.Combine(Paths.PluginPath, "QuickResume", "Archive");
        private static string ArchiveDir(bool offline) => Path.Combine(ArchiveRoot, offline ? "Offline" : "Coop");

        private static string CanonicalDir(bool offline) => offline ? CanonicalBase : CanonicalCoop;

        /// <summary>
        /// Copy any checkpoint-mod save file for the given category that isn't archived
        /// yet into our archive. Idempotent: a file whose write-time already has a
        /// matching archive entry is skipped. Call this after every save (and lazily
        /// before showing the picker, to also pick up saves made before this mod)
        /// </summary>
        public static void Sync(bool offline, ManualLogSource log)
        {
            try
            {
                MigrateLegacyFlatArchive(log);

                string src = CanonicalDir(offline);
                if (!Directory.Exists(src)) return;
                string archiveDir = ArchiveDir(offline);
                Directory.CreateDirectory(archiveDir);

                foreach (string file in Directory.GetFiles(src, "peak_save_*.json"))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (!SaveDiscovery.TryParseStem(stem, offline, out _))
                        continue; // legacy / unrecognized name, leave it alone

                    DateTime mt = File.GetLastWriteTimeUtc(file);
                    string dest = Path.Combine(archiveDir,
                        stem + Sep + mt.ToString(TsFormat, CultureInfo.InvariantCulture) + ".json");

                    if (File.Exists(dest)) continue; // already archived this exact save
                    File.Copy(file, dest, overwrite: false);
                    log?.LogInfo($"[archive] Archived '{Path.GetFileName(file)}' -> '{Path.GetFileName(dest)}'.");
                }
            }
            catch (Exception e)
            {
                log?.LogError($"[archive] Sync failed: {e}");
            }
        }

        // One-time move of archives made by earlier versions (flat Archive/ folder) into
        // the new Archive/Offline and Archive/Coop subfolders. Idempotent; runs once
        private static void MigrateLegacyFlatArchive(ManualLogSource log)
        {
            if (_migrated) return;
            _migrated = true;
            try
            {
                if (!Directory.Exists(ArchiveRoot)) return;
                foreach (string file in Directory.GetFiles(ArchiveRoot, "peak_save_*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    int si = name.LastIndexOf(Sep, StringComparison.Ordinal);
                    string stem = si > 0 ? name.Substring(0, si) : name;
                    bool offline = stem.EndsWith("_offline", StringComparison.Ordinal);

                    string destDir = ArchiveDir(offline);
                    Directory.CreateDirectory(destDir);
                    string dest = Path.Combine(destDir, Path.GetFileName(file));
                    if (File.Exists(dest)) continue; // already there, leave the stray copy
                    File.Move(file, dest);
                    log?.LogInfo($"[archive] Migrated '{Path.GetFileName(file)}' -> {(offline ? "Offline" : "Coop")}/.");
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"[archive] Legacy archive migration skipped: {e.Message}");
            }
        }

        /// <summary>
        /// All archived saves for the given category (offline vs coop), newest first
        /// Runs a <see cref="Sync"/> first so freshly-made and pre-existing saves show up
        /// </summary>
        public static List<ArchivedSave> List(bool offline, ManualLogSource log)
        {
            var result = new List<ArchivedSave>();
            try
            {
                Sync(offline, log);
                string archiveDir = ArchiveDir(offline);
                if (!Directory.Exists(archiveDir)) return result;

                // In co-op the checkpoint mod writes one save PER player (userId in the
                // filename), but only the host's own save is what a resume uses — so show
                // only the host's. If we can't determine our userId, fall back to showing
                // all (better than an empty list).
                string hostUserId = offline ? null : LocalUserId();

                foreach (string file in Directory.GetFiles(archiveDir, "peak_save_*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    int si = name.LastIndexOf(Sep, StringComparison.Ordinal);
                    if (si <= 0) continue;
                    string stem = name.Substring(0, si);
                    string tsStr = name.Substring(si + Sep.Length);

                    // Category split: offline stems end "_offline"; everything else is coop
                    bool isOffline = stem.EndsWith("_offline", StringComparison.Ordinal);
                    if (isOffline != offline) continue;

                    // Co-op: keep only the host's own saves (matched by userId in the stem).
                    if (!offline && !string.IsNullOrEmpty(hostUserId)
                        && (!TryGetCoopUserId(stem, out string uid) || uid != hostUserId))
                        continue;

                    if (!SaveDiscovery.TryParseStem(stem, offline, out SaveTarget target)) continue;
                    if (!DateTime.TryParseExact(tsStr, TsFormat, CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out DateTime sortTime))
                        sortTime = File.GetLastWriteTimeUtc(file);

                    var entry = new ArchivedSave
                    {
                        FilePath = file,
                        Offline = offline,
                        Target = target,
                        SortTime = sortTime,
                    };
                    ReadMetadata(entry, log);
                    result.Add(entry);
                }

                result.Sort((a, b) => b.SortTime.CompareTo(a.SortTime)); // newest first
            }
            catch (Exception e)
            {
                log?.LogError($"[archive] List failed: {e}");
            }
            return result;
        }

        /// <summary>
        /// Copy an archived save back over the checkpoint mod's canonical file for its
        /// difficulty/category, so the mod's own load path reads the chosen checkpoint
        /// </summary>
        public static bool Restore(ArchivedSave save, ManualLogSource log)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(save.FilePath);
                int si = name.LastIndexOf(Sep, StringComparison.Ordinal);
                string stem = si > 0 ? name.Substring(0, si) : name;

                string dir = CanonicalDir(save.Offline);
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, stem + ".json");

                File.Copy(save.FilePath, dest, overwrite: true);
                log?.LogInfo($"[archive] Restored '{Path.GetFileName(save.FilePath)}' -> canonical '{stem}.json'.");
                return true;
            }
            catch (Exception e)
            {
                log?.LogError($"[archive] Restore failed: {e}");
                return false;
            }
        }

        /// <summary>Permanently delete one archived save (does not touch the mod's files)</summary>
        public static bool Delete(ArchivedSave save, ManualLogSource log)
        {
            try
            {
                if (File.Exists(save.FilePath)) File.Delete(save.FilePath);
                log?.LogInfo($"[archive] Deleted '{Path.GetFileName(save.FilePath)}'.");
                return true;
            }
            catch (Exception e)
            {
                log?.LogError($"[archive] Delete failed: {e}");
                return false;
            }
        }

        /// <summary>Human label for the boarding-pass difficulty an ascent maps to</summary>
        public static string DifficultyLabel(SaveTarget t)
        {
            string official = TryGetOfficialAscentTitle(t);
            if (!string.IsNullOrEmpty(official)) return official;

            // Fallback if the game's own AscentData couldn't be reached (e.g. a future
            // update changes its shape); our own translations, better than nothing
            if (t.IsCustom) return SavePickerLocalization.Get(PickerText.CustomRun);
            switch (t.Ascent)
            {
                case -1: return SavePickerLocalization.Get(PickerText.Tenderfoot);
                case 0: return "PEAK";
                default: return string.Format(SavePickerLocalization.Get(PickerText.AscentFormat), t.Ascent);
            }
        }

        // Reuses the game's OWN localized difficulty names instead of re-translating them
        // ourselves, exact wording in every language, no guesswork (our own German
        // "Benutzerdefinierter Lauf" for a custom run, for instance, doesn't match the
        // game's own "Eigener Aufstieg"). Same indexing AscentUI itself uses:
        // ascents[0] = custom run, ascents[ascent + 2] = normal difficulty (so ascent -1 =
        // index 1 "Tenderfoot", ascent 0 = index 2 "PEAK", ascent 1 = index 3, ...)
        private static string TryGetOfficialAscentTitle(SaveTarget t)
        {
            try
            {
                var data = SingletonAsset<AscentData>.Instance;
                if (data?.ascents == null) return null;
                int index = t.IsCustom ? 0 : t.Ascent + 2;
                if (index < 0 || index >= data.ascents.Count) return null;
                return data.ascents[index].localizedTitle;
            }
            catch { return null; }
        }

        // Best-effort read of the display fields from the checkpoint mod's JSON schema
        private static void ReadMetadata(ArchivedSave entry, ManualLogSource log)
        {
            try
            {
                string json = File.ReadAllText(entry.FilePath);
                SaveMeta m = JsonConvert.DeserializeObject<SaveMeta>(json);
                if (m == null) return;
                entry.SaveDate = m.saveDate ?? "";
                entry.CampfireName = m.campfireName ?? "";
                entry.Playtime = m.timePlayed;
                if (m.biome_names != null && m.biome_names.Count > 0)
                    entry.BiomesSummary = m.biome_names[m.biome_names.Count - 1]; // deepest biome reached
                // playerNames is alphabetical (not host-first), so show the whole party.
                if (m.playerNames != null && m.playerNames.Count > 0)
                    entry.Players = string.Join(", ", m.playerNames);
            }
            catch (Exception e)
            {
                log?.LogWarning($"[archive] Could not read metadata for '{Path.GetFileName(entry.FilePath)}': {e.Message}");
            }
        }

        // Our Photon user id (== SteamID64 for this game) — the value the checkpoint mod
        // embeds in each co-op save filename. Empty if we're not in a networked session.
        private static string LocalUserId()
        {
            try { return PhotonNetwork.LocalPlayer?.UserId ?? ""; }
            catch { return ""; }
        }

        // Pull the userId out of a co-op stem like "peak_save_-1_7656..." or
        // "peak_save_CustomRun_7656..." — it is always the segment after the last '_'
        // (ascent tokens and "CustomRun" contain no underscore).
        private static bool TryGetCoopUserId(string stem, out string userId)
        {
            userId = "";
            int u = stem.LastIndexOf('_');
            if (u <= 0 || u >= stem.Length - 1) return false;
            userId = stem.Substring(u + 1);
            return userId.Length > 0;
        }

        // Subset of the checkpoint mod's SaveData we display. Newtonsoft ignores the rest
        private class SaveMeta
        {
            public string saveDate;
            public string campfireName;
            public float timePlayed;
            public List<string> biome_names;
            public List<string> playerNames;
        }
    }
}
