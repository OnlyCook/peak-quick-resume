using System;
using BepInEx.Logging;
using Photon.Pun;

namespace PEAKQuickResume
{
    /// <summary>
    /// Nadir's rising "anti-lava" field - the <c>LavaRising</c> with
    /// <c>risingFieldType == VoidGhosts</c> - and the state needed to park and re-arm it around
    /// a checkpoint restore. The commune arms it, so the pre-commune would otherwise start the
    /// hazard clock while everyone is still behind a loading screen. See docs/NADIR.md,
    /// "Third pass", for the mechanics this relies on.
    /// </summary>
    internal static class NadirRisingField
    {
        /// <summary>The active Void-biome rising field, or null if it isn't in the scene / not active yet.</summary>
        public static LavaRising Find(ManualLogSource log)
        {
            try
            {
                // ALL_LAVA is populated from OnEnable, so this only resolves once the Void
                // segment has actually been activated by the jump.
                if (LavaRising.ALL_LAVA == null) return null;
                foreach (LavaRising field in LavaRising.ALL_LAVA)
                {
                    if (field != null && field.risingFieldType == LavaRising.RisingFieldType.VoidGhosts)
                        return field;
                }
                return null;
            }
            catch (Exception e)
            {
                log?.LogWarning($"NadirRisingField.Find failed (non-fatal): {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Rewinds the field to its un-started state and holds it there. Meant to be called
        /// every frame, so nothing else (a stray sync, a second soul-freed event) can quietly
        /// start the clock mid-hold. The field re-lerps its height from <c>timeTraveled</c>
        /// every frame rather than accumulating, so zeroing that is enough to put it back down.
        /// </summary>
        public static void Park(LavaRising field)
        {
            if (field == null) return;
            field.started = false;
            field.ended = false;
            field.timeTraveled = 0f;
            field.secondsWaitedToStart = 0f;
        }

        /// <summary>
        /// Pushes the parked state to every client immediately instead of waiting out the
        /// host's own 15s sync tick. Only matters on a repeat load into a run where the field
        /// was already climbing: a client left with <c>started == true</c> would keep raising
        /// its own copy until the next sync arrived.
        /// </summary>
        public static void BroadcastParked(LavaRising field, ManualLogSource log)
        {
            if (field == null || PhotonNetwork.OfflineMode) return;
            try
            {
                PhotonView view = field.photonView;
                if (view != null) view.RPC("RPC_SyncLava", RpcTarget.Others, false, false, 0f, 0f);
            }
            catch (Exception e)
            {
                log?.LogWarning($"NadirRisingField.BroadcastParked failed (non-fatal, clients will pick the "
                    + $"parked state up on the host's next 15s sync instead): {e.Message}");
            }
        }

        /// <summary>
        /// Starts the clock, exactly the way <c>TestSoul</c> would have. The host's own Update
        /// takes it from here and syncs the start transition to everyone.
        /// </summary>
        public static void Release(LavaRising field, ManualLogSource log)
        {
            if (field == null) return;
            try
            {
                field.StartWaiting();
            }
            catch (Exception e)
            {
                log?.LogError($"NadirRisingField.Release failed - Nadir's rising field may stay down for the "
                    + $"rest of this run: {e}");
            }
        }
    }
}
