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
        /// original exactly - not reusing the level-state <c>OwnSaveData</c> passed
        /// into <see cref="OwnTeleportSequence"/>), since in coop each player has their
        /// own file. Solo has exactly one player whose file is also the host file, so
        /// this reads that same file back a second time - harmless, matches original
        ///
        /// This is the per-player HALF of a restore, and it never touches level/world
        /// state: which file each player's state comes from is resolved by
        /// <see cref="SaveSelection.TryGetPlayerFile"/>, and world state is restored
        /// separately by <see cref="OwnTeleportSequence"/> from the host's file alone
        ///
        /// <paramref name="restoringAsDead"/> (from
        /// <see cref="DeathStateRestore.ResolveSavedDeadUserIds"/>, empty in solo and
        /// whenever the setting is off) lists the players this load is putting back as a
        /// corpse - they are skipped outright, see the loop's own remarks
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

                // This player is being restored as a corpse (see DeathStateRestore): they
                // were dead when the checkpoint was written, and the death is re-applied
                // shortly after this coroutine finishes, still behind the loading screen.
                // Nothing below is worth doing for them - items, backpack, the held item,
                // afflictions, thorns and ticks are all either meaningless on a spectating
                // ghost or cleared again the moment anything ever revives them - and their
                // own save file's inventory is empty by construction anyway, since dying
                // dropped everything before that save was written. Their current slots are
                // deliberately left alone rather than emptied: that mirrors vanilla's own
                // SetDeadAfterReconnect (which also never drops), and emptying them would
                // need its own inventory resync to be visible on their machine at all.
                // Achievement progress is restored separately, earlier in the sequence, and
                // is deliberately NOT skipped - it's this run's progress either way, and it
                // still matters to them if a statue revives them later
                if (restoringAsDead != null && restoringAsDead.Contains(userId))
                {
                    log.Trace($"OwnInventoryRestore: skipping the per-player restore for '{userId}' "
                        + "- they were dead when this checkpoint was saved and are being restored as dead.");
                    continue;
                }

                // Every field read below is per-player state, and it all comes from THIS
                // player's own file within the chosen save event - never from the host's
                // file, and never from a near-miss file belonging to some other event (see
                // SaveSelection). A player with no file in this event isn't restored at
                // all: data stays null, every data-gated step below is skipped, and their
                // current, actually-correct inventory/afflictions/etc. are left alone
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

                // Silently: a plain RemoveAllThorns() counts as the player pulling every
                // thorn/arrow out, which applies Injury and plays the impact sound once
                // per arrow. See ThornsAndTicksRestore.ClearThornsSilently
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

                // v2.0.0: split off its own restore-player-temp-slot toggle (was bundled
                // under RestoreInventory before) - not part of the original's own
                // EmptyOut sweep (it never touched tempFullSlot at all - see class
                // remarks), clearing it here is new, needed so a stale held item from
                // before the reload can't survive underneath whatever LoadHeldItem
                // restores into it below
                if (cfg.RestorePlayerTempSlot.Value && data != null)
                {
                    try { ch.player.tempFullSlot?.EmptyOut(); }
                    catch { /* matches the original's own swallow */ }

                    // Equipping the restored item into slot 250 (below, after the
                    // SyncInventoryRPC wait) requires EquipSlot to run ON THE OWNING
                    // CLIENT's own machine - for a remote (non-host) player that's only
                    // reachable via OUR OWN RPC_EquipHeldItem (see the equip step's own
                    // remarks), which a player NOT running Quick Resume has no receiver
                    // for at all. Populating tempFullSlot via the vanilla SyncInventoryRPC
                    // alone (still perfectly happy to sync to ANY client, modded or not)
                    // without ever being able to equip it left a real vanilla client with
                    // the item showing in the UI, nothing in hand, and the slot
                    // permanently "reserved" - blocking any further pickup into it for the
                    // rest of the run. Confirmed via a real session report (host-only
                    // install, vanilla client, 2026-07-25). Skip restoring the held item
                    // at all for a player we can't confirm will ever equip it - leaving
                    // their tempFullSlot empty (already done above) is a real loss (that
                    // one item), but strictly better than the broken reserved-but-empty
                    // state
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

                // M5/M7: afflictions/skeleton/extra-stamina restore (decompile 2845-2934).
                // Offline: applied directly to the (only) local character. Coop: the
                // host can't write another client's Character fields locally in any way
                // that's actually visible to that client, so it RPCs RPC_ApplyAfflictions
                // to that player's own owner instead (decompile 2889-2933) - the skeleton
                // flag is the one exception, applied directly here since it's master-
                // authoritative networked state, not local-only Character data
                if (cfg.RestoreAfflictions.Value && data != null && offline)
                {
                    try
                    {
                        try { ch.data.SetSkeleton(data.isSkeleton); }
                        catch { /* matches the original's own swallow */ }

                        try { ch.SetExtraStamina(data.extraStamina > 0f && data.extraStamina <= 1f ? data.extraStamina : 0f); }
                        catch { /* matches the original's own swallow */ }

                        // Length-tolerant on purpose: 2.0.a appended three STATUSTYPEs, so
                        // an exact-length guard here would skip the whole restore for
                        // every save written before it. See AfflictionArrayCompat
                        CharacterAfflictions afflictions = ch.refs.afflictions;
                        AfflictionArrayCompat.CopyOverlap(data.afflictions_current, afflictions.currentStatuses);
                    }
                    catch { /* matches the original's own outer swallow */ }
                }
                else if (cfg.RestoreAfflictions.Value && data != null && !offline && PhotonNetwork.IsMasterClient && data.afflictions_current != null)
                {
                    try { ch.data.SetSkeleton(data.isSkeleton); }
                    catch { /* matches the original's own swallow */ }

                    try
                    {
                        if (playerView != null)
                            entryPoints?.Network?.ApplyAfflictionsTo(playerView, userId, data.afflictions_current, data.extraStamina);
                        else
                            log?.LogWarning("OwnInventoryRestore: Player has no PhotonView, cannot send afflictions RPC.");
                    }
                    catch (Exception e)
                    {
                        log?.LogWarning($"OwnInventoryRestore: failed to send afflictions RPC: {e.Message}");
                    }
                }

                // Physical thorns restore (own addition, no decompile counterpart - see
                // OwnSaveData.stuckThornIndices/ThornsAndTicksRestore's remarks).
                // Deliberately does NOT touch STATUSTYPE.Thorns directly - it's recomputed
                // every frame purely from which physicalThorns are stuckIn, so setting it
                // ourselves would just be overwritten within a frame; re-adding the
                // physical thorns via AddThorn is what brings the correct status level
                // back on its own. RemoveAllThorns() near the top of this loop (pre-
                // existing, unconditional) already cleared any stale thorns from before
                // the reload. Must run AFTER the skeleton restore above - AddThorn no-ops
                // for skeletons - and on the OWNING client, same offline/coop-RPC split as
                // the held-item equip step below
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

                // Tick (Bugfix) restore (own addition, no decompile counterpart - see
                // ThornsAndTicksRestore's remarks). Unlike thorns, no owner-side RPC
                // needed: any client (the host, here) can PhotonNetwork.Instantiate the
                // room object and broadcast AttachBug, exactly like vanilla's own
                // TickTrigger does it. Clears any leftover tick first - defensive, since
                // Bugfix.AllAttachedBugs is static/global, not scene-scoped, so a stale
                // entry could otherwise theoretically survive a level reload
                if (cfg.RestorePlayerEntities.Value && data != null)
                {
                    try { ThornsAndTicksRestore.RemoveExistingTick(ch, log); }
                    catch (Exception e) { log?.LogWarning($"OwnInventoryRestore: tick cleanup failed: {e.Message}"); }

                    if (data.hasTick)
                        ThornsAndTicksRestore.ApplyTick(ch, log);
                }

                // Mirrors decompile 2939-2942 (SendSyncInventory, coop-only): a vanilla
                // game RPC on the player's own PhotonView, no checkpoint-mod/OwnNetwork
                // dependency - resyncs the inventory writes above (made authoritatively
                // by the host, possibly onto ANOTHER player's slots) to that player's
                // own client and everyone else's view of them
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

                // New restore step, not present in the original at all (see
                // OwnSaveData.heldItemState remarks): the data LoadHeldItem wrote into
                // tempFullSlot above only sits in the inventory until something actively
                // equips it - vanilla only ever reaches slot 250 via a live pickup, which
                // always immediately calls CharacterItems.EquipSlot(250) right after (see
                // OnPickupAccepted, decompile ~6320-6346). Skipping that step left the
                // held item showing in the UI's item data but not actually spawned
                // in-hand, not blocking climbing, and silently overwritten by the next
                // pickup - confirmed exactly this in-game. EquipSlot is what spawns the
                // physical held item and sets currentSelectedSlot/currentItem. Must run
                // on the OWNING client: EquipSlot's own network spawn + RPC are gated on
                // photonView.IsMine, so calling it from the host for another player's
                // Character would silently no-op over the network - same problem/solution
                // shape as the afflictions branch above. Sent after the SyncInventoryRPC
                // wait above so a remote client's own local tempFullSlot copy is already
                // populated by the time it runs EquipSlot itself
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

            // M5: time-played sync (decompile 2947-2955). Run-level state, so it's read
            // from the HOST's file and applied once - not per player. The original had
            // this inside the loop guarded on "is this the master's character", which
            // amounted to the same single application; pulling it out makes it read the
            // right file by construction rather than by relying on that guard now that a
            // client's own file no longer carries run-level fields at all
            RestoreTimePlayed(selection, log);

            // M5/M7: post-loop cleanup (decompile 2957-2969). The "Loading savegame..."/
            // "Save game loaded!" captions themselves stay skipped (cosmetic, see M3/M5),
            // but that same moment is also the ONLY signal that ends TeleportWatchdog's
            // load window on every machine (see OwnNetwork's RPC_Loadingscreen remarks) -
            // ArmPendingWatch() is a direct local call since RpcTarget.Others never
            // reaches the sender itself
            for (int i = 0; i < 30; i++) yield return null;
            entryPoints?.MarkNotCurrentlyLoading();
            entryPoints?.ArmRecentlyLoadedCooldown(10f);
            entryPoints?.ArmRecentlyLitCampfireCooldown(32f);
            // End the watch window and forward the host's real teleport target to clients so
            // a client that never got warped can still recover to it rather than only seeing
            // the on-screen hint (see TeleportWatchdog.ArmPendingWatch)
            var watchdog = entryPoints?.Network?.Watchdog;
            watchdog?.ArmPendingWatch();
            entryPoints?.Network?.LoadingScreenOthers(false, watchdog?.KnownTarget);
            log?.LogInfo("OwnInventoryRestore: restore sequence complete.");
        }

        // Applies the saved run timer from the selection's host file, see the call site
        private static void RestoreTimePlayed(SaveSelection selection, ManualLogSource log)
        {
            try
            {
                if (string.IsNullOrEmpty(selection.HostFilePath) || !File.Exists(selection.HostFilePath)) return;

                var hostData = JsonConvert.DeserializeObject<OwnSaveData>(File.ReadAllText(selection.HostFilePath));
                if (hostData == null || hostData.timePlayed <= 0f) return;

                RunManager runManager = UnityEngine.Object.FindFirstObjectByType<RunManager>();
                if (runManager == null) return;

                // 2.0.a made the backing field private behind a read-only property, so
                // this can only be written by reflection now - see RunTimerCompat
                RunTimerCompat.TryWrite(runManager, hostData.timePlayed, log);
            }
            catch (Exception e)
            {
                log?.LogWarning($"OwnInventoryRestore: time-played sync failed (non-fatal): {e.Message}");
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

            // 2.0.a: a backpack is no longer one fixed item - it's whichever of
            // Backpack/Fannypack/Jetpack/Rocketpack was worn, so the ID is resolved from
            // the saved variant instead of the hardcoded 6 this used to pass (which now
            // means "a plain Backpack" specifically, not "the backpack item").
            // Player.AddItem stamps backpackSlot.backpackType from the prefab itself
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
        /// Puts the worn backpack's own stats back onto the slot that
        /// <c>Player.AddItem</c> just filled - in practice a Jetpack's/Rocketpack's
        /// <c>DataEntryKey.Fuel</c>. Must run AFTER the backpack has been added, since the
        /// slot's ItemInstanceData only exists from that point on; a freshly granted
        /// backpack otherwise carries the prefab's default (full) fuel.
        ///
        /// Gated on the same RestoreItemStats toggle as every other item's stats, and
        /// silently does nothing for saves written before backpackOwnValues existed
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
        /// New restore, not a port (see OwnSaveData.heldItemState remarks): puts the
        /// saved 4th item straight into Player.tempFullSlot via ItemSlot.SetItem,
        /// rather than going through Player.AddItem's slot-selection logic (which would
        /// require the 3 regular slots to already be full and reflection to invoke) -
        /// direct and unconditional, so the held item lands back in the hand slot even
        /// if one of the 3 regular-slot restores above happened to fail. Same shape as
        /// LoadBackpackFromSave's direct BackpackData.AddItem call. Only sets the DATA -
        /// the actual equip step (spawning it in-hand, setting currentSelectedSlot) is
        /// done separately by <see cref="RestoreAll"/> once this player's inventory sync
        /// has gone out, see its own remarks for why that has to happen later and on the
        /// owning client
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

        /// <summary>Mirrors GetBackpackData exactly (decompile 3188-3211)</summary>
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
