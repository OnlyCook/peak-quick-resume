using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using Newtonsoft.Json;
using Peak.Network;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our own port of <c>SavePlayerOffline</c>/<c>SavePlayerCoop</c> (decompile
    /// 3715-4603, ported M6/M7). Phase 8 M9: now the CANONICAL save-file writer -
    /// writes directly to <see cref="OwnSavePaths.For"/> (not a diagnostic path
    /// anymore) and independently triggers <see cref="BackpackSaveMitigation.ApplyPendingRestores"/>
    /// + <see cref="SaveArchive.Sync"/> right after writing, the same two steps
    /// <see cref="SavePatch"/>'s postfix used to apply only when the CHECKPOINT MOD's
    /// own save methods were called - our own capture needed the same trigger since
    /// it can now run with no checkpoint mod involved at all
    ///
    /// The 13 repeated per-key blocks in the original (one `if (TryGetKey(...) &amp;&amp;
    /// TryGetEntryObject(...) &amp;&amp; TryReadEntryNumeric(...))` per key, identical
    /// shape every time except two keys additionally check <c>ExcludedItemIds</c>) are
    /// collapsed into one loop over <see cref="OwnItemStateIO.ItemStateKeyNames"/> -
    /// a mechanical simplification, not a behavior change: confirmed from the
    /// decompile that every block is otherwise identical, and the exclusion check
    /// only ever applies to "ItemUses"/"UseRemainingPercentage" (decompile 3825,
    /// 3841, 3953, 3969)
    /// </summary>
    public static class OwnSaveCapture
    {
        // Matches ItemStateKeyNames entries whose exclusion is checked against
        // ExcludedItemIds when capturing (decompile: only these two)
        private static readonly HashSet<string> ExcludableKeys = new HashSet<string> { "ItemUses", "UseRemainingPercentage" };

        /// <summary>
        /// Mirrors SavePlayerCoop exactly (decompile 4139-4603). Unlike
        /// <see cref="SavePlayerOffline"/> (one local player), this saves EVERY
        /// connected player's own file in one pass - only ever actually invoked on the
        /// master client at its call sites (<see cref="CampfireAutoSavePatch"/>'s
        /// master branch, <see cref="OwnNetworkRpc.RPC_RequestSave"/>'s master-only
        /// guard), matching the original's own call-site-gated (not internally
        /// guarded) shape exactly - no internal IsMasterClient check added here either
        /// </summary>
        public static void SavePlayerCoop(PluginConfig cfg, ManualLogSource log, OwnNetwork network)
        {
            try
            {
                SaveTarget target = RunLauncher.IsCustomRun ? SaveTarget.Custom() : SaveTarget.Normal(Ascents.currentAscent);

                // Mirrors the original's own stale-coop-file cleanup (decompile 4201-4228):
                // deletes existing canonical files for this exact ascent bucket before
                // rewriting them fresh, so a player who left doesn't leave a stale file
                // behind. Matches the original's own gap exactly: only the per-ascent
                // bucket is cleaned, never the CustomRun bucket - not our bug to fix here
                if (PhotonNetwork.IsMasterClient && !target.IsCustom)
                {
                    try
                    {
                        string coopDir = Path.GetDirectoryName(OwnSavePaths.For(target, offline: false, userId: "x"))!;
                        if (Directory.Exists(coopDir))
                        {
                            foreach (string file in Directory.GetFiles(coopDir))
                            {
                                if (file.Contains($"peak_save_{target.Ascent}_"))
                                    File.Delete(file);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        log?.LogError($"OwnSaveCapture.SavePlayerCoop: error while deleting stale save files: {e}");
                    }
                }

                var playerNames = new List<string>();
                Player[] allPlayers = UnityEngine.Object.FindObjectsByType<Player>(UnityEngine.FindObjectsSortMode.None);
                foreach (Player p in allPlayers)
                {
                    if (p != null) playerNames.Add(p.character.characterName);
                }

                foreach (Player player in allPlayers)
                {
                    if (player == null)
                    {
                        log?.LogError("OwnSaveCapture.SavePlayerCoop: no Player found - cannot save progress.");
                        continue;
                    }

                    string userId = NetworkingUtilities.GetUserId(player);
                    string path = OwnSavePaths.For(target, offline: false, userId: userId);

                    Character character = player.character;
                    Vector3 pos = character != null ? character.Head : player.transform.position;
                    if (character == null)
                        log?.LogWarning("OwnSaveCapture.SavePlayerCoop: Character is null - used player.transform as fallback.");

                    string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

                    List<OwnSavedItemState> inventoryStates = CaptureInventory(player, cfg, log);
                    List<OwnSavedBackpackItemState> backpackStates = CaptureBackpack(player, cfg, log);

                    CharacterAfflictions afflictions = character.refs.afflictions;
                    float[] currentStatuses = afflictions.currentStatuses.ToArray();
                    float extraStamina = character.GetTotalStamina() - (1f - currentStatuses.Sum());
                    extraStamina = Mathf.Clamp(extraStamina, 0f, 1f);
                    extraStamina = (float)Math.Round(extraStamina, 2);

                    RunManager runManager = UnityEngine.Object.FindFirstObjectByType<RunManager>();
                    float timePlayed = (float)Math.Round(runManager.timeSinceRunStarted, 3);

                    DayNightManager dayNight = UnityEngine.Object.FindFirstObjectByType<DayNightManager>();
                    float timeOfDay = (float)Math.Round(dayNight.timeOfDay, 3);

                    MapHandler mapHandler = UnityEngine.Object.FindFirstObjectByType<MapHandler>();
                    Segment currentSegment = mapHandler != null ? mapHandler.GetCurrentSegment() : Segment.Beach;
                    string campfireName = currentSegment.ToString();

                    var biomeNames = new List<string>();
                    foreach (Biome.BiomeType biome in mapHandler.biomes)
                    {
                        biomeNames.Add(biome.ToString());
                        if (biome == Biome.BiomeType.Roots && currentSegment == Segment.Tropics) campfireName = biome.ToString();
                        else if (biome == Biome.BiomeType.Mesa && currentSegment == Segment.Alpine) campfireName = biome.ToString();
                    }

                    var data = new OwnSaveData
                    {
                        settingsVersion = 6,
                        posX = pos.x,
                        posY = pos.y,
                        posZ = pos.z,
                        saveDate = DateTime.Now.ToString("dd.MM.yyyy | HH:mm:ss"),
                        playerNames = playerNames,
                        campfireName = campfireName,
                        timePlayed = timePlayed,
                        timeOfDay = timeOfDay,
                        sceneName = sceneName,
                        biomes = mapHandler.biomes,
                        biome_names = biomeNames,
                        segment = currentSegment,
                        hasBackpack = player.backpackSlot != null && player.backpackSlot.hasBackpack,
                        isSkeleton = character.data.isSkeleton,
                        inventoryItemStates = inventoryStates,
                        backpackItemStates = backpackStates,
                        afflictions_current = currentStatuses,
                        extraStamina = extraStamina > 0f && extraStamina <= 1f ? extraStamina : 0f,
                        extModsPeakapaloozaPEAKTOBEACH = false,
                    };

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));

                    log?.LogInfo($"OwnSaveCapture.SavePlayerCoop: position + inventory saved for {userId}. "
                        + $"Pos: {pos} Scene: {sceneName}, Items: {inventoryStates.Count}.");
                }

                // Mirrors decompile lines 4585-4586: local ShowMessage on the host, then
                // RpcTarget.Others so every client sees it too
                network?.MessageOverlay?.Show("Saved game progress", new Color(0.5f, 1f, 0.5f, 1f), 4f);
                network?.SendMessageOthers("Saved game progress", "success", 4f);

                // Phase 8 M9: our own capture is now the canonical writer, so it has to
                // independently trigger the same two steps SavePatch's postfix used to
                // apply only when the checkpoint mod's own save methods were called
                BackpackSaveMitigation.ApplyPendingRestores(offline: false, log);
                SaveArchive.Sync(offline: false, log);
            }
            catch (Exception e)
            {
                log?.LogError($"OwnSaveCapture.SavePlayerCoop failed: {e}");
            }
        }

        /// <summary>Mirrors SavePlayerOffline exactly (decompile 3715-4137)</summary>
        public static void SavePlayerOffline(PluginConfig cfg, ManualLogSource log)
        {
            try
            {
                Player localPlayer = FindLocalPlayer(log);
                if (localPlayer == null)
                {
                    log?.LogError("OwnSaveCapture: no Player found - cannot save progress.");
                    return;
                }

                // Matches GetPlayerSaveFile exactly: custom runs save to their own file
                // regardless of ascent, read live off RunSettings.IsCustomRun
                SaveTarget target = RunLauncher.IsCustomRun ? SaveTarget.Custom() : SaveTarget.Normal(Ascents.currentAscent);
                string path = OwnSavePaths.For(target, offline: true, userId: "");

                Character localCharacter = Character.localCharacter;
                Vector3 pos = localCharacter != null ? localCharacter.Head : localPlayer.transform.position;
                if (localCharacter == null)
                    log?.LogWarning("OwnSaveCapture: Character.localCharacter is null - used player.transform as fallback.");

                string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

                List<OwnSavedItemState> inventoryStates = CaptureInventory(localPlayer, cfg, log);
                List<OwnSavedBackpackItemState> backpackStates = CaptureBackpack(localPlayer, cfg, log);

                CharacterAfflictions afflictions = Character.localCharacter.refs.afflictions;
                float[] currentStatuses = afflictions.currentStatuses.ToArray();
                float extraStamina = Character.localCharacter.GetTotalStamina() - (1f - currentStatuses.Sum());
                extraStamina = Mathf.Clamp(extraStamina, 0f, 1f);
                extraStamina = (float)Math.Round(extraStamina, 2);

                RunManager runManager = UnityEngine.Object.FindFirstObjectByType<RunManager>();
                float timePlayed = (float)Math.Round(runManager.timeSinceRunStarted, 3);

                DayNightManager dayNight = UnityEngine.Object.FindFirstObjectByType<DayNightManager>();
                float timeOfDay = (float)Math.Round(dayNight.timeOfDay, 3);

                MapHandler mapHandler = UnityEngine.Object.FindFirstObjectByType<MapHandler>();
                Segment currentSegment = mapHandler != null ? mapHandler.GetCurrentSegment() : Segment.Beach;
                string campfireName = currentSegment.ToString();

                var biomeNames = new List<string>();
                foreach (Biome.BiomeType biome in mapHandler.biomes)
                {
                    biomeNames.Add(biome.ToString());
                    // Mirrors the original's own biome-variant campfire-naming quirk
                    // exactly (decompile 4087-4094): Roots-variant Tropics and
                    // Mesa-variant Alpine name their campfire after the biome instead
                    // of the segment
                    if (biome == Biome.BiomeType.Roots && currentSegment == Segment.Tropics) campfireName = biome.ToString();
                    else if (biome == Biome.BiomeType.Mesa && currentSegment == Segment.Alpine) campfireName = biome.ToString();
                }

                var data = new OwnSaveData
                {
                    settingsVersion = 6, // matches the checkpoint mod's own hardcoded settingsVersion (decompile line 699)
                    posX = pos.x,
                    posY = pos.y,
                    posZ = pos.z,
                    saveDate = DateTime.Now.ToString("dd.MM.yyyy | HH:mm:ss"),
                    playerNames = new List<string> { localPlayer.character.characterName },
                    campfireName = campfireName,
                    timePlayed = timePlayed,
                    timeOfDay = timeOfDay,
                    sceneName = sceneName,
                    biomes = mapHandler.biomes,
                    biome_names = biomeNames,
                    segment = currentSegment,
                    hasBackpack = localPlayer.backpackSlot != null && localPlayer.backpackSlot.hasBackpack,
                    isSkeleton = Character.localCharacter.data.isSkeleton,
                    inventoryItemStates = inventoryStates,
                    backpackItemStates = backpackStates,
                    afflictions_current = currentStatuses,
                    extraStamina = extraStamina > 0f && extraStamina <= 1f ? extraStamina : 0f,
                    extModsPeakapaloozaPEAKTOBEACH = false,
                };

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));

                log?.LogInfo($"OwnSaveCapture: position + inventory saved. Pos: {pos} Scene: {sceneName}, Items: {inventoryStates.Count}.");

                // Phase 8 M9: our own capture is now the canonical writer, so it has to
                // independently trigger the same two steps SavePatch's postfix used to
                // apply only when the checkpoint mod's own save methods were called.
                // Matches the original SavePlayerOffline exactly: no on-screen message
                // for a solo autosave (silent), unlike SavePlayerCoop's above
                BackpackSaveMitigation.ApplyPendingRestores(offline: true, log);
                SaveArchive.Sync(offline: true, log);
            }
            catch (Exception e)
            {
                log?.LogError($"OwnSaveCapture.SavePlayerOffline failed: {e}");
            }
        }

        // Mirrors decompile 3806-3933 (inventory item-state capture). Note: slotIndex
        // here is a COMPACTED count of non-empty slots seen so far, NOT the raw array
        // index - matches the original exactly (its own `num` counter only increments
        // inside the non-empty branch)
        private static List<OwnSavedItemState> CaptureInventory(Player localPlayer, PluginConfig cfg, ManualLogSource log)
        {
            var result = new List<OwnSavedItemState>();
            if (localPlayer.itemSlots == null) return result;

            int slotIndex = 0;
            foreach (ItemSlot slot in localPlayer.itemSlots)
            {
                if (slot == null || slot.IsEmpty() || slot.prefab == null) continue;
                ItemInstanceData instanceData = slot.data;
                if (instanceData == null) continue;

                var state = new OwnSavedItemState { itemId = slot.prefab.itemID, slotIndex = slotIndex };
                CaptureItemStateValues(instanceData, slot.prefab.itemID, state.values, log);
                result.Add(state);
                slotIndex++;
            }
            return result;
        }

        // Mirrors decompile 3934-4069 (backpack item-state capture). slotIndex here IS
        // the raw byte index into backpackData.itemSlots, matching the original exactly
        private static List<OwnSavedBackpackItemState> CaptureBackpack(Player localPlayer, PluginConfig cfg, ManualLogSource log)
        {
            var result = new List<OwnSavedBackpackItemState>();
            try
            {
                BackpackData backpackData = OwnInventoryRestore.GetBackpackData(localPlayer);
                if (backpackData?.itemSlots == null) return result;

                for (byte slotIndex = 0; slotIndex < backpackData.itemSlots.Length; slotIndex++)
                {
                    ItemSlot slot = backpackData.itemSlots[slotIndex];
                    if (slot == null || slot.IsEmpty() || slot.prefab == null) continue;
                    ItemInstanceData instanceData = slot.data;
                    if (instanceData == null) continue;

                    var state = new OwnSavedBackpackItemState { slotIndex = slotIndex, itemId = slot.prefab.itemID };
                    CaptureItemStateValues(instanceData, slot.prefab.itemID, state.values, log);
                    result.Add(state);
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"OwnSaveCapture: backpackData capture failed (non-fatal): {e.Message}");
            }
            return result;
        }

        // Mirrors the 13 repeated per-key blocks exactly (see class remarks)
        private static void CaptureItemStateValues(ItemInstanceData instanceData, ushort itemId, Dictionary<string, OwnSavedEntry> values, ManualLogSource log)
        {
            bool excluded = OwnItemStateIO.ExcludedItemIds.Contains(itemId);
            foreach (string keyName in OwnItemStateIO.ItemStateKeyNames)
            {
                if (excluded && ExcludableKeys.Contains(keyName)) continue;
                if (!OwnItemStateIO.TryGetKey(keyName, out DataEntryKey key)) continue;
                if (!OwnItemStateIO.TryGetEntryObject(instanceData, key, out object entryObj)) continue;
                if (!OwnItemStateIO.TryReadEntryNumeric(entryObj, out float value)) continue;

                values[keyName] = new OwnSavedEntry { type = entryObj.GetType().AssemblyQualifiedName, value = value };
            }
        }

        private static Player _cachedLocalPlayer;

        // Mirrors GetLocalPlayer exactly (decompile 2151-2175)
        private static Player FindLocalPlayer(ManualLogSource log)
        {
            if (_cachedLocalPlayer != null) return _cachedLocalPlayer;

            foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(UnityEngine.FindObjectsSortMode.None))
            {
                var pv = player.GetComponent<Photon.Pun.PhotonView>();
                if (pv != null && pv.IsMine)
                {
                    _cachedLocalPlayer = player;
                    log?.LogInfo("OwnSaveCapture: local Player via PhotonView.IsMine found.");
                    return _cachedLocalPlayer;
                }
            }

            Player[] all = UnityEngine.Object.FindObjectsByType<Player>(UnityEngine.FindObjectsSortMode.None);
            if (all.Length != 0)
            {
                _cachedLocalPlayer = all[0];
                log?.LogWarning("OwnSaveCapture: local Player randomised (used first Player).");
            }
            return _cachedLocalPlayer;
        }
    }
}
