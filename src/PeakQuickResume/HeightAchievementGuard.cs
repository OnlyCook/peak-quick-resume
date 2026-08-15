using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Zorro.Core;

namespace PEAKQuickResume
{
    /// <summary>
    /// Stops a checkpoint load from handing out free progress on the permanent
    /// HeightClimbed Steam stat. RecordMaxHeight credits the delta above a per-run
    /// high-water mark (MaxHeightReached), which resets to 0 on the fresh run a load
    /// starts - so restoring a player 450m up a mountain would silently credit the
    /// whole 450m again as a fresh climb.
    ///
    /// Fix, in two halves: Suppress/Release bracket the load and teleport, during which
    /// RecordMaxHeight is skipped outright; on release the high-water mark is seeded
    /// from the player's actual current altitude, so the restored altitude is treated
    /// as already-climbed rather than credited a frame later. The permanent stat itself
    /// is never written or read here - this only prevents unearned credit, never grants
    /// any. Paired with AchievementProgressIO deliberately not restoring
    /// MaxHeightReached from the save.
    ///
    /// Runs on every machine: AchievementManager is a client-local singleton, so each
    /// player needs their own suppression window.
    /// </summary>
    internal static class HeightAchievementGuard
    {
        private static ManualLogSource _log;
        private static bool _patched;

        private static int _suppressDepth;
        private static float _suppressedUntil = -1f;

        /// <summary>Hard cap on how long a window may stay open, so a load that dies halfway can't disable height progress for the rest of the session.</summary>
        private const float MaxSuppressionSeconds = 180f;

        private static bool Suppressed
        {
            get
            {
                // Overrun forgets the outstanding depth too, or a stale count would never
                // return to zero and every later load would nest one level deeper.
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
        /// Returning false skips the original so no metres reach the permanent stat.
        /// The high-water mark is still moved up as the original would have - everything
        /// except IncrementSteamStat - so the mark keeps tracking real altitude and
        /// there's no gap to re-credit through once the window closes.
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
        /// Opens (or extends) the no-credit window. Reference counted since the resume
        /// orchestrator and teleport sequence legitimately overlap.
        /// </summary>
        public static void Suppress(string reason)
        {
            if (!_patched) return;

            _suppressDepth++;
            _suppressedUntil = Time.realtimeSinceStartup + MaxSuppressionSeconds;
            if (_suppressDepth == 1)
                _log.Trace($"HeightAchievementGuard: height credit paused ({reason}).");
        }

        /// <summary>Closes one window; when the last one closes, the mark is seeded to the player's current altitude.</summary>
        public static void Release(string reason)
        {
            // Reading Suppressed first lets an overrun window clear itself, so a late
            // Release can't decrement a stale count into the negatives.
            if (!_patched || !Suppressed) return;

            _suppressDepth--;
            if (_suppressDepth > 0) return;

            SeedMarkFromCurrentAltitude();
            _log.Trace($"HeightAchievementGuard: height credit resumed ({reason}).");
        }

        /// <summary>
        /// Raises MaxHeightReached to the local player's current altitude. Only ever
        /// raises it - lowering the mark is the one thing that could hand out credit.
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
