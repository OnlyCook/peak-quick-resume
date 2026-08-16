using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Peak.Network;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Save/restore for the "Ancient Statue" (RespawnChest) found near most campfires,
    /// which nothing in the game's own save systems tracks. Vanilla's mid-run Quicksave
    /// system covers a separate, narrower case (base-camp Luggage.IsOpen) that depends
    /// on spawn-tracker state we never set up, so it isn't reused here. Instead we
    /// reconstruct the saved item directly via PhotonNetwork.InstantiateItemRoom rather
    /// than replaying the vanilla open flow (which would roll a new random item).
    /// Host-only (world state, not per-player); every step fails soft since this class
    /// never touches disk.
    /// </summary>
    public static class AncientStatueRestore
    {
        // Statues aren't a consistent distance from their campfire (farthest confirmed
        // ~68m), so this is a generous hard cap rather than a tight proximity check.
        private const float StatueSearchRadius = 100f;

        // RespawnChest's revive spot tops out ~7m away; 8m covers a spawned item
        // without reaching unrelated ground loot.
        private const float ItemSearchRadius = 8f;

        /// <summary>
        /// Called from OwnSaveCapture before writing OwnSaveData. Searches near the
        /// Campfire closest to fallbackPos. Adds any found item to claimed so
        /// LuggageRestore/WorldItemRestore don't also save it. state stays null when no
        /// statue is found (vs. non-null with broken=false, meaning found but untouched).
        /// </summary>
        public static void Capture(Vector3 fallbackPos, HashSet<Item> claimed, ManualLogSource log, out OwnSavedStatueState state)
        {
            state = null;
            try
            {
                Vector3 searchCenter = CampfireAreaHelpers.ResolveNearestCampfirePos(fallbackPos);
                RespawnChest statue = FindNearestStatue(searchCenter);
                if (statue == null)
                {
                    log.Trace($"AncientStatueRestore.Capture: no Ancient Statue found within {StatueSearchRadius}m of {searchCenter}.");
                    return;
                }

                state = new OwnSavedStatueState { broken = statue.IsOpen };
                log.Trace($"AncientStatueRestore.Capture: found statue '{statue.name}' at {statue.transform.position} "
                    + $"({Vector3.Distance(statue.transform.position, searchCenter):F1}m from search center), broken={state.broken}.");
                if (!state.broken) return;

                Item groundItem = CampfireAreaHelpers.FindNearestFreeItem(statue.transform.position, ItemSearchRadius, claimed);
                if (groundItem == null)
                {
                    log.Trace("AncientStatueRestore.Capture: statue is broken but no unclaimed ground item found nearby.");
                    return;
                }

                var item = new OwnSavedPositionedItem
                {
                    itemId = groundItem.itemID,
                    posX = groundItem.transform.position.x,
                    posY = groundItem.transform.position.y,
                    posZ = groundItem.transform.position.z,
                    rotX = groundItem.transform.rotation.x,
                    rotY = groundItem.transform.rotation.y,
                    rotZ = groundItem.transform.rotation.z,
                    rotW = groundItem.transform.rotation.w,
                };
                foreach (var kv in OwnItemStateIO.ReadItemStateValues(groundItem.data))
                    item.values[kv.Key] = new OwnSavedEntry { type = kv.Value.TypeName, value = kv.Value.Value };
                state.item = item;

                claimed?.Add(groundItem);
                log.Trace($"AncientStatueRestore.Capture: statue holds item '{groundItem.name}' (id={item.itemId}) at {groundItem.transform.position}.");
            }
            catch (Exception e)
            {
                log?.LogWarning($"AncientStatueRestore.Capture failed (non-fatal): {e.Message}");
            }
        }

        /// <summary>
        /// Called once per load (host-only, world state) from <see cref="OwnTeleportSequence"/>
        /// right after <see cref="OwnWorldLootReset.ResetWorldLoot"/> resets every statue to
        /// Closed. No-op for pre-feature saves or campfires with no nearby statue.
        /// </summary>
        public static void Restore(OwnSaveData data, Vector3 fallbackPos, ManualLogSource log)
        {
            if (data?.ancientStatue == null || !data.ancientStatue.broken)
            {
                log.Trace("AncientStatueRestore.Restore: nothing to restore for this load (statue was unbroken when saved, or no save data).");
                return;
            }
            try
            {
                Vector3 searchCenter = CampfireAreaHelpers.ResolveNearestCampfirePos(fallbackPos);
                RespawnChest statue = FindNearestStatue(searchCenter);
                if (statue == null)
                {
                    log?.LogWarning($"AncientStatueRestore: no Ancient Statue found within {StatueSearchRadius}m of {searchCenter}, nothing to restore.");
                    return;
                }
                log.Trace($"AncientStatueRestore: found statue '{statue.name}' at {statue.transform.position} "
                    + $"({Vector3.Distance(statue.transform.position, searchCenter):F1}m from search center), currently open={statue.IsOpen}.");

                // Defensive: ResetWorldLoot should already have closed it; don't double-spawn.
                if (statue.IsOpen) return;

                statue.Break();
                log.Trace("AncientStatueRestore: restored the Ancient Statue to its broken state.");

                OwnSavedPositionedItem item = data.ancientStatue.item;
                if (item != null && ItemDatabase.TryGetItem(item.itemId, out Item prefab) && prefab != null)
                {
                    // Spawn at the item's captured position/rotation rather than the statue's
                    // spawn spot or a transform.up offset - both were tried and reverted, since
                    // neither reliably matches where the item actually settled.
                    Vector3 spawnPos = new Vector3(item.posX, item.posY, item.posZ);
                    Quaternion spawnRot = new Quaternion(item.rotX, item.rotY, item.rotZ, item.rotW);

                    GameObject spawned = PhotonNetwork.InstantiateItemRoom(prefab.name, spawnPos, spawnRot);
                    if (spawned != null)
                    {
                        // Mirrors Spawner.InitializePhysics's kinematic-freeze step so the
                        // restored item settles like a freshly-broken statue's item would.
                        if (statue.isKinematic && spawned.TryGetComponent<PhotonView>(out PhotonView view))
                            view.RPC("SetKinematicRPC", RpcTarget.AllBuffered, true, spawnPos, spawnRot);

                        CampfireAreaHelpers.ApplySavedItemValues(spawned, item.values, log);

                        log.Trace($"AncientStatueRestore: respawned saved item {item.itemId} at {spawnPos} on the Ancient Statue.");
                    }
                    else
                    {
                        log?.LogWarning($"AncientStatueRestore: InstantiateItemRoom returned null for item {item.itemId}.");
                    }
                }
            }
            catch (Exception e)
            {
                log?.LogError($"AncientStatueRestore.Restore failed (non-fatal): {e}");
            }
        }

        private static RespawnChest FindNearestStatue(Vector3 nearPos)
        {
            RespawnChest nearest = null;
            float best = float.MaxValue;
            foreach (RespawnChest chest in UnityEngine.Object.FindObjectsByType<RespawnChest>(FindObjectsSortMode.None))
            {
                if (chest == null) continue;
                float d = Vector3.Distance(chest.transform.position, nearPos);
                if (d <= StatueSearchRadius && d < best) { best = d; nearest = chest; }
            }
            return nearest;
        }
    }
}
