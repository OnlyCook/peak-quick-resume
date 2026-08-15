using System;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Vanilla bug fix: <c>RespawnChest.Interact_CastFinished</c> calls
    /// <c>GlobalEvents.TriggerRespawnChestOpened</c> unconditionally, unlike the base
    /// <c>Luggage.Interact_CastFinished</c> which guards on <c>state == Closed</c>. A repeat
    /// cast on an already-open statue re-fires the event, and
    /// <c>AchievementManager.TestRespawnChestOpened</c> re-credits ScoutsResurrected for every
    /// still-dead teammate each time it fires. Fixed via a Harmony prefix applying the same
    /// state guard the base class uses, skipping the redundant call entirely.
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

        // Fails open on error so this can only suppress a redundant call, never block a real one.
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
