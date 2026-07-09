using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Phase 8 milestone M6: our own port of <c>SavePlayerOffline</c> (decompile
    /// 3715-4137). Coop's <c>SavePlayerCoop</c> (4139-4605) is NOT ported yet - M7.
    ///
    /// Writes to a NON-canonical diagnostic path (<see cref="OwnSavePaths.ForDiagnosticCapture"/>),
    /// deliberately NOT the live save file the checkpoint mod's own autosave still
    /// writes and our own restore path (M3-M5) still reads. This milestone proves we
    /// CAN produce an equivalent capture - for diffing against whatever the
    /// checkpoint mod writes for the same in-game state - it does NOT yet make us
    /// the live save-file writer. Actually cutting the canonical write path over is a
    /// separate, later step (needs coordinating with <see cref="SaveArchive"/>/
    /// <see cref="SavePatch"/>, which currently only trigger off the checkpoint mod's
    /// own <c>SavePlayerOffline</c>/<c>SavePlayerCoop</c> being called) - see
    /// ROADMAP.md Phase 8 M6
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
                // regardless of ascent, read live off RunSettings.IsCustomRun. Written to
                // a NON-canonical diagnostic path for now (see OwnSavePaths.ForDiagnosticCapture) -
                // this milestone proves we CAN produce an equivalent file, it does not yet
                // replace the checkpoint mod as the live save-file writer
                SaveTarget target = RunLauncher.IsCustomRun ? SaveTarget.Custom() : SaveTarget.Normal(Ascents.currentAscent);
                string path = OwnSavePaths.ForDiagnosticCapture(target, offline: true, userId: "");

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
