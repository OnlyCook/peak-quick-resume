using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Shared lookups for the family of "restore an object near the campfire that was
    /// just saved-at/loaded-at" mechanics (see AncientStatueRestore, LuggageRestore).
    /// </summary>
    internal static class CampfireAreaHelpers
    {
        // Deliberately not MapHandler.CurrentCampfire: it resolves off currentSegment,
        // which hasn't advanced yet at capture time and resolves to the wrong,
        // already-passed campfire. Finding the nearest real Campfire object sidesteps
        // that bookkeeping.
        private const float CampfireSearchRadius = 30f;

        public static Vector3 ResolveNearestCampfirePos(Vector3 fallbackPos)
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

                if (NadirSearchRadius.CurrentlyInNadir())
                {
                    Vector3 pillar = ResolveNearestSoulPillarPos(fallbackPos);
                    if (pillar != fallbackPos) return pillar;
                }
            }
            catch { /* fall through to the fallback below */ }
            return fallbackPos;
        }

        /// <summary>
        /// Nadir's stand-in for a campfire: the scoutmaster's soul pillar, which is the point
        /// a Nadir checkpoint is taken at and the anchor everything in that biome is measured
        /// from. Resolving it here (rather than leaving the caller on the saving player's own
        /// position) means capture and restore centre on the exact same fixed point, instead of
        /// on wherever the player happened to be standing at the time. Returns
        /// <paramref name="fallbackPos"/> unchanged outside Nadir, or if no pillar is in range.
        /// </summary>
        private static Vector3 ResolveNearestSoulPillarPos(Vector3 fallbackPos)
        {
            Peak.ScoutmasterSoulPillar nearest = null;
            float best = float.MaxValue;
            foreach (Peak.ScoutmasterSoulPillar p in UnityEngine.Object.FindObjectsByType<Peak.ScoutmasterSoulPillar>(FindObjectsSortMode.None))
            {
                if (p == null) continue;
                float d = Vector3.Distance(p.transform.position, fallbackPos);
                if (d <= NadirSearchRadius.Radius && d < best) { best = d; nearest = p; }
            }
            return nearest != null ? nearest.transform.position : fallbackPos;
        }

        /// <summary>
        /// Is this actually free-floating world loot, as opposed to a player's own
        /// equipped gear that merely still reads ItemState.Ground (a worn Backpack's
        /// itemState never flips away from Ground while equipped)? Mirrors the check
        /// OwnWorldLootReset.ResetWorldLoot uses for the same false positive.
        /// includeBackpacks defaults to false since AncientStatueRestore/LuggageRestore
        /// never expect a Backpack as container loot; WorldItemRestore passes true.
        /// </summary>
        public static bool IsFreeWorldItem(Item item, bool includeBackpacks = false)
        {
            if (item == null || item.itemState != ItemState.Ground) return false;
            if (!includeBackpacks && item is Backpack) return false;
            if (item.GetComponentInParent<Player>(true) != null) return false;
            if (item.GetComponentInParent<Character>(true) != null) return false;
            return true;
        }

        /// <summary>Nearest free world item to pos within radius, optionally skipping items already in exclude.</summary>
        public static Item FindNearestFreeItem(Vector3 pos, float radius, HashSet<Item> exclude = null)
        {
            Item nearest = null;
            float best = float.MaxValue;
            foreach (Item item in UnityEngine.Object.FindObjectsByType<Item>(FindObjectsSortMode.None))
            {
                if (!IsFreeWorldItem(item)) continue;
                if (exclude != null && exclude.Contains(item)) continue;
                float d = Vector3.Distance(item.transform.position, pos);
                if (d <= radius && d < best) { best = d; nearest = item; }
            }
            return nearest;
        }

        /// <summary>Every free world item within radius of pos, nearest first.</summary>
        public static List<Item> FindFreeItemsWithin(Vector3 pos, float radius, bool includeBackpacks = false, HashSet<Item> exclude = null)
        {
            return UnityEngine.Object.FindObjectsByType<Item>(FindObjectsSortMode.None)
                .Where(item => IsFreeWorldItem(item, includeBackpacks)
                    && (exclude == null || !exclude.Contains(item))
                    && Vector3.Distance(item.transform.position, pos) <= radius)
                .OrderBy(item => Vector3.Distance(item.transform.position, pos))
                .ToList();
        }

        /// <summary>
        /// Builds a fresh ItemInstanceData from saved per-item "extra stats" (CookedAmount,
        /// Fuel, ItemUses, ...), same mechanism OwnInventoryRestore uses for player items.
        /// Doesn't touch any live Item - see PushItemInstanceData for why.
        /// </summary>
        public static ItemInstanceData BuildItemInstanceData(Dictionary<string, OwnSavedEntry> values, ManualLogSource log)
        {
            var instanceData = new ItemInstanceData(Guid.NewGuid());
            ItemInstanceDataHandler.AddInstanceData(instanceData);

            if (values == null) return instanceData;
            foreach (var kv in values)
            {
                if (!OwnItemStateIO.TryGetKey(kv.Key, out DataEntryKey key)) continue;
                OwnSavedEntry entry = kv.Value;
                if (entry != null && !OwnItemStateIO.TrySetOrCreateEntry(instanceData, key, entry.type, entry.value, log))
                    log?.LogWarning($"CampfireAreaHelpers.BuildItemInstanceData: could not apply '{kv.Key}'.");
            }
            return instanceData;
        }

        /// <summary>
        /// Assigns instanceData onto a freshly spawned item via the same RPC the vanilla
        /// drop-item flow uses, not by writing .data directly - that field is only ever
        /// assigned via this RPC; writing it in the same frame as spawning silently no-ops.
        /// </summary>
        public static void PushItemInstanceData(GameObject spawned, ItemInstanceData instanceData, ManualLogSource log)
        {
            if (spawned == null || instanceData == null) return;
            if (!spawned.TryGetComponent<PhotonView>(out PhotonView pv))
            {
                log?.LogWarning("CampfireAreaHelpers.PushItemInstanceData: spawned item has no PhotonView.");
                return;
            }
            pv.RPC("SetItemInstanceDataRPC", RpcTarget.All, instanceData);
        }

        /// <summary>Convenience wrapper: build + push in one call, skipped entirely when there's nothing to apply</summary>
        public static void ApplySavedItemValues(GameObject spawned, Dictionary<string, OwnSavedEntry> values, ManualLogSource log)
        {
            if (values == null || values.Count == 0) return;
            PushItemInstanceData(spawned, BuildItemInstanceData(values, log), log);
        }
    }
}
