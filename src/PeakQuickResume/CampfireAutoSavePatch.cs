using System;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our own copy of the checkpoint mod's <c>Campfire_AutoSave_Patch</c> (decompile
    /// 123-172): a Harmony postfix on <c>Campfire.Interact_CastFinished</c> that
    /// triggers a save capture when a campfire is lit. Solo-only for now (M6); coop's
    /// branch (host's own `SavePlayerCoop`/RPC_RequestSave relay) is M7's job.
    /// PEAKapalooza's branches are not ported (maintainer decision, see ROADMAP.md)
    ///
    /// **Runs ADDITIONALLY, alongside the checkpoint mod's own still-active patch on
    /// the same method** - this does NOT replace it. Our own capture goes to a
    /// non-canonical diagnostic path (see <see cref="OwnSaveCapture"/>/
    /// <see cref="OwnSavePaths.ForDiagnosticCapture"/>), purely so the maintainer can
    /// diff it against whatever the checkpoint mod's own (still-live) autosave writes
    /// to the real canonical file for the same in-game state. Cutting the CANONICAL
    /// write path over to our own capture is a distinct, later step - see ROADMAP.md
    /// Phase 8 M6
    /// </summary>
    public static class CampfireAutoSavePatch
    {
        public static void Apply(Harmony harmony, PluginConfig cfg, OwnLoadEntryPoints entryPoints, ManualLogSource log)
        {
            try
            {
                var target = AccessTools.Method(typeof(Campfire), "Interact_CastFinished");
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(CampfireAutoSavePatch), nameof(Postfix)));
                log.LogInfo("CampfireAutoSavePatch: patched Campfire.Interact_CastFinished (diagnostic capture, additive).");

                _cfg = cfg;
                _entryPoints = entryPoints;
                _log = log;
            }
            catch (Exception e)
            {
                log.LogError($"CampfireAutoSavePatch.Apply failed (non-fatal): {e}");
            }
        }

        private static PluginConfig _cfg;
        private static OwnLoadEntryPoints _entryPoints;
        private static ManualLogSource _log;

        // Mirrors AutoSaveOnCampfire's own (non-PEAKapalooza) guard/arm/dispatch shape
        // exactly (decompile 133, 143-169), minus the PEAKapalooza branches
        private static void Postfix(Campfire __instance)
        {
            try
            {
                if (__instance.beenBurningFor > 2f || __instance.name.Contains("PortableStovetop_Placed")) return;
                if (_entryPoints != null && _entryPoints.RecentlyLitCampfireUntil > UnityEngine.Time.time) return;
                if (!__instance.EveryoneInRange()) return;

                _entryPoints?.ArmRecentlyLitCampfireCooldown(32f);
                _entryPoints?.ArmRecentlyLoadedCooldown(30f);

                _log?.LogInfo("CampfireAutoSavePatch: campfire lit -> diagnostic capture triggered.");

                if (PhotonNetwork.OfflineMode)
                    OwnSaveCapture.SavePlayerOffline(_cfg, _log);
                // Coop: M7's job (host's own capture / RPC_RequestSave-equivalent relay)
            }
            catch (Exception e)
            {
                _log?.LogError($"CampfireAutoSavePatch.Postfix failed (non-fatal): {e}");
            }
        }
    }
}
