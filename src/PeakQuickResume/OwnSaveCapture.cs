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
    /// The save-file writer: writes each save event into the store
    /// (<see cref="OwnSavePaths.For"/>) and triggers
    /// <see cref="BackpackSaveMitigation.ApplyPendingRestores"/> to patch dropped-backpack
    /// restores into the just-written file(s). The archive IS the store, so nothing is
    /// copied anywhere afterward. Every file in one save event shares a single
    /// <see cref="OwnSavePaths.NewEventStamp"/> baked into its filename, letting a load
    /// find the co-op siblings belonging to that event (see <see cref="SaveSelection"/>).
    ///
    /// CO-OP FIELD SPLIT: level/world state (island, segment, position, time, ground
    /// items, statue, deployables, run metadata) is written only into the host's file.
    /// A client's file carries only that client's own state (inventory, backpack, held
    /// item, afflictions, thorns, ticks, achievement progress), so a client's save can
    /// never be loaded as authoritative for the level.
    /// </summary>
    public static class OwnSaveCapture
    {
        /// <summary>
        /// Saves every connected player's own file in one pass. Only ever invoked on the
        /// master client at its call sites; no internal IsMasterClient check here.
        /// No stale-file cleanup: the store is append-only, so a player who left simply
        /// has no file in later events.
        /// </summary>
        public static void SavePlayerCoop(PluginConfig cfg, ManualLogSource log, OwnNetwork network, Campfire igniteBuffSource = null)
        {
            try
            {
                SaveTarget target = RunLauncher.IsCustomRun ? SaveTarget.Custom() : SaveTarget.Normal(Ascents.currentAscent);

                string stamp = OwnSavePaths.NewEventStamp();
                string hostUserId = OwnSavePaths.LocalUserId();

                var playerNames = new List<string>();
                Player[] allPlayers = UnityEngine.Object.FindObjectsByType<Player>(UnityEngine.FindObjectsSortMode.None);
                foreach (Player p in allPlayers)
                {
                    if (p != null) playerNames.Add(p.character.characterName);
                }

                // One shared "claimed" set threads through all three captures so the same
                // physical item never ends up saved twice under two different mechanics.
                Vector3 worldAnchor = ResolveWorldAnchor(allPlayers, log);
                var claimedItems = new HashSet<Item>();
                AncientStatueRestore.Capture(worldAnchor, claimedItems, log, out OwnSavedStatueState statueState);
                LuggageRestore.Capture(worldAnchor, claimedItems, log, out List<OwnSavedLuggageState> luggageStates);
                WorldItemRestore.Capture(worldAnchor, claimedItems, log, out List<OwnSavedPositionedItem> worldItemStates);
                DeployableRestore.CaptureStoves(worldAnchor, log, out List<OwnSavedDeployableState> portableStoves);
                DeployableRestore.CaptureCannons(worldAnchor, log, out List<OwnSavedDeployableState> scoutCannons);

                // Read once here rather than re-read on every iteration below; these describe
                // the run/level, not any one player, and only land in the host's own file.
                string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

                RunManager runManager = UnityEngine.Object.FindFirstObjectByType<RunManager>();
                float timePlayed = (float)Math.Round(RunTimerCompat.Read(runManager), 3);

                DayNightManager dayNight = UnityEngine.Object.FindFirstObjectByType<DayNightManager>();
                float timeOfDay = (float)Math.Round(dayNight.timeOfDay, 3);
                int dayCount = dayNight.dayCount;

                MapHandler mapHandler = UnityEngine.Object.FindFirstObjectByType<MapHandler>();
                Segment currentSegment = mapHandler != null ? mapHandler.GetCurrentSegment() : Segment.Beach;
                string campfireName = currentSegment.ToString();

                var biomeNames = new List<string>();
                foreach (Biome.BiomeType biome in mapHandler.biomes)
                {
                    biomeNames.Add(biome.ToString());
                    // Roots-variant Tropics and Mesa-variant Alpine name their campfire after the biome.
                    if (biome == Biome.BiomeType.Roots && currentSegment == Segment.Tropics) campfireName = biome.ToString();
                    else if (biome == Biome.BiomeType.Mesa && currentSegment == Segment.Alpine) campfireName = biome.ToString();
                }

                // The hand-written variant cases above don't cover 2.0.a's new areas; ask the
                // game for the real area name and keep the above purely as a fallback.
                campfireName = AreaNameCompat.ResolveAreaName(currentSegment, campfireName, log);

                foreach (Player player in allPlayers)
                {
                    if (player == null)
                    {
                        log?.LogError("OwnSaveCapture.SavePlayerCoop: no Player found - cannot save progress.");
                        continue;
                    }

                    string userId = NetworkingUtilities.GetUserId(player);
                    string path = OwnSavePaths.For(target, offline: false, userId: userId, stamp: stamp);

                    // Only the host's file carries the level/world half. playerPv.IsMine is a
                    // local, always-available signal, unlike userId which can come back empty;
                    // falls back to comparing userIds, then to "everyone" rather than risk an
                    // event with no world state anywhere (permanently unloadable).
                    PhotonView playerPv = player.GetComponent<PhotonView>();
                    bool isHostFile = playerPv != null
                        ? playerPv.IsMine
                        : (hostUserId.Length == 0 || userId == hostUserId);

                    Character character = player.character;
                    if (character == null)
                        log?.LogWarning("OwnSaveCapture.SavePlayerCoop: Character is null for this player.");

                    List<OwnSavedItemState> inventoryStates = CaptureInventory(player, cfg, log);
                    List<OwnSavedBackpackItemState> backpackStates = CaptureBackpack(player, cfg, log);
                    OwnSavedItemState heldItemState = CaptureHeldItem(player, log);
                    List<ushort> stuckThornIndices = ThornsAndTicksRestore.CaptureThorns(character);
                    bool hasTick = ThornsAndTicksRestore.CaptureTick(character);

                    CharacterAfflictions afflictions = character.refs.afflictions;
                    float[] currentStatuses = afflictions.currentStatuses.ToArray();

                    // Read CharacterData.extraStamina directly rather than back-deriving it via
                    // GetTotalStamina() - (1 - statusSum). That derivation (the original
                    // checkpoint mod's own formula) assumes currentStamina always sits at its
                    // statusSum-only cap, which Petrify breaks: currentStamina's own cap
                    // (GetMaxStamina) never accounts for petrifyAmount, so a petrified player
                    // with zero real bonus stamina was getting most of the petrify amount
                    // captured as fake extraStamina instead. The field is public and synced
                    // like petrifyAmount, so it's just as safe to read directly for remote players.
                    float extraStamina = character.data.extraStamina;
                    int petrifyAmount = character.data.petrifyAmount;

                    if (cfg.CaptureCampfireIgniteBuff.Value && igniteBuffSource != null)
                        (extraStamina, petrifyAmount) = CampfireIgniteBuffCompat.Apply(igniteBuffSource, character, extraStamina, petrifyAmount, currentStatuses);

                    extraStamina = Mathf.Clamp(extraStamina, 0f, 1f);
                    extraStamina = (float)Math.Round(extraStamina, 2);

                    // AchievementManager is a client-local singleton; for other players,
                    // ReconnectHandler.TryGetReconnectData gives the game's host-side copy of
                    // that player's progress instead.
                    OwnSavedAchievementProgress achievementProgress = (playerPv != null && playerPv.IsMine)
                        ? AchievementProgressIO.CaptureLocal(log)
                        : (ReconnectHandler.TryGetReconnectData(userId, out _, out SerializableRunBasedValues remoteProgress)
                            ? AchievementProgressIO.ToSaved(remoteProgress, log)
                            : null);

                    var data = new OwnSaveData
                    {
                        settingsVersion = SaveArchive.CurrentSettingsVersion,
                        saveDate = DateTime.Now.ToString("dd.MM.yyyy | HH:mm:ss"),
                        hasBackpack = BackpackTypeCompat.HasAny(player),
                        backpackType = BackpackTypeCompat.Capture(player),
                        backpackOwnValues = CaptureBackpackOwnValues(player, log),
                        isSkeleton = character.data.isSkeleton,
                        // Who was a spectating ghost at save time, so a load can restore that
                        // instead of reviving everyone. Only data.dead - a merely passed-out
                        // player is knocked out, not dead, and gets legitimately revived on load.
                        isDead = character != null && character.data.dead,
                        inventoryItemStates = inventoryStates,
                        backpackItemStates = backpackStates,
                        heldItemState = heldItemState,
                        stuckThornIndices = stuckThornIndices,
                        hasTick = hasTick,
                        afflictions_current = currentStatuses,
                        extraStamina = extraStamina > 0f && extraStamina <= 1f ? extraStamina : 0f,
                        petrifyAmount = petrifyAmount,
                        achievementProgress = achievementProgress,
                        gameVersion = GameVersionCompat.Current,
                    };

                    // The saved position is the teleport target every player is warped to on
                    // load, not a per-player spawn point - a client's own position is never captured.
                    if (isHostFile)
                    {
                        data.posX = worldAnchor.x;
                        data.posY = worldAnchor.y;
                        data.posZ = worldAnchor.z;
                        data.playerNames = playerNames;
                        data.campfireName = campfireName;
                        data.timePlayed = timePlayed;
                        data.timeOfDay = timeOfDay;
                        data.dayCount = dayCount;
                        data.sceneName = sceneName;
                        data.biomes = mapHandler.biomes;
                        data.biome_names = biomeNames;
                        data.segment = currentSegment;
                        data.ancientStatue = statueState;
                        data.luggageStates = luggageStates;
                        data.worldItemStates = worldItemStates;
                        data.portableStoves = portableStoves;
                        data.scoutCannons = scoutCannons;

                        // Null on every non-Nadir checkpoint, which the restore reads as
                        // "the host stands in for the interactor".
                        data.nadirCommunerUserId = NadirCommuner.PendingUserId;
                        data.nadirCommunerName = NadirCommuner.PendingName;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));

                    log?.LogInfo($"OwnSaveCapture.SavePlayerCoop: saved {(isHostFile ? "host" : "client")} file for "
                        + $"{userId} (event {stamp}). Items: {inventoryStates.Count}"
                        + (isHostFile ? $", Pos: {worldAnchor}, Scene: {sceneName}." : "."));
                }

                string savedMsg = MessagesLocalization.Get(MsgKey.SavedGameProgress);
                network?.MessageOverlay?.Show(savedMsg, new Color(0.5f, 1f, 0.5f, 1f), 4f);
                network?.SendMessageOthers(savedMsg, "success", 4f);

                BackpackSaveMitigation.ApplyPendingRestores(offline: false, stamp, log);
            }
            catch (Exception e)
            {
                log?.LogError($"OwnSaveCapture.SavePlayerCoop failed: {e}");
            }
        }

        /// <summary>
        /// Where the world half of a co-op save is anchored: the teleport target every
        /// player is warped to on load, and the search centre for statue/luggage/ground-item
        /// capture. Normally the host's own head. A dead character's ragdoll is dragged
        /// toward a fixed off-map death position, so a checkpoint written while the host was
        /// dead would otherwise anchor the whole save 5km off the map. A dead host's position
        /// is therefore never used: falls back to any living player, then the checkpoint's own campfire.
        ///
        /// Nadir is the exception to "the host's own head": its checkpoint is a commune, which
        /// has none of a campfire's gather-the-party requirement, so the anchor is the communing
        /// player instead. See <see cref="NadirCommuner"/>.
        /// </summary>
        private static Vector3 ResolveWorldAnchor(Player[] allPlayers, ManualLogSource log)
        {
            if (NadirCommuner.TryGetPendingAnchor(out Vector3 communerHead)) return communerHead;

            Character local = Character.localCharacter;
            if (local != null && !local.data.dead) return local.Head;

            log?.LogWarning("OwnSaveCapture: the host is dead (or has no character) at save time, so their own "
                + "position is the off-map death zone - anchoring this save's world state on a living player instead.");

            if (allPlayers != null)
            {
                foreach (Player p in allPlayers)
                {
                    Character ch = p != null ? p.character : null;
                    if (ch == null || ch == local || ch.data.dead) continue;

                    log.Trace($"OwnSaveCapture: world state anchored on {ch.characterName} at {ch.Head}.");
                    return ch.Head;
                }
            }

            // Nobody alive at all - not reachable in a normal run, purely belt-and-braces.
            try
            {
                Campfire campfire = MapHandler.PreviousCampfire;
                if (campfire != null)
                {
                    Vector3 campfirePos = campfire.transform.position;
                    log?.LogWarning($"OwnSaveCapture: no living player found either - anchoring on the checkpoint's "
                        + $"own campfire at {campfirePos}.");
                    return campfirePos;
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"OwnSaveCapture: could not read the current campfire as a fallback anchor: {e.Message}");
            }

            log?.LogError("OwnSaveCapture: could not resolve any sane world anchor for this save - falling back to the "
                + "host's own position. If the host was dead, this checkpoint's world state and teleport target will "
                + "be wrong (the load path detects and works around this, see OwnTeleportSequence).");
            return local != null ? local.Head : Vector3.zero;
        }

        /// <summary>Also shows the "Saved game progress" message here, matching SavePlayerCoop's confirmation.</summary>
        public static void SavePlayerOffline(PluginConfig cfg, ManualLogSource log, OwnMessageOverlay messageOverlay = null, Campfire igniteBuffSource = null)
        {
            try
            {
                Player localPlayer = FindLocalPlayer(log);
                if (localPlayer == null)
                {
                    log?.LogError("OwnSaveCapture: no Player found - cannot save progress.");
                    return;
                }

                SaveTarget target = RunLauncher.IsCustomRun ? SaveTarget.Custom() : SaveTarget.Normal(Ascents.currentAscent);
                string stamp = OwnSavePaths.NewEventStamp();
                string path = OwnSavePaths.For(target, offline: true, userId: "", stamp: stamp);

                Character localCharacter = Character.localCharacter;
                Vector3 pos = localCharacter != null ? localCharacter.Head : localPlayer.transform.position;
                if (localCharacter == null)
                    log?.LogWarning("OwnSaveCapture: Character.localCharacter is null - used player.transform as fallback.");

                string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

                List<OwnSavedItemState> inventoryStates = CaptureInventory(localPlayer, cfg, log);
                List<OwnSavedBackpackItemState> backpackStates = CaptureBackpack(localPlayer, cfg, log);
                OwnSavedItemState heldItemState = CaptureHeldItem(localPlayer, log);
                List<ushort> stuckThornIndices = ThornsAndTicksRestore.CaptureThorns(localCharacter);
                bool hasTick = ThornsAndTicksRestore.CaptureTick(localCharacter);

                var claimedItems = new HashSet<Item>();
                AncientStatueRestore.Capture(pos, claimedItems, log, out OwnSavedStatueState statueState);
                LuggageRestore.Capture(pos, claimedItems, log, out List<OwnSavedLuggageState> luggageStates);
                WorldItemRestore.Capture(pos, claimedItems, log, out List<OwnSavedPositionedItem> worldItemStates);
                DeployableRestore.CaptureStoves(pos, log, out List<OwnSavedDeployableState> portableStoves);
                DeployableRestore.CaptureCannons(pos, log, out List<OwnSavedDeployableState> scoutCannons);

                CharacterAfflictions afflictions = Character.localCharacter.refs.afflictions;
                float[] currentStatuses = afflictions.currentStatuses.ToArray();

                // See the coop capture path above for why this reads CharacterData.extraStamina
                // directly instead of deriving it from GetTotalStamina() - (1 - statusSum).
                float extraStamina = Character.localCharacter.data.extraStamina;
                int petrifyAmount = Character.localCharacter.data.petrifyAmount;

                if (cfg.CaptureCampfireIgniteBuff.Value && igniteBuffSource != null)
                    (extraStamina, petrifyAmount) = CampfireIgniteBuffCompat.Apply(igniteBuffSource, localCharacter, extraStamina, petrifyAmount, currentStatuses);

                extraStamina = Mathf.Clamp(extraStamina, 0f, 1f);
                extraStamina = (float)Math.Round(extraStamina, 2);

                RunManager runManager = UnityEngine.Object.FindFirstObjectByType<RunManager>();
                float timePlayed = (float)Math.Round(RunTimerCompat.Read(runManager), 3);

                DayNightManager dayNight = UnityEngine.Object.FindFirstObjectByType<DayNightManager>();
                float timeOfDay = (float)Math.Round(dayNight.timeOfDay, 3);
                int dayCount = dayNight.dayCount;

                MapHandler mapHandler = UnityEngine.Object.FindFirstObjectByType<MapHandler>();
                Segment currentSegment = mapHandler != null ? mapHandler.GetCurrentSegment() : Segment.Beach;
                string campfireName = currentSegment.ToString();

                var biomeNames = new List<string>();
                foreach (Biome.BiomeType biome in mapHandler.biomes)
                {
                    biomeNames.Add(biome.ToString());
                    // Roots-variant Tropics and Mesa-variant Alpine name their campfire after the biome.
                    if (biome == Biome.BiomeType.Roots && currentSegment == Segment.Tropics) campfireName = biome.ToString();
                    else if (biome == Biome.BiomeType.Mesa && currentSegment == Segment.Alpine) campfireName = biome.ToString();
                }

                // The hand-written variant cases above don't cover 2.0.a's new areas; ask the
                // game for the real area name and keep the above purely as a fallback.
                campfireName = AreaNameCompat.ResolveAreaName(currentSegment, campfireName, log);

                OwnSavedAchievementProgress achievementProgress = AchievementProgressIO.CaptureLocal(log);

                var data = new OwnSaveData
                {
                    settingsVersion = SaveArchive.CurrentSettingsVersion,
                    posX = pos.x,
                    posY = pos.y,
                    posZ = pos.z,
                    saveDate = DateTime.Now.ToString("dd.MM.yyyy | HH:mm:ss"),
                    playerNames = new List<string> { localPlayer.character.characterName },
                    campfireName = campfireName,
                    timePlayed = timePlayed,
                    timeOfDay = timeOfDay,
                    dayCount = dayCount,
                    sceneName = sceneName,
                    biomes = mapHandler.biomes,
                    biome_names = biomeNames,
                    segment = currentSegment,
                    hasBackpack = BackpackTypeCompat.HasAny(localPlayer),
                    backpackType = BackpackTypeCompat.Capture(localPlayer),
                    backpackOwnValues = CaptureBackpackOwnValues(localPlayer, log),
                    isSkeleton = Character.localCharacter.data.isSkeleton,
                    // For save-shape consistency only - solo can never reach a saved-while-dead
                    // state, and the restore side ignores this field offline.
                    isDead = Character.localCharacter.data.dead,
                    inventoryItemStates = inventoryStates,
                    backpackItemStates = backpackStates,
                    heldItemState = heldItemState,
                    stuckThornIndices = stuckThornIndices,
                    hasTick = hasTick,
                    afflictions_current = currentStatuses,
                    extraStamina = extraStamina > 0f && extraStamina <= 1f ? extraStamina : 0f,
                    petrifyAmount = petrifyAmount,
                    ancientStatue = statueState,
                    luggageStates = luggageStates,
                    worldItemStates = worldItemStates,
                    achievementProgress = achievementProgress,
                    portableStoves = portableStoves,
                    scoutCannons = scoutCannons,
                    // Solo resolves the interactor locally and never reads these back.
                    nadirCommunerUserId = NadirCommuner.PendingUserId,
                    nadirCommunerName = NadirCommuner.PendingName,
                    gameVersion = GameVersionCompat.Current,
                };

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));

                log?.LogInfo($"OwnSaveCapture: position + inventory saved. Pos: {pos} Scene: {sceneName}, Items: {inventoryStates.Count}.");

                messageOverlay?.Show(MessagesLocalization.Get(MsgKey.SavedGameProgress), new Color(0.5f, 1f, 0.5f, 1f), 4f);

                BackpackSaveMitigation.ApplyPendingRestores(offline: true, stamp, log);
            }
            catch (Exception e)
            {
                log?.LogError($"OwnSaveCapture.SavePlayerOffline failed: {e}");
            }
        }

        // slotIndex here is a compacted count of non-empty slots seen so far, not the raw array index.
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
                CaptureItemStateValues(instanceData, state.values);
                result.Add(state);
                slotIndex++;
            }
            return result;
        }

        /// <summary>
        /// The worn backpack's own stats (e.g. Jetpack/Rocketpack fuel), as opposed to
        /// <see cref="CaptureBackpack"/>'s walk of what's inside it. Null when there's no backpack.
        /// </summary>
        private static Dictionary<string, OwnSavedEntry> CaptureBackpackOwnValues(Player localPlayer, ManualLogSource log)
        {
            try
            {
                if (!BackpackTypeCompat.HasAny(localPlayer)) return null;

                ItemInstanceData instanceData = localPlayer.backpackSlot?.data;
                if (instanceData == null) return null;

                var values = new Dictionary<string, OwnSavedEntry>();
                CaptureItemStateValues(instanceData, values);

                // Only the prefab carries the JetpackItem component, while values live on the
                // slot's instance data - hence the two separate arguments.
                BackpackTypeCompat.EnsureFuelCaptured(localPlayer.backpackSlot?.prefab, instanceData, values, log);

                return values.Count > 0 ? values : null;
            }
            catch (Exception e)
            {
                log?.LogWarning($"OwnSaveCapture: worn-backpack state capture failed (non-fatal): {e.Message}");
                return null;
            }
        }

        // slotIndex here is the raw byte index into backpackData.itemSlots.
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
                    CaptureItemStateValues(instanceData, state.values);
                    result.Add(state);
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"OwnSaveCapture: backpackData capture failed (non-fatal): {e.Message}");
            }
            return result;
        }

        // Captures the item in Player.tempFullSlot (slot ID 250), the 4th item held when all
        // 3 regular itemSlots are full. slotIndex is stamped as 250 for JSON readability only.
        private static OwnSavedItemState CaptureHeldItem(Player localPlayer, ManualLogSource log)
        {
            ItemSlot slot = localPlayer?.tempFullSlot;
            if (slot == null || slot.IsEmpty() || slot.prefab == null) return null;
            ItemInstanceData instanceData = slot.data;
            if (instanceData == null) return null;

            var state = new OwnSavedItemState { itemId = slot.prefab.itemID, slotIndex = 250 };
            CaptureItemStateValues(instanceData, state.values);
            return state;
        }

        private static void CaptureItemStateValues(ItemInstanceData instanceData, Dictionary<string, OwnSavedEntry> values)
        {
            foreach (var kv in OwnItemStateIO.ReadItemStateValues(instanceData))
                values[kv.Key] = new OwnSavedEntry { type = kv.Value.TypeName, value = kv.Value.Value };
        }

        private static Player _cachedLocalPlayer;

        private static Player FindLocalPlayer(ManualLogSource log)
        {
            if (_cachedLocalPlayer != null) return _cachedLocalPlayer;

            foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(UnityEngine.FindObjectsSortMode.None))
            {
                var pv = player.GetComponent<Photon.Pun.PhotonView>();
                if (pv != null && pv.IsMine)
                {
                    _cachedLocalPlayer = player;
                    log.Trace("OwnSaveCapture: local Player via PhotonView.IsMine found.");
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
