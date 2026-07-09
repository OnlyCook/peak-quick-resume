using BepInEx.Logging;

namespace PEAKQuickResume
{
    /// <summary>
    /// STUB for Phase 8 milestone M2. The real port of <c>CustomJumpToSegment</c>
    /// (decompile 2263-2563 - teleport, revive, afflictions, inventory, world-loot
    /// reset, environment reset) lands in milestones M3-M5. This placeholder exists
    /// only so <see cref="OwnLoadEntryPoints"/>'s guard chain has somewhere to hand
    /// off to and can be exercised end-to-end; it does not move the player, restore
    /// any state, or touch the game world in any way
    /// </summary>
    public static class OwnTeleportSequence
    {
        public static void Begin(OwnSaveData data, ManualLogSource log)
        {
            log?.LogInfo("OwnTeleportSequence (STUB, Phase 8 M2): guard chain reached the restore "
                + $"hand-off. Would restore segment={data.segment}, "
                + $"pos=({data.posX:F1}, {data.posY:F1}, {data.posZ:F1}), scene={data.sceneName}, "
                + $"campfire='{data.campfireName}'. No real teleport/restore happens yet (see M3+ "
                + "in ROADMAP.md Phase 8).");
        }
    }
}
