using System;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;

namespace PEAKQuickResume
{
    /// <summary>
    /// A Harmony postfix on <c>Campfire.Light_Rpc</c> that triggers a save capture when a
    /// campfire is lit (descended from the original save format's own
    /// <c>Campfire_AutoSave_Patch</c>, decompile 123-172, but deliberately hooked one
    /// level deeper - see below).
    ///
    /// This is the autosave trigger - <see cref="OwnSaveCapture"/> writes the CANONICAL
    /// save file directly
    ///
    /// WHY Light_Rpc AND NOT Interact_CastFinished:
    /// <c>Campfire.Interact_CastFinished</c> is invoked by <c>Interaction.Update</c> on the
    /// INTERACTING machine only (it is a plain local method call, never an RPC), so when a
    /// client lights the fire it never runs on the host at all. The old hook therefore only
    /// reached the host when the HOST personally lit the campfire; any client lighting it
    /// left the host silently never saving, which is fatal for a mod advertised as
    /// host-only. What the client's ignition DOES send everywhere is
    /// <c>view.RPC("Light_Rpc", RpcTarget.All, true, 0f)</c> - a reliable RPC-to-all that
    /// includes the master - so Light_Rpc is the only campfire signal guaranteed to reach
    /// the host regardless of who lit it, and it is what we hook now
    ///
    /// <c>updateSegment</c> distinguishes a REAL ignition (Interact_CastFinished's light
    /// branch, or DebugLight - both pass true) from the two calls that merely replicate an
    /// already-lit fire's state: the late-joiner sync (<c>CheckIfSyncNeeded</c>) and
    /// <c>LightWithoutReveal</c>, which both pass false. Only the former is a campfire
    /// actually being lit, so only the former saves. This also makes the old
    /// <c>currentlyCookingItem</c> guard unnecessary: finishing a cook on an already-lit
    /// campfire goes down Interact_CastFinished's OTHER branch and never sends Light_Rpc
    ///
    /// The old <c>EveryoneInRange()</c> guard is gone for the same reason: it re-checked, on
    /// whatever machine happened to be running the hook, a condition the game had already
    /// checked on the LIGHTER's machine before sending the RPC. By the time the RPC lands on
    /// the host, positions have moved on (and in coop are only ever an interpolated copy of
    /// the client's), so re-testing it could only ever produce a false negative that silently
    /// skips the save of a campfire that demonstrably did light
    ///
    /// PEAKapalooza's branches are not ported (maintainer decision, see ROADMAP.md)
    /// </summary>
    public static class CampfireAutoSavePatch
    {
        public static void Apply(Harmony harmony, PluginConfig cfg, OwnLoadEntryPoints entryPoints, OwnNetwork network, ManualLogSource log)
        {
            try
            {
                var target = AccessTools.Method(typeof(Campfire), "Light_Rpc");

                // Priority.Last so this postfix runs AFTER BackpackSaveMitigation's own
                // Light_Rpc postfix, which queues its pending restores for the save file
                // this one is about to have written. Both are postfixes on the same method
                // now, and Harmony's default ordering is by patch-application order - which
                // would put us first (Plugin.Awake applies this patch before that one) and
                // silently drop every dropped-backpack mitigation. An explicit priority
                // pins the order regardless of who is applied first
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
                if (!updateSegment) return; // state replication, not an ignition (see class remarks)
                if (__instance.name.Contains("PortableStovetop_Placed")) return;
                if (_entryPoints != null && _entryPoints.RecentlyLitCampfireUntil > UnityEngine.Time.time) return;

                _entryPoints?.ArmRecentlyLitCampfireCooldown(32f);
                _entryPoints?.ArmRecentlyLoadedCooldown(30f);

                // A non-host machine has nothing to do beyond the cooldowns above: only the
                // host ever writes save files, and the host runs this exact postfix off the
                // same RPC. The old client branch (RPC_RequestSave to the master) existed
                // because the hook back then never reached the host on its own - now it
                // always does, so keeping it would only ever produce a duplicate save
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
