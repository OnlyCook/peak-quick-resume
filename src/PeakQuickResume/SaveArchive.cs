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
    /// is always the HOST's file for a save event - the one carrying the level/world
    /// state (see <see cref="SaveArchive.List"/>); the connected clients' own files from
    /// that same event are its siblings, resolved into a <see cref="SaveSelection"/> at
    /// load time
    /// </summary>
    public class ArchivedSave
    {
        public string FilePath; // full path to the .json in our store
        public bool Offline; // category: offline vs coop
        public SaveTarget Target; // difficulty / custom-run this save belongs to
        public string Stamp; // the save event's identity, from the filename (see OwnSavePaths)
        public string OwnerUserId = ""; // whose file this is (co-op only; "" offline)
        public DateTime SortTime; // parsed from Stamp
        public bool Starred; // pinned to the top of the F7 picker; can't be deleted while true

        // Display metadata (read from the save's JSON; best-effort)
        public string SaveDate = "";
        public string CampfireName = "";
        public float Playtime;
        public string BiomesSummary = "";
        // Everyone who played this run (co-op). Player names are stored alphabetically,
        // NOT host-first, so we show the whole list.
        public string Players = "";

        // The scene this save's level state belongs to. Only ever written into the file
        // that carries world state (the host's, or the single offline file) - a co-op
        // CLIENT's file has none, which is what lets List() tell the two apart even when
        // the local Photon userId isn't resolvable
        public string SceneName = "";

        // Save-format version. >= SaveArchive.ArchiveNativeSettingsVersion means this
        // event was written straight into the archive with a shared per-event stamp, so
        // its siblings can be matched exactly; below that it's a legacy save copied in
        // from the old canonical-file layout, whose siblings need the fuzzy timestamp
        // match instead (see SaveSelection.Build)
        public int SettingsVersion;

        // Game version this save was written under (e.g. "1.65.a"), or "" if it
        // predates that field entirely (see SavePicker's use of GameVersionCompat.NoVersionDisplay
        // for how that's shown).
        public string GameVersion = "";

        public string DifficultyLabel => SaveArchive.DifficultyLabel(Target);

        /// <summary>
        /// True if this save was written under an older game version than the one
        /// currently running - the map pool was very likely rotated since, so it may
        /// load the wrong island (see GameVersionCompat, SavePicker's use of this for
        /// the "vX.Y.z instead of playtime" row indicator). A missing GameVersion counts
        /// as stale too (GameVersionCompat.IsOlderThan treats "" that way) - we just don't
        /// know which version it was, so best to flag it rather than assume it's current.
        /// </summary>
        public bool IsStaleVersion => GameVersionCompat.IsOlderThan(GameVersion, GameVersionCompat.Current);
    }

    /// <summary>
    /// Everything one load needs to know about which files to read, resolved ONCE up
    /// front by <see cref="SaveArchive.BuildSelection"/> and threaded through the whole
    /// load path (see <see cref="OwnLoadEntryPoints.TryLoadPlayer"/>)
    ///
    /// The split this type encodes is the whole point of it, and is deliberately
    /// enforced by having exactly one path for each half:
    ///  - <see cref="HostFilePath"/> is the ONLY file the level/world restore ever
    ///    reads: which island and segment to load, where to teleport to, time of day,
    ///    day counter, ground items, luggage, the ancient statue, deployables
    ///  - <see cref="TryGetPlayerFile"/> gives each connected player THEIR OWN file, the
    ///    only place per-player state is ever read from: inventory, backpack, held item,
    ///    afflictions, extra stamina, skeleton flag, thorns, ticks, achievement progress
    ///
    /// A player with no file in this save event simply isn't in the map, and every
    /// per-player restore step skips them - their current in-game state is left exactly
    /// as it is rather than being overwritten from some other run's file
    /// </summary>
    public class SaveSelection
    {
        public bool Offline;
        public SaveTarget Target;
        public string Stamp = "";
        public string HostFilePath = "";

        // userId -> that player's own file within this save event. Empty offline (the
        // single file is both host and player file, see TryGetPlayerFile)
        public readonly Dictionary<string, string> PlayerFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// The file to read <paramref name="userId"/>'s own per-player state from.
        /// Offline always resolves to the single save file; co-op resolves only to a
        /// file from THIS save event, never a near-miss from another one
        /// </summary>
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
    /// living in <c>BepInEx/plugins/QuickResume/Archive</c> split by category into
    /// <c>Offline</c> and <c>Coop</c>
    ///
    /// This is the ONE store - there is no separate "canonical" current
    /// save file, and dominik0207's "PEAK Checkpoint Save" folder is never read, written
    /// or migrated from (see <see cref="OwnSavePaths"/> for the full reasoning).
    /// <see cref="OwnSaveCapture"/> writes save events directly in here, and loading a
    /// checkpoint just reads the files back - nothing is ever copied over anything else,
    /// which is what makes a hand-edited save safe: its contents are read as-is, and its
    /// identity comes from its filename, not its modification time
    ///
    /// Filename: <c>{stem}__{stamp}.json</c>, e.g.
    /// <c>peak_save_2_offline__20260702_140311_204.json</c>. The stem carries the
    /// difficulty/custom + category (+ the owning userId in co-op); the stamp identifies
    /// the save EVENT and is shared by every player's file from that same autosave
    /// </summary>
    public static class SaveArchive
    {
        /// <summary>
        /// <c>settingsVersion</c> written by the archive-native save path. Saves at or
        /// above this were written with a shared per-event stamp, so their co-op siblings
        /// match exactly; anything below is a legacy save copied in from the old
        /// canonical-file layout, where each player's file got its own write-time stamp a
        /// few milliseconds apart and siblings can only be matched fuzzily
        /// </summary>
        public const int ArchiveNativeSettingsVersion = 7;

        private static bool _migrated;

        // Starred saves, persisted as a flat JSON array of archive filenames (unique
        // across both categories: offline stems always end "_offline", coop stems
        // never do). One shared file rather than one per category, there's no
        // per-category state here worth splitting. Loaded lazily, cached in memory for
        // the rest of the session, written back to disk on every change
        private static string StarredFile => Path.Combine(OwnSavePaths.ArchiveRoot, "starred.json");
        private static HashSet<string> _starredCache;

        /// <summary>
        /// How far apart two LEGACY files may be and still count as the same save event.
        /// Only ever consulted for saves below <see cref="ArchiveNativeSettingsVersion"/>
        /// (see <see cref="BuildSelection"/>): those were written one-file-at-a-time into
        /// the old canonical layout and copied in here with each file's own write time,
        /// so a co-op event's files land a few milliseconds - not zero - apart. The window
        /// is wide enough to absorb that jitter and far too narrow to ever span two
        /// different runs. Archive-native saves need none of this: every file in an event
        /// carries the same stamp by construction
        /// </summary>
        private static readonly TimeSpan MaxLegacySiblingDelta = TimeSpan.FromMinutes(2);

        /// <summary>
        /// All archived saves for the given category (offline vs coop), newest first -
        /// exactly ONE row per save event
        ///
        /// In co-op an event has one file per player, but only the host's carries the
        /// level/world state a load actually resumes from, so that's the one shown (and
        /// the one <see cref="BuildSelection"/> resolves siblings around). Earlier
        /// versions matched the host's file against the live Photon userId and fell back
        /// to listing EVERY player's file when that wasn't resolvable (browsing outside a
        /// room), which showed the same checkpoint several times over and let a client's
        /// file be loaded as if it were the host's. Grouping by event stamp instead makes
        /// one row per event structural rather than something we have to get right
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

                // Keep only host files, then collapse by event so one event can never
                // contribute two rows even if the filter above lets a pair through (a
                // legacy event browsed outside a room, say)
                //
                // Keyed by run target AND stamp, not the stamp alone: two LEGACY files are
                // only siblings if they belong to the same run, and their stamps are
                // independent file write times rather than a shared event id. Two
                // different ascents saved in the same millisecond is unlikely but not
                // impossible, and keying on the stamp alone would silently hide one of
                // them. Archive-native events share both fields, so this splits nothing
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

        // Does this file carry the level/world half of its save event - i.e. is it the
        // one a load actually resumes from, and therefore the one the picker should show?
        //
        //  - Offline: there's only ever one file per event, and it's both halves at once
        //  - Archive-native co-op: a client's file has no world state in it AT ALL (see
        //    OwnSaveCapture's field split), so the presence of a scene name answers this
        //    structurally, with no guessing and no dependence on live Photon state. This
        //    is what makes browsing correct even outside a room, where earlier versions
        //    gave up and listed every player's file as a separate row for the same
        //    checkpoint
        //  - Legacy co-op: every player's file has a full copy of the world state, so the
        //    only available signal is whether it's ours (we only ever write saves as the
        //    host, so our own file IS the host's). With no local userId to compare
        //    against, fall back to accepting them all, exactly as earlier versions did -
        //    there is genuinely nothing in those files to tell them apart
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

                // A stamp we can't parse as a timestamp is still a perfectly good event
                // IDENTITY (sibling matching only ever compares stamps for equality) - it
                // just can't order the list on its own, so fall back to the file's own
                // write time for that, exactly as earlier versions did. This is what keeps
                // a hand-renamed file (a "__before-edit" backup, say) visible and loadable
                // instead of silently vanishing from the picker
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
        /// Resolve one archived save into the full set of files a load should read: the
        /// host's file for level/world state, plus each connected player's own file for
        /// their own state. See <see cref="SaveSelection"/> for why that split matters
        ///
        /// Nothing is copied, moved, or rewritten here - a selection is just a set of
        /// paths. Loading an older checkpoint therefore leaves every file in the store
        /// byte-identical, which is what stops a load from re-stamping files and
        /// duplicating rows in the picker
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

                // Legacy events only (see MaxLegacySiblingDelta): those were copied in from
                // the old canonical layout with per-file write times, so an event's files
                // don't share a stamp and the exact match above finds only the host's own.
                // Fall back to the nearest file per userId within a tight window, exactly
                // like earlier versions did. Never applied to archive-native saves: there,
                // "no file with this stamp" genuinely means that player wasn't part of the
                // event, and pulling in their nearest OTHER save is the precise mistake
                // this rewrite exists to remove
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
                        log?.LogInfo($"[archive] Legacy save: matched userId '{kv.Key}' to a sibling "
                            + $"{kv.Value.delta.TotalSeconds:F1}s away from the chosen checkpoint.");
                    }
                }

                log?.LogInfo($"[archive] Selection for event '{save.Stamp}' ({save.Target}): "
                    + $"host='{Path.GetFileName(save.FilePath)}', {selection.PlayerFiles.Count} player file(s).");
            }
            catch (Exception e)
            {
                log?.LogError($"[archive] BuildSelection failed to resolve siblings: {e}");
            }

            return selection;
        }

        /// <summary>
        /// The newest save event for <paramref name="target"/>, as a ready-to-load
        /// selection. This is what a plain "continue" resume uses - with no canonical
        /// current-save file anymore, "the current save" simply IS the most recent event
        /// in the store for that run
        /// </summary>
        public static SaveSelection TryGetLatestSelection(bool offline, SaveTarget target, ManualLogSource log)
        {
            List<ArchivedSave> all = List(offline, log);
            // Ordered by resolved time, not by raw stamp string: a stamp that isn't a
            // parseable timestamp falls back to the file's write time (see ReadAll), and
            // ordinal-comparing those two kinds of stamp against each other would let a
            // hand-renamed file sort itself to the top and be picked as "latest"
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
        /// writes it - see that class for why the patch has to land at this exact point.
        /// The exact file is addressed by path (run target + owning userId + save event),
        /// never searched for: an earlier version scanned a folder and patched whichever
        /// file came back first, which could silently write the restore into a stale,
        /// unrelated save
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
        /// Permanently delete one archived save. In co-op that means the WHOLE save
        /// event - the host's file and every client file sharing its stamp - since a row
        /// in the picker represents the event, not one file, and leaving the clients'
        /// files behind would strand them with no host file to ever be loaded alongside.
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
                var paths = new HashSet<string>(StringComparer.Ordinal) { save.FilePath };

                // EXACT stamp matches only - deliberately not BuildSelection, whose legacy
                // fallback also accepts files merely CLOSE in time. That's a reasonable
                // guess when deciding what to read; it is not one to make when deleting,
                // where a wrong match destroys a neighbouring run's save outright
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

            // Fallback if the game's own AscentData couldn't be reached (e.g. a future
            // update changes its shape); our own translations, better than nothing
            if (t.IsCustom) return TruncateCustomLabel(SavePickerLocalization.Get(PickerText.CustomRun));
            switch (t.Ascent)
            {
                case -1: return SavePickerLocalization.Get(PickerText.Tenderfoot);
                case 0: return "PEAK";
                default: return string.Format(SavePickerLocalization.Get(PickerText.AscentFormat), t.Ascent);
            }
        }

        // The "Custom" run's OWN (official or our fallback) label is by far the longest
        // difficulty string in a couple of languages - Ukrainian "КОРИСТУВАЦЬКИЙ ЗАБІГ"
        // and Polish "SPERSONALIZOWANE PODEJŚCIE" both run well past every other
        // difficulty label (PEAK/Tenderfoot/Ascent N are short everywhere). Since the
        // difficulty column's reserved width is the max across EVERY archived save, one
        // long custom-run label blows up the row layout for every OTHER row too, not
        // just its own. Custom runs are rare, so trading a clipped/ellipsized label on
        // them (only them, only when actually this long) for a sane column width
        // everywhere else is the right trade
        private const int MaxCustomLabelLength = 12;

        private static string TruncateCustomLabel(string label)
        {
            if (string.IsNullOrEmpty(label) || label.Length <= MaxCustomLabelLength) return label;
            return label.Substring(0, MaxCustomLabelLength).TrimEnd() + "…";
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

        /// <summary>Human, localized label for a save's deepest-reached campfire/segment
        /// (see <see cref="ArchivedSave.CampfireName"/> - NOT BiomesSummary, which is the
        /// level's whole fixed biome roster baked in at edit time, not player progress,
        /// see the comment on CampfireLocKeys below). Falls back to the raw stored name
        /// (English, as OwnSaveCapture wrote it) if the game's own localization table
        /// can't be reached, better than nothing</summary>
        public static string CampfireLabel(string internalName)
        {
            string official = TryGetOfficialCampfireTitle(internalName);
            return !string.IsNullOrEmpty(official) ? official : internalName;
        }

        // Same reasoning as TryGetOfficialAscentTitle: the raw name saved to disk is an
        // internal English dev enum name, not what players ever see on screen. The game
        // shows these via the "big label" on climb progress (MountainProgressHandler.
        // progressPoints), which are plain LocalizedText.GetText(key) lookups, so we can
        // hit that same table directly by key without needing the scene-attached
        // MountainProgressHandler singleton itself.
        //
        // CampfireName is `MapHandler.GetCurrentSegment().ToString()` (Segment: Beach,
        // Tropics, Alpine, Caldera, TheKiln, Peak), NOT a Biome.BiomeType name - except
        // for two special cases (OwnSaveCapture overrides it to the BiomeType name
        // "Roots"/"Mesa" for the Tropics/Alpine cave variants), so this table needs to
        // cover both enums' literal names, keyed by whichever one CampfireName actually
        // ends up holding. "Volcano" is also mapped here (even though it's not a Segment
        // name and isn't written by the current OwnSaveCapture code above) since some
        // older/other save sources do store the plain Biome.BiomeType name "Volcano" as
        // campfireName - same target as TheKiln, since a saved checkpoint is always the
        // DEEPEST point reached, and "The Kiln" is that biome's upper/later progress label
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
        };

        private static string TryGetOfficialCampfireTitle(string internalName)
        {
            try
            {
                if (string.IsNullOrEmpty(internalName)) return null;
                if (!CampfireLocKeys.TryGetValue(internalName, out string key)) return null;
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
