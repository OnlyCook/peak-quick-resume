using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        public bool Starred; // pinned to the top of the F7 picker; can't be deleted while true

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

        // Starred saves, persisted as a flat JSON array of archive filenames (unique
        // across both categories: offline stems always end "_offline", coop stems
        // never do, see List() below). One shared file rather than one per category,
        // there's no per-category state here worth splitting. Loaded lazily, cached in
        // memory for the rest of the session, written back to disk on every change
        private static string StarredFile => Path.Combine(ArchiveRoot, "starred.json");
        private static HashSet<string> _starredCache;

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
                        Starred = LoadStarred(log).Contains(Path.GetFileName(file)),
                    };
                    ReadMetadata(entry, log);
                    result.Add(entry);
                }

                result.Sort(CompareForDisplay);
            }
            catch (Exception e)
            {
                log?.LogError($"[archive] List failed: {e}");
            }
            return result;
        }

        /// <summary>
        /// Userids of connected coop players that <see cref="RestoreCoopSiblings"/>
        /// could NOT find a close-enough sibling archive for during the most recent
        /// <see cref="Restore"/> call. <see cref="OwnInventoryRestore.RestoreAll"/>
        /// checks this and skips force-applying that player's (possibly wildly stale)
        /// canonical file, leaving their current in-game inventory untouched instead.
        ///
        /// Cleared at the top of every <see cref="Restore"/> call so it never leaks
        /// into an unrelated resume (e.g. a plain "continue" that never goes through
        /// this class at all, or a later archive restore that doesn't skip anyone)
        /// </summary>
        public static readonly HashSet<string> LastSkippedCoopUserIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Copy an archived save back over the checkpoint mod's canonical file for its
        /// difficulty/category, so the mod's own load path reads the chosen checkpoint.
        /// Coop: also rolls back every OTHER connected player's own canonical file to
        /// the matching moment, see <see cref="RestoreCoopSiblings"/> for why that's
        /// required, not optional
        /// </summary>
        public static bool Restore(ArchivedSave save, ManualLogSource log)
        {
            LastSkippedCoopUserIds.Clear();
            if (!RestoreOne(save.FilePath, save.Offline, log)) return false;
            if (!save.Offline) RestoreCoopSiblings(save, log);
            return true;
        }

        private static bool RestoreOne(string archivedFilePath, bool offline, ManualLogSource log)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(archivedFilePath);
                int si = name.LastIndexOf(Sep, StringComparison.Ordinal);
                string stem = si > 0 ? name.Substring(0, si) : name;

                string dir = CanonicalDir(offline);
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, stem + ".json");

                File.Copy(archivedFilePath, dest, overwrite: true);
                log?.LogInfo($"[archive] Restored '{Path.GetFileName(archivedFilePath)}' -> canonical '{stem}.json'.");
                return true;
            }
            catch (Exception e)
            {
                log?.LogError($"[archive] RestoreOne failed: {e}");
                return false;
            }
        }

        /// <summary>
        /// The checkpoint mod's own <c>LoadPlayerCoop</c> (<c>LoadInventoryDelayed</c>)
        /// restores EVERY connected player from their OWN per-user canonical file, not
        /// just the host's - <see cref="List"/> only shows the host's own save per
        /// timestamp (one row per real save event, from the host's perspective), so
        /// picking an older checkpoint and only restoring the host's file left every
        /// other player's canonical file untouched, silently keeping whatever their
        /// MOST RECENT actual save left it at regardless of which older moment the host
        /// picked. Confirmed against real session logs: same host save restored the
        /// host's own first-campfire items correctly, but the client kept its
        /// second-campfire items every time, matching this exactly
        ///
        /// Coop per-player autosaves all fire within the same save event, but a few
        /// milliseconds apart (sequential file writes), not at an identical instant, so
        /// siblings are matched by NEAREST archived write-time to the chosen host save
        /// (restricted to the same ascent/custom-run target), not an exact match
        ///
        /// That "nearest" match has no ceiling on how far away it can be: if a client is
        /// missing an archive from the same save event entirely (never wrote one, wrote
        /// one for a different target, cleared their archive, etc.), the nearest file for
        /// their userId could be from a completely different run - hours or days old -
        /// and would get silently restored as-is, overwriting that client's current
        /// progress with items/state from a run they weren't even part of. <see
        /// cref="MaxSiblingDelta"/> bounds the match to a window wide enough to absorb
        /// real save-event jitter but far too narrow to ever span two different runs; a
        /// candidate outside it is treated as no match, and that client's canonical file
        /// is left untouched here (same as if this whole feature didn't run for them)
        /// rather than restoring the too-far candidate
        ///
        /// Untouched is NOT the same as safe, though: <see
        /// cref="OwnInventoryRestore.RestoreAll"/> unconditionally re-reads and
        /// force-applies every connected player's own canonical file on every resume,
        /// regardless of what (if anything) happened here - so a client whose canonical
        /// file is itself stale (last written in some earlier session, never touched
        /// this one) would still get that stale state force-applied even though we
        /// declined to overwrite it with an equally-stale archive. Any currently
        /// connected coop player who doesn't end up with a verified-close match below -
        /// whether because the nearest candidate was outside <see
        /// cref="MaxSiblingDelta"/> or because no candidate for their userId/target
        /// existed at all - is recorded in <see cref="LastSkippedCoopUserIds"/> so that
        /// unconditional re-apply can be suppressed for them instead
        /// </summary>
        private static readonly TimeSpan MaxSiblingDelta = TimeSpan.FromMinutes(2);

        private static void RestoreCoopSiblings(ArchivedSave hostSave, ManualLogSource log)
        {
            try
            {
                string archiveDir = ArchiveDir(offline: false);
                string hostUserId = LocalUserId();
                var bestByUser = new Dictionary<string, (string file, TimeSpan delta)>();

                if (Directory.Exists(archiveDir))
                {
                    foreach (string file in Directory.GetFiles(archiveDir, "peak_save_*.json"))
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        int si = name.LastIndexOf(Sep, StringComparison.Ordinal);
                        if (si <= 0) continue;
                        string stem = name.Substring(0, si);
                        string tsStr = name.Substring(si + Sep.Length);

                        if (!TryGetCoopUserId(stem, out string uid) || uid == hostUserId) continue;
                        if (!SaveDiscovery.TryParseStem(stem, offlineMode: false, out SaveTarget target)) continue;
                        if (target.IsCustom != hostSave.Target.IsCustom || target.Ascent != hostSave.Target.Ascent) continue;
                        if (!DateTime.TryParseExact(tsStr, TsFormat, CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out DateTime sortTime))
                            continue;

                        TimeSpan delta = (sortTime - hostSave.SortTime).Duration();
                        if (delta > MaxSiblingDelta) continue;
                        if (!bestByUser.TryGetValue(uid, out var current) || delta < current.delta)
                            bestByUser[uid] = (file, delta);
                    }
                }

                foreach (var kv in bestByUser)
                    RestoreOne(kv.Value.file, offline: false, log);

                foreach (Photon.Realtime.Player p in PhotonNetwork.PlayerList)
                {
                    string uid = p?.UserId ?? "";
                    if (uid.Length == 0 || uid == hostUserId || bestByUser.ContainsKey(uid)) continue;
                    LastSkippedCoopUserIds.Add(uid);
                    log?.LogInfo($"[archive] No archived save within {MaxSiblingDelta.TotalMinutes:F0}m of the "
                        + $"chosen checkpoint for userId '{uid}'; leaving their inventory untouched on restore.");
                }
            }
            catch (Exception e)
            {
                log?.LogError($"[archive] RestoreCoopSiblings failed: {e}");
            }
        }

        /// <summary>
        /// Applies a JSON field patch to the canonical (not-yet-archived) save file for
        /// the given userId AND run target (ascent / custom run) in the current
        /// category, called by BackpackSaveMitigation right after the checkpoint mod
        /// writes it and before Sync copies it into the archive - see that class for why
        /// the patch has to land at this exact point.
        ///
        /// <paramref name="target"/> is required even offline: multiple canonical files
        /// (one per ascent, plus a separate custom-run one) can and do coexist
        /// side-by-side in the same folder once more than one difficulty/custom run has
        /// ever been played, so "the one canonical file" for a category is NOT a safe
        /// assumption - an earlier version of this method patched whichever file
        /// <c>Directory.GetFiles</c> happened to list first (filesystem-order, not
        /// recency), which could silently write the restore into a stale, unrelated
        /// save instead of the one actually being written by the save that triggered it
        /// </summary>
        public static bool PatchCanonicalFileForUser(bool offline, SaveTarget target, string userId, Action<JObject> patch, ManualLogSource log)
        {
            try
            {
                string dir = CanonicalDir(offline);
                if (!Directory.Exists(dir)) return false;

                foreach (string file in Directory.GetFiles(dir, "peak_save_*.json"))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (!SaveDiscovery.TryParseStem(stem, offline, out SaveTarget fileTarget)) continue;
                    if (fileTarget.IsCustom != target.IsCustom || fileTarget.Ascent != target.Ascent) continue;

                    if (!offline)
                    {
                        if (!TryGetCoopUserId(stem, out string uid) || uid != userId) continue;
                    }

                    var json = JObject.Parse(File.ReadAllText(file));
                    patch(json);
                    File.WriteAllText(file, json.ToString(Formatting.Indented));
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                log?.LogError($"[archive] PatchCanonicalFileForUser failed: {e}");
                return false;
            }
        }

        /// <summary>
        /// Permanently delete one archived save (does not touch the mod's files).
        /// Refuses starred saves outright (the F7 picker's own two-step confirm should
        /// never even reach here for one, see SavePicker.OnDeletePressed, this is just
        /// the defensive backstop)
        /// </summary>
        public static bool Delete(ArchivedSave save, ManualLogSource log)
        {
            if (save.Starred)
            {
                log?.LogWarning($"[archive] Refused to delete starred save '{Path.GetFileName(save.FilePath)}'; unstar it first.");
                return false;
            }
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

        /// <summary>Stars or unstars an archived save, persisted to disk immediately</summary>
        public static void SetStarred(ArchivedSave save, bool starred, ManualLogSource log)
        {
            var set = LoadStarred(log);
            string key = Path.GetFileName(save.FilePath);
            bool changed = starred ? set.Add(key) : set.Remove(key);
            save.Starred = starred;
            if (changed) SaveStarredToDisk(log);
        }

        /// <summary>
        /// Display order for the F7 picker: every starred save sorts before every
        /// non-starred one, newest-first within each of those two groups
        /// </summary>
        public static int CompareForDisplay(ArchivedSave a, ArchivedSave b)
        {
            int byStar = (b.Starred ? 1 : 0) - (a.Starred ? 1 : 0);
            return byStar != 0 ? byStar : b.SortTime.CompareTo(a.SortTime);
        }

        private static HashSet<string> LoadStarred(ManualLogSource log)
        {
            if (_starredCache != null) return _starredCache;
            try
            {
                _starredCache = File.Exists(StarredFile)
                    ? new HashSet<string>(
                        JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(StarredFile)) ?? new List<string>(),
                        StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
            }
            catch (Exception e)
            {
                log?.LogWarning($"[archive] Could not read starred list, starting empty: {e.Message}");
                _starredCache = new HashSet<string>(StringComparer.Ordinal);
            }
            return _starredCache;
        }

        private static void SaveStarredToDisk(ManualLogSource log)
        {
            try
            {
                Directory.CreateDirectory(ArchiveRoot);
                File.WriteAllText(StarredFile, JsonConvert.SerializeObject(new List<string>(_starredCache)));
            }
            catch (Exception e)
            {
                log?.LogError($"[archive] Could not persist starred list: {e.Message}");
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
