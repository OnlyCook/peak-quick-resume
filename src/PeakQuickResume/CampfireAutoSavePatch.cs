using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;

namespace PEAKQuickResume
{
    /// <summary>
    /// A Harmony postfix on <c>Campfire.Interact_CastFinished</c> that triggers a save
    /// capture when a campfire is lit (ported from the original save format's own
    /// <c>Campfire_AutoSave_Patch</c>, decompile 123-172). Coop branches (host's own
    /// <c>SavePlayerCoop</c> + <c>RPC_RecentlyLitCampfire</c> relay, or a client's
    /// <c>RPC_RequestSave</c> to the host) mirror decompile 156-169 exactly.
    /// PEAKapalooza's branches are not ported (maintainer decision, see ROADMAP.md)
    ///
    /// This is the autosave trigger - <see cref="OwnSaveCapture"/> writes the CANONICAL
    /// save file directly
    ///
    /// Uses the reliable <c>currentlyCookingItem</c>-field check (rather than the
    /// original's <c>beenBurningFor > 2f</c> timing guess) to skip a cook finishing on an
    /// already-lit campfire - a fix folded in here directly since this trigger is
    /// authoritative for the canonical file
    /// </summary>
    public static class CampfireAutoSavePatch
    {
        private static FieldInfo _currentlyCookingItemField;

        public static void Apply(Harmony harmony, PluginConfig cfg, OwnLoadEntryPoints entryPoints, OwnNetwork network, ManualLogSource log)
        {
            try
            {
                var target = AccessTools.Method(typeof(Campfire), "Interact_CastFinished");
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(CampfireAutoSavePatch), nameof(Postfix)));
                log.LogInfo("CampfireAutoSavePatch: patched Campfire.Interact_CastFinished (canonical autosave).");

                _currentlyCookingItemField = AccessTools.Field(typeof(Campfire), "currentlyCookingItem");
                if (_currentlyCookingItemField == null)
                    log.LogWarning("CampfireAutoSavePatch: Campfire.currentlyCookingItem not found (vanilla game "
                        + "likely changed); cook-triggered duplicate saves may occur.");

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
        // exactly (decompile 133, 143-169), minus the PEAKapalooza branches - the
        // beenBurningFor timing guess is replaced with the reliable cook-vs-light check
        // (see class remarks)
        private static void Postfix(Campfire __instance)
        {
            try
            {
                if (__instance.name.Contains("PortableStovetop_Placed")) return;
                if (_currentlyCookingItemField != null && _currentlyCookingItemField.GetValue(__instance) != null) return;
                if (_entryPoints != null && _entryPoints.RecentlyLitCampfireUntil > UnityEngine.Time.time) return;
                if (!__instance.EveryoneInRange()) return;

                _entryPoints?.ArmRecentlyLitCampfireCooldown(32f);
                _entryPoints?.ArmRecentlyLoadedCooldown(30f);

                _log?.LogInfo("CampfireAutoSavePatch: campfire lit -> autosave triggered.");

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
