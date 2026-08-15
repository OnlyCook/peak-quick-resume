using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Newtonsoft.Json;
using Peak.Network;
using Photon.Pun;
using Zorro.Core;

namespace PEAKQuickResume
{
    /// <summary>
    /// Saves/restores this run's in-progress achievement tracking, which
    /// InitRunBasedValues() otherwise resets to empty on every load. Also restores
    /// MaxHeightReached before a teleport so it isn't miscredited as a climb (see
    /// RestoreAllPlayers). Reflects into SerializableRunBasedValues' internal fields;
    /// every entry point fails soft so a reflection error can never corrupt a save.
    /// </summary>
    public static class AchievementProgressIO
    {
        private static readonly Type NativeType = typeof(SerializableRunBasedValues);
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly FieldInfo FRunBasedInts = NativeType.GetField("runBasedInts", Flags);
        private static readonly FieldInfo FRunBasedFloats = NativeType.GetField("runBasedFloats", Flags);
        private static readonly FieldInfo FFruits = NativeType.GetField("runBasedFruitsEaten", Flags);
        private static readonly FieldInfo FShroomBerries = NativeType.GetField("shroomBerriesEaten", Flags);
        private static readonly FieldInfo FNonToxicMushrooms = NativeType.GetField("nonToxicMushroomsEaten", Flags);
        private static readonly FieldInfo FGourmand = NativeType.GetField("gourmandRequirementsEaten", Flags);
        private static readonly FieldInfo FEarnedThisRun = NativeType.GetField("achievementsEarnedThisRun", Flags);
        private static readonly FieldInfo FCompletedAscents = NativeType.GetField("completedAscentsThisRun", Flags);

        private static bool AnyFieldMissing() =>
            FRunBasedInts == null || FRunBasedFloats == null || FFruits == null || FShroomBerries == null
            || FNonToxicMushrooms == null || FGourmand == null || FEarnedThisRun == null || FCompletedAscents == null;

        /// <summary>Read-only counts for the "eat N different X" trackers, used by <see cref="AchievementDebugLogging"/>.</summary>
        public static (int fruits, int shroomBerries, int nonToxicMushrooms, int gourmand) GetEatenCounts()
        {
            try
            {
                if (Singleton<AchievementManager>.Instance == null || AnyFieldMissing()) return (0, 0, 0, 0);
                object boxedNative = Singleton<AchievementManager>.Instance.runBasedValueData;
                int fruits = (FFruits.GetValue(boxedNative) as List<ushort>)?.Count ?? 0;
                int shroomBerries = (FShroomBerries.GetValue(boxedNative) as List<ushort>)?.Count ?? 0;
                int nonToxicMushrooms = (FNonToxicMushrooms.GetValue(boxedNative) as List<ushort>)?.Count ?? 0;
                int gourmand = (FGourmand.GetValue(boxedNative) as List<ushort>)?.Count ?? 0;
                return (fruits, shroomBerries, nonToxicMushrooms, gourmand);
            }
            catch { return (0, 0, 0, 0); }
        }

        /// <summary>Reads the LOCAL client's own current achievement progress (used for the local player in both offline and coop saves)</summary>
        public static OwnSavedAchievementProgress CaptureLocal(ManualLogSource log)
        {
            try
            {
                if (Singleton<AchievementManager>.Instance == null) return null;
                object boxedNative = Singleton<AchievementManager>.Instance.runBasedValueData;
                return ToSaved(boxedNative, log);
            }
            catch (Exception e)
            {
                log?.LogWarning($"AchievementProgressIO.CaptureLocal failed (non-fatal): {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Converts a boxed native SerializableRunBasedValues into our JSON-friendly snapshot.
        /// Used by <see cref="CaptureLocal"/> and, in coop, for remote players via
        /// ReconnectHandler.TryGetReconnectData (see OwnSaveCapture.SavePlayerCoop).
        /// </summary>
        public static OwnSavedAchievementProgress ToSaved(object boxedNative, ManualLogSource log)
        {
            try
            {
                if (boxedNative == null || AnyFieldMissing()) return null;
                var result = new OwnSavedAchievementProgress();

                if (FRunBasedInts.GetValue(boxedNative) is Dictionary<RUNBASEDVALUETYPE, int> ints)
                    foreach (var kv in ints) result.runBasedInts[(int)kv.Key] = kv.Value;

                if (FRunBasedFloats.GetValue(boxedNative) is Dictionary<RUNBASEDVALUETYPE, float> floats)
                    foreach (var kv in floats) result.runBasedFloats[(int)kv.Key] = kv.Value;

                if (FFruits.GetValue(boxedNative) is List<ushort> fruits) result.runBasedFruitsEaten = new List<ushort>(fruits);
                if (FShroomBerries.GetValue(boxedNative) is List<ushort> shrooms) result.shroomBerriesEaten = new List<ushort>(shrooms);
                if (FNonToxicMushrooms.GetValue(boxedNative) is List<ushort> mush) result.nonToxicMushroomsEaten = new List<ushort>(mush);
                if (FGourmand.GetValue(boxedNative) is List<ushort> gourmand) result.gourmandRequirementsEaten = new List<ushort>(gourmand);

                if (FEarnedThisRun.GetValue(boxedNative) is List<ACHIEVEMENTTYPE> earned)
                    result.achievementsEarnedThisRun = earned.Select(a => (int)a).ToList();

                if (FCompletedAscents.GetValue(boxedNative) is List<int> ascents) result.completedAscentsThisRun = new List<int>(ascents);

                return result;
            }
            catch (Exception e)
            {
                log?.LogWarning($"AchievementProgressIO.ToSaved failed (non-fatal): {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Applies a saved snapshot to the LOCAL client's AchievementManager. Safe with
        /// saved == null (just re-primes a fresh baseline). Deliberately does not restore
        /// steamAchievementsPreviouslyUnlocked - ConstructNew() always rebuilds that from
        /// the client's actual current Steam state.
        /// </summary>
        public static void ApplyLocal(OwnSavedAchievementProgress saved, ManualLogSource log)
        {
            try
            {
                if (Singleton<AchievementManager>.Instance == null) return;
                object boxedNative = SerializableRunBasedValues.ConstructNew();

                if (saved != null && !AnyFieldMissing())
                {
                    // MaxHeightReached is excluded here; it's instead seeded from the player's
                    // live altitude after load (see HeightAchievementGuard) to avoid
                    // double-crediting the permanent HeightClimbed Steam stat.
                    var ints = new Dictionary<RUNBASEDVALUETYPE, int>();
                    if (saved.runBasedInts != null)
                        foreach (var kv in saved.runBasedInts)
                        {
                            if ((RUNBASEDVALUETYPE)kv.Key == RUNBASEDVALUETYPE.MaxHeightReached) continue;
                            ints[(RUNBASEDVALUETYPE)kv.Key] = kv.Value;
                        }
                    FRunBasedInts.SetValue(boxedNative, ints);

                    var floats = new Dictionary<RUNBASEDVALUETYPE, float>();
                    if (saved.runBasedFloats != null)
                        foreach (var kv in saved.runBasedFloats) floats[(RUNBASEDVALUETYPE)kv.Key] = kv.Value;
                    FRunBasedFloats.SetValue(boxedNative, floats);

                    FFruits.SetValue(boxedNative, new List<ushort>(saved.runBasedFruitsEaten ?? new List<ushort>()));
                    FShroomBerries.SetValue(boxedNative, new List<ushort>(saved.shroomBerriesEaten ?? new List<ushort>()));
                    FNonToxicMushrooms.SetValue(boxedNative, new List<ushort>(saved.nonToxicMushroomsEaten ?? new List<ushort>()));
                    FGourmand.SetValue(boxedNative, new List<ushort>(saved.gourmandRequirementsEaten ?? new List<ushort>()));

                    var earned = (saved.achievementsEarnedThisRun ?? new List<int>()).Select(i => (ACHIEVEMENTTYPE)i).ToList();
                    FEarnedThisRun.SetValue(boxedNative, earned);

                    FCompletedAscents.SetValue(boxedNative, new List<int>(saved.completedAscentsThisRun ?? new List<int>()));
                }

                Singleton<AchievementManager>.Instance.InitRunBasedValues((SerializableRunBasedValues)boxedNative);

                // InitRunBasedValues writes runBasedValueData directly, bypassing the setters
                // that normally push updates into the host's reconnect cache. Without this
                // manual trigger, the host's cached progress for this player goes stale and
                // gets silently re-baked into the next save, compounding drift across loads.
                Player.localPlayer?.OnAchievementProgressChanged();

                if (saved != null)
                {
                    log.Trace("[achievement-debug] Restored this run's achievement progress from save:\n" + FormatDump(saved));
                }
                else
                {
                    log.Trace("[achievement-debug] No saved achievement progress found - primed a fresh baseline (matches vanilla's own fresh-run behavior).");
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"AchievementProgressIO.ApplyLocal failed (non-fatal): {e.Message}");
            }
        }

        private static string FormatDump(OwnSavedAchievementProgress saved)
        {
            string ints = saved.runBasedInts != null && saved.runBasedInts.Count > 0
                ? string.Join(", ", saved.runBasedInts.Select(kv => $"{(RUNBASEDVALUETYPE)kv.Key}={kv.Value}"))
                : "(none)";
            string floats = saved.runBasedFloats != null && saved.runBasedFloats.Count > 0
                ? string.Join(", ", saved.runBasedFloats.Select(kv => $"{(RUNBASEDVALUETYPE)kv.Key}={kv.Value}"))
                : "(none)";
            return $"  ints: {ints}\n"
                + $"  floats: {floats}\n"
                + $"  fruitsEaten={saved.runBasedFruitsEaten?.Count ?? 0}, shroomBerriesEaten={saved.shroomBerriesEaten?.Count ?? 0}, "
                + $"nonToxicMushroomsEaten={saved.nonToxicMushroomsEaten?.Count ?? 0}, gourmandRequirementsEaten={saved.gourmandRequirementsEaten?.Count ?? 0}\n"
                + $"  completedAscentsThisRun=[{string.Join(",", saved.completedAscentsThisRun ?? new List<int>())}]";
        }

        /// <summary>Logs a tagged snapshot of the local client's current achievement progress, for verifying save/restore correctness.</summary>
        public static void LogSnapshot(string tag, ManualLogSource log)
        {
            try
            {
                OwnSavedAchievementProgress saved = CaptureLocal(log);
                if (saved == null)
                {
                    log.Trace($"[achievement-debug] SNAPSHOT[{tag}]: AchievementManager not available, nothing to snapshot.");
                    return;
                }
                string who = Character.localCharacter != null ? Character.localCharacter.characterName : "(unknown)";
                log.Trace($"[achievement-debug] SNAPSHOT[{tag}] for {who}:\n" + FormatDump(saved));
            }
            catch (Exception e)
            {
                log?.LogWarning($"AchievementProgressIO.LogSnapshot({tag}) failed (non-fatal): {e.Message}");
            }
        }

        public static string ToJson(OwnSavedAchievementProgress saved)
        {
            try { return saved == null ? null : JsonConvert.SerializeObject(saved); }
            catch { return null; }
        }

        public static OwnSavedAchievementProgress FromJson(string json, ManualLogSource log)
        {
            try { return string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<OwnSavedAchievementProgress>(json); }
            catch (Exception e)
            {
                log?.LogWarning($"AchievementProgressIO.FromJson failed (non-fatal): {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Host-only: called at the start of OwnTeleportSequence.RunSequence, before any
        /// warp. AchievementManager is a client-local singleton, so the host applies
        /// itself directly and RPCs every other player's restore to their own machine,
        /// same pattern as OwnInventoryRestore.RestoreAll. A player with no file in this
        /// save event just gets a fresh baseline.
        /// </summary>
        /// <param name="alreadyRestored">
        /// Set of userIds already restored by an earlier pass; players in here are
        /// skipped so a retry (see OwnTeleportSequence) doesn't roll back progress already applied.
        /// </param>
        public static void RestoreAllPlayers(SaveSelection selection, OwnLoadEntryPoints entryPoints, ManualLogSource log,
            HashSet<string> alreadyRestored = null)
        {
            try
            {
                bool offline = selection.Offline;

                Player[] players = UnityEngine.Object.FindObjectsByType<Player>(UnityEngine.FindObjectsSortMode.None);
                log.Trace($"[achievement-debug] RestoreAllPlayers: {players.Length} player object(s) found.");

                foreach (Player player in players)
                {
                    if (player == null) continue;

                    // Deliberately not gated on player.character: remote players are mid-respawn
                    // (character not yet assigned) during a load, and nothing below needs it.
                    string userId = offline ? "" : NetworkingUtilities.GetUserId(player);
                    if (alreadyRestored != null && alreadyRestored.Contains(userId)) continue;
                    PhotonView playerView = player.GetComponent<PhotonView>();

                    OwnSavedAchievementProgress saved = null;
                    if (selection.TryGetPlayerFile(userId, out string path) && File.Exists(path))
                    {
                        try
                        {
                            var data = JsonConvert.DeserializeObject<OwnSaveData>(File.ReadAllText(path));
                            saved = data?.achievementProgress;
                        }
                        catch (Exception e)
                        {
                            log?.LogWarning($"AchievementProgressIO.RestoreAllPlayers: could not read save for userId '{userId}': {e.Message}");
                        }
                    }
                    else
                    {
                        log.Trace($"AchievementProgressIO: skipping restore for '{userId}' - no save file in this "
                            + "checkpoint's save event; priming a fresh baseline instead.");
                    }

                    if (offline || (playerView != null && playerView.IsMine))
                    {
                        log.Trace($"[achievement-debug] RestoreAllPlayers: '{userId}' is this machine - applying locally.");
                        ApplyLocal(saved, log);
                        alreadyRestored?.Add(userId);
                    }
                    else if (PhotonNetwork.IsMasterClient && playerView != null)
                    {
                        entryPoints?.Network?.RestoreAchievementProgressFor(playerView, userId, ToJson(saved));
                        alreadyRestored?.Add(userId);
                    }
                    else
                    {
                        log.Trace($"[achievement-debug] RestoreAllPlayers: '{userId}' NOT restored - "
                            + $"isMasterClient={PhotonNetwork.IsMasterClient}, playerView={(playerView == null ? "null" : "present")}.");
                    }
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"AchievementProgressIO.RestoreAllPlayers failed (non-fatal): {e.Message}");
            }
        }
    }
}
