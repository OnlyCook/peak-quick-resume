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
    /// Native save/restore for ordinary Luggage boxes near the campfire (not the
    /// Ancient Statue - see AncientStatueRestore, and RespawnChest is explicitly
    /// excluded below so the two mechanics never double-handle the same object).
    /// Second of the "item/object restore around the campfire" mechanics; same shape
    /// as AncientStatueRestore, generalized to (a) any number of candidate boxes
    /// within range and (b) a box holding more than one item at once
    ///
    /// Unlike the statue, plain Luggage has no public no-spawn "just mark it open"
    /// method (RespawnChest.Break() is RespawnChest-only). But Luggage's own
    /// [PunRPC] OpenLuggageRPC(bool spawnItems) - the same one Break() calls under the
    /// hood - lives on the shared Luggage base class, so we call it the exact same way
    /// on any Luggage instance via PhotonView.RPC (Photon's own RPC dispatch finds
    /// [PunRPC] methods by name regardless of C# accessibility, same technique already
    /// used for SetKinematicRPC below): spawnItems=false marks it open with no auto
    /// spawn, then we place the saved item(s) ourselves at their own CAPTURED position/
    /// rotation (see OwnSavedLuggageItem's remarks for why not a configured spawn spot
    /// or a slot index)
    ///
    /// Host-only throughout (world state, not per-player). Every step is wrapped and
    /// non-fatal: this class never touches disk, so a failure here can only mean a
    /// luggage box restores wrong (or not at all), never a corrupted save
    /// </summary>
    public static class LuggageRestore
    {
        // Generous hard cap, not a tight proximity check - matches AncientStatueRestore's
        // own reasoning. No special-case skip for the Caldera/"Volcano" segment (which
        // never has luggage this close to its campfire): reliably knowing which segment
        // a just-lit campfire belongs to at CAPTURE time hits the exact same stale
        // MapHandler.currentSegment timing bug CampfireAreaHelpers was written to avoid
        // (see its own remarks) - an empty search there costs one harmless extra scan
        private const float LuggageSearchRadius = 30f;

        // Search radius for a box's still-unclaimed item(s), centered on the box's own
        // transform - loose enough to comfortably cover a "Big Luggage"'s 3 spread-out
        // items, tight enough to stay clear of unrelated ground loot nearby
        private const float ItemSearchRadius = 10f;

        /// <summary>Called from OwnSaveCapture right before writing OwnSaveData</summary>
        public static void Capture(Vector3 fallbackPos, ManualLogSource log, out List<OwnSavedLuggageState> states)
        {
            states = new List<OwnSavedLuggageState>();
            try
            {
                Vector3 searchCenter = CampfireAreaHelpers.ResolveNearestCampfirePos(fallbackPos);
                List<Luggage> boxes = FindLuggageNear(searchCenter);
                if (boxes.Count == 0)
                {
                    log?.LogInfo($"LuggageRestore.Capture: no luggage found within {LuggageSearchRadius}m of {searchCenter}.");
                    return;
                }

                // Items already claimed by an earlier (closer-to-the-campfire) box in
                // this same pass aren't eligible for a later one - keeps two nearby
                // boxes from both grabbing the same physical item
                var claimed = new HashSet<Item>();
                foreach (Luggage box in boxes)
                {
                    var state = new OwnSavedLuggageState { opened = box.IsOpen };
                    if (box.IsOpen)
                    {
                        foreach (Item item in CampfireAreaHelpers.FindFreeItemsWithin(box.transform.position, ItemSearchRadius))
                        {
                            if (claimed.Contains(item)) continue;
                            state.items.Add(new OwnSavedLuggageItem
                            {
                                itemId = item.itemID,
                                posX = item.transform.position.x,
                                posY = item.transform.position.y,
                                posZ = item.transform.position.z,
                                rotX = item.transform.rotation.x,
                                rotY = item.transform.rotation.y,
                                rotZ = item.transform.rotation.z,
                                rotW = item.transform.rotation.w,
                            });
                            claimed.Add(item);
                        }
                    }
                    states.Add(state);
                    log?.LogInfo($"LuggageRestore.Capture: luggage '{box.name}' at {box.transform.position}, "
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
        /// AncientStatueRestore.Restore in OwnTeleportSequence - same placement
        /// reasoning: OwnWorldLootReset.ResetWorldLoot just closed every Luggage in the
        /// scene, so this has to run after that or it'd be undone immediately.
        /// Candidates are matched to saved states by ascending distance from the
        /// campfire, on both sides - reliable as long as the scene regenerates
        /// identically (fixed map seed, already relied on elsewhere in this mod)
        /// </summary>
        public static void Restore(OwnSaveData data, Vector3 fallbackPos, ManualLogSource log)
        {
            if (data?.luggageStates == null || data.luggageStates.Count == 0)
            {
                log?.LogInfo("LuggageRestore.Restore: nothing to restore for this load.");
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

            // Defensive only - ResetWorldLoot should already have closed it (see
            // AncientStatueRestore's own identical guard for why this can't be assumed away)
            if (box.IsOpen) return;

            PhotonView pv = box.GetComponent<PhotonView>();
            if (pv == null)
            {
                log?.LogWarning($"LuggageRestore: luggage '{box.name}' has no PhotonView, cannot restore.");
                return;
            }

            // spawnItems=false: marks it Open (plays the open animation) without the
            // vanilla flow rolling a fresh random item - mirrors RespawnChest.Break()
            // exactly, see class remarks
            pv.RPC("OpenLuggageRPC", RpcTarget.AllBuffered, false);
            log?.LogInfo($"LuggageRestore: restored luggage '{box.name}' to its open state.");

            if (state.items.Count == 0) return;

            foreach (OwnSavedLuggageItem saved in state.items)
            {
                if (!ItemDatabase.TryGetItem(saved.itemId, out Item prefab) || prefab == null)
                {
                    log?.LogWarning($"LuggageRestore: could not find item prefab for id {saved.itemId}, skipping.");
                    continue;
                }

                // Spawn at the item's OWN captured position/rotation, not a configured
                // spawn spot or a slot index - see OwnSavedLuggageItem's remarks
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

                log?.LogInfo($"LuggageRestore: respawned saved item {saved.itemId} at {spawnPos} for luggage '{box.name}'.");
            }
        }

        // Excludes RespawnChest (the Ancient Statue, handled separately) even though
        // it's technically a Luggage subclass. Sorted by ascending distance from the
        // campfire so capture/restore pair candidates up consistently (see Restore)
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
