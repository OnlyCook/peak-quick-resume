using System;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our own copy of the checkpoint mod's <c>Campfire_AutoSave_Patch</c> (decompile
    /// 123-172): a Harmony postfix on <c>Campfire.Interact_CastFinished</c> that
    /// triggers a save capture when a campfire is lit. M7 adds the coop branch (host's
    /// own <c>SavePlayerCoop</c> + <c>RPC_RecentlyLitCampfire</c> relay, or a client's
    /// <c>RPC_RequestSave</c> to the host), mirroring decompile 156-169 exactly.
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
        public static void Apply(Harmony harmony, PluginConfig cfg, OwnLoadEntryPoints entryPoints, OwnNetwork network, ManualLogSource log)
        {
            try
            {
                var target = AccessTools.Method(typeof(Campfire), "Interact_CastFinished");
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(CampfireAutoSavePatch), nameof(Postfix)));
                log.LogInfo("CampfireAutoSavePatch: patched Campfire.Interact_CastFinished (diagnostic capture, additive).");

                _cfg = cfg;
                _entryPoints = entryPoints;
                _network = network;
                _log = log;
            }
            catch (Exception e)
            {
                log.LogError($"CampfireAutoSavePatch.Apply failed (non-fatal): {e}");
            }
        }

        private static PluginConfig _cfg;
        private static OwnLoadEntryPoints _entryPoints;
        private static OwnNetwork _network;
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
                {
                    OwnSaveCapture.SavePlayerOffline(_cfg, _log);
                }
                else if (PhotonNetwork.IsMasterClient)
                {
                    _network?.RecentlyLitCampfireOthers();
                    OwnSaveCapture.SavePlayerCoop(_cfg, _log, _network);
                }
                else
                {
                    _network?.RequestSaveToMaster();
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"CampfireAutoSavePatch.Postfix failed (non-fatal): {e}");
            }
        }
    }
}
