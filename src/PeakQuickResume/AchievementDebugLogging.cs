using System;
using BepInEx.Logging;
using HarmonyLib;
using Zorro.Core;

namespace PEAKQuickResume
{
    /// <summary>
    /// Pure observability - every patch here is a postfix (or a prefix that only reads
    /// state via <c>__state</c>), never blocks or changes any game behavior. Added so
    /// the achievement-progress restore (see <see cref="AchievementProgressIO"/>) can
    /// be verified straight from <c>LogOutput.log</c> - specifically so a false
    /// "climbed height" credit from teleporting (High Altitude Badge) shows up as a
    /// clearly tagged log line instead of silently inflating a permanent Steam stat,
    /// and so the run-scoped counters (Plunderer, First Aid, Foraging, Mycology, Advanced
    /// Mycology, Gourmand, Knot Tying, Clutch, ...) can be watched ticking up from their
    /// restored value while testing, all WITHOUT needing Steam Achievement Manager or
    /// risking an accidental real unlock of something the player wants to earn legitimately.
    ///
    /// Every log line here is tagged "[achievement-debug]" for easy grepping out of a
    /// full session log. HighAltitudeBadge-relevant lines are logged at Warning level
    /// (so they stand out) when they'd actually credit height; everything else is Info
    /// </summary>
    public static class AchievementDebugLogging
    {
        private static ManualLogSource _log;

        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            try
            {
                var amType = typeof(AchievementManager);

                harmony.Patch(AccessTools.Method(amType, "RecordMaxHeight"),
                    prefix: new HarmonyMethod(typeof(AchievementDebugLogging), nameof(RecordMaxHeight_Prefix)),
                    postfix: new HarmonyMethod(typeof(AchievementDebugLogging), nameof(RecordMaxHeight_Postfix)));

                harmony.Patch(AccessTools.Method(amType, nameof(AchievementManager.IncrementSteamStat)),
                    postfix: new HarmonyMethod(typeof(AchievementDebugLogging), nameof(IncrementSteamStat_Postfix)));

                harmony.Patch(AccessTools.Method(amType, nameof(AchievementManager.SetRunBasedInt)),
                    postfix: new HarmonyMethod(typeof(AchievementDebugLogging), nameof(SetRunBasedInt_Postfix)));

                harmony.Patch(AccessTools.Method(amType, nameof(AchievementManager.SetRunBasedFloat)),
                    postfix: new HarmonyMethod(typeof(AchievementDebugLogging), nameof(SetRunBasedFloat_Postfix)));

                harmony.Patch(AccessTools.Method(amType, "AddToRunBasedFruitsEaten"),
                    postfix: new HarmonyMethod(typeof(AchievementDebugLogging), nameof(FruitsEaten_Postfix)));
                harmony.Patch(AccessTools.Method(amType, "AddToShroomBerriesEaten"),
                    postfix: new HarmonyMethod(typeof(AchievementDebugLogging), nameof(ShroomBerries_Postfix)));
                harmony.Patch(AccessTools.Method(amType, "AddToNonToxicMushroomsEaten"),
                    postfix: new HarmonyMethod(typeof(AchievementDebugLogging), nameof(NonToxicMushrooms_Postfix)));
                harmony.Patch(AccessTools.Method(amType, "AddToGourmandRequirementsEaten"),
                    postfix: new HarmonyMethod(typeof(AchievementDebugLogging), nameof(GourmandEaten_Postfix)));

                harmony.Patch(AccessTools.Method(amType, "ThrowAchievement"),
                    prefix: new HarmonyMethod(typeof(AchievementDebugLogging), nameof(ThrowAchievement_Prefix)));

                harmony.Patch(AccessTools.Method(amType, "TestWonRun"),
                    postfix: new HarmonyMethod(typeof(AchievementDebugLogging), nameof(TestWonRun_Postfix)));

                harmony.Patch(AccessTools.Method(amType, "TestRespawnChestOpened"),
                    prefix: new HarmonyMethod(typeof(AchievementDebugLogging), nameof(TestRespawnChestOpened_Prefix)));

                log.LogInfo("AchievementDebugLogging: patched AchievementManager for [achievement-debug] logging.");
            }
            catch (Exception e)
            {
                log.LogError($"AchievementDebugLogging.Apply failed (non-fatal): {e}");
            }
        }

        // --- High Altitude Badge: the one that matters most for teleport-safety ---

        // Captures the permanent HeightClimbed Steam stat BEFORE RecordMaxHeight runs,
        // via __state, so the postfix can log the exact delta it produced (0 is what a
        // correctly-restored teleport should show)
        private static void RecordMaxHeight_Prefix(out int __state)
        {
            __state = 0;
            try { Singleton<AchievementManager>.Instance?.GetSteamStatInt(STEAMSTATTYPE.HeightClimbed, out __state); }
            catch { /* best-effort logging only */ }
        }

        private static void RecordMaxHeight_Postfix(int meters, int __state)
        {
            try
            {
                AchievementManager am = Singleton<AchievementManager>.Instance;
                if (am == null) return;
                am.GetSteamStatInt(STEAMSTATTYPE.HeightClimbed, out int after);
                int maxHeightReached = am.GetRunBasedInt(RUNBASEDVALUETYPE.MaxHeightReached);
                int delta = after - __state;
                if (delta != 0)
                    _log?.LogWarning($"[achievement-debug] HighAltitudeBadge: altitude check at {meters}m (this run's MaxHeightReached was {maxHeightReached}m) "
                        + $"-> CREDITED {delta}m to the permanent HeightClimbed stat (now {after}m total). If this happened right after a teleport/load, the fix did not work.");
                else
                    _log?.LogInfo($"[achievement-debug] HighAltitudeBadge: altitude check at {meters}m (this run's MaxHeightReached was {maxHeightReached}m) "
                        + $"-> no credit (HeightClimbed stat stays at {after}m total). Correct behavior right after a teleport/load.");
            }
            catch (Exception e) { _log?.LogWarning($"[achievement-debug] RecordMaxHeight_Postfix failed: {e.Message}"); }
        }

        private static void IncrementSteamStat_Postfix(STEAMSTATTYPE steamStatType, int value, int __result)
        {
            try
            {
                if (steamStatType == STEAMSTATTYPE.HeightClimbed)
                    _log?.LogWarning($"[achievement-debug] HighAltitudeBadge: IncrementSteamStat(HeightClimbed, +{value}m) -> new total {__result}m.");
                else
                    _log?.LogInfo($"[achievement-debug] Steam stat '{steamStatType}' incremented by {value} -> new total {__result}.");
            }
            catch { /* best-effort logging only */ }
        }

        // --- Run-scoped counters (Plunderer, First Aid, Clutch, Knot Tying, ...) ---

        private static void SetRunBasedInt_Postfix(RUNBASEDVALUETYPE type, int value)
        {
            if (type == RUNBASEDVALUETYPE.LuggageOpened)
                _log?.LogInfo($"[achievement-debug] PlundererBadge: {value}/15 luggages opened this run.");
            else if (type == RUNBASEDVALUETYPE.ScoutsResurrected)
                _log?.LogInfo($"[achievement-debug] ClutchBadge: {value}/3 scouts resurrected this run.");
            else
                _log?.LogInfo($"[achievement-debug] run-based int '{type}' set to {value}.");
        }

        private static void SetRunBasedFloat_Postfix(RUNBASEDVALUETYPE type, float value)
            => _log?.LogInfo($"[achievement-debug] run-based float '{type}' set to {value}.");

        // --- "Eat N different X" trackers (Foraging/Advanced Mycology/Mycology/Gourmand) ---

        private static void FruitsEaten_Postfix()
        {
            var counts = AchievementProgressIO.GetEatenCounts();
            _log?.LogInfo($"[achievement-debug] ForagingBadge: {counts.fruits}/5 different berries eaten this run.");
        }

        private static void ShroomBerries_Postfix()
        {
            var counts = AchievementProgressIO.GetEatenCounts();
            _log?.LogInfo($"[achievement-debug] AdvancedMycologyBadge: {counts.shroomBerries}/5 different Shroomberries eaten this run.");
        }

        private static void NonToxicMushrooms_Postfix()
        {
            var counts = AchievementProgressIO.GetEatenCounts();
            _log?.LogInfo($"[achievement-debug] MycologyBadge: {counts.nonToxicMushrooms}/4 different non-toxic mushrooms eaten this run.");
        }

        private static void GourmandEaten_Postfix()
        {
            var counts = AchievementProgressIO.GetEatenCounts();
            _log?.LogInfo($"[achievement-debug] GourmandBadge: {counts.gourmand}/4 required items cooked+eaten this run (coconut half, honeycomb, yellow winterberry, egg).");
        }

        // --- Every achievement check, for correlating "I just did X" with the log ---

        // --- Run won: fires once per player, locally, the moment THEY reach the peak ---

        private static void TestWonRun_Postfix()
            => AchievementProgressIO.LogSnapshot("run-won", _log);

        // --- Temporary (2026-08-06 session): diagnosing a reproducible Clutch over-credit
        // right after a Quick Resume load. TestRespawnChestOpened's native loop grants one
        // ScoutsResurrected credit per Character.AllCharacters entry currently dead/fully-
        // passed-out - if that list ever holds a stale/duplicate entry (e.g. left over from
        // a previous load in the same session) alongside the real one, a single statue touch
        // would over-credit. This dumps every entry's identity + flags at the exact moment
        // the native loop is about to run, so a repro immediately shows whether that's what's
        // happening. Remove once resolved
        private static void TestRespawnChestOpened_Prefix(RespawnChest chest, Character opener)
        {
            try
            {
                _log?.LogInfo($"[achievement-debug] TestRespawnChestOpened: chest='{chest?.name}' opener='{opener?.characterName}' "
                    + $"(opener.IsLocal={opener?.IsLocal}). Character.AllCharacters snapshot:");
                foreach (Character c in Character.AllCharacters)
                {
                    string userId = "?";
                    try { userId = c?.player != null ? Peak.Network.NetworkingUtilities.GetUserId(c.player) : "(no player)"; }
                    catch { /* best-effort */ }
                    _log?.LogInfo($"  - '{c?.characterName}' instanceId={c?.GetInstanceID()} userId={userId} "
                        + $"dead={c?.data.dead} fullyPassedOut={c?.data.fullyPassedOut} passedOut={c?.data.passedOut}");
                }
            }
            catch (Exception e) { _log?.LogWarning($"[achievement-debug] TestRespawnChestOpened_Prefix failed: {e.Message}"); }
        }

        private static void ThrowAchievement_Prefix(ACHIEVEMENTTYPE type)
        {
            try
            {
                AchievementManager am = Singleton<AchievementManager>.Instance;
                bool alreadyUnlocked = am != null && am.IsAchievementUnlocked(type);
                _log?.LogInfo($"[achievement-debug] ThrowAchievement({type}) called - already unlocked: {alreadyUnlocked}.");
            }
            catch { /* best-effort logging only */ }
        }
    }
}
