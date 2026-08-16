using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Zorro.Core;

namespace PEAKQuickResume
{
    /// <summary>
    /// One archived checkpoint save the player can pick from the F7 menu. In co-op this
    /// is always the HOST's file for a save event (the one carrying level/world state); the
    /// connected clients' own files from that event are its siblings, resolved into a
    /// <see cref="SaveSelection"/> at load time.
    /// </summary>
    public class ArchivedSave
    {
        public string FilePath;
        public bool Offline;
        public SaveTarget Target;
        public string Stamp; // the save event's identity, from the filename (see OwnSavePaths)
        public string OwnerUserId = ""; // co-op only; "" offline
        public DateTime SortTime; // parsed from Stamp
        public bool Starred; // pinned to the top of the F7 picker; can't be deleted while true

        // Display metadata (read from the save's JSON; best-effort)
        public string SaveDate = "";
        public string CampfireName = "";
        public float Playtime;
        public string BiomesSummary = "";
        public string Players = ""; // co-op; alphabetical, not host-first

        // Only written into the file carrying world state (host's, or the single offline
        // file); a co-op client's file has none, which lets List() tell them apart.
        public string SceneName = "";

        // >= ArchiveNativeSettingsVersion: written with a shared per-event stamp, siblings
        // match exactly. Below: legacy save from the old canonical-file layout, needs fuzzy
        // timestamp matching (see SaveSelection.Build).
        public int SettingsVersion;

        public string GameVersion = ""; // "" if written before this field existed

        public string DifficultyLabel => SaveArchive.DifficultyLabel(Target);

        /// <summary>
        /// GameVersion as stored, or a safe guess for settingsVersion 6 (which could only
        /// have been written during the 1.65.a update window). Otherwise "".
        /// </summary>
        public string DisplayGameVersion =>
            !string.IsNullOrEmpty(GameVersion) ? GameVersion
            : SettingsVersion == 6 ? GameVersionCompat.LegacySettingsVersion6GameVersion
            : "";

        /// <summary>
        /// True if written under an older game version, meaning the map pool may have
        /// rotated since. A missing DisplayGameVersion counts as stale too.
        /// </summary>
        public bool IsStaleVersion => GameVersionCompat.IsOlderThan(DisplayGameVersion, GameVersionCompat.Current);
    }

    /// <summary>
    /// Everything one load needs to know about which files to read, resolved once by
    /// <see cref="SaveArchive.BuildSelection"/>. <see cref="HostFilePath"/> is the only file
    /// level/world state is read from; <see cref="TryGetPlayerFile"/> gives each connected
    /// player their own file for per-player state. A player missing from the map is skipped
    /// entirely, leaving their current state untouched.
    /// </summary>
    public class SaveSelection
    {
        public bool Offline;
        public SaveTarget Target;
        public string Stamp = "";
        public string HostFilePath = "";

        // userId -> that player's own file within this save event. Empty offline.
        public readonly Dictionary<string, string> PlayerFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Offline resolves to the single save file; co-op resolves only within this save event.</summary>
        public bool TryGetPlayerFile(string userId, out string path)
        {
            if (Offline)
            {
                path = HostFilePath;
                return !string.IsNullOrEmpty(path);
            }
            return PlayerFiles.TryGetValue(userId ?? "", out path);
        }
    }

    /// <summary>
    /// The mod's save store: every checkpoint ever written, browsable from the F7 menu,
    /// living in <c>BepInEx/plugins/QuickResume/Archive</c> split into <c>Offline</c>/<c>Coop</c>.
    /// The one store; dominik0207's "PEAK Checkpoint Save" folder is never touched (see
    /// <see cref="OwnSavePaths"/>). Files are never copied over each other, so a hand-edited
    /// save is safe and identity comes from its filename, not modification time.
    ///
    /// Filename: <c>{stem}__{stamp}.json</c>. The stem carries difficulty/custom + category
    /// (+ owning userId in co-op); the stamp identifies the save event, shared across every
    /// player's file from that autosave.
    /// </summary>
    public static class SaveArchive
    {
        /// <summary>
        /// <c>settingsVersion</c> stamped into every save this version writes. Bumped to 8
        /// for PEAK 2.0.a (typed backpacks, backpackOwnValues, 15-entry afflictions_current,
        /// progress-point area names). Reading stays backward compatible; see
        /// BackpackTypeCompat.FromSave, AfflictionArrayCompat.CopyOverlap, and the
        /// campfire-name table below.
        /// </summary>
        public const int CurrentSettingsVersion = 8;

        /// <summary>
        /// Oldest <c>settingsVersion</c> written by the archive-native save path (shared
        /// per-event stamp, exact sibling matching). Below this, a legacy save's siblings
        /// need fuzzy timestamp matching. Deliberately NOT bumped alongside
        /// <see cref="CurrentSettingsVersion"/> — this is a threshold, not the current version.
        /// </summary>
        public const int ArchiveNativeSettingsVersion = 7;

        private static bool _migrated;

        // Starred filenames, persisted as one shared JSON array across both categories
        // (offline/coop stems are distinguishable). Cached in memory, written on every change.
        private static string StarredFile => Path.Combine(OwnSavePaths.ArchiveRoot, "starred.json");
        private static HashSet<string> _starredCache;

        /// <summary>
        /// Max gap between two LEGACY files to still count as the same save event (see
        /// <see cref="BuildSelection"/>). Legacy files were copied in with per-file write
        /// times a few ms apart; archive-native saves share an exact stamp and don't need this.
        /// </summary>
        private static readonly TimeSpan MaxLegacySiblingDelta = TimeSpan.FromMinutes(2);

        /// <summary>
        /// All archived saves for the given category, newest first, exactly one row per save
        /// event. Grouped by event stamp (not matched against the live Photon userId) so
        /// browsing outside a room doesn't duplicate rows or misattribute a client's file as the host's.
        /// </summary>
        public static List<ArchivedSave> List(bool offline, ManualLogSource log)
        {
            var result = new List<ArchivedSave>();
            try
            {
                MigrateLegacyFlatArchive(log);

                string archiveDir = OwnSavePaths.ArchiveDir(offline);
                if (!Directory.Exists(archiveDir)) return result;

                string localUserId = offline ? "" : OwnSavePaths.LocalUserId();

                // Keyed by run target AND stamp: legacy stamps are independent file write
                // times, not a shared event id, so two different ascents saved in the same
                // millisecond must stay distinct.
                var byEvent = new Dictionary<(bool custom, int ascent, string stamp), ArchivedSave>();
                foreach (ArchivedSave entry in ReadAll(archiveDir, offline, log))
                {
                    if (!IsHostFile(entry, offline, localUserId)) continue;

                    var key = (entry.Target.IsCustom, entry.Target.IsCustom ? 0 : entry.Target.Ascent, entry.Stamp);
                    if (byEvent.TryGetValue(key, out ArchivedSave existing)
                        && string.CompareOrdinal(existing.OwnerUserId, entry.OwnerUserId) <= 0)
                        continue; // keep the stable (lowest owner id) choice
                    byEvent[key] = entry;
                }

                foreach (ArchivedSave host in byEvent.Values)
                {
                    host.Starred = LoadStarred(log).Contains(Path.GetFileName(host.FilePath));
                    result.Add(host);
                }

                result.Sort(CompareForDisplay);
            }
            catch (Exception e)
            {
                log?.LogError($"[archive] List failed: {e}");
            }
            return result;
        }

        // Does this file carry the level/world half of its save event (the one the picker
        // should show)? Offline: always. Archive-native co-op: presence of a scene name
        // (client files have none). Legacy co-op: no reliable signal without a local userId,
        // so falls back to accepting them all.
        private static bool IsHostFile(ArchivedSave entry, bool offline, string localUserId)
        {
            if (offline) return true;
            if (entry.SettingsVersion >= ArchiveNativeSettingsVersion) return !string.IsNullOrEmpty(entry.SceneName);
            return string.IsNullOrEmpty(localUserId) || entry.OwnerUserId == localUserId;
        }

        private static IEnumerable<ArchivedSave> ReadAll(string archiveDir, bool offline, ManualLogSource log)
        {
            foreach (string file in Directory.GetFiles(archiveDir, "peak_save_*.json"))
            {
                if (!OwnSavePaths.TrySplit(file, out string stem, out string stamp)) continue;

                // Category split: offline stems end "_offline"; everything else is coop
                bool isOffline = stem.EndsWith("_offline", StringComparison.Ordinal);
                if (isOffline != offline) continue;

                if (!SaveDiscovery.TryParseStem(stem, offline, out SaveTarget target)) continue;

                string ownerUserId = "";
                if (!offline) OwnSavePaths.TryGetCoopUserId(stem, out ownerUserId);

                // An unparseable stamp is still a valid event identity for sibling matching;
                // fall back to the file's write time just for sort order (keeps a
                // hand-renamed file visible instead of vanishing from the picker).
                if (!DateTime.TryParseExact(stamp, OwnSavePaths.StampFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime sortTime))
                {
                    try { sortTime = File.GetLastWriteTimeUtc(file); }
                    catch { continue; }
                }

                var entry = new ArchivedSave
                {
                    FilePath = file,
                    Offline = offline,
                    Target = target,
                    Stamp = stamp,
                    OwnerUserId = ownerUserId,
                    SortTime = sortTime,
                };
                ReadMetadata(entry, log);
                yield return entry;
            }
        }

        // One-time move of archives made by earlier versions (flat Archive/ folder) into
        // the Archive/Offline and Archive/Coop subfolders. Idempotent; runs once
        private static void MigrateLegacyFlatArchive(ManualLogSource log)
        {
            if (_migrated) return;
            _migrated = true;
            try
            {
                string root = OwnSavePaths.ArchiveRoot;
                if (!Directory.Exists(root)) return;
                foreach (string file in Directory.GetFiles(root, "peak_save_*.json"))
                {
                    if (!OwnSavePaths.TrySplit(file, out string stem, out _))
                        stem = Path.GetFileNameWithoutExtension(file);
                    bool offline = stem.EndsWith("_offline", StringComparison.Ordinal);

                    string destDir = OwnSavePaths.ArchiveDir(offline);
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
        /// Resolves one archived save into the full set of files a load should read. Nothing
        /// is copied, moved, or rewritten; a selection is just a set of paths. See <see cref="SaveSelection"/>.
        /// </summary>
        public static SaveSelection BuildSelection(ArchivedSave save, ManualLogSource log)
        {
            if (save == null || string.IsNullOrEmpty(save.FilePath) || !File.Exists(save.FilePath))
            {
                log?.LogError("[archive] BuildSelection: the chosen save no longer exists on disk.");
                return null;
            }

            var selection = new SaveSelection
            {
                Offline = save.Offline,
                Target = save.Target,
                Stamp = save.Stamp,
                HostFilePath = save.FilePath,
            };

            if (save.Offline) return selection;

            try
            {
                string archiveDir = OwnSavePaths.ArchiveDir(offline: false);
                var siblings = Directory.Exists(archiveDir)
                    ? ReadAll(archiveDir, offline: false, log).Where(e => e.Target.SameRunAs(save.Target)).ToList()
                    : new List<ArchivedSave>();

                foreach (ArchivedSave e in siblings)
                {
                    if (e.Stamp != save.Stamp || e.OwnerUserId.Length == 0) continue;
                    selection.PlayerFiles[e.OwnerUserId] = e.FilePath;
                }

                // Legacy events only (see MaxLegacySiblingDelta): files don't share an exact
                // stamp, so fall back to the nearest file per userId within a tight window.
                // Never applied to archive-native saves, where "no file with this stamp"
                // means that player genuinely wasn't part of the event.
                if (save.SettingsVersion < ArchiveNativeSettingsVersion)
                {
                    var bestByUser = new Dictionary<string, (string file, TimeSpan delta)>(StringComparer.Ordinal);
                    foreach (ArchivedSave e in siblings)
                    {
                        if (e.OwnerUserId.Length == 0 || selection.PlayerFiles.ContainsKey(e.OwnerUserId)) continue;
                        TimeSpan delta = (e.SortTime - save.SortTime).Duration();
                        if (delta > MaxLegacySiblingDelta) continue;
                        if (!bestByUser.TryGetValue(e.OwnerUserId, out var current) || delta < current.delta)
                            bestByUser[e.OwnerUserId] = (e.FilePath, delta);
                    }
                    foreach (var kv in bestByUser)
                    {
                        selection.PlayerFiles[kv.Key] = kv.Value.file;
                        log.Trace($"[archive] Legacy save: matched userId '{kv.Key}' to a sibling "
                            + $"{kv.Value.delta.TotalSeconds:F1}s away from the chosen checkpoint.");
                    }
                }

                log.Trace($"[archive] Selection for event '{save.Stamp}' ({save.Target}): "
                    + $"host='{Path.GetFileName(save.FilePath)}', {selection.PlayerFiles.Count} player file(s).");
            }
            catch (Exception e)
            {
                log?.LogError($"[archive] BuildSelection failed to resolve siblings: {e}");
            }

            return selection;
        }

        /// <summary>The newest save event for <paramref name="target"/>, as a ready-to-load selection.</summary>
        public static SaveSelection TryGetLatestSelection(bool offline, SaveTarget target, ManualLogSource log)
        {
            List<ArchivedSave> all = List(offline, log);
            // Ordered by resolved SortTime, not raw stamp string, or a hand-renamed file
            // with an unparseable stamp could sort itself to the top.
            ArchivedSave newest = all
                .Where(e => e.Target.SameRunAs(target))
                .OrderByDescending(e => e.SortTime)
                .FirstOrDefault();

            if (newest == null)
            {
                log?.LogWarning($"[archive] No save found for {target} ({(offline ? "offline" : "coop")}).");
                return null;
            }
            return BuildSelection(newest, log);
        }

        /// <summary>
        /// Applies a JSON field patch to one already-written save file, used by
        /// <see cref="BackpackSaveMitigation"/> right after <see cref="OwnSaveCapture"/>
        /// writes it. Addressed by exact path, never searched for, to avoid patching a
        /// stale, unrelated save.
        /// </summary>
        public static bool PatchSaveFile(string path, Action<JObject> patch, ManualLogSource log)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                var json = JObject.Parse(File.ReadAllText(path));
                patch(json);
                File.WriteAllText(path, json.ToString(Formatting.Indented));
                return true;
            }
            catch (Exception e)
            {
                log?.LogError($"[archive] PatchSaveFile failed for '{path}': {e}");
                return false;
            }
        }

        /// <summary>
        /// Permanently deletes one archived save. In co-op that's the whole save event
        /// (host file + every client file sharing its stamp), since a picker row represents
        /// the event, not one file. Refuses starred saves as a defensive backstop.
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
                var paths = new HashSet<string>(StringComparer.Ordinal) { save.FilePath };

                // Exact stamp matches only; unlike BuildSelection's fuzzy legacy fallback,
                // a wrong match here would destroy a neighbouring run's save.
                if (!save.Offline)
                {
                    string archiveDir = OwnSavePaths.ArchiveDir(offline: false);
                    if (Directory.Exists(archiveDir))
                    {
                        foreach (string file in Directory.GetFiles(archiveDir, "peak_save_*.json"))
                        {
                            if (OwnSavePaths.TrySplit(file, out _, out string stamp) && stamp == save.Stamp)
                                paths.Add(file);
                        }
                    }
                }

                foreach (string path in paths)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                log?.LogInfo($"[archive] Deleted save event '{save.Stamp}' ({paths.Count} file(s)).");
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
                Directory.CreateDirectory(OwnSavePaths.ArchiveRoot);
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
            if (!string.IsNullOrEmpty(official))
                return t.IsCustom ? TruncateCustomLabel(official) : official;

            // Fallback if the game's own AscentData couldn't be reached; our own translations.
            if (t.IsCustom) return TruncateCustomLabel(SavePickerLocalization.Get(PickerText.CustomRun));
            switch (t.Ascent)
            {
                case -1: return SavePickerLocalization.Get(PickerText.Tenderfoot);
                case 0: return "PEAK";
                default: return string.Format(SavePickerLocalization.Get(PickerText.AscentFormat), t.Ascent);
            }
        }

        // "Custom" run labels are by far the longest difficulty string in some languages
        // (e.g. Ukrainian, Polish) and would blow up the picker's shared column width.
        private const int MaxCustomLabelLength = 12;

        private static string TruncateCustomLabel(string label)
        {
            if (string.IsNullOrEmpty(label) || label.Length <= MaxCustomLabelLength) return label;
            return label.Substring(0, MaxCustomLabelLength).TrimEnd() + "…";
        }

        // Reuses the game's own localized difficulty names for exact wording. Same indexing
        // AscentUI uses: ascents[0] = custom run, ascents[ascent + 2] = normal difficulty.
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

        /// <summary>
        /// Human, localized label for a save's deepest-reached campfire/segment. Falls back
        /// to the raw stored name if the game's localization table can't be reached.
        /// </summary>
        public static string CampfireLabel(string internalName)
        {
            string official = TryGetOfficialCampfireTitle(internalName);
            return !string.IsNullOrEmpty(official) ? official : internalName;
        }

        // CampfireName is normally a Segment name (Beach, Tropics, Alpine, Caldera, TheKiln,
        // Peak), except OwnSaveCapture overrides it to "Roots"/"Mesa" for the Tropics/Alpine
        // cave variants, so this table covers both enums' literal names. "Volcano" is also
        // mapped (some save sources store the plain BiomeType name) to the same target as
        // TheKiln, its later progress label.
        private static readonly Dictionary<string, string> CampfireLocKeys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Beach", "SHORE" },
            { "Tropics", "TROPICS" },
            { "Roots", "ROOTS" }, // Tropics cave variant (override case)
            { "Alpine", "ALPINE" },
            { "Mesa", "MESA" }, // Alpine cave variant (override case)
            { "Caldera", "CALDERA" },
            { "TheKiln", "THE KILN" },
            { "Volcano", "THE KILN" }, // alias, see comment above
            { "Peak", "PEAK" },
            // Nadir is the one area whose progress-point title is not itself a localization
            // key: the table has no "NADIR" row, it stores the area under "AREA_VOID"
            // (English "NADIR", e.g. Polish "OTCHŁAŃ"). AreaNameCompat writes "NADIR" into
            // campfireName because that is the progress point's title, so the mapping has to
            // happen here or the raw-key fallback below misses and the picker shows the
            // untranslated internal name.
            { "NADIR", "AREA_VOID" },
        };

        // Saves written since AreaNameCompat store the progress-point title directly as the
        // localization key, so an unmapped name is tried as a key before giving up. Gated on
        // the key actually existing in the table, since GetText logs an error on a miss even
        // with printDebug false.
        private static string TryGetOfficialCampfireTitle(string internalName)
        {
            try
            {
                if (string.IsNullOrEmpty(internalName)) return null;

                if (!CampfireLocKeys.TryGetValue(internalName, out string key))
                {
                    // GetText upper-cases the id before lookup, so match that here
                    key = internalName.ToUpperInvariant();
                    var table = LocalizedText.mainTable;
                    if (table == null || !table.ContainsKey(key)) return null;
                }

                string text = LocalizedText.GetText(key, printDebug: false);
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch { return null; }
        }

        // Best-effort read of the display fields from the save JSON
        private static void ReadMetadata(ArchivedSave entry, ManualLogSource log)
        {
            try
            {
                string json = File.ReadAllText(entry.FilePath);
                SaveMeta m = JsonConvert.DeserializeObject<SaveMeta>(json);
                if (m == null) return;
                entry.SettingsVersion = m.settingsVersion;
                entry.SceneName = m.sceneName ?? "";
                entry.SaveDate = m.saveDate ?? "";
                entry.CampfireName = m.campfireName ?? "";
                entry.Playtime = m.timePlayed;
                if (m.biome_names != null && m.biome_names.Count > 0)
                    entry.BiomesSummary = m.biome_names[m.biome_names.Count - 1]; // deepest biome reached
                // Space is tight in the save picker, so show a player count instead of names.
                if (m.playerNames != null && m.playerNames.Count > 0)
                    entry.Players = $"{m.playerNames.Count}P";
                entry.GameVersion = m.gameVersion ?? "";
            }
            catch (Exception e)
            {
                log?.LogWarning($"[archive] Could not read metadata for '{Path.GetFileName(entry.FilePath)}': {e.Message}");
            }
        }

        // Subset of OwnSaveData we display. Newtonsoft ignores the rest
        private class SaveMeta
        {
            public int settingsVersion;
            public string sceneName;
            public string saveDate;
            public string campfireName;
            public float timePlayed;
            public List<string> biome_names;
            public List<string> playerNames;
            public string gameVersion;
        }
    }
}
