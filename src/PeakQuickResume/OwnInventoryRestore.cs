using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Newtonsoft.Json;
using Peak.Network;
using Photon.Pun;
using UnityEngine;
using Zorro.Core;
using Zorro.Core.Serizalization;

namespace PEAKQuickResume
{
    /// <summary>
    /// Restores per-player inventory/backpack/afflictions state and run time-played
    /// after a load. <see cref="RestoreAll"/> runs the full per-player loop plus
    /// post-loop cleanup as a fire-and-forget coroutine from <see cref="OwnTeleportSequence"/>.
    /// Deliberately does not port the checkpoint mod's "Loading savegame..." UI captions
    /// (redundant with <c>ResumeOrchestrator</c>'s own completion message) or one-time-load
    /// file deletion (not applicable since that config toggle isn't ported).
    /// </summary>
    public static class OwnInventoryRestore
    {
        private static MethodInfo _playerAddItemMethod;

        /// <summary>
        /// Per-player restore loop. Each player's own save file is re-read independently
        /// via <see cref="SaveSelection.TryGetPlayerFile"/> (level/world state is restored
        /// separately by <see cref="OwnTeleportSequence"/> from the host's file).
        /// <paramref name="restoringAsDead"/> lists players being put back as a corpse; they're skipped outright.
        /// </summary>
        public static IEnumerator RestoreAll(SaveSelection selection, PluginConfig cfg, OwnLoadEntryPoints entryPoints, ManualLogSource log,
            HashSet<string> restoringAsDead = null)
        {
            for (int i = 0; i < 60; i++) yield return null;

            bool offline = selection.Offline;

            foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
            {
                Character ch = player.character;
                if (ch == null) continue;

                string userId = offline ? "" : NetworkingUtilities.GetUserId(ch.player);
                PhotonView playerView = player.GetComponent<PhotonView>();

                // Restored as a corpse (see DeathStateRestore): death is re-applied shortly
                // after this coroutine finishes, so items/afflictions/thorns restore is
                // skipped entirely. Slots are left alone rather than emptied (mirrors
                // vanilla's SetDeadAfterReconnect). Achievement progress is NOT skipped here.
                if (restoringAsDead != null && restoringAsDead.Contains(userId))
                {
                    log.Trace($"OwnInventoryRestore: skipping the per-player restore for '{userId}' "
                        + "- they were dead when this checkpoint was saved and are being restored as dead.");
                    continue;
                }

                // Per-player state comes from this player's own file in the chosen save event
                // (never the host's file). No file means no restore; data stays null and
                // every data-gated step below is skipped.
                OwnSaveData data = null;
                if (selection.TryGetPlayerFile(userId, out string path))
                {
                    try
                    {
                        data = JsonConvert.DeserializeObject<OwnSaveData>(File.ReadAllText(path));
                    }
                    catch (Exception e)
                    {
                        log?.LogWarning($"OwnInventoryRestore: could not read the save for '{userId}': {e.Message}");
                    }
                }
                else
                {
                    log.Trace($"OwnInventoryRestore: skipping restore for '{userId}' - they have no save file "
                        + "in this checkpoint's save event; leaving their current state untouched.");
                }

                ThornsAndTicksRestore.ClearThornsSilently(ch, log);

                if (cfg.RestoreInventory.Value && data != null)
                {
                    if (ch.player.itemSlots != null)
                    {
                        foreach (ItemSlot slot in ch.player.itemSlots)
                        {
                            if (slot == null) continue;
                            try { slot.EmptyOut(); } catch { /* matches the original's own swallow */ }
                        }
                    }
                    if (BackpackTypeCompat.HasAny(ch.player.backpackSlot))
                    {
                        try { ((ItemSlot)ch.player.backpackSlot).EmptyOut(); }
                        catch { /* matches the original's own swallow */ }
                    }

                    for (int k = 0; k < 30; k++) yield return null;

                    LoadPlayerInventory(data, ch.player, ch, playerView, cfg, log);
                    if (playerView != null && playerView.Owner != null && data.backpackItemStates.Count > 0)
                        LoadBackpackFromSave(ch.player, data, cfg, log);
                }

                if (cfg.RestorePlayerTempSlot.Value && data != null)
                {
                    try { ch.player.tempFullSlot?.EmptyOut(); }
                    catch { /* matches the original's own swallow */ }

                    // Equipping requires EquipSlot to run on the owning client, which for a
                    // remote player needs our own RPC_EquipHeldItem - unreachable for a player
                    // not running Quick Resume. Restoring anyway would leave a real vanilla
                    // client with the item shown but unequipped and the slot permanently
                    // "reserved" (confirmed via session report). Skip instead.
                    bool canEquipRemotely = offline
                        || (playerView != null && playerView.IsMine)
                        || (entryPoints?.Network?.PlayerReportedMod(userId) ?? false);

                    if (data.heldItemState != null && canEquipRemotely)
                    {
                        try { LoadHeldItem(data.heldItemState, ch.player, cfg, log); }
                        catch (Exception e) { log?.LogWarning($"OwnInventoryRestore: held-item restore failed: {e.Message}"); }
                    }
                    else if (data.heldItemState != null)
                    {
                        log.Trace($"OwnInventoryRestore: skipping held-item restore for '{userId}' "
                            + "- not confirmed to be running Quick Resume, would leave the slot reserved but empty.");
                    }
                }

                // Offline: applied directly to the local character. Coop: the host can't
                // write another client's Character fields locally, so it RPCs instead
                // (skeleton flag is the exception, since it's master-authoritative networked state).
                if (cfg.RestoreAfflictions.Value && data != null && offline)
                {
                    try
                    {
                        try { ch.data.SetSkeleton(data.isSkeleton); }
                        catch { /* matches the original's own swallow */ }

                        try { ch.SetExtraStamina(data.extraStamina > 0f && data.extraStamina <= 1f ? data.extraStamina : 0f); }
                        catch { /* matches the original's own swallow */ }

                        // Length-tolerant: an exact-length guard would skip saves written
                        // before 2.0.a appended three STATUSTYPEs. See AfflictionArrayCompat.
                        CharacterAfflictions afflictions = ch.refs.afflictions;
                        AfflictionArrayCompat.CopyOverlap(data.afflictions_current, afflictions.currentStatuses);

                        // Bypasses currentStatuses entirely (see OwnSaveData.petrifyAmount) - restored
                        // after extraStamina since SetPetrify reclamps it against the new petrify cap.
                        if (cfg.RestorePetrify.Value)
                        {
                            try { ch.data.SetPetrify(data.petrifyAmount); }
                            catch { /* matches the original's own swallow */ }
                        }
                    }
                    catch { /* matches the original's own outer swallow */ }
                }
                else if (cfg.RestoreAfflictions.Value && data != null && !offline && PhotonNetwork.IsMasterClient && data.afflictions_current != null)
                {
                    try { ch.data.SetSkeleton(data.isSkeleton); }
                    catch { /* matches the original's own swallow */ }

                    try
                    {
                        // Vanilla's master-to-client push first (so a client without this mod
                        // still gets afflictions back), then ours overwrites with the exact
                        // saved values (and extraStamina, which vanilla can't set) on modded clients.
                        bool viaVanilla = entryPoints?.Network?.ApplyStatusesViaVanilla(ch, data.afflictions_current) ?? false;

                        // Host-authoritative, like every other restore toggle here (RestoreAll
                        // only ever runs on the host) - a disabled setting sends 0 rather than
                        // skipping the RPC param entirely, which also clears any petrify a
                        // client happened to already have live at the moment of load.
                        int petrifyToApply = cfg.RestorePetrify.Value ? data.petrifyAmount : 0;

                        if (playerView != null)
                            entryPoints?.Network?.ApplyAfflictionsTo(playerView, userId, data.afflictions_current, data.extraStamina, petrifyToApply);
                        else if (!viaVanilla)
                            log?.LogWarning("OwnInventoryRestore: Player has no PhotonView, cannot send afflictions RPC.");
                    }
                    catch (Exception e)
                    {
                        log?.LogWarning($"OwnInventoryRestore: failed to send afflictions RPC: {e.Message}");
                    }
                }

                // Doesn't touch STATUSTYPE.Thorns directly - it's recomputed every frame from
                // stuckIn physicalThorns, so re-adding via AddThorn is what restores the status.
                // Must run after the skeleton restore above (AddThorn no-ops for skeletons).
                if (cfg.RestorePlayerEntities.Value && data != null && data.stuckThornIndices != null && data.stuckThornIndices.Count > 0)
                {
                    try
                    {
                        if (offline || (playerView != null && playerView.IsMine))
                            ThornsAndTicksRestore.ApplyThorns(ch, data.stuckThornIndices, log);
                        else if (PhotonNetwork.IsMasterClient && playerView != null)
                            entryPoints?.Network?.RestoreThornsFor(playerView, userId, data.stuckThornIndices.Select(i => (int)i).ToArray());
                    }
                    catch (Exception e)
                    {
                        log?.LogWarning($"OwnInventoryRestore: thorn restore failed: {e.Message}");
                    }
                }

                // Unlike thorns, no owner-side RPC needed here (any client can Instantiate +
                // broadcast, like vanilla's TickTrigger). Clears any leftover tick first since
                // Bugfix.AllAttachedBugs is static/global and could survive a level reload.
                if (cfg.RestorePlayerEntities.Value && data != null)
                {
                    try { ThornsAndTicksRestore.RemoveExistingTick(ch, log); }
                    catch (Exception e) { log?.LogWarning($"OwnInventoryRestore: tick cleanup failed: {e.Message}"); }

                    if (data.hasTick)
                        ThornsAndTicksRestore.ApplyTick(ch, log);
                }

                // Resyncs the inventory writes above (made authoritatively by the host,
                // possibly onto another player's slots) via vanilla's own RPC.
                for (int k = 0; k < 20; k++) yield return null;
                if (!offline)
                {
                    try
                    {
                        if (playerView != null)
                        {
                            var syncData = new InventorySyncData(ch.player.itemSlots, ch.player.backpackSlot, ch.player.tempFullSlot);
                            playerView.RPC("SyncInventoryRPC", RpcTarget.Others, IBinarySerializable.ToManagedArray(syncData), true);
                        }
                    }
                    catch (Exception e)
                    {
                        log?.LogWarning($"OwnInventoryRestore: SendSyncInventory failed: {e.Message}");
                    }
                }
                for (int k = 0; k < 20; k++) yield return null;

                // Vanilla only reaches slot 250 via a live pickup, which always immediately
                // calls EquipSlot(250) right after. Without this step the held item showed in
                // UI but wasn't spawned in-hand, and got silently overwritten by the next pickup
                // (confirmed in-game). Must run on the owning client - EquipSlot's network spawn
                // is gated on photonView.IsMine - after the SyncInventoryRPC wait above so the
                // remote client's tempFullSlot copy is already populated.
                if (cfg.RestorePlayerTempSlot.Value && data != null && data.heldItemState != null
                    && ch.player?.tempFullSlot != null && !ch.player.tempFullSlot.IsEmpty())
                {
                    try
                    {
                        if (offline || (playerView != null && playerView.IsMine))
                            ch.refs.items.EquipSlot(Optionable<byte>.Some((byte)250));
                        else if (PhotonNetwork.IsMasterClient && playerView != null)
                            entryPoints?.Network?.EquipHeldItemFor(playerView, userId);
                    }
                    catch (Exception e)
                    {
                        log?.LogWarning($"OwnInventoryRestore: held-item equip failed: {e.Message}");
                    }
                }

            }

            // Run-level state, read from the host's file and applied once, not per player.
            RestoreTimePlayed(selection, log);

            for (int i = 0; i < 30; i++) yield return null;
            entryPoints?.MarkNotCurrentlyLoading();
            entryPoints?.ArmRecentlyLoadedCooldown(10f);
            entryPoints?.ArmRecentlyLitCampfireCooldown(32f);
            // Ends the watch window and forwards the host's real teleport target to clients so
            // one that never got warped can still recover to it, not just see the on-screen hint.
            var watchdog = entryPoints?.Network?.Watchdog;
            watchdog?.ArmPendingWatch();
            entryPoints?.Network?.LoadingScreenOthers(false, watchdog?.KnownTarget);
            log?.LogInfo("OwnInventoryRestore: restore sequence complete.");
        }

        private static void RestoreTimePlayed(SaveSelection selection, ManualLogSource log)
        {
            try
            {
                if (string.IsNullOrEmpty(selection.HostFilePath) || !File.Exists(selection.HostFilePath)) return;

                var hostData = JsonConvert.DeserializeObject<OwnSaveData>(File.ReadAllText(selection.HostFilePath));
                if (hostData == null || hostData.timePlayed <= 0f) return;

                RunManager runManager = UnityEngine.Object.FindFirstObjectByType<RunManager>();
                if (runManager == null) return;

                RunTimerCompat.TryWrite(runManager, hostData.timePlayed, log);
            }
            catch (Exception e)
            {
                log?.LogWarning($"OwnInventoryRestore: time-played sync failed (non-fatal): {e.Message}");
            }
        }

        public static void LoadPlayerInventory(OwnSaveData data, Player player, Character ch, PhotonView playerView, PluginConfig cfg, ManualLogSource log)
        {
            if (player == null)
            {
                log?.LogWarning("OwnInventoryRestore.LoadPlayerInventory: no player.");
                return;
            }
            if (ch == null || ch.photonView == null)
            {
                log?.LogWarning("OwnInventoryRestore.LoadPlayerInventory: missing Character or photonView.");
                return;
            }

            // A backpack is one of Backpack/Fannypack/Jetpack/Rocketpack; resolve the ID from
            // the saved variant. Player.AddItem stamps backpackSlot.backpackType from the prefab.
            int savedBackpackType = BackpackTypeCompat.FromSave(data.hasBackpack, data.backpackType);
            try
            {
                if (BackpackTypeCompat.TryResolveItemId(savedBackpackType, out ushort backpackItemId, log))
                {
                    AddItemToInventory(player, backpackItemId, log);
                    RestoreBackpackOwnValues(data, player, cfg, log);
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"OwnInventoryRestore.LoadPlayerInventory: backpack restore failed for type {savedBackpackType}: {e}");
            }

            if (data.inventoryItemStates != null && data.inventoryItemStates.Count > 0)
            {
                foreach (OwnSavedItemState itemState in data.inventoryItemStates)
                {
                    if (itemState == null || !AddItemToInventory_GetSlot(player, itemState.itemId, out ItemSlot createdSlot, log) || createdSlot == null)
                        continue;

                    ItemInstanceData instanceData = createdSlot.data;
                    if (instanceData == null || !cfg.RestoreItemStats.Value) continue;

                    foreach (var kv in itemState.values)
                    {
                        if (!OwnItemStateIO.TryGetKey(kv.Key, out DataEntryKey key)) continue;
                        OwnSavedEntry entry = kv.Value;
                        if (entry != null && !OwnItemStateIO.TrySetOrCreateEntry(instanceData, key, entry.type, entry.value, log))
                            log?.LogWarning($"OwnInventoryRestore: could not apply '{kv.Key}' for item {itemState.itemId}.");
                    }
                }
            }
        }

        /// <summary>
        /// Puts the worn backpack's own stats (e.g. Jetpack/Rocketpack fuel) back onto the
        /// slot <c>Player.AddItem</c> just filled. Must run after the backpack is added,
        /// since its ItemInstanceData only exists from that point on.
        /// </summary>
        private static void RestoreBackpackOwnValues(OwnSaveData data, Player player, PluginConfig cfg, ManualLogSource log)
        {
            if (data.backpackOwnValues == null || data.backpackOwnValues.Count == 0) return;
            if (cfg == null || !cfg.RestoreItemStats.Value) return;

            ItemInstanceData instanceData = player?.backpackSlot?.data;
            if (instanceData == null)
            {
                log?.LogWarning("OwnInventoryRestore: the restored backpack has no instance data; its own stats (e.g. fuel) were not applied.");
                return;
            }

            foreach (var kv in data.backpackOwnValues)
            {
                if (!OwnItemStateIO.TryGetKey(kv.Key, out DataEntryKey key)) continue;
                OwnSavedEntry entry = kv.Value;
                if (entry != null && !OwnItemStateIO.TrySetOrCreateEntry(instanceData, key, entry.type, entry.value, log))
                    log?.LogWarning($"OwnInventoryRestore: could not apply worn-backpack stat '{kv.Key}'.");
            }
        }

        /// <summary>
        /// Puts the saved 4th item directly into Player.tempFullSlot via ItemSlot.SetItem,
        /// bypassing Player.AddItem's slot-selection logic. Only sets the data - the actual
        /// equip step is done separately by <see cref="RestoreAll"/>, later and on the owning client.
        /// </summary>
        private static void LoadHeldItem(OwnSavedItemState itemState, Player player, PluginConfig cfg, ManualLogSource log)
        {
            if (player?.tempFullSlot == null) return;
            if (!ItemDatabase.TryGetItem(itemState.itemId, out Item item) || item == null)
            {
                log?.LogWarning($"OwnInventoryRestore: held item {itemState.itemId} not found in ItemDatabase, skipping.");
                return;
            }

            ItemInstanceData instanceData = new ItemInstanceData(Guid.NewGuid());
            ItemInstanceDataHandler.AddInstanceData(instanceData);
            player.tempFullSlot.SetItem(item, instanceData);

            if (!cfg.RestoreItemStats.Value) return;
            foreach (var kv in itemState.values)
            {
                if (!OwnItemStateIO.TryGetKey(kv.Key, out DataEntryKey key)) continue;
                OwnSavedEntry entry = kv.Value;
                if (entry != null && !OwnItemStateIO.TrySetOrCreateEntry(instanceData, key, entry.type, entry.value, log))
                    log?.LogWarning($"OwnInventoryRestore: could not apply held-item '{kv.Key}' for item {itemState.itemId}.");
            }
        }

        public static void LoadBackpackFromSave(Player player, OwnSaveData data, PluginConfig cfg, ManualLogSource log)
        {
            if (player == null || data == null || data.backpackItemStates == null || data.backpackItemStates.Count == 0) return;

            BackpackData backpackData = GetBackpackData(player);
            if (backpackData == null || backpackData.itemSlots == null) return;

            foreach (OwnSavedBackpackItemState itemState in data.backpackItemStates)
            {
                if (itemState == null || itemState.slotIndex >= backpackData.itemSlots.Length
                    || !ItemDatabase.TryGetItem(itemState.itemId, out Item item) || item == null)
                    continue;

                ItemInstanceData instanceData = new ItemInstanceData(Guid.NewGuid());
                ItemInstanceDataHandler.AddInstanceData(instanceData);
                backpackData.AddItem(item, instanceData, itemState.slotIndex);

                ItemInstanceData slotData = backpackData.itemSlots[itemState.slotIndex]?.data;
                if (slotData == null || !cfg.RestoreItemStats.Value) continue;

                foreach (var kv in itemState.values)
                {
                    if (!OwnItemStateIO.TryGetKey(kv.Key, out DataEntryKey key)) continue;
                    OwnSavedEntry entry = kv.Value;
                    if (entry != null) OwnItemStateIO.TrySetOrCreateEntry(slotData, key, entry.type, entry.value, log);
                }
            }

            log.Trace($"OwnInventoryRestore: backpack states loaded for {NetworkingUtilities.GetUserId(player)} (items={data.backpackItemStates.Count}).");
        }

        public static BackpackData GetBackpackData(Player p)
        {
            if (p == null || !BackpackTypeCompat.HasAny(p)) return null;

            ItemSlot backpackSlot = p.backpackSlot;
            if (backpackSlot.data == null) return null;

            const DataEntryKey key = (DataEntryKey)7;
            if (!backpackSlot.data.TryGetDataEntry(key, out BackpackData data) || data == null)
            {
                backpackSlot.data.RegisterNewEntry<BackpackData>(key);
                backpackSlot.data.TryGetDataEntry(key, out data);
            }
            return data;
        }

        public static bool AddItemToInventory(Player player, ushort itemId, ManualLogSource log)
        {
            if (player == null) return false;
            try
            {
                if (!EnsurePlayerAddItemMethod(log)) return false;

                ItemInstanceData instanceData = new ItemInstanceData(Guid.NewGuid());
                object[] parameters = { itemId, instanceData, null };
                _playerAddItemMethod.Invoke(player, parameters);
                return true;
            }
            catch (Exception e)
            {
                log?.LogError($"OwnInventoryRestore.AddItemToInventory error: {e}");
                return false;
            }
        }

        public static bool AddItemToInventory_GetSlot(Player player, ushort itemId, out ItemSlot createdSlot, ManualLogSource log)
        {
            createdSlot = null;
            if (player == null) return false;

            try
            {
                if (!EnsurePlayerAddItemMethod(log)) return false;

                object[] parameters = { itemId, null, null };
                _playerAddItemMethod.Invoke(player, parameters);
                createdSlot = parameters[2] as ItemSlot;
                return createdSlot != null;
            }
            catch (Exception e)
            {
                log?.LogError($"OwnInventoryRestore.AddItemToInventory_GetSlot error: {e}");
                return false;
            }
        }

        private static bool EnsurePlayerAddItemMethod(ManualLogSource log)
        {
            if (_playerAddItemMethod != null) return true;

            _playerAddItemMethod = typeof(Player).GetMethod("AddItem",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(ushort), typeof(ItemInstanceData), typeof(ItemSlot).MakeByRefType() }, null);

            if (_playerAddItemMethod == null)
            {
                log?.LogError("OwnInventoryRestore: Player.AddItem(...) method not found.");
                return false;
            }
            return true;
        }
    }
}
