using System.Collections.Generic;
using BepInEx.Logging;
using Zorro.Core;

namespace PEAKQuickResume
{
    /// <summary>
    /// Compat shim around the backpack slot, which PEAK 2.0.a reworked from a single
    /// hasBackpack bool into a typed BackpackType enum (None/Backpack/Fannypack/
    /// Jetpack/Rocketpack). Routing every backpack read through here prevents a
    /// repeat of the old bug where a missing field threw inside a restore coroutine
    /// and hung the loading screen. TryResolveItemId resolves variants through the
    /// game's own name-keyed lookup rather than a hardcoded item ID, so restores stay
    /// correct if the database is renumbered.
    /// </summary>
    internal static class BackpackTypeCompat
    {
        /// <summary>Save-schema value for "no backpack", matching BackpackType.None</summary>
        public const int NoBackpack = 0;

        /// <summary>Save-schema value for a plain Backpack; also what a pre-2.0.a hasBackpack:true save reads back as (see FromSave).</summary>
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

        /// <summary>Reads the variant back out of a save, bridging the two schema generations.</summary>
        public static int FromSave(bool legacyHasBackpack, int savedBackpackType)
        {
            if (savedBackpackType != NoBackpack) return savedBackpackType;
            return legacyHasBackpack ? PlainBackpack : NoBackpack;
        }

        /// <summary>
        /// Maps a persisted variant to the item ID to hand to Player.AddItem. Returns
        /// false when nothing matches, so the caller skips the backpack rather than
        /// substitute the wrong variant.
        /// </summary>
        public static bool TryResolveItemId(int backpackType, out ushort itemId, ManualLogSource log)
        {
            itemId = 0;
            if (backpackType == NoBackpack) return false;

            var wanted = (BackpackSlot.BackpackType)backpackType;
            string prefabName = PrefabNameFor(backpackType);

            // Runs inside the restore coroutine, where an unhandled exception hangs the
            // loading screen; must never escape.
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

                // Fallback: the game's own name-keyed lookup.
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
        /// Guarantees the save carries an explicit Fuel value for a Jetpack. Without it,
        /// restore leaves the ItemInstanceData with no Fuel entry, and
        /// JetpackItem.OnInstanceDataSet defaults a missing entry to a full tank
        /// (startingFuel) rather than empty - unlike Backpack.GetFuel, which defaults a
        /// missing entry to 0. Writing the value explicitly sidesteps both defaults.
        /// </summary>
        /// <param name="fuelSource">
        /// The component carrying the fuel behaviour: the item itself for a loose item,
        /// or the slot's prefab for a worn backpack (only the prefab has the component).
        /// </param>
        /// <param name="data">The instance data actually holding this backpack's values</param>
        public static void EnsureFuelCaptured(Item fuelSource, ItemInstanceData data, Dictionary<string, OwnSavedEntry> values, ManualLogSource log)
        {
            if (fuelSource == null || values == null || values.ContainsKey("Fuel")) return;

            try
            {
                var jetpack = fuelSource.GetComponent<JetpackItem>();
                if (jetpack == null) return;

                float fuel;
                string source;
                if (data != null && data.TryGetDataEntry(DataEntryKey.Fuel, out FloatItemData entry) && entry != null)
                {
                    fuel = entry.Value;
                    source = "stored entry";
                }
                // Only trust the live synced field when fuelSource is the actual live item;
                // for a worn backpack it's the prefab, whose field is just the authored value.
                else if (ReferenceEquals(fuelSource.data, data) && TryReadLiveFuel(jetpack, out fuel))
                {
                    source = "live JetpackItem.fuel";
                }
                else
                {
                    fuel = jetpack.startingFuel;
                    source = "startingFuel default";
                }

                values["Fuel"] = new OwnSavedEntry { type = typeof(FloatItemData).AssemblyQualifiedName, value = fuel };
                log?.LogInfo($"OwnSaveCapture: '{fuelSource.name}' had no saved Fuel value; stamped "
                    + $"Fuel={fuel:0.###} from its {source} (without this it would restore as a full tank).");
            }
            catch (System.Exception e)
            {
                log?.LogWarning($"BackpackTypeCompat: could not capture jetpack fuel ({e.Message}); "
                    + "it may come back full.");
            }
        }

        /// <summary><c>JetpackItem.fuel</c> is private and [SerializeField], hence reflection</summary>
        private static bool TryReadLiveFuel(JetpackItem jetpack, out float fuel)
        {
            fuel = 0f;
            var field = typeof(JetpackItem).GetField("fuel",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (field == null) return false;

            object value = field.GetValue(jetpack);
            if (!(value is float f)) return false;

            fuel = f;
            return true;
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
