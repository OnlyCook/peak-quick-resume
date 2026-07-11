using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Peak.Network;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Native save/restore for the "Ancient Statue" found near most campfires - a
    /// <c>RespawnChest</c> (decompile ~69353, subclass of <c>Luggage</c>): touching it
    /// either revives a downed/dead teammate (if one qualifies) or breaks it open for a
    /// random mystical item. First of the "item/object restore around the campfire"
    /// mechanics (more to follow); nothing in the game's own save systems tracks this.
    ///
    /// PEAK's OWN mid-run "Quicksave" system (decompile ~88340, <c>Quicksave.RunProgress</c>)
    /// tracks a SEPARATE, narrower case - only base-camp <c>Luggage.IsOpen</c>
    /// (<c>openLuggageViewIds</c>), restored via item-spawn history
    /// (<c>SpawnedItemTracker</c>/<c>Hash128 SpawnerId</c>). Deliberately not reused here:
    /// that history mechanism only exists on a GameObject once
    /// <c>MapHandler.EnsureSpawnTrackersAttached()</c> has run, which itself is only ever
    /// triggered from the vanilla Quicksave load path (<c>RunManager.Start</c>) - we never
    /// use vanilla Quicksave, so relying on it here would be silently inert on a fresh
    /// map load. Restoring the saved item id ourselves (rather than replaying the
    /// vanilla open flow, which would just roll a fresh random item) matches this mod's
    /// existing approach elsewhere (see BackpackSaveMitigation, OwnInventoryRestore):
    /// reconstruct saved state directly instead of re-triggering original gameplay
    /// events and hoping they land the same way
    ///
    /// Three states captured/restored, matching Luggage's own state machine exactly:
    ///  - Closed (unbroken): nothing to do, this is the scene's own default after
    ///    <see cref="OwnWorldLootReset.ResetWorldLoot"/> runs
    ///  - Open (broken), no item nearby (already picked up, or the touch revived a
    ///    player instead of spawning anything): <c>chest.Break()</c> only - sets
    ///    state=Open with no spawn, mirroring the vanilla revive-bookkeeping path's own
    ///    no-item open exactly (decompile: <c>RespawnChest.Break()</c>)
    ///  - Open (broken), with an unclaimed item nearby: <c>chest.Break()</c> plus we
    ///    spawn the saved item ourselves via <c>PhotonNetwork.InstantiateItemRoom</c>
    ///    (the same API <c>Spawner</c>/<c>SpawnedItemTracker</c> use), instead of letting
    ///    the vanilla open flow (<c>Interact_CastFinished</c> -&gt; <c>OpenLuggageRPC(true)</c>
    ///    -&gt; <c>SpawnItemRoutine</c>) roll a new random one
    ///
    /// Host-only throughout (world state, not per-player - only the host should ever
    /// touch it). Every step is wrapped and non-fatal: this class never touches disk, so
    /// a failure here can only mean a statue restores wrong (or not at all), never a
    /// corrupted save - matching the maintainer's explicit priority for this mechanic
    /// </summary>
    public static class AncientStatueRestore
    {
        // Statues aren't placed a consistent distance from their campfire (session-
        // reported: not always ~30m as first assumed), so this is a generous hard cap
        // rather than a tight proximity check - just enough to stop a full map scan.
        // The farthest confirmed real placement is ~68m; a statue can't be reached
        // before its paired campfire is lit anyway (session-confirmed: it's the final
        // one, gated behind the campfire), so there's no real case for the nearest one
        // within range being some OTHER, unrelated statue. We still only ever take the
        // SINGLE nearest candidate within it, matching BackpackSaveMitigation's own
        // approach: if a buggy scene somehow put two RespawnChest within range, the
        // farther one can't be the one paired with this campfire anyway
        private const float StatueSearchRadius = 100f;

        // RespawnChest's own revive spot formula (decompile: RandomRevivePoint =
        // transform.position + transform.up * 6f + Random.onUnitSphere) tops out
        // ~7m from the statue - 8m comfortably covers a real spawned item without
        // reaching into unrelated ground loot a player happened to drop nearby
        private const float ItemSearchRadius = 8f;

        /// <summary>
        /// Called from OwnSaveCapture right before writing OwnSaveData. Searches around
        /// the actual Campfire object nearest <paramref name="fallbackPos"/> (the saving
        /// player's own position - whoever lit the campfire is standing right next to
        /// it), falling back to that raw position if no Campfire is found nearby at all.
        /// Adds the found item (if any) to <paramref name="claimed"/> so LuggageRestore/
        /// WorldItemRestore, called right after this in the same save, don't also save
        /// it as their own - see WorldItemRestore's class remarks for why that matters.
        /// Returns null when there's no statue nearby at all (as opposed to a non-null
        /// state with <c>broken = false</c>, which means a statue WAS found, just untouched)
        /// </summary>
        public static void Capture(Vector3 fallbackPos, HashSet<Item> claimed, ManualLogSource log, out OwnSavedStatueState state)
        {
            state = null;
            try
            {
                Vector3 searchCenter = CampfireAreaHelpers.ResolveNearestCampfirePos(fallbackPos);
                RespawnChest statue = FindNearestStatue(searchCenter);
                if (statue == null)
                {
                    log?.LogInfo($"AncientStatueRestore.Capture: no Ancient Statue found within {StatueSearchRadius}m of {searchCenter}.");
                    return;
                }

                state = new OwnSavedStatueState { broken = statue.IsOpen };
                log?.LogInfo($"AncientStatueRestore.Capture: found statue '{statue.name}' at {statue.transform.position} "
                    + $"({Vector3.Distance(statue.transform.position, searchCenter):F1}m from search center), broken={state.broken}.");
                if (!state.broken) return;

                Item groundItem = CampfireAreaHelpers.FindNearestFreeItem(statue.transform.position, ItemSearchRadius, claimed);
                if (groundItem == null)
                {
                    log?.LogInfo("AncientStatueRestore.Capture: statue is broken but no unclaimed ground item found nearby.");
                    return;
                }

                var item = new OwnSavedPositionedItem
                {
                    itemId = groundItem.itemID,
                    posX = groundItem.transform.position.x,
                    posY = groundItem.transform.position.y,
                    posZ = groundItem.transform.position.z,
                    rotX = groundItem.transform.rotation.x,
                    rotY = groundItem.transform.rotation.y,
                    rotZ = groundItem.transform.rotation.z,
                    rotW = groundItem.transform.rotation.w,
                };
                foreach (var kv in OwnItemStateIO.ReadItemStateValues(groundItem.data, groundItem.itemID))
                    item.values[kv.Key] = new OwnSavedEntry { type = kv.Value.TypeName, value = kv.Value.Value };
                state.item = item;

                claimed?.Add(groundItem);
                log?.LogInfo($"AncientStatueRestore.Capture: statue holds item '{groundItem.name}' (id={item.itemId}) at {groundItem.transform.position}.");
            }
            catch (Exception e)
            {
                log?.LogWarning($"AncientStatueRestore.Capture failed (non-fatal): {e.Message}");
            }
        }

        /// <summary>
        /// Called once per load (host-only, world state - not per player, so callers
        /// should only invoke this once regardless of coop player count) from
        /// <see cref="OwnTeleportSequence"/> right after
        /// <see cref="OwnWorldLootReset.ResetWorldLoot"/> has reset every Luggage/
        /// RespawnChest back to Closed, which would otherwise silently undo whatever
        /// this restores. A no-op for saves made before this feature existed (the new
        /// fields default to false/false/0 on deserialize) and for campfires with no
        /// nearby statue at all
        /// </summary>
        public static void Restore(OwnSaveData data, Vector3 fallbackPos, ManualLogSource log)
        {
            if (data?.ancientStatue == null || !data.ancientStatue.broken)
            {
                log?.LogInfo("AncientStatueRestore.Restore: nothing to restore for this load (statue was unbroken when saved, or no save data).");
                return;
            }
            try
            {
                Vector3 searchCenter = CampfireAreaHelpers.ResolveNearestCampfirePos(fallbackPos);
                RespawnChest statue = FindNearestStatue(searchCenter);
                if (statue == null)
                {
                    log?.LogWarning($"AncientStatueRestore: no Ancient Statue found within {StatueSearchRadius}m of {searchCenter}, nothing to restore.");
                    return;
                }
                log?.LogInfo($"AncientStatueRestore: found statue '{statue.name}' at {statue.transform.position} "
                    + $"({Vector3.Distance(statue.transform.position, searchCenter):F1}m from search center), currently open={statue.IsOpen}.");

                // Defensive only - ResetWorldLoot should already have closed it. If it's
                // somehow already open (e.g. a repeat load this round, or a future game
                // update changes the field ResetWorldLoot resets), don't re-break it and
                // don't spawn a second copy of the item on top of whatever's already there
                if (statue.IsOpen) return;

                statue.Break();
                log?.LogInfo("AncientStatueRestore: restored the Ancient Statue to its broken state.");

                OwnSavedPositionedItem item = data.ancientStatue.item;
                if (item != null && ItemDatabase.TryGetItem(item.itemId, out Item prefab) && prefab != null)
                {
                    // Spawn at the item's OWN captured position/rotation (see
                    // OwnSavedPositionedItem's remarks, same reasoning applies here) rather
                    // than the statue's configured spawn spot or a transform.up offset -
                    // both were tried and reverted (session-confirmed): a spawn spot is
                    // where physics DROPS the item, not necessarily where it settles,
                    // and transform.up isn't reliably "the hands" on uneven terrain
                    Vector3 spawnPos = new Vector3(item.posX, item.posY, item.posZ);
                    Quaternion spawnRot = new Quaternion(item.rotX, item.rotY, item.rotZ, item.rotW);

                    GameObject spawned = PhotonNetwork.InstantiateItemRoom(prefab.name, spawnPos, spawnRot);
                    if (spawned != null)
                    {
                        // Mirrors Spawner.InitializePhysics's own kinematic-freeze step
                        // (decompile ~23649) so a restored item settles the same way a
                        // freshly-broken statue's item would, using the statue's own
                        // isKinematic setting (public field on Spawner) rather than
                        // assuming true
                        if (statue.isKinematic && spawned.TryGetComponent<PhotonView>(out PhotonView view))
                            view.RPC("SetKinematicRPC", RpcTarget.AllBuffered, true, spawnPos, spawnRot);

                        CampfireAreaHelpers.ApplySavedItemValues(spawned, item.values, log);

                        log?.LogInfo($"AncientStatueRestore: respawned saved item {item.itemId} at {spawnPos} on the Ancient Statue.");
                    }
                    else
                    {
                        log?.LogWarning($"AncientStatueRestore: InstantiateItemRoom returned null for item {item.itemId}.");
                    }
                }
            }
            catch (Exception e)
            {
                log?.LogError($"AncientStatueRestore.Restore failed (non-fatal): {e}");
            }
        }

        private static RespawnChest FindNearestStatue(Vector3 nearPos)
        {
            RespawnChest nearest = null;
            float best = float.MaxValue;
            foreach (RespawnChest chest in UnityEngine.Object.FindObjectsByType<RespawnChest>(FindObjectsSortMode.None))
            {
                if (chest == null) continue;
                float d = Vector3.Distance(chest.transform.position, nearPos);
                if (d <= StatueSearchRadius && d < best) { best = d; nearest = chest; }
            }
            return nearest;
        }
    }
}
