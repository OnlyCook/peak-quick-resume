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
    /// 3715-4603, ported M6/M7). The save-file writer - writes each save event straight
    /// into the store (<see cref="OwnSavePaths.For"/>, i.e. QuickResume/Archive) and
    /// then triggers <see cref="BackpackSaveMitigation.ApplyPendingRestores"/> to patch
    /// the just-written file(s) for dropped-backpack restores. Nothing is copied
    /// anywhere afterwards: the archive IS the store, so there's no second
    /// canonical file to keep in step and no sync pass at all
    ///
    /// Every file in one save event shares a single <see cref="OwnSavePaths.NewEventStamp"/>
    /// baked into its filename, which is what lets a load find exactly the co-op siblings
    /// belonging to that event (see <see cref="SaveSelection"/>)
    ///
    /// CO-OP FIELD SPLIT (see <see cref="SaveSelection"/> for the matching read side):
    /// level/world state - which island and segment, the teleport position, time of day,
    /// the day counter, ground items, luggage, the ancient statue, deployables, and the
    /// run-level metadata the F7 picker shows - is written ONLY into the host's file.
    /// A client's file carries only that client's OWN state: inventory, backpack, held
    /// item, afflictions, extra stamina, skeleton flag, thorns, ticks, achievement
    /// progress. Earlier versions stamped a full copy of the world state into every
    /// player's file, which meant a client's save could be loaded (or hand-edited) as if
    /// it were authoritative for the level and silently restore a different moment's
    /// world than the host's file described
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
        ///
        /// The original's stale-coop-file cleanup (decompile 4201-4228, deleting the
        /// existing files for this ascent bucket before rewriting them) is deliberately
        /// NOT carried over: it only made sense when a single canonical file per player
        /// had to be kept current. The store is append-only now - each event writes its
        /// own set of files under its own stamp - so a player who left simply has no file
        /// in later events, which is exactly the state a load needs to see anyway
        /// </summary>
        public static void SavePlayerCoop(PluginConfig cfg, ManualLogSource log, OwnNetwork network)
        {
            try
            {
                SaveTarget target = RunLauncher.IsCustomRun ? SaveTarget.Custom() : SaveTarget.Normal(Ascents.currentAscent);

                // One stamp for the whole event, shared by every player's filename - see
                // the class remarks and SaveSelection
                string stamp = OwnSavePaths.NewEventStamp();
                string hostUserId = OwnSavePaths.LocalUserId();

                var playerNames = new List<string>();
                Player[] allPlayers = UnityEngine.Object.FindObjectsByType<Player>(UnityEngine.FindObjectsSortMode.None);
                foreach (Player p in allPlayers)
                {
                    if (p != null) playerNames.Add(p.character.characterName);
                }

                // World state (not per-player) - captured once around the anchor resolved
                // below and written ONLY into the host's own file, see the class remarks
                // for the field split. One shared "claimed" set threads through all three
                // captures so the same physical item never ends up saved twice under two
                // different mechanics - see WorldItemRestore's class remarks for why that
                // matters
                Vector3 worldAnchor = ResolveWorldAnchor(allPlayers, log);
                var claimedItems = new HashSet<Item>();
                AncientStatueRestore.Capture(worldAnchor, claimedItems, log, out OwnSavedStatueState statueState);
                LuggageRestore.Capture(worldAnchor, claimedItems, log, out List<OwnSavedLuggageState> luggageStates);
                WorldItemRestore.Capture(worldAnchor, claimedItems, log, out List<OwnSavedPositionedItem> worldItemStates);
                DeployableRestore.CaptureStoves(worldAnchor, log, out List<OwnSavedDeployableState> portableStoves);
                DeployableRestore.CaptureCannons(worldAnchor, log, out List<OwnSavedDeployableState> scoutCannons);

                // The rest of the level/world half. Read once here rather than re-read
                // identically on every iteration below - these describe the run and the
                // level, not any one player, and only ever land in the host's own file
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
                    // Mirrors the original's own biome-variant campfire-naming quirk
                    // exactly (decompile 4087-4094): Roots-variant Tropics and
                    // Mesa-variant Alpine name their campfire after the biome instead
                    // of the segment
                    if (biome == Biome.BiomeType.Roots && currentSegment == Segment.Tropics) campfireName = biome.ToString();
                    else if (biome == Biome.BiomeType.Mesa && currentSegment == Segment.Alpine) campfireName = biome.ToString();
                }

                // The hand-written variant cases above only ever knew about Roots and
                // Mesa, so 2.0.a's two new areas saved as plain "Caldera"/"TheKiln". Ask
                // the game for the real area name and keep the above purely as a
                // fallback - see AreaNameCompat
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

                    // Only the host's own file carries the level/world half of the save.
                    // This method only ever runs on the master client, so the one Player
                    // whose view IsMine is the host's - a local, always-available signal,
                    // unlike the userId string, which can come back empty. Falls back to
                    // comparing userIds if there's no view at all, and to "everyone" if
                    // that's unavailable too: writing an event with no world state in it
                    // anywhere would leave it permanently unloadable, which is worse than
                    // writing a redundant copy
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
                    float extraStamina = character.GetTotalStamina() - (1f - currentStatuses.Sum());
                    extraStamina = Mathf.Clamp(extraStamina, 0f, 1f);
                    extraStamina = (float)Math.Round(extraStamina, 2);

                    // AchievementManager is a client-LOCAL singleton - we only ever see
                    // our OWN achievement progress directly. For every other player,
                    // ReconnectHandler.TryGetReconnectData gives us the game's own
                    // native, already-kept-up-to-date host-side copy of that player's
                    // progress (the same one the game uses for its own disconnect/
                    // reconnect support) - see AchievementProgressIO's remarks
                    OwnSavedAchievementProgress achievementProgress = (playerPv != null && playerPv.IsMine)
                        ? AchievementProgressIO.CaptureLocal(log)
                        : (ReconnectHandler.TryGetReconnectData(userId, out _, out SerializableRunBasedValues remoteProgress)
                            ? AchievementProgressIO.ToSaved(remoteProgress, log)
                            : null);

                    // Per-player half - written into every file, including the host's
                    var data = new OwnSaveData
                    {
                        settingsVersion = SaveArchive.CurrentSettingsVersion,
                        saveDate = DateTime.Now.ToString("dd.MM.yyyy | HH:mm:ss"),
                        hasBackpack = BackpackTypeCompat.HasAny(player),
                        backpackType = BackpackTypeCompat.Capture(player),
                        backpackOwnValues = CaptureBackpackOwnValues(player, log),
                        isSkeleton = character.data.isSkeleton,
                        // Own addition (see OwnSaveData.isDead / DeathStateRestore): who was
                        // a spectating ghost at save time, so a load can put them back that
                        // way instead of silently reviving everyone. Deliberately only
                        // data.dead - a merely passed-out player is knocked out, not dead,
                        // and the load's own revive step legitimately brings them back up
                        isDead = character != null && character.data.dead,
                        inventoryItemStates = inventoryStates,
                        backpackItemStates = backpackStates,
                        heldItemState = heldItemState,
                        stuckThornIndices = stuckThornIndices,
                        hasTick = hasTick,
                        afflictions_current = currentStatuses,
                        extraStamina = extraStamina > 0f && extraStamina <= 1f ? extraStamina : 0f,
                        achievementProgress = achievementProgress,
                        gameVersion = GameVersionCompat.Current,
                    };

                    // Level/world half - host's file only, see the class remarks. The
                    // saved position is the world anchor resolved once above (normally
                    // the host's own): it's the teleport target every player is warped to
                    // on load, not a per-player spawn point, so a client's own position is
                    // never captured and never restored
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
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));

                    log?.LogInfo($"OwnSaveCapture.SavePlayerCoop: saved {(isHostFile ? "host" : "client")} file for "
                        + $"{userId} (event {stamp}). Items: {inventoryStates.Count}"
                        + (isHostFile ? $", Pos: {worldAnchor}, Scene: {sceneName}." : "."));
                }

                // Mirrors decompile lines 4585-4586: local ShowMessage on the host, then
                // RpcTarget.Others so every client sees it too
                string savedMsg = MessagesLocalization.Get(MsgKey.SavedGameProgress);
                network?.MessageOverlay?.Show(savedMsg, new Color(0.5f, 1f, 0.5f, 1f), 4f);
                network?.SendMessageOthers(savedMsg, "success", 4f);

                // Patch the just-written file(s) for any pending dropped-backpack restores.
                // No archive step follows - the files above ARE the archive entries
                BackpackSaveMitigation.ApplyPendingRestores(offline: false, stamp, log);
            }
            catch (Exception e)
            {
                log?.LogError($"OwnSaveCapture.SavePlayerCoop failed: {e}");
            }
        }

        /// <summary>
        /// Where the WORLD half of a co-op save is anchored: the position written into the
        /// host's file (the teleport target every player is warped to on load) AND the search
        /// centre for the ancient statue / luggage / ground item / deployable capture
        ///
        /// Normally simply the host's own head, exactly as before. The exception this method
        /// exists for is a session-reported bug (2026-07-26): a dead character's ragdoll is
        /// dragged every FixedUpdate towards <c>Character.DeathPos()</c>, a fixed spot at
        /// <c>(0, 5000, -5000)</c> far outside the map, so a checkpoint written while the HOST
        /// was dead (a client lighting the campfire is enough to trigger one - see
        /// <c>RPC_RequestSave</c>) anchored the entire world half in that empty death zone.
        /// The result was a save whose statue/luggage/ground-item capture found nothing at all
        /// and whose teleport target was 5km off the map, so loading it warped everyone into
        /// the void and no terrain ever loaded around them
        ///
        /// A dead host's own position is therefore never used: the anchor falls back to any
        /// LIVING player (they're standing at the campfire that triggered the save - that's
        /// what a checkpoint is), and only if there isn't one to the campfire this checkpoint
        /// belongs to. The host-is-alive path is untouched, so nothing about a normal save changes
        /// </summary>
        private static Vector3 ResolveWorldAnchor(Player[] allPlayers, ManualLogSource log)
        {
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

            // Nobody alive at all. Not reachable in a normal run (vanilla ends the run the
            // moment every player is dead), so this is purely belt-and-braces: the campfire
            // this checkpoint belongs to is still a far better anchor than the death zone
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

        /// <summary>
        /// Mirrors SavePlayerOffline (decompile 3715-4137), plus one deliberate deviation:
        /// the original shows no on-screen confirmation for a solo autosave, but
        /// SavePlayerCoop's host/client branches both do - an inconsistency players
        /// noticed, so we show the same "Saved game progress" message here too
        /// </summary>
        public static void SavePlayerOffline(PluginConfig cfg, ManualLogSource log, OwnMessageOverlay messageOverlay = null)
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
                float extraStamina = Character.localCharacter.GetTotalStamina() - (1f - currentStatuses.Sum());
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
                    // Mirrors the original's own biome-variant campfire-naming quirk
                    // exactly (decompile 4087-4094): Roots-variant Tropics and
                    // Mesa-variant Alpine name their campfire after the biome instead
                    // of the segment
                    if (biome == Biome.BiomeType.Roots && currentSegment == Segment.Tropics) campfireName = biome.ToString();
                    else if (biome == Biome.BiomeType.Mesa && currentSegment == Segment.Alpine) campfireName = biome.ToString();
                }

                // The hand-written variant cases above only ever knew about Roots and
                // Mesa, so 2.0.a's two new areas saved as plain "Caldera"/"TheKiln". Ask
                // the game for the real area name and keep the above purely as a
                // fallback - see AreaNameCompat
                campfireName = AreaNameCompat.ResolveAreaName(currentSegment, campfireName, log);

                OwnSavedAchievementProgress achievementProgress = AchievementProgressIO.CaptureLocal(log);

                var data = new OwnSaveData
                {
                    // Offline has exactly one player, so this single file is both the host
                    // file and that player's own file - no split to make here
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
                    // Captured for save-shape consistency only - solo can never actually
                    // reach a saved-while-dead state (the one player being dead IS the
                    // whole team being dead, which ends the run), and the restore side
                    // ignores this field entirely offline. See DeathStateRestore
                    isDead = Character.localCharacter.data.dead,
                    inventoryItemStates = inventoryStates,
                    backpackItemStates = backpackStates,
                    heldItemState = heldItemState,
                    stuckThornIndices = stuckThornIndices,
                    hasTick = hasTick,
                    afflictions_current = currentStatuses,
                    extraStamina = extraStamina > 0f && extraStamina <= 1f ? extraStamina : 0f,
                    ancientStatue = statueState,
                    luggageStates = luggageStates,
                    worldItemStates = worldItemStates,
                    achievementProgress = achievementProgress,
                    portableStoves = portableStoves,
                    scoutCannons = scoutCannons,
                    gameVersion = GameVersionCompat.Current,
                };

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));

                log?.LogInfo($"OwnSaveCapture: position + inventory saved. Pos: {pos} Scene: {sceneName}, Items: {inventoryStates.Count}.");

                messageOverlay?.Show(MessagesLocalization.Get(MsgKey.SavedGameProgress), new Color(0.5f, 1f, 0.5f, 1f), 4f);

                // Patch the just-written file for any pending dropped-backpack restores.
                // No archive step follows - the file above IS the archive entry
                BackpackSaveMitigation.ApplyPendingRestores(offline: true, stamp, log);
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

        /// <summary>
        /// The worn backpack's OWN stats, as opposed to <see cref="CaptureBackpack"/>'s
        /// walk of what's inside it. This is where a Jetpack's/Rocketpack's fuel lives
        /// (<c>DataEntryKey.Fuel</c> on the backpack slot's own ItemInstanceData), which
        /// nothing captured before - so a worn jetpack always came back at default fuel,
        /// while the very same jetpack dropped on the ground restored correctly, because
        /// world items are captured whole. Null when there's no backpack to describe
        /// </summary>
        private static Dictionary<string, OwnSavedEntry> CaptureBackpackOwnValues(Player localPlayer, ManualLogSource log)
        {
            try
            {
                if (!BackpackTypeCompat.HasAny(localPlayer)) return null;

                ItemInstanceData instanceData = localPlayer.backpackSlot?.data;
                if (instanceData == null) return null;

                var values = new Dictionary<string, OwnSavedEntry>();
                // itemID only drives the ExcludedItemIds consumable carve-out, which can't
                // apply to a backpack - 0 simply means "not an excluded consumable"
                CaptureItemStateValues(instanceData, 0, values, log);

                // Only the PREFAB carries the JetpackItem component (the worn backpack is a
                // slot, not a live item), while the values live on the slot's instance
                // data - hence the two separate arguments. Without an explicit Fuel here a
                // worn jetpack restores as a full tank, same as a loose one
                BackpackTypeCompat.EnsureFuelCaptured(localPlayer.backpackSlot?.prefab, instanceData, values, log);

                return values.Count > 0 ? values : null;
            }
            catch (Exception e)
            {
                log?.LogWarning($"OwnSaveCapture: worn-backpack state capture failed (non-fatal): {e.Message}");
                return null;
            }
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

        // New capture, not a port (see OwnSaveData.heldItemState remarks): the item
        // sitting in Player.tempFullSlot (slot ID 250), i.e. the 4th item held in
        // hand when all 3 regular itemSlots are already full. Same shape as
        // CaptureInventory's per-item state but for the single fixed temp slot instead
        // of a loop - slotIndex is stamped as 250 purely for readability in the saved
        // JSON, restore never reads it back
        private static OwnSavedItemState CaptureHeldItem(Player localPlayer, ManualLogSource log)
        {
            ItemSlot slot = localPlayer?.tempFullSlot;
            if (slot == null || slot.IsEmpty() || slot.prefab == null) return null;
            ItemInstanceData instanceData = slot.data;
            if (instanceData == null) return null;

            var state = new OwnSavedItemState { itemId = slot.prefab.itemID, slotIndex = 250 };
            CaptureItemStateValues(instanceData, slot.prefab.itemID, state.values, log);
            return state;
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
