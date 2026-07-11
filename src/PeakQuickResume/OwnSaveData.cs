using System.Collections.Generic;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our own copy of dominik0207's checkpoint-mod save-file shape
    /// (<c>PEAK_Checkpoint_Save.Plugin.SaveData</c> and friends, decompile
    /// lines 389-459). Field names/types are kept byte-for-byte identical
    /// (same names, same order, same nested types) so a save file written by
    /// either mod deserializes correctly in the other during the Phase 8
    /// transition window - see ROADMAP.md Phase 8, "Full SaveData field
    /// reference"
    ///
    /// Do NOT rename, retype, reorder-with-effect, or drop any field here
    /// without re-checking that section of ROADMAP.md first
    /// </summary>
    public class OwnSaveData
    {
        public int settingsVersion;
        public string saveDate;
        public List<string> playerNames;
        public string campfireName;
        public float timePlayed;
        public float timeOfDay;
        public float posX;
        public float posY;
        public float posZ;
        public string sceneName;
        public List<Biome.BiomeType> biomes;
        public List<string> biome_names;
        public Segment segment;
        public bool hasBackpack;
        public bool isSkeleton;
        public List<OwnSavedItemState> inventoryItemStates;
        public List<OwnSavedBackpackItemState> backpackItemStates;
        public float[] afflictions_current;
        public float extraStamina;

        // The game's own 4th-item "held in hands" mechanic: with all 3 regular
        // itemSlots full, a further pickup lands in Player.tempFullSlot (slot ID 250,
        // TemporaryItemSlot) instead - carried, but blocks climbing until dropped/
        // stashed. Neither the checkpoint mod nor our own capture ever looked at this
        // slot before, so it's a genuinely new field, not a port - null when nothing
        // was held, or on any save predating this feature (treated identically). See
        // OwnInventoryRestore's remarks for why restoring it is safe to bolt on
        public OwnSavedItemState heldItemState;

        // Physical thorns stuck to this player's body (Cactus/Tumbleweed hazard) -
        // indices into CharacterAfflictions.physicalThorns' fixed pool of pre-placed
        // body-mesh slots (see ThornsAndTicksRestore). The "Thorns" status effect is
        // entirely DERIVED from these every frame (CharacterAfflictions.UpdateWeight),
        // so it must never be restored directly - only via this list, see
        // ThornsAndTicksRestore's remarks. Null/empty on saves predating this feature
        // or a player with no thorns stuck
        public List<ushort> stuckThornIndices;

        // Whether a tick (Bugfix) is attached to this player - see
        // ThornsAndTicksRestore. A character can only ever carry one at a time
        // (vanilla's own TickTrigger enforces this), so a bool is enough
        public bool hasTick;

        // World-object restore around the loaded campfire (see AncientStatueRestore).
        // Null on any save predating this feature or where no statue was found nearby -
        // treated the same as "nothing to restore"
        public OwnSavedStatueState ancientStatue;

        // Luggage restore around the loaded campfire (see LuggageRestore). Null on any
        // save predating this feature - treated identically to an empty list (nothing
        // to restore)
        public List<OwnSavedLuggageState> luggageStates;

        // Generic ground-item restore within 30m of the loaded campfire (see
        // WorldItemRestore) - backpacks (natural or player-dropped), berries, coconuts,
        // campfire food, anything else lying free. Deliberately excludes whatever
        // AncientStatueRestore/LuggageRestore/BackpackSaveMitigation already claimed,
        // see WorldItemRestore's own remarks. Capped at 50 entries
        public List<OwnSavedPositionedItem> worldItemStates;

        // Kept for round-trip compatibility with checkpoint-mod files during the
        // transition window (see ROADMAP.md). We never support PEAKapalooza, so
        // this is always written false; deliberately not acted on when reading
        public bool extModsPeakapaloozaPEAKTOBEACH;
    }

    /// <summary>Decompile: PEAK_Checkpoint_Save.Plugin.SavedItemState (line 434)</summary>
    public class OwnSavedItemState
    {
        public int slotIndex;
        public ushort itemId;
        public Dictionary<string, OwnSavedEntry> values = new Dictionary<string, OwnSavedEntry>();
    }

    /// <summary>Decompile: PEAK_Checkpoint_Save.Plugin.SavedBackpackItemState (line 444)</summary>
    public class OwnSavedBackpackItemState
    {
        public byte slotIndex;
        public ushort itemId;
        public Dictionary<string, OwnSavedEntry> values = new Dictionary<string, OwnSavedEntry>();
    }

    /// <summary>
    /// The Ancient Statue's state near a saved campfire (see AncientStatueRestore).
    /// <c>item</c> is null when unbroken, or when broken with nothing left unclaimed
    /// nearby (already picked up, or the touch revived a player instead of spawning
    /// anything) - only non-null when there's an actual item to restore
    /// </summary>
    public class OwnSavedStatueState
    {
        public bool broken;
        public OwnSavedPositionedItem item;
    }

    /// <summary>
    /// One Luggage box's state near a saved campfire (see LuggageRestore). A box can
    /// hold more than one item at once (a "Big Luggage" has 3 spawn spots vs a normal
    /// one's 2), hence a list
    /// </summary>
    public class OwnSavedLuggageState
    {
        public bool opened;
        public List<OwnSavedPositionedItem> items = new List<OwnSavedPositionedItem>();
    }

    /// <summary>
    /// One item found near a Luggage box (or, via WorldItemRestore, anywhere within
    /// range of the campfire), with its OWN observed position/rotation, not just which
    /// configured spawn spot it's nearest to - session-reported: matching items to
    /// spawn-spot transforms (by index, or even just "nearest spot") got the wrong
    /// result once a box's items had settled somewhere other than exactly on their
    /// original spawn point (gravity, jostling, or simply time passing before the
    /// player actually lit the campfire) - a spot floating above where an item actually
    /// rests, or two items close enough together to confuse "nearest spot" matching,
    /// both produced a wrong result. Recording exactly where the item WAS and putting
    /// it back there avoids needing to reconstruct that correspondence at all
    /// </summary>
    public class OwnSavedPositionedItem
    {
        public ushort itemId;
        public float posX;
        public float posY;
        public float posZ;
        public float rotX;
        public float rotY;
        public float rotZ;
        public float rotW;

        // Per-item "extra stats" (CookedAmount, Fuel, ItemUses, ...) - same
        // OwnItemStateIO mechanism/key set already used for inventory/backpack items
        // and BackpackSaveMitigation's phantom backpack restore. Without this, a
        // cooked marshmallow/hotdog sitting near the campfire came back raw on load
        public Dictionary<string, OwnSavedEntry> values = new Dictionary<string, OwnSavedEntry>();

        // Only populated when this item is itself a dropped/naturally-spawned Backpack
        // (see WorldItemRestore) - its own contents, same shape as a player's equipped
        // backpackItemStates. Null for every other item. A backpack's contents live in
        // a separate BackpackData entry, not in "values" above (which only covers flat
        // numeric stats like CookedAmount - irrelevant to the backpack itself)
        public List<OwnSavedBackpackItemState> backpackContents;
    }

    /// <summary>
    /// Decompile: PEAK_Checkpoint_Save.Plugin.SavedEntry (line 454). <c>type</c> is a
    /// <see cref="System.Type.AssemblyQualifiedName"/> string used by the checkpoint
    /// mod's own <c>TrySetOrCreateEntry</c> (Activator.CreateInstance) to rebuild the
    /// right wrapper type on load - keep as a string, do not simplify to a plain float
    /// </summary>
    public class OwnSavedEntry
    {
        public string type;
        public float value;
    }
}
