using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Peak.Network;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Save/restore for ordinary Luggage boxes near the campfire (RespawnChest/Ancient
    /// Statue is handled separately, see AncientStatueRestore, and explicitly excluded
    /// below). Generalizes that same approach to any number of candidate boxes and boxes
    /// holding more than one item.
    ///
    /// Luggage has no public no-spawn "mark it open" method like RespawnChest.Break(),
    /// but its [PunRPC] OpenLuggageRPC(spawnItems) is on the shared base class, so we
    /// call it directly with spawnItems=false to mark it open without a random spawn,
    /// then place the saved item(s) at their captured position/rotation ourselves.
    /// Host-only; every step fails soft since this class never touches disk.
    /// </summary>
    public static class LuggageRestore
    {
        private const float LuggageSearchRadius = 30f;

        // Loose enough to cover a "Big Luggage"'s 3 spread-out items, tight enough to
        // stay clear of unrelated ground loot.
        private const float ItemSearchRadius = 10f;

        /// <summary>
        /// Called from OwnSaveCapture before writing OwnSaveData, after
        /// AncientStatueRestore.Capture has added its own find to claimed. Adds every
        /// item found here to it too, so WorldItemRestore's capture doesn't also save
        /// the same items as loose loot.
        /// </summary>
        public static void Capture(Vector3 fallbackPos, HashSet<Item> claimed, ManualLogSource log, out List<OwnSavedLuggageState> states)
        {
            states = new List<OwnSavedLuggageState>();
            try
            {
                Vector3 searchCenter = CampfireAreaHelpers.ResolveNearestCampfirePos(fallbackPos);
                List<Luggage> boxes = FindLuggageNear(searchCenter);
                if (boxes.Count == 0)
                {
                    log.Trace($"LuggageRestore.Capture: no luggage found within {LuggageSearchRadius}m of {searchCenter}.");
                    return;
                }

                foreach (Luggage box in boxes)
                {
                    var state = new OwnSavedLuggageState { opened = box.IsOpen };
                    if (box.IsOpen)
                    {
                        // Excludes items already claimed by an earlier box or the statue.
                        foreach (Item item in CampfireAreaHelpers.FindFreeItemsWithin(box.transform.position, ItemSearchRadius, exclude: claimed))
                        {
                            var positioned = new OwnSavedPositionedItem
                            {
                                itemId = item.itemID,
                                posX = item.transform.position.x,
                                posY = item.transform.position.y,
                                posZ = item.transform.position.z,
                                rotX = item.transform.rotation.x,
                                rotY = item.transform.rotation.y,
                                rotZ = item.transform.rotation.z,
                                rotW = item.transform.rotation.w,
                            };
                            foreach (var kv in OwnItemStateIO.ReadItemStateValues(item.data, item.itemID))
                                positioned.values[kv.Key] = new OwnSavedEntry { type = kv.Value.TypeName, value = kv.Value.Value };

                            state.items.Add(positioned);
                            claimed?.Add(item);
                        }
                    }
                    states.Add(state);
                    log.Trace($"LuggageRestore.Capture: luggage '{box.name}' at {box.transform.position}, "
                        + $"opened={state.opened}, items={state.items.Count}.");
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"LuggageRestore.Capture failed (non-fatal): {e.Message}");
            }
        }

        /// <summary>
        /// Called once per load (host-only, world state), right after
        /// AncientStatueRestore.Restore since OwnWorldLootReset.ResetWorldLoot must run
        /// first. Candidates are matched to saved states by ascending distance from the
        /// campfire, reliable as long as the scene regenerates identically (fixed map seed).
        /// </summary>
        public static void Restore(OwnSaveData data, Vector3 fallbackPos, ManualLogSource log)
        {
            if (data?.luggageStates == null || data.luggageStates.Count == 0)
            {
                log.Trace("LuggageRestore.Restore: nothing to restore for this load.");
                return;
            }
            try
            {
                Vector3 searchCenter = CampfireAreaHelpers.ResolveNearestCampfirePos(fallbackPos);
                List<Luggage> boxes = FindLuggageNear(searchCenter);
                if (boxes.Count == 0)
                {
                    log?.LogWarning($"LuggageRestore: no luggage found within {LuggageSearchRadius}m of {searchCenter}, nothing to restore.");
                    return;
                }

                int count = Math.Min(boxes.Count, data.luggageStates.Count);
                if (boxes.Count != data.luggageStates.Count)
                    log?.LogWarning($"LuggageRestore: found {boxes.Count} luggage box(es) but saved {data.luggageStates.Count} - "
                        + $"restoring the first {count}, matched by ascending distance from the campfire.");

                for (int i = 0; i < count; i++)
                    RestoreOne(boxes[i], data.luggageStates[i], log);
            }
            catch (Exception e)
            {
                log?.LogError($"LuggageRestore.Restore failed (non-fatal): {e}");
            }
        }

        private static void RestoreOne(Luggage box, OwnSavedLuggageState state, ManualLogSource log)
        {
            if (state == null || !state.opened) return;

            // Defensive: ResetWorldLoot should already have closed it.
            if (box.IsOpen) return;

            PhotonView pv = box.GetComponent<PhotonView>();
            if (pv == null)
            {
                log?.LogWarning($"LuggageRestore: luggage '{box.name}' has no PhotonView, cannot restore.");
                return;
            }

            // spawnItems=false: marks it Open without the vanilla flow rolling a fresh random item.
            pv.RPC("OpenLuggageRPC", RpcTarget.AllBuffered, false);
            log.Trace($"LuggageRestore: restored luggage '{box.name}' to its open state.");

            if (state.items.Count == 0) return;

            foreach (OwnSavedPositionedItem saved in state.items)
            {
                if (!ItemDatabase.TryGetItem(saved.itemId, out Item prefab) || prefab == null)
                {
                    log?.LogWarning($"LuggageRestore: could not find item prefab for id {saved.itemId}, skipping.");
                    continue;
                }

                Vector3 spawnPos = new Vector3(saved.posX, saved.posY, saved.posZ);
                Quaternion spawnRot = new Quaternion(saved.rotX, saved.rotY, saved.rotZ, saved.rotW);

                GameObject spawned = PhotonNetwork.InstantiateItemRoom(prefab.name, spawnPos, spawnRot);
                if (spawned == null)
                {
                    log?.LogWarning($"LuggageRestore: InstantiateItemRoom returned null for item {saved.itemId}.");
                    continue;
                }

                if (box.isKinematic && spawned.TryGetComponent<PhotonView>(out PhotonView itemView))
                    itemView.RPC("SetKinematicRPC", RpcTarget.AllBuffered, true, spawnPos, spawnRot);

                CampfireAreaHelpers.ApplySavedItemValues(spawned, saved.values, log);

                log.Trace($"LuggageRestore: respawned saved item {saved.itemId} at {spawnPos} for luggage '{box.name}'.");
            }
        }

        // Excludes RespawnChest (the Ancient Statue) even though it's a Luggage subclass.
        private static List<Luggage> FindLuggageNear(Vector3 center)
        {
            return UnityEngine.Object.FindObjectsByType<Luggage>(FindObjectsSortMode.None)
                .Where(box => box != null && !(box is RespawnChest)
                    && Vector3.Distance(box.transform.position, center) <= LuggageSearchRadius)
                .OrderBy(box => Vector3.Distance(box.transform.position, center))
                .ToList();
        }
    }
}
