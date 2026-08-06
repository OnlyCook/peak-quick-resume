using System;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Vanilla bug fix (session-diagnosed 2026-08-06, via the [achievement-debug]
    /// TestRespawnChestOpened snapshot added to chase a reproducible Clutch Badge
    /// over-credit): <c>RespawnChest.Interact_CastFinished</c> (decompile) is:
    /// <code>
    /// public override void Interact_CastFinished(Character interactor)
    /// {
    ///     base.Interact_CastFinished(interactor);
    ///     GlobalEvents.TriggerRespawnChestOpened(this, interactor);
    /// }
    /// </code>
    /// The base <c>Luggage.Interact_CastFinished</c> guards its own work with
    /// <c>if (state == LuggageState.Closed)</c> - so a second cast-completion against an
    /// already-open statue correctly no-ops the RPC/loot-trigger. But RespawnChest's own
    /// <c>TriggerRespawnChestOpened</c> call has NO such guard: it fires unconditionally,
    /// every single time <c>Interact_CastFinished</c> runs, open-or-not. That event is what
    /// <c>AchievementManager.TestRespawnChestOpened</c> listens to, and it grants one
    /// ScoutsResurrected credit per currently dead/fully-passed-out
    /// <c>Character.AllCharacters</c> entry EVERY time it fires - so a second, no-op cast
    /// on an already-broken statue (confirmed via logging: two back-to-back
    /// TestRespawnChestOpened firings, identical character roster both times, nobody
    /// actually revived a second time) silently re-credits the same still-dead
    /// teammate(s) again, for free. Repro'd twice in testing, both times as the very
    /// first statue touch right after a Quick Resume load - plausibly because a player
    /// eager to test-revive immediately after regaining control is more likely to
    /// double-tap/hold-through a queued second cast than during normal play, but nothing
    /// about the bug itself is load-specific: it is a vanilla RespawnChest defect that can
    /// fire from any repeat interaction with an already-open statue
    ///
    /// Fixed the only way possible without touching vanilla source: a Harmony prefix that
    /// applies the SAME state guard the base class already uses, skipping the whole
    /// original call (so neither the redundant RPC nor the achievement trigger runs) when
    /// the chest is already open. Purely a no-op skip - an already-open statue's
    /// <c>Interact_CastFinished</c> does nothing useful today anyway (SpawnItems/
    /// RespawnAllPlayersHere never runs a second time either), so this changes no
    /// legitimate behavior, only removes the erroneous side effect
    /// </summary>
    public static class RespawnChestDoubleCreditFix
    {
        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(RespawnChest), nameof(RespawnChest.Interact_CastFinished)),
                    prefix: new HarmonyMethod(typeof(RespawnChestDoubleCreditFix), nameof(Prefix)));
                log.LogInfo("RespawnChestDoubleCreditFix: patched RespawnChest.Interact_CastFinished "
                    + "(skip repeat cast-completion on an already-open Ancient Statue).");
            }
            catch (Exception e)
            {
                log.LogError($"RespawnChestDoubleCreditFix.Apply failed (non-fatal): {e}");
            }
        }

        // Returning false skips the original method (and every other patch's prefix that
        // would otherwise still run harmlessly here - there are none on this method today).
        // Fails open (returns true, i.e. runs normally) on any error so this can only ever
        // suppress a redundant call, never block a real one
        private static bool Prefix(RespawnChest __instance)
        {
            try
            {
                return __instance == null || !__instance.IsOpen;
            }
            catch
            {
                return true;
            }
        }
    }
}
