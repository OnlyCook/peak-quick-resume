using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using Newtonsoft.Json;
using Peak.Network;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our own port of <c>LoadInventoryDelayed</c> (decompile 2781-2968) plus the
    /// functions it calls - <c>LoadPlayerInventory</c> (3070-3138),
    /// <c>LoadBackpackFromSave</c>/<c>GetBackpackData</c> (3140-3211),
    /// <c>AddItemToInventory</c>/<c>AddItemToInventory_GetSlot</c> (3213-3256,
    /// 3482-3525). <see cref="RestoreAll"/> covers the FULL per-player loop
    /// (inventory/backpack restore - M4; afflictions/skeleton/extra-stamina restore
    /// and time-played sync - M5) plus the post-loop cleanup, called as a
    /// fire-and-forget coroutine from <see cref="OwnTeleportSequence"/> exactly like
    /// the original starts <c>LoadInventoryDelayed</c> without yielding on it
    ///
    /// Deliberately NOT ported (documented, not silent): the checkpoint mod's own
    /// "Loading savegame..."/"Save game loaded!" UI captions and the hero-title
    /// banner sequence are cosmetic only - <c>ResumeOrchestrator</c> already shows
    /// its own completion message right after starting this coroutine (same timing
    /// quirk the original has too: its own message only fires once THIS coroutine
    /// finishes, seconds after the restore was kicked off, same as ours), so a
    /// second, redundant caption would be dead weight, consistent with the same call
    /// made for the loading caption in M3. One-time-load (Hardmode) file deletion is
    /// also not ported - <c>configOnetimeLoad</c> isn't ported yet (see
    /// <see cref="OwnLoadEntryPoints.OneTimeLoadEnabled"/>), so this can never
    /// trigger for us regardless
    /// </summary>
    public static class OwnInventoryRestore
    {
        private static MethodInfo _playerAddItemMethod;

        /// <summary>
        /// Mirrors the per-player loop in <c>LoadInventoryDelayed</c> (decompile
        /// 2789-2956) in full, plus its post-loop cleanup (decompile 2966-2969).
        /// Each player's own save file is re-read independently (matching the
        /// original exactly - not reusing the segment-level <c>OwnSaveData</c>
        /// passed into <see cref="OwnTeleportSequence"/>), since in coop each player
        /// has their own file. Solo has exactly one player, so this reads the same
        /// file back a second time - harmless, matches original
        /// </summary>
        public static IEnumerator RestoreAll(SaveTarget target, bool offline, PluginConfig cfg, OwnLoadEntryPoints entryPoints, ManualLogSource log)
        {
            for (int i = 0; i < 60; i++) yield return null;

            foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
            {
                Character ch = player.character;
                if (ch == null) continue;

                string userId = offline ? "" : NetworkingUtilities.GetUserId(ch.player);
                PhotonView playerView = player.GetComponent<PhotonView>();

                OwnSaveData data = null;
                try
                {
                    string path = OwnSavePaths.For(target, offline, userId);
                    data = JsonConvert.DeserializeObject<OwnSaveData>(File.ReadAllText(path));
                }
                catch { /* matches the original: null data below is handled per-field */ }

                ch.refs.afflictions.RemoveAllThorns();

                if (cfg.OwnInventory.Value && data != null)
                {
                    if (ch.player.itemSlots != null)
                    {
                        foreach (ItemSlot slot in ch.player.itemSlots)
                        {
                            if (slot == null) continue;
                            try { slot.EmptyOut(); } catch { /* matches the original's own swallow */ }
                        }
                    }
                    if (ch.player.backpackSlot.hasBackpack)
                    {
                        try { ((ItemSlot)ch.player.backpackSlot).EmptyOut(); }
                        catch { /* matches the original's own swallow */ }
                    }

                    for (int k = 0; k < 30; k++) yield return null;

                    LoadPlayerInventory(data, ch.player, ch, playerView, cfg, log);
                    if (playerView != null && playerView.Owner != null && data.backpackItemStates.Count > 0)
                        LoadBackpackFromSave(ch.player, data, cfg, log);
                }

                // M5: afflictions/skeleton/extra-stamina restore (decompile 2845-2934).
                // Solo-only branch ported; the coop (RPC_ApplyAfflictions) branch is
                // M7's job, same as everywhere else coop-specific in this port
                if (cfg.OwnAfflictions.Value && data != null && offline)
                {
                    try
                    {
                        try { ch.data.SetSkeleton(data.isSkeleton); }
                        catch { /* matches the original's own swallow */ }

                        try { ch.SetExtraStamina(data.extraStamina > 0f && data.extraStamina <= 1f ? data.extraStamina : 0f); }
                        catch { /* matches the original's own swallow */ }

                        CharacterAfflictions afflictions = ch.refs.afflictions;
                        if (data.afflictions_current != null && afflictions.currentStatuses != null
                            && afflictions.currentStatuses.Length == data.afflictions_current.Length)
                        {
                            Array.Copy(data.afflictions_current, afflictions.currentStatuses, afflictions.currentStatuses.Length);
                        }
                    }
                    catch { /* matches the original's own outer swallow */ }
                }

                // M5: decompile 2935-2946 (SendSyncInventory is coop-only, M7's job)
                for (int k = 0; k < 20; k++) yield return null;
                for (int k = 0; k < 20; k++) yield return null;

                // M5: time-played sync (decompile 2947-2955)
                if (ch.photonView != null && ch.photonView.Owner != null && ch.photonView.Owner.IsMasterClient && data != null && data.timePlayed > 0f)
                {
                    RunManager runManager = UnityEngine.Object.FindFirstObjectByType<RunManager>();
                    if (runManager != null)
                    {
                        runManager.timeSinceRunStarted = data.timePlayed;
                        typeof(RunManager).GetMethod("SyncTimeMaster", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?.Invoke(runManager, null);
                    }
                }
            }

            // M5: post-loop cleanup (decompile 2957-2969; LoadingScreen(false)/
            // RPC_Loadingscreen are the cosmetic caption skipped since M3, not ported)
            for (int i = 0; i < 30; i++) yield return null;
            entryPoints?.MarkNotCurrentlyLoading();
            entryPoints?.ArmRecentlyLoadedCooldown(10f);
            entryPoints?.ArmRecentlyLitCampfireCooldown(32f);
            log?.LogInfo("OwnInventoryRestore: restore sequence complete.");
        }

        /// <summary>Mirrors LoadPlayerInventory exactly (decompile 3070-3138)</summary>
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

            if (data.hasBackpack)
            {
                try { AddItemToInventory(player, 6, log); }
                catch (Exception e)
                {
                    log?.LogWarning($"OwnInventoryRestore.LoadPlayerInventory: instantiate failed for 'Backpack': {e}");
                }
            }

            if (data.inventoryItemStates == null || data.inventoryItemStates.Count <= 0) return;

            foreach (OwnSavedItemState itemState in data.inventoryItemStates)
            {
                if (itemState == null || !AddItemToInventory_GetSlot(player, itemState.itemId, out ItemSlot createdSlot, log) || createdSlot == null)
                    continue;

                ItemInstanceData instanceData = createdSlot.data;
                if (instanceData == null || !cfg.OwnItemStats.Value) continue;

                foreach (var kv in itemState.values)
                {
                    if (!OwnItemStateIO.TryGetKey(kv.Key, out DataEntryKey key)) continue;
                    OwnSavedEntry entry = kv.Value;
                    if (entry != null && !OwnItemStateIO.TrySetOrCreateEntry(instanceData, key, entry.type, entry.value, log))
                        log?.LogWarning($"OwnInventoryRestore: could not apply '{kv.Key}' for item {itemState.itemId}.");
                }
            }
        }

        /// <summary>Mirrors LoadBackpackFromSave exactly (decompile 3140-3186)</summary>
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
                if (slotData == null || !cfg.OwnItemStats.Value) continue;

                foreach (var kv in itemState.values)
                {
                    if (!OwnItemStateIO.TryGetKey(kv.Key, out DataEntryKey key)) continue;
                    OwnSavedEntry entry = kv.Value;
                    if (entry != null) OwnItemStateIO.TrySetOrCreateEntry(slotData, key, entry.type, entry.value, log);
                }
            }

            log?.LogInfo($"OwnInventoryRestore: backpack states loaded for {NetworkingUtilities.GetUserId(player)} (items={data.backpackItemStates.Count}).");
        }

        /// <summary>Mirrors GetBackpackData exactly (decompile 3188-3211)</summary>
        public static BackpackData GetBackpackData(Player p)
        {
            if (p == null || p.backpackSlot == null || !p.backpackSlot.hasBackpack) return null;

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

        /// <summary>Mirrors AddItemToInventory exactly (decompile 3213-3256)</summary>
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

        /// <summary>Mirrors AddItemToInventory_GetSlot exactly (decompile 3482-3525)</summary>
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
