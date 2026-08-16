using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Peak.Network;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Native save/restore for whatever's lying free within 30m of the campfire (backpacks,
    /// berries, coconuts, campfire food, anything dropped nearby) - every loose <c>Item</c>
    /// in range, unlike the container-specific AncientStatueRestore/LuggageRestore.
    ///
    /// Restore always respawns items frozen in place (SetKinematicRPC), never as live
    /// physics: no velocity/trajectory is saved, so robustness matters more than a
    /// perfectly re-simulated throw.
    ///
    /// Must not double-handle: items already claimed by AncientStatueRestore/LuggageRestore
    /// (via the shared "claimed" set), or a dropped backpack BackpackSaveMitigation is
    /// already queued to restore equipped onto its owner (would otherwise duplicate it).
    ///
    /// Host-only. Every step is wrapped and non-fatal: this class never touches disk, so a
    /// failure here can only mean items restore wrong, never a corrupted save.
    /// </summary>
    public static class WorldItemRestore
    {
        private const float SearchRadius = 30f;

        // Hard cap, not a tuning knob: an unbounded pile of loot (or an adversarial save
        // file) shouldn't be able to make a load spawn unlimited items. Applied to what we
        // save, not the delete pass, which always clears everything in range regardless.
        private const int MaxItems = 50;

        /// <summary>Called from OwnSaveCapture after AncientStatueRestore/LuggageRestore have added their finds to <paramref name="claimed"/>.</summary>
        public static void Capture(Vector3 fallbackPos, HashSet<Item> claimed, ManualLogSource log, out List<OwnSavedPositionedItem> items)
        {
            items = new List<OwnSavedPositionedItem>();
            try
            {
                Vector3 searchCenter = CampfireAreaHelpers.ResolveNearestCampfirePos(fallbackPos);
                float radius = NadirSearchRadius.ForCurrentSegment(SearchRadius);
                HashSet<int> pendingBackpackViewIds = BackpackSaveMitigation.GetPendingBackpackViewIds();

                List<Item> candidates = CampfireAreaHelpers.FindFreeItemsWithin(searchCenter, radius, includeBackpacks: true, exclude: claimed);

                int skippedPendingBackpacks = 0;
                foreach (Item item in candidates)
                {
                    if (items.Count >= MaxItems)
                    {
                        log?.LogWarning($"WorldItemRestore.Capture: hit the {MaxItems}-item cap within {radius}m of {searchCenter}, stopping early.");
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
                    foreach (var kv in OwnItemStateIO.ReadItemStateValues(item.data))
                        positioned.values[kv.Key] = new OwnSavedEntry { type = kv.Value.TypeName, value = kv.Value.Value };

                    // A jetpack with no Fuel entry restores as a full tank unless written explicitly.
                    BackpackTypeCompat.EnsureFuelCaptured(item, item.data, positioned.values, log);

                    if (item is Backpack backpack)
                    {
                        List<OwnSavedBackpackItemState> contents = CaptureBackpackContents(backpack, log);
                        if (contents.Count > 0) positioned.backpackContents = contents;
                    }

                    items.Add(positioned);
                    claimed.Add(item);
                }

                log.Trace($"WorldItemRestore.Capture: found {candidates.Count} candidate(s) within {radius}m of {searchCenter}, "
                    + $"saved {items.Count} (skipped {skippedPendingBackpacks} pending backpack-mitigation restore(s)).");
            }
            catch (Exception e)
            {
                log?.LogWarning($"WorldItemRestore.Capture failed (non-fatal): {e.Message}");
            }
        }

        /// <summary>
        /// Called once per load, host-only, BEFORE AncientStatueRestore.Restore and
        /// LuggageRestore.Restore: this class's delete pass clears every loose item in range
        /// unconditionally, so it must run first or it would destroy what those just placed.
        /// No-op for saves predating this feature. Each of the two categories (items,
        /// backpacks) is independently toggleable and skipped on both sides when disabled.
        /// </summary>
        public static void Restore(OwnSaveData data, Vector3 fallbackPos, PluginConfig cfg, ManualLogSource log)
        {
            if (data?.worldItemStates == null)
            {
                log.Trace("WorldItemRestore.Restore: no saved data for this feature (old save, or nothing was ever captured), skipping.");
                return;
            }
            bool restoreItems = cfg.RestoreGroundedItems.Value;
            bool restoreBackpacks = cfg.RestoreGroundedBackpacks.Value;
            if (!restoreItems && !restoreBackpacks)
            {
                log.Trace("WorldItemRestore.Restore: both restore-grounded-items and restore-grounded-backpacks are disabled, skipping.");
                return;
            }
            try
            {
                Vector3 searchCenter = CampfireAreaHelpers.ResolveNearestCampfirePos(fallbackPos);
                float radius = NadirSearchRadius.ForSavedSegment(SearchRadius, data);

                // Clear whatever naturally (re)spawned here so restoring our saved items doesn't duplicate it.
                List<Item> stale = CampfireAreaHelpers.FindFreeItemsWithin(searchCenter, radius, includeBackpacks: true);
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
                log.Trace($"WorldItemRestore: cleared {destroyed} naturally-spawned item(s) within {radius}m of {searchCenter}.");
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

                    // Always frozen in place, never a live physics object; no velocity/trajectory is ever saved.
                    if (spawned.TryGetComponent<PhotonView>(out PhotonView view))
                        view.RPC("SetKinematicRPC", RpcTarget.AllBuffered, true, spawnPos, spawnRot);

                    // A dropped backpack's contents must land in the SAME ItemInstanceData push
                    // as its own values; populating BackpackData after the fact would race
                    // PushItemInstanceData's RPC and get silently discarded.
                    if ((saved.values != null && saved.values.Count > 0) || (saved.backpackContents != null && saved.backpackContents.Count > 0))
                    {
                        ItemInstanceData instanceData = CampfireAreaHelpers.BuildItemInstanceData(saved.values, log);
                        if (saved.backpackContents != null && saved.backpackContents.Count > 0)
                            PopulateBackpackContents(instanceData, saved.backpackContents, log);
                        CampfireAreaHelpers.PushItemInstanceData(spawned, instanceData, log);
                    }

                    restored++;
                }
                log.Trace($"WorldItemRestore: restored {restored}/{data.worldItemStates.Count} saved item(s) within {radius}m of {searchCenter}.");
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
                    foreach (var kv in OwnItemStateIO.ReadItemStateValues(slot.data))
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
