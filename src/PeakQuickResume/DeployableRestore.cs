using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Save/restore for two player-placed deployables near the loaded campfire: the
    /// Portable Stove and the Scout Cannon. A third, the Checkpoint Flag, was tried and
    /// reverted (broken in solo) - see ROADMAP.md's "Deployable restore" section.
    ///
    /// The checkpoint mod's own stale-object cleanup destroys any player-placed
    /// instance of these on a repeat load and never restores them; this class is the
    /// first thing that actually does. Both are spawned via PhotonNetwork.Instantiate
    /// at the saved position/rotation, same as the vanilla construction flow.
    /// Position/rotation only - no burn/fuel/fired state, since neither prop has any
    /// meaningful persistent state beyond existing. Host-only; every step fails soft
    /// since this class never touches disk.
    /// </summary>
    public static class DeployableRestore
    {
        private const float SearchRadius = 30f;

        // Hard cap: a buggy or adversarial save shouldn't spawn unbounded networked props.
        private const int MaxPerType = 20;

        public static void CaptureStoves(Vector3 fallbackPos, ManualLogSource log, out List<OwnSavedDeployableState> states)
            => Capture("PortableStovetop_Placed", "Portable Stove", fallbackPos, log, out states);

        public static void CaptureCannons(Vector3 fallbackPos, ManualLogSource log, out List<OwnSavedDeployableState> states)
            => Capture("ScoutCannon_Placed", "Scout Cannon", fallbackPos, log, out states);

        public static void RestoreStoves(OwnSaveData data, Vector3 fallbackPos, ManualLogSource log)
            => Restore("PortableStovetop_Placed", "Portable Stove", data?.portableStoves, fallbackPos, log);

        public static void RestoreCannons(OwnSaveData data, Vector3 fallbackPos, ManualLogSource log)
            => Restore("ScoutCannon_Placed", "Scout Cannon", data?.scoutCannons, fallbackPos, log);

        /// <summary>
        /// Called from OwnSaveCapture before writing OwnSaveData. Only considers
        /// player-placed instances (CreatorActorNr > 0, not a room view), mirroring
        /// OwnWorldLootReset.DestroyStaleWorldObjects's filter, so a scene-baked prop is
        /// never touched.
        /// </summary>
        private static void Capture(string prefabNameNeedle, string label, Vector3 fallbackPos, ManualLogSource log, out List<OwnSavedDeployableState> states)
        {
            states = new List<OwnSavedDeployableState>();
            try
            {
                Vector3 searchCenter = CampfireAreaHelpers.ResolveNearestCampfirePos(fallbackPos);
                List<PhotonView> found = FindPlayerPlaced(prefabNameNeedle, searchCenter);

                foreach (PhotonView pv in found)
                {
                    if (states.Count >= MaxPerType)
                    {
                        log?.LogWarning($"DeployableRestore.Capture({label}): hit the {MaxPerType}-item cap within {SearchRadius}m of {searchCenter}, stopping early.");
                        break;
                    }

                    Transform t = pv.transform;
                    states.Add(new OwnSavedDeployableState
                    {
                        posX = t.position.x, posY = t.position.y, posZ = t.position.z,
                        rotX = t.rotation.x, rotY = t.rotation.y, rotZ = t.rotation.z, rotW = t.rotation.w,
                    });
                }

                log.Trace($"DeployableRestore.Capture({label}): found {found.Count} player-placed within {SearchRadius}m of {searchCenter}, saved {states.Count}.");
            }
            catch (Exception e)
            {
                log?.LogWarning($"DeployableRestore.Capture({label}) failed (non-fatal): {e.Message}");
            }
        }

        /// <summary>
        /// Called once per load (host-only, world state) from OwnTeleportSequence.
        /// Must run after OwnWorldLootReset.DestroyStaleWorldObjects, not before, or that
        /// pass would immediately destroy this restore's fresh spawns.
        /// </summary>
        private static void Restore(string prefabName, string label, List<OwnSavedDeployableState> saved, Vector3 fallbackPos, ManualLogSource log)
        {
            if (saved == null || saved.Count == 0)
            {
                log.Trace($"DeployableRestore.Restore({label}): nothing to restore for this load.");
                return;
            }
            try
            {
                int restored = 0;
                foreach (OwnSavedDeployableState state in saved)
                {
                    if (state == null) continue;
                    Vector3 pos = new Vector3(state.posX, state.posY, state.posZ);
                    Quaternion rot = new Quaternion(state.rotX, state.rotY, state.rotZ, state.rotW);

                    GameObject spawned = PhotonNetwork.Instantiate(prefabName, pos, rot, 0);
                    if (spawned == null)
                    {
                        log?.LogWarning($"DeployableRestore.Restore({label}): PhotonNetwork.Instantiate('{prefabName}') returned null.");
                        continue;
                    }
                    restored++;
                }
                log.Trace($"DeployableRestore.Restore({label}): restored {restored}/{saved.Count} within range of the loaded campfire.");
            }
            catch (Exception e)
            {
                log?.LogError($"DeployableRestore.Restore({label}) failed (non-fatal): {e}");
            }
        }

        internal static List<PhotonView> FindPlayerPlaced(string nameNeedle, Vector3 center)
        {
            var result = new List<PhotonView>();
            foreach (PhotonView pv in UnityEngine.Object.FindObjectsByType<PhotonView>(FindObjectsSortMode.None))
            {
                if (pv == null || pv.gameObject == null) continue;
                if (!pv.gameObject.name.Contains(nameNeedle)) continue;

                bool isRoomView;
                try { isRoomView = pv.IsRoomView; } catch { continue; }
                if (isRoomView || pv.CreatorActorNr <= 0) continue;

                if (Vector3.Distance(pv.transform.position, center) <= SearchRadius)
                    result.Add(pv);
            }
            return result;
        }
    }
}
