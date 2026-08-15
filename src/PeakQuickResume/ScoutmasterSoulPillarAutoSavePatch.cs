using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Peak;
using Photon.Pun;

namespace PEAKQuickResume
{
    /// <summary>
    /// Nadir's save point. The Void biome has no campfire, so the checkpoint is taken
    /// when a player communes with the scoutmaster's soul statue
    /// (<c>ScoutmasterSoulPillar</c>, hold E for 2s).
    ///
    /// Hooked on the RPC rather than Interact_CastFinished for exactly the same reason
    /// <see cref="CampfireAutoSavePatch"/> is: Interact_CastFinished only runs on the
    /// machine that did the interacting, so a client communing would never reach the host,
    /// and only the host writes save files. RPC_Break is sent to RpcTarget.All.
    ///
    /// The type argument distinguishes the real one-shot break (0) from the "somebody is
    /// holding E" / "they let go" telegraph pings (1/2), which must not save. The
    /// prefix/postfix pair around the pillar's private <c>_broken</c> flag makes this fire
    /// exactly once per pillar: vanilla's own guard lives inside SetBroken, so two players
    /// finishing the hold on the same frame would otherwise run this postfix twice.
    /// Late-joiner state sync calls SetBroken directly and never goes through RPC_Break,
    /// so it can't re-trigger a save either.
    /// </summary>
    public static class ScoutmasterSoulPillarAutoSavePatch
    {
        public static void Apply(Harmony harmony, PluginConfig cfg, OwnLoadEntryPoints entryPoints, OwnNetwork network, ManualLogSource log)
        {
            try
            {
                var target = AccessTools.Method(typeof(ScoutmasterSoulPillar), "RPC_Break");
                if (target == null)
                {
                    log.LogWarning("ScoutmasterSoulPillarAutoSavePatch: ScoutmasterSoulPillar.RPC_Break not found - "
                        + "the Nadir save point is disabled this session. Every other checkpoint is unaffected.");
                    return;
                }

                _brokenField = AccessTools.Field(typeof(ScoutmasterSoulPillar), "_broken");
                if (_brokenField == null)
                    log.LogWarning("ScoutmasterSoulPillarAutoSavePatch: ScoutmasterSoulPillar._broken not found - "
                        + "falling back to the cooldown alone to keep the Nadir save from being written twice.");

                // Priority.Last to match CampfireAutoSavePatch: any other postfix that wants to
                // queue work before the save file is written gets to run first.
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(ScoutmasterSoulPillarAutoSavePatch), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(ScoutmasterSoulPillarAutoSavePatch), nameof(Postfix))
                    {
                        priority = Priority.Last,
                    });
                log.LogInfo("ScoutmasterSoulPillarAutoSavePatch: patched ScoutmasterSoulPillar.RPC_Break "
                    + "(Nadir autosave on communing with the scoutmaster's soul).");

                _cfg = cfg;
                _entryPoints = entryPoints;
                _network = network;
                _log = log;
            }
            catch (Exception e)
            {
                log.LogError($"ScoutmasterSoulPillarAutoSavePatch.Apply failed (non-fatal): {e}");
            }
        }

        private static PluginConfig _cfg;
        private static OwnLoadEntryPoints _entryPoints;
        private static OwnNetwork _network;
        private static ManualLogSource _log;
        private static FieldInfo _brokenField;
        private static float _suppressUntil = -1f;

        /// <summary>
        /// Called by <see cref="OwnTeleportSequence"/> immediately before it breaks the pillar
        /// itself as part of restoring a Nadir checkpoint. Without this the restore's own break
        /// would come straight back through this postfix and write a fresh save mid-load.
        /// A time window rather than a flag so a delivery hiccup can't leave it stuck on; the
        /// break is sent to RpcTarget.All, which PUN runs locally on the sender synchronously,
        /// so the window only ever has to cover the send itself.
        /// </summary>
        internal static void SuppressNextBreak(float seconds) => _suppressUntil = UnityEngine.Time.time + seconds;

        /// <summary>True if a pillar break happening right now is one we triggered ourselves.</summary>
        internal static bool BreakIsSuppressed => _suppressUntil > UnityEngine.Time.time;

        private static bool ReadBroken(ScoutmasterSoulPillar pillar)
        {
            if (_brokenField == null || pillar == null) return false;
            try { return (bool)_brokenField.GetValue(pillar); }
            catch { return false; }
        }

        /// <summary>
        /// Records whether this pillar was already broken before vanilla's own handler ran, so
        /// the postfix can tell a real first break from a duplicate. Deliberately does nothing
        /// else: this sits in front of a vanilla RPC handler, so it must never throw and must
        /// never skip the original.
        /// </summary>
        private static void Prefix(ScoutmasterSoulPillar __instance, out bool __state)
        {
            __state = ReadBroken(__instance);
        }

        private static void Postfix(ScoutmasterSoulPillar __instance, int type, bool __state)
        {
            try
            {
                // 1 = "a player started holding E", 2 = "they cancelled". Only 0 breaks the pillar.
                if (type != 0) return;

                // Our own pre-commune during a Nadir restore. Checked before the cooldowns are
                // armed so a suppressed break leaves no trace at all.
                if (BreakIsSuppressed)
                {
                    _log?.LogInfo("ScoutmasterSoulPillarAutoSavePatch: pillar broken by the restore's own "
                        + "pre-commune, not by a player - no save written.");
                    return;
                }

                // Already broken before this call, so this is a duplicate RPC (two players
                // finishing the hold together) - vanilla no-oped it and so do we.
                if (__state) return;

                // Nothing actually changed (guard failed, or vanilla's handler bailed out), so
                // there is no commune event to save on.
                if (_brokenField != null && !ReadBroken(__instance)) return;

                if (_entryPoints != null && _entryPoints.RecentlyLitCampfireUntil > UnityEngine.Time.time) return;

                _entryPoints?.ArmRecentlyLitCampfireCooldown(32f);
                _entryPoints?.ArmRecentlyLoadedCooldown(30f);

                // Only the host writes save files; it runs this same postfix off the same RPC.
                if (!PhotonNetwork.OfflineMode && !PhotonNetwork.IsMasterClient) return;

                _log?.LogInfo("ScoutmasterSoulPillarAutoSavePatch: scoutmaster's soul freed -> Nadir autosave triggered.");
                AchievementProgressIO.LogSnapshot("soul-freed", _log);

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
                _log?.LogError($"ScoutmasterSoulPillarAutoSavePatch.Postfix failed (non-fatal): {e}");
            }
        }
    }
}
