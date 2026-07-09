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
    /// Phase 8 milestone M4: our own port of the inventory/backpack-restoring half of
    /// <c>LoadInventoryDelayed</c> (decompile 2781-2844) plus the functions it calls -
    /// <c>LoadPlayerInventory</c> (3070-3138), <c>LoadBackpackFromSave</c>/
    /// <c>GetBackpackData</c> (3140-3211), <c>AddItemToInventory</c>/
    /// <c>AddItemToInventory_GetSlot</c> (3213-3256, 3482-3525)
    ///
    /// The REMAINING pieces of <c>LoadInventoryDelayed</c> (afflictions/skeleton/
    /// extra-stamina restore, time-played sync, the "Save game loaded!"
    /// message/hero-title banner, one-time-load file deletion,
    /// <c>currentlyLoading</c>/<c>RecentlyLoaded</c> cleanup) are NOT ported yet -
    /// that's M5. <see cref="RestoreAll"/> mirrors ONLY decompile lines 2781-2844
    /// (the 60-frame wait, per-player loop, thorn removal, and the inventory/backpack
    /// restore itself), called as a fire-and-forget coroutine from
    /// <see cref="OwnTeleportSequence"/> exactly like the original starts
    /// <c>LoadInventoryDelayed</c> without yielding on it
    /// </summary>
    public static class OwnInventoryRestore
    {
        private static MethodInfo _playerAddItemMethod;

        /// <summary>
        /// Mirrors the per-player loop in <c>LoadInventoryDelayed</c> (decompile
        /// 2789-2844) for JUST inventory/backpack restore. Each player's own save
        /// file is re-read independently (matching the original exactly - not reusing
        /// the segment-level <c>OwnSaveData</c> passed into <see cref="OwnTeleportSequence"/>),
        /// since in coop each player has their own file. Solo has exactly one player,
        /// so this reads the same file back a second time - harmless, matches original
        /// </summary>
        public static IEnumerator RestoreAll(SaveTarget target, bool offline, PluginConfig cfg, ManualLogSource log)
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
            }
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
