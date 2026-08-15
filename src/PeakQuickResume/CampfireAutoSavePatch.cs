using System;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;

namespace PEAKQuickResume
{
    /// <summary>
    /// Harmony postfix on Campfire.Light_Rpc that triggers a save capture when a campfire
    /// is lit. Hooked here rather than Interact_CastFinished because that method only
    /// runs locally on the lighting machine - a client lighting the fire would never
    /// reach the host. Light_Rpc is an RPC-to-all, so it's the only signal guaranteed to
    /// reach the host regardless of who lit it. updateSegment distinguishes a real
    /// ignition from late-joiner state-sync calls, which pass false and shouldn't save.
    /// PEAKapalooza's branches are not ported (see ROADMAP.md).
    /// </summary>
    public static class CampfireAutoSavePatch
    {
        public static void Apply(Harmony harmony, PluginConfig cfg, OwnLoadEntryPoints entryPoints, OwnNetwork network, ManualLogSource log)
        {
            try
            {
                var target = AccessTools.Method(typeof(Campfire), "Light_Rpc");

                // Priority.Last so this runs after BackpackSaveMitigation's Light_Rpc postfix,
                // which must queue its pending restores before this one writes the save file.
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(CampfireAutoSavePatch), nameof(Postfix))
                {
                    priority = Priority.Last,
                });
                log.LogInfo("CampfireAutoSavePatch: patched Campfire.Light_Rpc (canonical autosave, "
                    + "reached on the host however the fire was lit).");

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

        private static void Postfix(Campfire __instance, bool updateSegment)
        {
            try
            {
                if (!updateSegment) return;
                if (__instance.name.Contains("PortableStovetop_Placed")) return;
                if (_entryPoints != null && _entryPoints.RecentlyLitCampfireUntil > UnityEngine.Time.time) return;

                _entryPoints?.ArmRecentlyLitCampfireCooldown(32f);
                _entryPoints?.ArmRecentlyLoadedCooldown(30f);

                // Only the host writes save files; it runs this same postfix off the same RPC.
                if (!PhotonNetwork.OfflineMode && !PhotonNetwork.IsMasterClient) return;

                _log?.LogInfo("CampfireAutoSavePatch: campfire lit -> autosave triggered.");
                AchievementProgressIO.LogSnapshot("campfire-lit", _log);

                if (PhotonNetwork.OfflineMode)
                {
                    OwnSaveCapture.SavePlayerOffline(_cfg, _log, _network?.MessageOverlay);
                }
                else
                {
                    _network?.RecentlyLitCampfireOthers();
                    OwnSaveCapture.SavePlayerCoop(_cfg, _log, _network);
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"CampfireAutoSavePatch.Postfix failed (non-fatal): {e}");
            }
        }
    }
}
