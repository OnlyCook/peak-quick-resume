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

        // World-object restore around the loaded campfire (see AncientStatueRestore).
        // Not present in checkpoint-mod files or pre-existing saves - Newtonsoft leaves
        // these at their defaults (false/false/0) when absent, which correctly means
        // "nothing to restore" for both an old save and a campfire with no statue at all
        public bool ancientStatueBroken;
        public bool ancientStatueHasItem;
        public ushort ancientStatueItemId;

        // Where the item actually was, not just which item - see OwnSavedLuggageItem's
        // own remarks for why capturing a spawn point/slot instead of the item's real
        // observed transform isn't good enough (items drift off their spawn point once
        // physics settles, well before a player gets around to lighting the campfire)
        public float ancientStatueItemPosX;
        public float ancientStatueItemPosY;
        public float ancientStatueItemPosZ;
        public float ancientStatueItemRotX;
        public float ancientStatueItemRotY;
        public float ancientStatueItemRotZ;
        public float ancientStatueItemRotW;

        // Luggage restore around the loaded campfire (see LuggageRestore). Null on any
        // save predating this feature - treated identically to an empty list (nothing
        // to restore)
        public List<OwnSavedLuggageState> luggageStates;

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
    /// One Luggage box's state near a saved campfire (see LuggageRestore). A box can
    /// hold more than one item at once (a "Big Luggage" has 3 spawn spots vs a normal
    /// one's 2), hence a list
    /// </summary>
    public class OwnSavedLuggageState
    {
        public bool opened;
        public List<OwnSavedLuggageItem> items = new List<OwnSavedLuggageItem>();
    }

    /// <summary>
    /// One item found near a Luggage box, with its OWN observed position/rotation, not
    /// just which configured spawn spot it's nearest to - session-reported: matching
    /// items to spawn-spot transforms (by index, or even just "nearest spot") got the
    /// wrong result once a box's items had settled somewhere other than exactly on
    /// their original spawn point (gravity, jostling, or simply time passing before the
    /// player actually lit the campfire) - a spot floating above where an item actually
    /// rests, or two items close enough together to confuse "nearest spot" matching,
    /// both produced a wrong result. Recording exactly where the item WAS and putting
    /// it back there avoids needing to reconstruct that correspondence at all
    /// </summary>
    public class OwnSavedLuggageItem
    {
        public ushort itemId;
        public float posX;
        public float posY;
        public float posZ;
        public float rotX;
        public float rotY;
        public float rotZ;
        public float rotW;
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
