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
    /// just saved-at/loaded-at" mechanics (see AncientStatueRestore, LuggageRestore,
    /// more to come). Factored out once a second consumer needed the exact same two
    /// pieces of logic - keep this file free of anything mechanic-specific
    /// </summary>
    internal static class CampfireAreaHelpers
    {
        // Deliberately NOT MapHandler.CurrentCampfire (tried first for the statue,
        // reverted - session-confirmed broken): that resolves off
        // Singleton<MapHandler>.Instance.currentSegment, which only advances inside
        // JumpToSegmentLogic - i.e. during an actual teleport. At CAPTURE time
        // (Campfire.Interact_CastFinished just fired) no teleport has happened yet, so
        // currentSegment is still the PREVIOUS segment and CurrentCampfire silently
        // resolves to the wrong, already-passed campfire instead of the one that was
        // just lit. Finding the nearest real Campfire object to the given position
        // sidesteps that bookkeeping entirely and is reliable both at capture time (the
        // player is standing right at the campfire they just lit) and at restore time
        // (the player's saved position was captured right next to that same campfire)
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
            }
            catch { /* fall through to the fallback below */ }
            return fallbackPos;
        }

        /// <summary>
        /// Is this actually free-floating world loot, as opposed to a player's own
        /// equipped gear that merely happens to still read <c>ItemState.Ground</c>?
        /// Session-confirmed: a worn Backpack's own itemState never flips away from
        /// Ground while equipped (its Update() only ever toggles ground/held MESH
        /// visuals off that field, decompile ~176 - it isn't "was this picked up",
        /// just "which mesh to show"), so a player merely standing near a statue or
        /// luggage box got their own backpack (and whatever's visually nested inside
        /// it) swept up as "the item it's holding". Mirrors the exact same check
        /// <see cref="OwnWorldLootReset.ResetWorldLoot"/> already uses to tell real
        /// dropped items apart from player-attached ones (decompile-adjacent, not
        /// itself ported from anywhere - a mitigation for this specific false positive)
        ///
        /// <paramref name="includeBackpacks"/> defaults to false (AncientStatueRestore/
        /// LuggageRestore's own container-item searches never expect a Backpack as
        /// loot) - WorldItemRestore passes true, since a dropped/naturally-spawned
        /// backpack IS exactly the kind of ground item it's meant to restore (with its
        /// own separate exclusion for backpacks BackpackSaveMitigation already owns,
        /// see that class)
        /// </summary>
        public static bool IsFreeWorldItem(Item item, bool includeBackpacks = false)
        {
            if (item == null || item.itemState != ItemState.Ground) return false;
            if (!includeBackpacks && item is Backpack) return false;
            if (item.GetComponentInParent<Player>(true) != null) return false;
            if (item.GetComponentInParent<Character>(true) != null) return false;
            return true;
        }

        /// <summary>
        /// The nearest <see cref="IsFreeWorldItem"/> to <paramref name="pos"/> within
        /// <paramref name="radius"/>, optionally skipping anything already in
        /// <paramref name="exclude"/> (so a caller matching several spawn spots against
        /// several nearby items doesn't hand the same physical item to two of them)
        /// </summary>
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

        /// <summary>
        /// Every <see cref="IsFreeWorldItem"/> within <paramref name="radius"/> of
        /// <paramref name="pos"/>, nearest first, optionally skipping anything already
        /// in <paramref name="exclude"/> (see <see cref="FindNearestFreeItem"/>)
        /// </summary>
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
        /// Builds a fresh, populated <c>ItemInstanceData</c> from saved per-item "extra
        /// stats" (CookedAmount, Fuel, ItemUses, ...) - same OwnItemStateIO mechanism
        /// OwnInventoryRestore already uses for player inventory/backpack items. Doesn't
        /// touch any live Item - see <see cref="PushItemInstanceData"/> for why building
        /// this off to the side and pushing it via RPC, rather than writing values
        /// directly onto a freshly spawned item's own <c>.data</c>, is required
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
        /// Assigns <paramref name="instanceData"/> onto a freshly spawned item via the
        /// same RPC the vanilla drop-item flow itself uses (decompile ~6295-6313,
        /// <c>Item.SetItemInstanceDataRPC</c>) - NOT by writing to the spawned item's
        /// own <c>.data</c> field directly. Session-confirmed: that field is only ever
        /// assigned via this same RPC, never synchronously right after
        /// <c>PhotonNetwork.InstantiateItemRoom</c> returns (its own <c>Start()</c>
        /// lazily creates an empty one instead, decompile ~9707-9718/10480-10496, before
        /// this RPC has a chance to land) - writing directly onto <c>.data</c> in the
        /// same frame as spawning silently no-ops (a cooked item came back raw)
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
