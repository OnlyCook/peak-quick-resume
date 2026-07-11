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
