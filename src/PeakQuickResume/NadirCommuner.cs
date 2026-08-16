using System;
using BepInEx.Logging;
using Peak.Network;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Who communed with the scoutmaster's soul: carried from the save hook into the save file,
    /// and resolved back to a live character on load. Two things depend on it - where the save
    /// is anchored (the soul pillar has none of a campfire's gather-the-party requirement, so
    /// the host can be anywhere when it happens) and which player the scoutmaster's ghost
    /// orbits (vanilla picks it from the PhotonView passed through RPC_Break). See
    /// docs/NADIR.md, "Fourth pass".
    /// </summary>
    internal static class NadirCommuner
    {
        // The save is written synchronously by the same postfix that arms this, so the window
        // only has to cover that one call; it's here so a throw before ClearPending can't leak
        // this pillar's anchor into a later campfire's save.
        private const float PendingWindowSeconds = 5f;

        private static float _pendingUntil = -1f;
        private static Vector3 _pendingAnchor;

        /// <summary>The interactor's Steam/Photon user id, or "" offline (and when it couldn't be read).</summary>
        public static string PendingUserId { get; private set; }

        /// <summary>The interactor's character name. Diagnostics and log lines only - never matched on.</summary>
        public static string PendingName { get; private set; }

        private static bool Pending => _pendingUntil > Time.time;

        /// <summary>
        /// Records the player who just finished the 2s hold, for the save about to be written.
        /// In co-op this is the client who communed, even though the host runs the hook.
        /// </summary>
        public static void ArmPending(Character interactor, ManualLogSource log)
        {
            ClearPending();
            try
            {
                if (interactor == null)
                {
                    log?.LogWarning("NadirCommuner: the commune RPC carried no usable character, so this save falls "
                        + "back to anchoring on the host and the ghost will orbit the host on load.");
                    return;
                }

                // A dead character's ragdoll sits in the off-map death zone, the one anchor
                // ResolveWorldAnchor refuses to write. Shouldn't be reachable from a real hold.
                if (interactor.data != null && interactor.data.dead)
                {
                    log?.LogWarning($"NadirCommuner: {interactor.characterName} reads as dead at the moment they "
                        + "communed, so their position is the off-map death zone - anchoring this save the normal way instead.");
                    return;
                }

                _pendingAnchor = interactor.Head;
                PendingName = interactor.characterName;
                PendingUserId = ReadUserId(interactor);
                _pendingUntil = Time.time + PendingWindowSeconds;

                log?.LogInfo($"NadirCommuner: {PendingName} ({(PendingUserId.Length > 0 ? PendingUserId : "offline")}) "
                    + $"communed at {_pendingAnchor} - this save is anchored on them, not on the host.");
            }
            catch (Exception e)
            {
                ClearPending();
                log?.LogWarning($"NadirCommuner.ArmPending failed (non-fatal, the save falls back to the host's own "
                    + $"position): {e.Message}");
            }
        }

        public static void ClearPending()
        {
            _pendingUntil = -1f;
            _pendingAnchor = Vector3.zero;
            PendingUserId = null;
            PendingName = null;
        }

        /// <summary>
        /// The communing player's head at the moment they communed. False for every save that
        /// isn't commune-triggered, i.e. every other checkpoint in the game.
        /// </summary>
        public static bool TryGetPendingAnchor(out Vector3 anchor)
        {
            anchor = _pendingAnchor;
            return Pending;
        }

        /// <summary>
        /// The live character to replay the commune as. Falls back to the local (host's)
        /// character whenever the saved player can't be identified: offline, a save predating
        /// this field, or that player not being in the session. Matched on user id, the same
        /// key the save store itself is filed under, so it survives actor renumbering.
        /// </summary>
        public static Character ResolveSavedCommuner(OwnSaveData data, ManualLogSource log)
        {
            Character local = Character.localCharacter;
            string savedUserId = data != null ? data.nadirCommunerUserId : null;
            string savedName = data != null ? data.nadirCommunerName : null;

            // Solo has exactly one character, and it is by definition the one that communed.
            if (PhotonNetwork.OfflineMode) return local;

            if (string.IsNullOrEmpty(savedUserId))
            {
                log?.LogInfo("NadirCommuner: this Nadir checkpoint predates recording who communed, so the host "
                    + "stands in for the interactor (the scoutmaster's ghost will orbit the host).");
                return local;
            }

            try
            {
                foreach (Player p in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
                {
                    Character ch = p != null ? p.character : null;
                    if (ch == null || ch.photonView == null) continue;
                    if (ReadUserId(ch) != savedUserId) continue;

                    log?.LogInfo($"NadirCommuner: replaying the commune as {ch.characterName} ({savedUserId}), the "
                        + "player who originally communed.");
                    return ch;
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"NadirCommuner.ResolveSavedCommuner failed (non-fatal, falling back to the host): {e.Message}");
                return local;
            }

            log?.LogWarning($"NadirCommuner: the player who communed at this checkpoint "
                + $"({(string.IsNullOrEmpty(savedName) ? savedUserId : savedName + ", " + savedUserId)}) isn't in this "
                + "session, so the host stands in for them and the scoutmaster's ghost orbits the host instead. "
                + "Nothing else about the restore changes.");
            return local;
        }

        /// <summary>Empty rather than null on any failure, so callers can compare and serialize freely.</summary>
        private static string ReadUserId(Character character)
        {
            try
            {
                if (PhotonNetwork.OfflineMode || character == null || character.player == null) return "";
                return NetworkingUtilities.GetUserId(character.player) ?? "";
            }
            catch
            {
                return "";
            }
        }
    }
}
