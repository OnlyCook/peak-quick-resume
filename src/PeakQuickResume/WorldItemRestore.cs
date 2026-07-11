using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Peak.Network;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Native save/restore for whatever's just lying free within 30m of the campfire -
    /// backpacks (natural spawns or player drops), berries still sitting where they fell
    /// off a bush, coconuts, campfire hotdogs/marshmallows, anything a player threw or
    /// dropped nearby. Third of the "item/object restore around the campfire" mechanics,
    /// and the most general one: unlike AncientStatueRestore/LuggageRestore, this isn't
    /// tied to a specific container type - it's every loose <c>Item</c> in range
    ///
    /// Capture records each item's own observed position/rotation, same reasoning as
    /// OwnSavedPositionedItem (items settle wherever gravity/jostling left them, not
    /// necessarily anywhere meaningful to reconstruct after the fact). Restore always
    /// respawns them frozen in place (SetKinematicRPC) rather than as live physics
    /// objects - a deliberate simplification (maintainer's own call): no velocity/
    /// trajectory is ever saved, so an item mid-flight when the campfire lit just stays
    /// floating exactly where it was on reload. Acceptable; robustness (never corrupting
    /// a save) matters far more here than a perfectly re-simulated throw
    ///
    /// Two things this class must NOT double-handle, both threaded through explicitly:
    ///  - Whatever AncientStatueRestore/LuggageRestore already captured as a container's
    ///    OWN held item(s) - <see cref="Capture"/> takes the same shared "claimed" set
    ///    those two add to (see OwnSaveCapture's call order), so an item already counted
    ///    as "the statue's held item" or "in this luggage box" is never ALSO saved here
    ///    as a generic loose item (which would restore it twice)
    ///  - A dropped backpack BackpackSaveMitigation is already queued to restore
    ///    (equipped) onto its owner - see <see cref="BackpackSaveMitigation.GetPendingBackpackViewIds"/>.
    ///    Without this exclusion, that same physical backpack would come back BOTH
    ///    equipped on the player (that mitigation's job) AND dropped on the ground again
    ///    (this class's job) - the exact footgun the maintainer asked to avoid
    ///    reintroducing while keeping the existing mitigation as-is
    ///
    /// Host-only throughout (world state, not per-player). Every step is wrapped and
    /// non-fatal: this class never touches disk, so a failure here can only mean items
    /// restore wrong (or not at all), never a corrupted save
    /// </summary>
    public static class WorldItemRestore
    {
        private const float SearchRadius = 30f;

        // Hard cap, not a tuning knob - a campfire surrounded by an unusually large
        // pile of loot (or a buggy/adversarial save file) shouldn't be able to make a
        // load spawn an unbounded number of items. Applied to what we SAVE, not to the
        // delete pass below (that always clears everything in range regardless, so a
        // capped save doesn't leave naturally-regenerated leftovers behind uncleared)
        private const int MaxItems = 50;

        /// <summary>
        /// Called from OwnSaveCapture right before writing OwnSaveData, AFTER
        /// AncientStatueRestore.Capture and LuggageRestore.Capture have added their own
        /// finds to <paramref name="claimed"/> - see class remarks for why sharing that
        /// set matters
        /// </summary>
        public static void Capture(Vector3 fallbackPos, HashSet<Item> claimed, ManualLogSource log, out List<OwnSavedPositionedItem> items)
        {
            items = new List<OwnSavedPositionedItem>();
            try
            {
                Vector3 searchCenter = CampfireAreaHelpers.ResolveNearestCampfirePos(fallbackPos);
                HashSet<int> pendingBackpackViewIds = BackpackSaveMitigation.GetPendingBackpackViewIds();

                List<Item> candidates = CampfireAreaHelpers.FindFreeItemsWithin(searchCenter, SearchRadius, includeBackpacks: true, exclude: claimed);

                int skippedPendingBackpacks = 0;
                foreach (Item item in candidates)
                {
                    if (items.Count >= MaxItems)
                    {
                        log?.LogWarning($"WorldItemRestore.Capture: hit the {MaxItems}-item cap within {SearchRadius}m of {searchCenter}, stopping early.");
                        break;
                    }

                    if (item is Backpack && pendingBackpackViewIds.Contains(item.photonView.ViewID))
                    {
                        skippedPendingBackpacks++;
                        continue; // BackpackSaveMitigation already owns restoring this one
                    }

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

                    if (item is Backpack backpack)
                    {
                        List<OwnSavedBackpackItemState> contents = CaptureBackpackContents(backpack, log);
                        if (contents.Count > 0) positioned.backpackContents = contents;
                    }

                    items.Add(positioned);
                    claimed.Add(item);
                }

                log?.LogInfo($"WorldItemRestore.Capture: found {candidates.Count} candidate(s) within {SearchRadius}m of {searchCenter}, "
                    + $"saved {items.Count} (skipped {skippedPendingBackpacks} pending backpack-mitigation restore(s)).");
            }
            catch (Exception e)
            {
                log?.LogWarning($"WorldItemRestore.Capture failed (non-fatal): {e.Message}");
            }
        }

        /// <summary>
        /// Called once per load (host-only, world state), BEFORE AncientStatueRestore.Restore
        /// and LuggageRestore.Restore in OwnTeleportSequence - this class's own delete
        /// pass clears every loose item within range unconditionally, so it has to run
        /// FIRST, before those two place anything, or it would immediately destroy what
        /// they just restored. A no-op for saves predating this feature (data.worldItemStates
        /// is null, not just empty - see remarks on that field in OwnSaveData)
        ///
        /// v2.0.0: split into two independently-toggleable categories (restore-grounded-items,
        /// restore-grounded-backpacks). A category that's disabled is skipped on BOTH sides
        /// (neither cleared nor restored) - so turning off backpack restore leaves whatever
        /// naturally spawned there alone, rather than deleting it with nothing put back
        /// </summary>
        public static void Restore(OwnSaveData data, Vector3 fallbackPos, PluginConfig cfg, ManualLogSource log)
        {
            if (data?.worldItemStates == null)
            {
                log?.LogInfo("WorldItemRestore.Restore: no saved data for this feature (old save, or nothing was ever captured), skipping.");
                return;
            }
            bool restoreItems = cfg.RestoreGroundedItems.Value;
            bool restoreBackpacks = cfg.RestoreGroundedBackpacks.Value;
            if (!restoreItems && !restoreBackpacks)
            {
                log?.LogInfo("WorldItemRestore.Restore: both restore-grounded-items and restore-grounded-backpacks are disabled, skipping.");
                return;
            }
            try
            {
                Vector3 searchCenter = CampfireAreaHelpers.ResolveNearestCampfirePos(fallbackPos);

                // Clear out whatever naturally (re)spawned here on this fresh map
                // regeneration - berries, coconuts, campfire food, naturally-placed
                // backpacks, anything left behind - so restoring our own saved items
                // below doesn't end up duplicating them. Only within whichever
                // category(ies) are enabled - see class remarks
                List<Item> stale = CampfireAreaHelpers.FindFreeItemsWithin(searchCenter, SearchRadius, includeBackpacks: true);
                int destroyed = 0;
                foreach (Item item in stale)
                {
                    if (item == null) continue;
                    if (item is Backpack ? !restoreBackpacks : !restoreItems) continue;
                    try
                    {
                        PhotonView pv = item.GetComponent<PhotonView>();
                        if (!PhotonNetwork.OfflineMode && pv != null) PhotonNetwork.Destroy(pv);
                        else UnityEngine.Object.Destroy(item.gameObject);
                        destroyed++;
                    }
                    catch (Exception e)
                    {
                        log?.LogWarning($"WorldItemRestore: failed to clear stale item '{item.name}' (non-fatal): {e.Message}");
                    }
                }
                log?.LogInfo($"WorldItemRestore: cleared {destroyed} naturally-spawned item(s) within {SearchRadius}m of {searchCenter}.");

                int restored = 0;
                foreach (OwnSavedPositionedItem saved in data.worldItemStates)
                {
                    if (!ItemDatabase.TryGetItem(saved.itemId, out Item prefab) || prefab == null)
                    {
                        log?.LogWarning($"WorldItemRestore: could not find item prefab for id {saved.itemId}, skipping.");
                        continue;
                    }
                    if (prefab is Backpack ? !restoreBackpacks : !restoreItems) continue;

                    Vector3 spawnPos = new Vector3(saved.posX, saved.posY, saved.posZ);
                    Quaternion spawnRot = new Quaternion(saved.rotX, saved.rotY, saved.rotZ, saved.rotW);

                    GameObject spawned = PhotonNetwork.InstantiateItemRoom(prefab.name, spawnPos, spawnRot);
                    if (spawned == null)
                    {
                        log?.LogWarning($"WorldItemRestore: InstantiateItemRoom returned null for item {saved.itemId}.");
                        continue;
                    }

                    // Always frozen in place, never a live physics object - see class
                    // remarks (no velocity/trajectory is ever saved, by design)
                    if (spawned.TryGetComponent<PhotonView>(out PhotonView view))
                        view.RPC("SetKinematicRPC", RpcTarget.AllBuffered, true, spawnPos, spawnRot);

                    // A dropped backpack's contents have to land in the SAME
                    // ItemInstanceData push as its own values (both go out in one
                    // SetItemInstanceDataRPC) - populating BackpackData onto the spawned
                    // item's live .data separately, after the fact, would race the RPC
                    // that assigns .data in the first place (see PushItemInstanceData)
                    // and get silently discarded when it lands
                    if ((saved.values != null && saved.values.Count > 0) || (saved.backpackContents != null && saved.backpackContents.Count > 0))
                    {
                        ItemInstanceData instanceData = CampfireAreaHelpers.BuildItemInstanceData(saved.values, log);
                        if (saved.backpackContents != null && saved.backpackContents.Count > 0)
                            PopulateBackpackContents(instanceData, saved.backpackContents, log);
                        CampfireAreaHelpers.PushItemInstanceData(spawned, instanceData, log);
                    }

                    restored++;
                }
                log?.LogInfo($"WorldItemRestore: restored {restored}/{data.worldItemStates.Count} saved item(s) within {SearchRadius}m of {searchCenter}.");
            }
            catch (Exception e)
            {
                log?.LogError($"WorldItemRestore.Restore failed (non-fatal): {e}");
            }
        }

        // Mirrors OwnSaveCapture.CaptureBackpack, just reading a standalone dropped
        // Backpack's own BackpackData instead of a Player's backpackSlot
        private static List<OwnSavedBackpackItemState> CaptureBackpackContents(Backpack backpack, ManualLogSource log)
        {
            var result = new List<OwnSavedBackpackItemState>();
            try
            {
                BackpackData bpData = backpack.GetData<BackpackData>(DataEntryKey.BackpackData);
                if (bpData?.itemSlots == null) return result;

                for (byte slotIndex = 0; slotIndex < bpData.itemSlots.Length; slotIndex++)
                {
                    ItemSlot slot = bpData.itemSlots[slotIndex];
                    if (slot == null || slot.IsEmpty() || slot.prefab == null || slot.data == null) continue;

                    var state = new OwnSavedBackpackItemState { slotIndex = slotIndex, itemId = slot.prefab.itemID };
                    foreach (var kv in OwnItemStateIO.ReadItemStateValues(slot.data, slot.prefab.itemID))
                        state.values[kv.Key] = new OwnSavedEntry { type = kv.Value.TypeName, value = kv.Value.Value };
                    result.Add(state);
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"WorldItemRestore: could not read dropped backpack contents (non-fatal): {e.Message}");
            }
            return result;
        }

        // Mirrors OwnInventoryRestore.LoadBackpackFromSave, but populates a freestanding
        // ItemInstanceData (not yet assigned to any live item) instead of a player's
        // backpackSlot.data - see the caller for why this has to happen BEFORE the
        // single SetItemInstanceDataRPC push, not after
        private static void PopulateBackpackContents(ItemInstanceData instanceData, List<OwnSavedBackpackItemState> contents, ManualLogSource log)
        {
            const DataEntryKey backpackDataKey = (DataEntryKey)7; // matches OwnInventoryRestore.GetBackpackData
            if (!instanceData.TryGetDataEntry(backpackDataKey, out BackpackData bpData) || bpData == null)
            {
                instanceData.RegisterNewEntry<BackpackData>(backpackDataKey);
                instanceData.TryGetDataEntry(backpackDataKey, out bpData);
            }
            if (bpData?.itemSlots == null) return;

            foreach (OwnSavedBackpackItemState itemState in contents)
            {
                if (itemState.slotIndex >= bpData.itemSlots.Length
                    || !ItemDatabase.TryGetItem(itemState.itemId, out Item item) || item == null)
                    continue;

                var slotInstanceData = new ItemInstanceData(Guid.NewGuid());
                ItemInstanceDataHandler.AddInstanceData(slotInstanceData);
                bpData.AddItem(item, slotInstanceData, itemState.slotIndex);

                ItemInstanceData slotData = bpData.itemSlots[itemState.slotIndex]?.data;
                if (slotData == null) continue;
                foreach (var kv in itemState.values)
                {
                    if (!OwnItemStateIO.TryGetKey(kv.Key, out DataEntryKey key)) continue;
                    OwnSavedEntry entry = kv.Value;
                    if (entry != null) OwnItemStateIO.TrySetOrCreateEntry(slotData, key, entry.type, entry.value, log);
                }
            }
        }
    }
}
