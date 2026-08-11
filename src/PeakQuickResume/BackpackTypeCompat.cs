using BepInEx.Logging;
using Zorro.Core;

namespace PEAKQuickResume
{
    /// <summary>
    /// Compat shim around the backpack slot, which PEAK 2.0.a reworked from a single
    /// boolean into a typed enum:
    /// <code>
    /// public bool hasBackpack;                                       // up to 1.65.a
    /// public BackpackType backpackType;                              // 2.0.a
    /// // None / Backpack / Fannypack / Jetpack / Rocketpack
    /// </code>
    ///
    /// This one was responsible for the "LOADING SAVE..." hang: the old
    /// <c>hasBackpack</c> read sat inside <c>OwnInventoryRestore.RestoreAll</c>, and
    /// because a coroutine compiles to a single <c>MoveNext</c>, the missing field threw
    /// on the FIRST call - so the entire restore died before running a single statement,
    /// never reported done, and left the loading screen up forever. Routing every
    /// backpack read through here keeps that blast radius from recurring.
    ///
    /// <see cref="TryResolveItemId"/> exists because a backpack can no longer be restored
    /// by hardcoding item ID 6 ("Backpack"): the variant has to come back as the variant
    /// it was. It resolves through the game's own name-keyed lookup - the same route
    /// vanilla itself uses for the backpack slot (<c>ItemDatabase.TryGetItem(
    /// backpackSlot.GetPrefabName(), ...)</c>) - so the IDs are never guessed and stay
    /// correct if the database is renumbered. <c>Player.AddItem</c> then stamps
    /// <c>backpackSlot.backpackType</c> from the prefab's own <c>Backpack.backpackType</c>
    /// on its own, so nothing here has to set the type by hand
    /// </summary>
    internal static class BackpackTypeCompat
    {
        /// <summary>Save-schema value for "no backpack", matching BackpackType.None</summary>
        public const int NoBackpack = 0;

        /// <summary>
        /// Save-schema value for a plain Backpack. Also what a pre-2.0.a save with
        /// <c>hasBackpack: true</c> and no stored type is read back as - see
        /// <see cref="FromSave"/>
        /// </summary>
        public const int PlainBackpack = (int)BackpackSlot.BackpackType.Backpack;

        /// <summary>True if this player is wearing any backpack variant at all</summary>
        public static bool HasAny(Player p) =>
            p?.backpackSlot != null && p.backpackSlot.backpackType != BackpackSlot.BackpackType.None;

        /// <summary>True if this slot holds any backpack variant at all</summary>
        public static bool HasAny(BackpackSlot slot) =>
            slot != null && slot.backpackType != BackpackSlot.BackpackType.None;

        /// <summary>The variant to persist, as its raw enum value (0 when there's none)</summary>
        public static int Capture(Player p) =>
            p?.backpackSlot != null ? (int)p.backpackSlot.backpackType : NoBackpack;

        /// <summary>
        /// Reads the variant back out of a save, bridging the two schema generations.
        /// Saves written before 2.0.a only carry <c>hasBackpack</c> and no
        /// <c>backpackType</c> at all (so it deserializes as 0/None) - back then a plain
        /// Backpack was the only kind that existed, so that combination is unambiguous
        /// and reads back as <see cref="PlainBackpack"/>
        /// </summary>
        public static int FromSave(bool legacyHasBackpack, int savedBackpackType)
        {
            if (savedBackpackType != NoBackpack) return savedBackpackType;
            return legacyHasBackpack ? PlainBackpack : NoBackpack;
        }

        /// <summary>
        /// Maps a persisted variant to the item ID to hand to <c>Player.AddItem</c>.
        /// False when there's nothing to restore or no matching prefab can be found, in
        /// which case the caller should skip the backpack rather than substitute a wrong
        /// one (handing back the wrong variant is worse than handing back none).
        ///
        /// Resolution is by the prefab's own <c>Backpack.backpackType</c> rather than by
        /// name, which is what actually defines the variant - <c>Player.AddItem</c> reads
        /// that same field to stamp the slot. The name lookup is only a fallback for the
        /// case where a prefab somehow isn't a <c>Backpack</c> component
        /// </summary>
        public static bool TryResolveItemId(int backpackType, out ushort itemId, ManualLogSource log)
        {
            itemId = 0;
            if (backpackType == NoBackpack) return false;

            var wanted = (BackpackSlot.BackpackType)backpackType;
            string prefabName = PrefabNameFor(backpackType);

            // Nothing in here is allowed to escape: this runs inside the restore
            // coroutine, where a single unhandled exception takes down the ENTIRE restore
            // and hangs the loading screen - which is exactly how the 2.0.a backpack
            // change broke loading in the first place. Losing the backpack is recoverable;
            // losing the restore is not
            try
            {
                var lookup = SingletonAsset<ItemDatabase>.Instance?.itemLookup;
                if (lookup != null)
                {
                    foreach (var kv in lookup)
                    {
                        if (kv.Value is Backpack backpack && backpack.backpackType == wanted)
                        {
                            itemId = kv.Key;
                            return true;
                        }
                    }
                }

                // Fallback: the game's own name-keyed lookup, the same route vanilla uses
                // for the backpack slot (ItemDatabase.TryGetItem(GetPrefabName(), ...))
                if (ItemDatabase.TryGetItem(prefabName, out Item item) && item != null)
                {
                    itemId = item.itemID;
                    return true;
                }
            }
            catch (System.Exception e)
            {
                log?.LogWarning($"BackpackTypeCompat: backpack lookup for type {wanted} failed ({e.Message}) - skipping the backpack restore.");
                return false;
            }

            log?.LogWarning($"BackpackTypeCompat: no prefab for backpack type {wanted} ('{prefabName}') in the item database - skipping the backpack restore.");
            return false;
        }

        /// <summary>
        /// Mirrors <c>BackpackSlot.GetPrefabName</c> for a persisted enum value. Kept as
        /// an explicit switch rather than <c>ToString()</c> so an enum member being
        /// renamed shows up here instead of silently missing the database lookup
        /// </summary>
        private static string PrefabNameFor(int backpackType)
        {
            switch ((BackpackSlot.BackpackType)backpackType)
            {
                case BackpackSlot.BackpackType.Fannypack: return "Fannypack";
                case BackpackSlot.BackpackType.Jetpack: return "Jetpack";
                case BackpackSlot.BackpackType.Rocketpack: return "Rocketpack";
                default: return "Backpack";
            }
        }
    }
}
