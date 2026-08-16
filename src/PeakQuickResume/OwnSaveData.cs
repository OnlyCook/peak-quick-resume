using System.Collections.Generic;

namespace PEAKQuickResume
{
    /// <summary>
    /// The save-file shape. Field names/types are kept identical to the checkpoint mod's
    /// own save format so files remain cross-compatible. Do not rename, retype, or drop
    /// any field here without checking ROADMAP.md's "Full SaveData field reference" first.
    /// </summary>
    public class OwnSaveData
    {
        public int settingsVersion;
        public string saveDate;
        public List<string> playerNames;
        public string campfireName;
        public float timePlayed;
        public float timeOfDay;

        // DayNightManager.dayCount, the run's day counter. Not synced by any vanilla RPC,
        // so restoring it needs an explicit broadcast (see OwnNetwork.SyncDayCountAll).
        // 0 means "nothing to restore" (a fresh run's dayCount starts at 1).
        public int dayCount;

        public float posX;
        public float posY;
        public float posZ;
        public string sceneName;
        public List<Biome.BiomeType> biomes;
        public List<string> biome_names;
        public Segment segment;
        // Kept for compatibility: it's the only backpack field a pre-2.0.a save carries.
        public bool hasBackpack;

        // The typed backpack variant (None/Backpack/Fannypack/Jetpack/Rocketpack). 0/None on
        // any save predating this field, which BackpackTypeCompat.FromSave resolves against hasBackpack instead.
        public int backpackType;

        // The worn backpack's own instance data (not its contents - those are
        // backpackItemStates below), e.g. a Jetpack/Rocketpack's fuel. Null on saves predating this field.
        public Dictionary<string, OwnSavedEntry> backpackOwnValues;

        public bool isSkeleton;
        public List<OwnSavedItemState> inventoryItemStates;
        public List<OwnSavedBackpackItemState> backpackItemStates;
        public float[] afflictions_current;
        public float extraStamina;

        // The game's 4th-item "held in hands" slot (Player.tempFullSlot, slot ID 250) -
        // carried but blocks climbing until dropped. Null when nothing was held. See
        // OwnInventoryRestore for why restoring it is safe to bolt on.
        public OwnSavedItemState heldItemState;

        // Physical thorns stuck to this player's body - indices into
        // CharacterAfflictions.physicalThorns' fixed pool (see ThornsAndTicksRestore). The
        // "Thorns" status effect is derived from these every frame, so it must never be
        // restored directly, only via this list.
        public List<ushort> stuckThornIndices;

        // Whether a tick (Bugfix) is attached - see ThornsAndTicksRestore. Only one at a time.
        public bool hasTick;

        // Ancient Statue restore around the loaded campfire (see AncientStatueRestore).
        public OwnSavedStatueState ancientStatue;

        // Luggage restore around the loaded campfire (see LuggageRestore).
        public List<OwnSavedLuggageState> luggageStates;

        // Generic ground-item restore within 30m of the loaded campfire (see
        // WorldItemRestore), excluding whatever AncientStatueRestore/LuggageRestore/
        // BackpackSaveMitigation already claimed. Capped at 50 entries.
        public List<OwnSavedPositionedItem> worldItemStates;

        // Vestigial: only existed to round-trip through the old checkpoint mod during the
        // transition window. No longer written; kept only so old saves still deserialize.
        public bool extModsPeakapaloozaPEAKTOBEACH;

        // This player's in-progress achievement/Steam-stat tracking for the current run
        // (see AchievementProgressIO). Native code resets all of this on a fresh run start,
        // which our resume flow triggers, so without this every run-scoped achievement
        // silently loses progress on load.
        public OwnSavedAchievementProgress achievementProgress;

        // Deployable restore around the loaded campfire (see DeployableRestore). Checkpoint
        // Flag was deliberately not added here - tried and reverted, since its revival
        // relies on per-machine state the game never syncs (session-confirmed broken in
        // solo). Stove/Cannon have no per-player binding, so they're kept.
        public List<OwnSavedDeployableState> portableStoves;
        public List<OwnSavedDeployableState> scoutCannons;

        // Whether this player was dead (a spectating ghost, not merely knocked out) at the
        // moment this checkpoint was written. See DeathStateRestore for how this is put
        // back. A player with no file in the loaded save event is always restored alive.
        public bool isDead;

        // Nadir only: who communed with the scoutmaster's soul to write this checkpoint (see
        // NadirCommuner). The restore matches on the user id; the name is for logs. Null on
        // every other checkpoint and on pre-2.3.0 Nadir saves, which fall back to the host.
        public string nadirCommunerUserId;
        public string nadirCommunerName;

        // UnityEngine.Application.version at the moment this file was written (e.g.
        // "1.65.a"). Lets SaveArchive/SavePicker flag a save as possibly stale after a
        // game update rotates the map pool (see GameVersionCompat).
        public string gameVersion;
    }

    public class OwnSavedItemState
    {
        public int slotIndex;
        public ushort itemId;
        public Dictionary<string, OwnSavedEntry> values = new Dictionary<string, OwnSavedEntry>();
    }

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
    /// One item found near a Luggage box or campfire, with its own observed
    /// position/rotation rather than a configured spawn-spot index - matching by nearest
    /// spot got the wrong result once items had settled away from their original spawn point.
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

        // Per-item "extra stats" (CookedAmount, Fuel, ItemUses, ...). Without this, a
        // cooked marshmallow/hotdog sitting near the campfire came back raw on load.
        public Dictionary<string, OwnSavedEntry> values = new Dictionary<string, OwnSavedEntry>();

        // Only populated when this item is a dropped/naturally-spawned Backpack (see
        // WorldItemRestore) - its own contents. Null for every other item.
        public List<OwnSavedBackpackItemState> backpackContents;
    }

    /// <summary>
    /// <c>type</c> is a <see cref="System.Type.AssemblyQualifiedName"/> string used by
    /// <c>TrySetOrCreateEntry</c> to rebuild the right wrapper type on load - keep as a
    /// string, do not simplify to a plain float.
    /// </summary>
    public class OwnSavedEntry
    {
        public string type;
        public float value;
    }

    /// <summary>
    /// A JSON-friendly mirror of the game's <c>SerializableRunBasedValues</c>, whose
    /// fields are all <c>internal</c> - <see cref="AchievementProgressIO"/> reflects them
    /// in/out of this class instead. Deliberately omits
    /// <c>steamAchievementsPreviouslyUnlocked</c>: always rebuilt fresh from the local
    /// client's actual current Steam state on restore, never trusted from a save file.
    /// </summary>
    public class OwnSavedAchievementProgress
    {
        public Dictionary<int, int> runBasedInts = new Dictionary<int, int>();
        public Dictionary<int, float> runBasedFloats = new Dictionary<int, float>();
        public List<ushort> runBasedFruitsEaten = new List<ushort>();
        public List<ushort> shroomBerriesEaten = new List<ushort>();
        public List<ushort> nonToxicMushroomsEaten = new List<ushort>();
        public List<ushort> gourmandRequirementsEaten = new List<ushort>();
        public List<int> achievementsEarnedThisRun = new List<int>();
        public List<int> completedAscentsThisRun = new List<int>();
    }

    /// <summary>
    /// One player-placed deployable (Portable Stove or Scout Cannon, see
    /// DeployableRestore) near a saved campfire. Full quaternion kept (not just yaw) so
    /// an angled placement restores exactly as built.
    /// </summary>
    public class OwnSavedDeployableState
    {
        public float posX;
        public float posY;
        public float posZ;
        public float rotX;
        public float rotY;
        public float rotZ;
        public float rotW;
    }
}
