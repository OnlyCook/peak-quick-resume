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
        /// it), falling back to that raw position if no Campfire is found nearby at all
        /// </summary>
        public static void Capture(Vector3 fallbackPos, ManualLogSource log, out bool broken, out bool hasItem, out ushort itemId)
        {
            broken = false;
            hasItem = false;
            itemId = 0;
            try
            {
                Vector3 searchCenter = ResolveCampfirePos(fallbackPos);
                RespawnChest statue = FindNearestStatue(searchCenter);
                if (statue == null)
                {
                    log?.LogInfo($"AncientStatueRestore.Capture: no Ancient Statue found within {StatueSearchRadius}m of {searchCenter}.");
                    return;
                }

                broken = statue.IsOpen;
                log?.LogInfo($"AncientStatueRestore.Capture: found statue '{statue.name}' at {statue.transform.position} "
                    + $"({Vector3.Distance(statue.transform.position, searchCenter):F1}m from search center), broken={broken}.");
                if (!broken) return;

                Item groundItem = FindNearestGroundItem(statue.transform.position);
                if (groundItem == null)
                {
                    log?.LogInfo("AncientStatueRestore.Capture: statue is broken but no unclaimed ground item found nearby.");
                    return;
                }

                hasItem = true;
                itemId = groundItem.itemID;
                log?.LogInfo($"AncientStatueRestore.Capture: statue holds item '{groundItem.name}' (id={itemId}).");
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
            if (data == null || !data.ancientStatueBroken)
            {
                log?.LogInfo("AncientStatueRestore.Restore: nothing to restore for this load (statue was unbroken when saved, or no save data).");
                return;
            }
            try
            {
                Vector3 searchCenter = ResolveCampfirePos(fallbackPos);
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

                if (data.ancientStatueHasItem
                    && ItemDatabase.TryGetItem(data.ancientStatueItemId, out Item prefab) && prefab != null)
                {
                    // Use the statue's OWN configured spawn spot (session-reported: our
                    // earlier transform.position + transform.up guess landed the item
                    // floating well off to the side and in the air - transform.up isn't
                    // reliably "the hands" for a statue placed on uneven terrain). This
                    // is exactly what Spawner.SpawnItems itself uses (GetSpawnSpots(),
                    // decompile ~23604/31752 for Luggage specifically), so it lands in
                    // the same spot the vanilla open flow would have put it
                    if (!TryGetSpawnTransform(statue, out Vector3 spawnPos, out Quaternion spawnRot))
                    {
                        spawnPos = statue.transform.position + statue.transform.up * 1.5f;
                        spawnRot = statue.transform.rotation;
                        log?.LogWarning("AncientStatueRestore: statue has no configured spawn spot, falling back to an offset above its transform.");
                    }

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

                        log?.LogInfo($"AncientStatueRestore: respawned saved item {data.ancientStatueItemId} at {spawnPos} on the Ancient Statue.");
                    }
                    else
                    {
                        log?.LogWarning($"AncientStatueRestore: InstantiateItemRoom returned null for item {data.ancientStatueItemId}.");
                    }
                }
            }
            catch (Exception e)
            {
                log?.LogError($"AncientStatueRestore.Restore failed (non-fatal): {e}");
            }
        }

        // Deliberately NOT MapHandler.CurrentCampfire here (tried first, reverted -
        // session-confirmed broken): that resolves off Singleton<MapHandler>.Instance.
        // currentSegment, which only advances inside JumpToSegmentLogic - i.e. during
        // an actual teleport. At CAPTURE time (Campfire.Interact_CastFinished just
        // fired) no teleport has happened yet, so currentSegment is still the PREVIOUS
        // segment and CurrentCampfire silently resolved to the wrong, already-passed
        // campfire instead of the one that was just lit. Finding the nearest real
        // Campfire object to the given position sidesteps that bookkeeping entirely and
        // is reliable both at capture time (the player is standing right at the
        // campfire they just lit) and at restore time (the player's saved position was
        // captured right next to that same campfire)
        private const float CampfireSearchRadius = 30f;
        private static Vector3 ResolveCampfirePos(Vector3 fallbackPos)
        {
            try
            {
                Campfire nearest = null;
                float best = float.MaxValue;
                foreach (Campfire c in UnityEngine.Object.FindObjectsByType<Campfire>(FindObjectsSortMode.None))
                {
                    if (c == null) continue;
                    float d = Vector3.Distance(c.transform.position, fallbackPos);
                    if (d <= CampfireSearchRadius && d < best) { best = d; nearest = c; }
                }
                if (nearest != null) return nearest.transform.position;
            }
            catch { /* fall through to the fallback below */ }
            return fallbackPos;
        }

        // Reads the statue's own configured spawn point(s) directly off Spawner's public
        // fields (spawnPointMode/spawnSpots/weightedSpawnSpots), mirroring what
        // Spawner.GetSpawnSpots() (protected, decompile ~23604) itself returns - so an
        // item we spawn here lands exactly where the vanilla open flow would put it,
        // rather than guessing an offset from the statue's transform
        private static bool TryGetSpawnTransform(RespawnChest statue, out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = default;

            List<Transform> spots = statue.spawnPointMode == Spawner.SpawnPointMode.WeightedLists
                ? PickWeightedSpots(statue.weightedSpawnSpots)
                : statue.spawnSpots;

            if (spots == null) return false;
            foreach (Transform t in spots)
            {
                if (t == null) continue;
                pos = t.position;
                rot = t.rotation;
                return true;
            }
            return false;
        }

        private static List<Transform> PickWeightedSpots(List<Spawner.WeightedSpawnPointEntry> entries)
        {
            if (entries == null || entries.Count == 0) return null;

            int totalWeight = 0;
            foreach (Spawner.WeightedSpawnPointEntry e in entries) totalWeight += Mathf.Max(0, e.weight);
            if (totalWeight <= 0) return entries[0].spawnSpots;

            int roll = UnityEngine.Random.Range(0, totalWeight);
            foreach (Spawner.WeightedSpawnPointEntry e in entries)
            {
                roll -= Mathf.Max(0, e.weight);
                if (roll < 0) return e.spawnSpots;
            }
            return entries[entries.Count - 1].spawnSpots;
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

        private static Item FindNearestGroundItem(Vector3 statuePos)
        {
            Item nearest = null;
            float best = float.MaxValue;
            foreach (Item item in UnityEngine.Object.FindObjectsByType<Item>(FindObjectsSortMode.None))
            {
                if (item == null || item.itemState != ItemState.Ground) continue;
                float d = Vector3.Distance(item.transform.position, statuePos);
                if (d <= ItemSearchRadius && d < best) { best = d; nearest = item; }
            }
            return nearest;
        }
    }
}
