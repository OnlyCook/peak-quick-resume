using System;
using BepInEx.Logging;
using Photon.Pun;

namespace PEAKQuickResume
{
    /// <summary>
    /// Fixes a solo-only vanilla gap: loading a save with the checkpoint mod's
    /// <c>teleportJumpLogic = 0</c> (SetSegmentOnSpawn, the default) spawns the player at
    /// the previous segment's campfire without ever lighting it. Vanilla's OWN
    /// <c>MapHandler.JumpToSegment</c> (jumpLogic 1) calls
    /// <c>PreviousCampfire?.LightWithoutReveal()</c> right after warping segments, but
    /// <c>SetSegmentOnSpawn</c> (what jumpLogic 0 actually drives) only reactivates the
    /// campfire's GameObject, never sets its <c>Lit</c> state - so unless it happened to
    /// already be lit from some earlier interaction, the player spawns next to a cold
    /// unlit fire despite clearly having a checkpoint there. Coop is unaffected: reported
    /// symptom (and testing) only ever showed this in solo
    ///
    /// <c>Campfire.LightWithoutReveal()</c> is the exact tool for this: it's vanilla's
    /// own <c>Light_Rpc(updateSegment: false, 0f)</c> call, setting <c>state = Lit</c>
    /// (which alone makes <c>IsInteractible</c> false for players afterward, so it can't
    /// be re-lit) without advancing the segment or triggering <c>Quicksave.SaveNow()</c>
    /// - no save, no on-screen indicator, exactly what's wanted here
    ///
    /// Hooked off the same universal "a checkpoint load just finished" signal
    /// <see cref="TeleportWatchdog"/> uses (see <see cref="SavegameLoadedMessagePatch"/>),
    /// so this fires identically whether the load came from Quick Resume (F7) or the
    /// checkpoint mod's own native F6
    /// </summary>
    public static class CampfireRelightFix
    {
        public static void TryRelightAfterLoad(CheckpointInterop checkpoint, ManualLogSource log)
        {
            try
            {
                if (!PhotonNetwork.OfflineMode) return; // solo-only, see class remarks

                if (checkpoint == null || !checkpoint.TryGetTeleportConfig(out int jumpLogic, out _, out _)) return;
                if (jumpLogic != 0) return; // only SetSegmentOnSpawn (jumpLogic 0) has this gap

                var campfire = MapHandler.PreviousCampfire;
                if (campfire == null) return; // e.g. spawned at the Beach, nothing to light
                if (campfire.Lit) return; // already lit (does happen sometimes) - nothing to do

                campfire.LightWithoutReveal();
                log?.LogInfo("CampfireRelightFix: relit the previous segment's campfire after a solo load "
                    + "(teleportJumpLogic=0 never does this itself).");
            }
            catch (Exception e)
            {
                log?.LogWarning($"CampfireRelightFix failed (non-fatal): {e.Message}");
            }
        }
    }
}
