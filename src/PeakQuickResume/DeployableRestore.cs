using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Native save/restore for two player-placed deployables around the loaded
    /// campfire: the Portable Stove ("PortableStovetop_Placed") and the Scout Cannon
    /// ("ScoutCannon_Placed"). A third, the Checkpoint Flag, was tried (its own
    /// per-player binding logic, since removed) and reverted - session-confirmed
    /// broken in solo (a planted flag came back missing after save/load) - see
    /// ROADMAP.md's "Deployable restore" section for the full writeup and why it
    /// wasn't worth chasing further for what's a minor QoL mechanic
    ///
    /// Genuinely new ground, not a port: <see cref="OwnWorldLootReset"/>'s class remarks
    /// (ROADMAP.md Phase 8) already established that the checkpoint mod's own
    /// "world object state" list (which includes these exact two prefab names, plus
    /// the flag, plus several others this mod doesn't restore) is destroy-only - on a
    /// repeat load it deletes any player-placed instance matching those names and
    /// never puts anything back. Nothing in the game's own save systems tracks these
    /// either. This class is the first thing that actually restores them
    ///
    /// Both are built via the game's own generic <c>Constructable</c> ItemComponent
    /// (decompile: <c>Constructable.FinishConstruction</c>) - a plain placed prop with
    /// a <c>PhotonView</c>, spawned with <c>PhotonNetwork.Instantiate(prefabName, pos,
    /// rot, 0)</c> exactly like the vanilla construction flow itself does (confirmed:
    /// <c>constructedPrefab.GetComponent&lt;PhotonView&gt;() != null</c> for the
    /// PUN-instantiate branch, the only one either of these prefabs could plausibly
    /// use given they're networked, interactible props). Restoring them is exactly
    /// that same call with the saved position/rotation instead of a fresh preview hit
    ///
    /// Deliberately position/rotation only - no burn state, no fuel, no fired/lit
    /// flag: the Portable Stove is a plain <c>Campfire</c> instance (confirmed via the
    /// checkpoint mod's own <c>Campfire_AutoSave_Patch</c>, which excludes objects
    /// named "PortableStovetop_Placed" from ever counting as an autosave trigger -
    /// there is no dedicated "PortableStove" class at all) and the Scout Cannon
    /// (decompile: <c>ScoutCannon</c>) only has a transient ~4s lit/firing window with
    /// no persistent ammo/fuel/already-fired state - both are fully reusable exactly
    /// as freshly placed, with nothing meaningful left to capture beyond "it exists,
    /// here". Matches this mod's existing restore mechanics' own bias (see
    /// WorldItemRestore's remarks): correctness/robustness over full-fidelity replay
    ///
    /// Host-only throughout (world state, not per-player). Every step is wrapped and
    /// non-fatal: this class never touches disk, so a failure here can only mean a
    /// deployable restores wrong (or not at all), never a corrupted save
    /// </summary>
    public static class DeployableRestore
    {
        // Matches LuggageRestore's own radius - the maintainer's explicit instruction
        // for this feature ("restore all of those within 30m of the campfire")
        private const float SearchRadius = 30f;

        // Hard cap, not a tuning knob - matches WorldItemRestore's own reasoning: a
        // buggy or adversarial save file shouldn't be able to make a load spawn an
        // unbounded number of networked props
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
        /// Called from OwnSaveCapture right before writing OwnSaveData, same call site
        /// as AncientStatueRestore/LuggageRestore/WorldItemRestore. Only ever considers
        /// PLAYER-PLACED instances (<c>PhotonView.CreatorActorNr &gt; 0</c>, not a room
        /// view) - mirrors <see cref="OwnWorldLootReset.DestroyStaleWorldObjects"/>'s own
        /// filter exactly, so a scene-baked Scout Cannon (e.g. the ones
        /// <c>ScoutCannonAchievementZone</c> implies exist as level dressing at some
        /// points) is never touched, saved, or duplicated by this class
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

                log?.LogInfo($"DeployableRestore.Capture({label}): found {found.Count} player-placed within {SearchRadius}m of {searchCenter}, saved {states.Count}.");
            }
            catch (Exception e)
            {
                log?.LogWarning($"DeployableRestore.Capture({label}) failed (non-fatal): {e.Message}");
            }
        }

        /// <summary>
        /// Called once per load (host-only, world state), from OwnTeleportSequence -
        /// unlike AncientStatueRestore/LuggageRestore this MUST run after
        /// <see cref="OwnWorldLootReset.DestroyStaleWorldObjects"/>, not before: that
        /// pass destroys any player-placed object whose name contains
        /// "PortableStovetop_Placed"/"ScoutCannon_Placed" (it's in the checkpoint
        /// mod's own original stale-object list, ported verbatim) on every REPEAT
        /// load this session - running this restore earlier would have its own fresh
        /// spawns immediately destroyed by that same pass moments later
        /// </summary>
        private static void Restore(string prefabName, string label, List<OwnSavedDeployableState> saved, Vector3 fallbackPos, ManualLogSource log)
        {
            if (saved == null || saved.Count == 0)
            {
                log?.LogInfo($"DeployableRestore.Restore({label}): nothing to restore for this load.");
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
                log?.LogInfo($"DeployableRestore.Restore({label}): restored {restored}/{saved.Count} within range of the loaded campfire.");
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
