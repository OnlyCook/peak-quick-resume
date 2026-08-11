using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Zorro.Core;

namespace PEAKQuickResume
{
    /// <summary>
    /// Stops a checkpoint load from handing out free progress on the "HeightClimbed"
    /// Steam stat (the High Altitude / mountaineering badges).
    ///
    /// HOW THE GAME COUNTS IT. <c>HeightClimbed</c> is a PERMANENT Steam stat that grows
    /// by the delta above a per-run high-water mark:
    /// <code>
    /// internal void RecordMaxHeight(int meters) {
    ///     if (meters >= 25) {
    ///         int mark = GetRunBasedInt(MaxHeightReached);
    ///         if (meters >= mark + 5) {
    ///             IncrementSteamStat(HeightClimbed, meters - mark);   // permanent
    ///             SetRunBasedInt(MaxHeightReached, meters);           // per-run mark
    ///         }
    ///     }
    /// }
    /// </code>
    /// <c>MaxHeightReached</c> is run-based, so starting the fresh run a load needs resets
    /// it to 0. The player is then restored 450m up a mountain, <c>RecordMaxHeight</c> sees
    /// 450 against a mark of 0, and credits the whole 450m AGAIN - metres they already
    /// earned before saving. Every single load inflated the stat by the player's altitude,
    /// silently (it is a Steam stat, nothing on screen says so). Captured live in a co-op
    /// session log:
    /// <code>
    /// run-based int 'MaxHeightReached' set to 0.
    /// HighAltitudeBadge: IncrementSteamStat(HeightClimbed, +456m) -> new total 363353m.
    /// </code>
    ///
    /// THE FIX, in two halves:
    ///  1. <see cref="Suppress"/>/<see cref="Release"/> bracket the whole load and teleport,
    ///     during which <c>RecordMaxHeight</c> is skipped outright. Nothing is credited for
    ///     the wild altitude swings a load goes through (airport, spawn point, warp target).
    ///  2. On release the high-water mark is SEEDED from where the player actually is now,
    ///     so the altitude they were restored at is treated as already-climbed rather than
    ///     as a fresh climb. Without this, lifting the suppression would simply hand over
    ///     the same credit one frame later.
    ///
    /// The permanent stat itself is never written, read or "repaired" here - Steam keeps
    /// counting it on its own, exactly as the game intends. This only ever prevents credit
    /// that was not earned; it can never grant any. Deliberately paired with
    /// <c>AchievementProgressIO</c> NOT restoring <c>MaxHeightReached</c> from the save
    /// (see the exclusion there), so the mark always comes from live altitude.
    ///
    /// Runs on EVERY machine, host and client alike: <c>AchievementManager</c> is a
    /// client-local singleton, so each player's own game does its own counting and needs
    /// its own suppression window
    /// </summary>
    internal static class HeightAchievementGuard
    {
        private static ManualLogSource _log;
        private static bool _patched;

        private static int _suppressDepth;
        private static float _suppressedUntil = -1f;

        /// <summary>
        /// Hard cap on how long a window may stay open. A load that dies halfway (a failed
        /// resume, a disconnect mid-sequence) must never leave height progress permanently
        /// disabled for the rest of the session, so the window always expires on its own
        /// </summary>
        private const float MaxSuppressionSeconds = 180f;

        private static bool Suppressed
        {
            get
            {
                // An overrun doesn't just stop suppressing - it also FORGETS the
                // outstanding depth. Leaving a stale count behind would mean the matching
                // Release never brings it back to zero, so every later load would nest one
                // level deeper and eventually never lift at all
                if (_suppressDepth > 0 && Time.realtimeSinceStartup >= _suppressedUntil)
                {
                    _suppressDepth = 0;
                    _log?.LogWarning("HeightAchievementGuard: the no-credit window outlasted its safety timeout "
                        + "(a load that never finished?); height credit resumes normally from here.");
                }
                return _suppressDepth > 0;
            }
        }

        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            try
            {
                var target = AccessTools.Method(typeof(AchievementManager), "RecordMaxHeight");
                if (target == null)
                {
                    log.LogWarning("HeightAchievementGuard: AchievementManager.RecordMaxHeight not found - "
                        + "loading a save may inflate the HeightClimbed stat.");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(typeof(HeightAchievementGuard), nameof(RecordMaxHeightPrefix)));
                _patched = true;
                log.LogInfo("HeightAchievementGuard: patched AchievementManager.RecordMaxHeight (no height credit while loading).");
            }
            catch (Exception e)
            {
                log.LogError($"HeightAchievementGuard.Apply failed (non-fatal): {e}");
            }
        }

        /// <summary>
        /// Returning false skips the original, so no metres reach the permanent stat.
        /// While skipping, the run's high-water mark is still moved up exactly as the
        /// original would have - everything the game does EXCEPT the
        /// <c>IncrementSteamStat</c>. That keeps the mark tracking the player's real
        /// altitude throughout the load, so the moment the window closes there is no gap
        /// left for the altitude to be re-credited through.
        ///
        /// Safe to write directly: <c>MaxHeightReached</c> is not one of the
        /// <c>runBasedAchievements</c>, so <c>SetRunBasedInt</c> only stores the value and
        /// refreshes the progress UI - it cannot unlock anything
        /// </summary>
        private static bool RecordMaxHeightPrefix(AchievementManager __instance, int meters)
        {
            if (!Suppressed) return true;

            try
            {
                if (__instance != null && meters >= 25)
                {
                    int mark = __instance.GetRunBasedInt(RUNBASEDVALUETYPE.MaxHeightReached);
                    if (meters >= mark + 5)
                        __instance.SetRunBasedInt(RUNBASEDVALUETYPE.MaxHeightReached, meters);
                }
            }
            catch { /* worst case the release-time seeding below still covers us */ }

            return false;
        }

        /// <summary>
        /// Opens (or extends) the no-credit window. Reference counted, because the resume
        /// orchestrator and the teleport sequence legitimately overlap - the outer one must
        /// not be closed early by the inner one finishing
        /// </summary>
        public static void Suppress(string reason)
        {
            if (!_patched) return;

            _suppressDepth++;
            _suppressedUntil = Time.realtimeSinceStartup + MaxSuppressionSeconds;
            if (_suppressDepth == 1)
                _log.Trace($"HeightAchievementGuard: height credit paused ({reason}).");
        }

        /// <summary>
        /// Closes one window. When the last one closes, the run's high-water mark is moved
        /// up to the player's current altitude so the metres they were restored at are not
        /// then credited as a fresh climb
        /// </summary>
        public static void Release(string reason)
        {
            // Reading Suppressed first lets an overrun window clear itself, so a late
            // Release can't decrement a stale count into the negatives
            if (!_patched || !Suppressed) return;

            _suppressDepth--;
            if (_suppressDepth > 0) return;

            SeedMarkFromCurrentAltitude();
            _log.Trace($"HeightAchievementGuard: height credit resumed ({reason}).");
        }

        /// <summary>
        /// Raises <c>MaxHeightReached</c> to the local player's current altitude, read from
        /// <c>CharacterStats.heightInMeters</c> - the very value the game itself passes to
        /// <c>RecordMaxHeight</c>, so the two can never disagree about what "current
        /// altitude" means.
        ///
        /// Only ever RAISES it - if the mark is somehow already higher, it is left alone,
        /// since lowering it is the one thing that could hand out credit
        /// </summary>
        private static void SeedMarkFromCurrentAltitude()
        {
            try
            {
                Character local = Character.localCharacter;
                var achievements = Singleton<AchievementManager>.Instance;
                CharacterStats stats = local != null && local.refs != null ? local.refs.stats : null;
                if (local == null || achievements == null || stats == null) return;

                int meters = Mathf.RoundToInt(stats.heightInMeters);
                if (meters < 25) return; // below the game's own floor, nothing to protect

                int mark = achievements.GetRunBasedInt(RUNBASEDVALUETYPE.MaxHeightReached);
                if (mark >= meters) return;

                achievements.SetRunBasedInt(RUNBASEDVALUETYPE.MaxHeightReached, meters);
                _log?.LogInfo($"HeightAchievementGuard: set this run's height mark to {meters}m (was {mark}m) "
                    + "so the altitude you were restored at isn't credited as a fresh climb.");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"HeightAchievementGuard: could not seed the height mark ({e.Message}); "
                    + "the HeightClimbed stat may gain your current altitude once.");
            }
        }
    }
}
