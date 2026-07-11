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
    /// Native save/restore for this run's in-progress achievement tracking (own
    /// addition, no decompile counterpart - the checkpoint mod never touched
    /// achievements at all).
    ///
    /// The game tracks a handful of achievements (Plunderer, First Aid, Clutch, Knot
    /// Tying, Foraging, Mycology, Advanced Mycology, Gourmand, and every "without ever
    /// X" badge) purely per-run, in <c>AchievementManager.runBasedValueData</c>
    /// (typed <c>SerializableRunBasedValues</c>, decompile ~43602) - reset to an empty
    /// baseline every time <c>AchievementManager.InitRunBasedValues()</c> runs with no
    /// argument, which is exactly what happens at the start of the fresh run our own
    /// resume flow always starts before loading a checkpoint into it. Without this,
    /// every one of those achievements silently loses all progress on every load.
    ///
    /// It ALSO fixes a subtler problem: <c>AchievementManager.RecordMaxHeight</c>
    /// (decompile ~15214) only credits the permanent HeightClimbed Steam stat (High
    /// Altitude Badge) for altitude above <c>RUNBASEDVALUETYPE.MaxHeightReached</c>'s
    /// current run-based value. A teleport is an instant jump, not a climb - but if
    /// that tracker is still sitting at its fresh-run default (0) the moment the jump
    /// happens, the game can't tell the difference and credits the whole jump as
    /// climbed. Restoring the real value BEFORE the teleport happens (see
    /// <see cref="RestoreAllPlayers"/>'s call site in OwnTeleportSequence) closes that
    /// gap entirely - no need to pause or block achievement tracking during the
    /// teleport itself.
    ///
    /// <c>SerializableRunBasedValues</c>' own fields are all <c>internal</c>, so this
    /// class reflects them in/out of our own JSON-friendly <see cref="OwnSavedAchievementProgress"/>.
    /// Every public entry point here is wrapped so a reflection/Steam-stats failure
    /// just skips restoring achievement progress (silently falls back to vanilla's own
    /// "loses this run's counters" behavior) - it must never be able to corrupt a save
    /// or leave AchievementManager in a broken state
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

        /// <summary>
        /// Read-only counts for the "eat N different X" trackers (Foraging/Advanced
        /// Mycology/Mycology/Gourmand), for <see cref="AchievementDebugLogging"/> - lets
        /// these be verified from the log after loading a save without needing Steam
        /// Achievement Manager or actually reaching the threshold
        /// </summary>
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
        /// Converts a boxed native <c>SerializableRunBasedValues</c> into our own
        /// JSON-friendly snapshot. Used both for <see cref="CaptureLocal"/>'s own read
        /// and, in coop, for a REMOTE player's data fetched via
        /// <c>ReconnectHandler.TryGetReconnectData</c> (see OwnSaveCapture.SavePlayerCoop) -
        /// the game's own native mechanism for keeping a live host-side copy of every
        /// connected player's achievement progress, already required for its own
        /// disconnect/reconnect support
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
        /// Applies a saved snapshot to the LOCAL client's AchievementManager. Safe to
        /// call with <paramref name="saved"/> == null (just re-primes a correct fresh
        /// baseline - the same thing the native no-argument InitRunBasedValues() would
        /// have done anyway). Deliberately does NOT restore
        /// <c>steamAchievementsPreviouslyUnlocked</c> from the save - that list is
        /// always rebuilt by <c>SerializableRunBasedValues.ConstructNew()</c> from this
        /// client's actual CURRENT Steam achievement state, so an achievement earned by
        /// other means between saving and loading is never miscounted as "not yet had it"
        /// </summary>
        public static void ApplyLocal(OwnSavedAchievementProgress saved, ManualLogSource log)
        {
            try
            {
                if (Singleton<AchievementManager>.Instance == null) return;
                object boxedNative = SerializableRunBasedValues.ConstructNew();

                if (saved != null && !AnyFieldMissing())
                {
                    var ints = new Dictionary<RUNBASEDVALUETYPE, int>();
                    if (saved.runBasedInts != null)
                        foreach (var kv in saved.runBasedInts) ints[(RUNBASEDVALUETYPE)kv.Key] = kv.Value;
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

                // [achievement-debug]: dumps exactly what got restored, so this can be
                // eyeballed straight from LogOutput.log after a load - no SAM, no risk
                // of actually testing an achievement threshold for real. See
                // AchievementDebugLogging for the matching live "did a stat/achievement
                // just change" logging while playing
                if (saved != null)
                {
                    string ints = saved.runBasedInts != null && saved.runBasedInts.Count > 0
                        ? string.Join(", ", saved.runBasedInts.Select(kv => $"{(RUNBASEDVALUETYPE)kv.Key}={kv.Value}"))
                        : "(none)";
                    string floats = saved.runBasedFloats != null && saved.runBasedFloats.Count > 0
                        ? string.Join(", ", saved.runBasedFloats.Select(kv => $"{(RUNBASEDVALUETYPE)kv.Key}={kv.Value}"))
                        : "(none)";
                    log?.LogInfo("[achievement-debug] Restored this run's achievement progress from save:\n"
                        + $"  ints: {ints}\n"
                        + $"  floats: {floats}\n"
                        + $"  fruitsEaten={saved.runBasedFruitsEaten?.Count ?? 0}, shroomBerriesEaten={saved.shroomBerriesEaten?.Count ?? 0}, "
                        + $"nonToxicMushroomsEaten={saved.nonToxicMushroomsEaten?.Count ?? 0}, gourmandRequirementsEaten={saved.gourmandRequirementsEaten?.Count ?? 0}\n"
                        + $"  completedAscentsThisRun=[{string.Join(",", saved.completedAscentsThisRun ?? new List<int>())}]");
                }
                else
                {
                    log?.LogInfo("[achievement-debug] No saved achievement progress found - primed a fresh baseline (matches vanilla's own fresh-run behavior).");
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"AchievementProgressIO.ApplyLocal failed (non-fatal): {e.Message}");
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
        /// Host-only orchestration - called once at the very START of
        /// OwnTeleportSequence.RunSequence, BEFORE any segment/position warp (see the
        /// High Altitude Badge timing note in this class' remarks for why that ordering
        /// matters). Loops every connected player exactly like
        /// OwnInventoryRestore.RestoreAll does for inventory/afflictions, for the same
        /// reason: AchievementManager is a client-LOCAL singleton (each player only
        /// ever sees their own), so the host can only apply this directly to itself -
        /// every other player's restore has to be handed to that player's own machine
        /// via a targeted RPC
        /// </summary>
        public static void RestoreAllPlayers(SaveTarget target, bool offline, OwnLoadEntryPoints entryPoints, ManualLogSource log)
        {
            try
            {
                foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(UnityEngine.FindObjectsSortMode.None))
                {
                    Character ch = player?.character;
                    if (ch == null) continue;

                    string userId = offline ? "" : NetworkingUtilities.GetUserId(ch.player);
                    PhotonView playerView = player.GetComponent<PhotonView>();

                    OwnSavedAchievementProgress saved = null;
                    try
                    {
                        string path = OwnSavePaths.For(target, offline, userId);
                        if (File.Exists(path))
                        {
                            var data = JsonConvert.DeserializeObject<OwnSaveData>(File.ReadAllText(path));
                            saved = data?.achievementProgress;
                        }
                    }
                    catch (Exception e)
                    {
                        log?.LogWarning($"AchievementProgressIO.RestoreAllPlayers: could not read save for userId '{userId}': {e.Message}");
                    }

                    if (offline || (playerView != null && playerView.IsMine))
                    {
                        ApplyLocal(saved, log);
                    }
                    else if (PhotonNetwork.IsMasterClient && playerView != null)
                    {
                        entryPoints?.Network?.RestoreAchievementProgressFor(playerView, userId, ToJson(saved));
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
