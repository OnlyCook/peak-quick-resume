using System.Collections.Generic;
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
        /// Guarantees the save carries an explicit <c>Fuel</c> value for anything that
        /// burns it (currently the Jetpack, via 2.0.a's new <c>JetpackItem</c> component).
        ///
        /// WHY THIS IS NEEDED: on restore we push an ItemInstanceData built purely from
        /// what the save contains, and that push makes the game run
        /// <code>
        /// JetpackItem.OnInstanceDataSet()
        ///   -> fuel = GetData(DataEntryKey.Fuel, SetupDefaultFuel).Value
        ///      -> SetupDefaultFuel() => new FloatItemData { Value = startingFuel }   // FULL
        /// </code>
        /// So a MISSING Fuel entry is not restored as "no fuel" - the game helpfully
        /// creates one at <c>startingFuel</c> and hands the player a full tank. A jetpack
        /// that had a fuel entry (say 19%) round-trips correctly; one without ever having
        /// had it comes back full, which is the reported bug.
        ///
        /// Note the game is inconsistent about this default: <c>Backpack.GetFuel()</c>
        /// reads <c>GetData&lt;FloatItemData&gt;(Fuel)</c> with no factory, so a missing
        /// entry defaults to 0 there, while JetpackItem defaults it to full. Writing the
        /// value explicitly means neither default is ever consulted on restore
        /// </summary>
        /// <param name="fuelSource">
        /// The component carrying the fuel behaviour. For a loose item that is the item
        /// itself; for a worn backpack it is the slot's prefab, since only the prefab has
        /// the component while the live values live on the slot's own instance data
        /// </param>
        /// <param name="data">The instance data actually holding this backpack's values</param>
        public static void EnsureFuelCaptured(Item fuelSource, ItemInstanceData data, Dictionary<string, OwnSavedEntry> values, ManualLogSource log)
        {
            if (fuelSource == null || values == null || values.ContainsKey("Fuel")) return;

            try
            {
                var jetpack = fuelSource.GetComponent<JetpackItem>();
                if (jetpack == null) return; // nothing here burns fuel

                // The stored entry first, since that is authoritative in both shapes and is
                // the only meaningful source for a worn backpack
                float fuel;
                string source;
                if (data != null && data.TryGetDataEntry(DataEntryKey.Fuel, out FloatItemData entry) && entry != null)
                {
                    fuel = entry.Value;
                    source = "stored entry";
                }
                // No entry: for a LOOSE item the component's own synced field is the live
                // truth (JetpackItem keeps `fuel` in step over IPunObservable). Only trust
                // it when fuelSource really is that live item - for a worn backpack it is
                // the prefab, whose field is just the authored value and says nothing about
                // this player's tank
                else if (ReferenceEquals(fuelSource.data, data) && TryReadLiveFuel(jetpack, out fuel))
                {
                    source = "live JetpackItem.fuel";
                }
                // Nothing better available: match what the game would have defaulted to,
                // so this is never worse than the behaviour without us
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
