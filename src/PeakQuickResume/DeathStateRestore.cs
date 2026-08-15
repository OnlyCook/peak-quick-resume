using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json;
using Peak.Network;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Restores each player's alive/dead state to match the checkpoint (see
    /// OwnSaveData.isDead). Co-op only - solo can never save while dead. Fixes a bug
    /// where a late-joining player killed on arrival (spawned as a spectating ghost)
    /// stayed stuck as a ghost after a load, because the revive step's local write to
    /// Character.data.dead never reached the client - dead state only syncs through the
    /// RPCA_Die/RPCA_Revive/RPCA_SetDead RPC family. Whoever the checkpoint recorded as
    /// dead is put back via RPCA_SetDead (not RPCA_Die, which would also drop their
    /// inventory - matching vanilla's own reconnect path).
    ///
    /// Split into ResolveSavedDeadUserIds/ApplySavedDeaths so who ends up dead is decided
    /// up front, letting OwnInventoryRestore.RestoreAll skip those players and the death
    /// land while the loading screen is still opaque, rather than visibly happening after
    /// the player watches themselves stand up.
    ///
    /// A player with no file in the loaded save event is always restored alive.
    /// </summary>
    public static class DeathStateRestore
    {
        /// <summary>
        /// Host-side, co-op only. Works out up front which currently-connected players the
        /// loaded checkpoint recorded as dead. Always empty offline, on a client, or when
        /// the answer would be "everybody" (see guard below).
        /// </summary>
        public static HashSet<string> ResolveSavedDeadUserIds(SaveSelection selection, ManualLogSource log)
        {
            var deadUserIds = new HashSet<string>(StringComparer.Ordinal);
            if (selection == null || selection.Offline || !PhotonNetwork.IsMasterClient) return deadUserIds;

            try
            {
                int aliveAfter = 0;
                foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
                {
                    Character ch = player != null ? player.character : null;
                    if (ch == null) continue;

                    string userId = NetworkingUtilities.GetUserId(ch.player);
                    if (WasSavedDead(selection, userId, log)) deadUserIds.Add(userId);
                    else aliveAfter++;
                }

                // Vanilla's CheckIfAllPlayersDead ends the run the moment everyone reads as
                // dead; if restoring would leave nobody standing, leave everyone alive instead.
                if (deadUserIds.Count > 0 && aliveAfter == 0)
                {
                    log?.LogWarning($"DeathStateRestore: the loaded checkpoint has all {deadUserIds.Count} player(s) "
                        + "recorded as dead; skipping the death restore entirely rather than ending the run on load.");
                    deadUserIds.Clear();
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"DeathStateRestore.ResolveSavedDeadUserIds failed (non-fatal, everyone stays alive): {e.Message}");
                deadUserIds.Clear();
            }

            return deadUserIds;
        }

        /// <summary>
        /// Host-side, co-op only. Marks everyone in deadUserIds dead again. Runs once the
        /// teleport and per-player restore have settled, while the loading screen is still
        /// opaque.
        /// </summary>
        public static void ApplySavedDeaths(HashSet<string> deadUserIds, OwnLoadEntryPoints entryPoints, ManualLogSource log)
        {
            if (deadUserIds == null || deadUserIds.Count == 0 || !PhotonNetwork.IsMasterClient) return;

            try
            {
                foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
                {
                    Character ch = player != null ? player.character : null;
                    if (ch == null) continue;
                    if (!deadUserIds.Contains(NetworkingUtilities.GetUserId(ch.player))) continue;
                    if (ch.data.dead) continue;

                    try
                    {
                        // Suppress the teleport watchdog first: it treats dying right after a
                        // load as a bad-teleport symptom and would otherwise flag a death we
                        // deliberately restored. Only reaches players running Quick Resume.
                        SuppressWatchdogFor(player, ch, entryPoints, log);

                        // RpcTarget.All: only the owning client's machine reflects being dead
                        // (ghost spawn, spectate camera); a local write on the host wouldn't.
                        ch.photonView.RPC("RPCA_SetDead", RpcTarget.All);
                        log.Trace($"DeathStateRestore: restored {ch.characterName} as dead (they were dead when this checkpoint was saved).");
                    }
                    catch (Exception e)
                    {
                        log?.LogWarning($"DeathStateRestore: could not restore {ch.characterName} as dead (non-fatal): {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"DeathStateRestore.ApplySavedDeaths failed (non-fatal, everyone stays alive): {e.Message}");
            }
        }

        /// <summary>
        /// Host-side, co-op only. Counterpart to ApplySavedDeaths: makes sure everyone the
        /// checkpoint did NOT record as dead is actually alive. Catches anyone who died in
        /// the window after their warp landed but before the screen comes back (a fall
        /// during restore, a late-arrival kill), run before ApplySavedDeaths.
        /// </summary>
        public static void EnsureUnsavedPlayersAlive(HashSet<string> deadUserIds, ManualLogSource log)
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.OfflineMode) return;

            try
            {
                foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
                {
                    Character ch = player != null ? player.character : null;
                    if (ch == null || ch.photonView == null) continue;

                    if (deadUserIds != null && deadUserIds.Contains(NetworkingUtilities.GetUserId(ch.player))) continue;

                    // Only data.dead: by this point the wake-up effect has collapsed everyone
                    // into the passed-out pose, so treating passedOut as "needs reviving" would
                    // revive every player and wipe the state just restored (ReviveCharacter
                    // clears afflictions/thorns).
                    if (!ch.data.dead) continue;

                    try
                    {
                        ch.photonView.RPC(ReviveRpcName, RpcTarget.All, false);
                        log?.LogInfo($"DeathStateRestore: {ch.characterName} died during the load and this "
                            + "checkpoint records no death for them - revived so they come back alive.");
                    }
                    catch (Exception e)
                    {
                        log?.LogWarning($"DeathStateRestore: could not revive {ch.characterName} (non-fatal): {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                log?.LogWarning($"DeathStateRestore.EnsureUnsavedPlayersAlive failed (non-fatal): {e.Message}");
            }
        }

        /// <summary>Character's revive [PunRPC]; PEAK 2.0.a renamed this from RPCA_Revive to ReviveCharacter.</summary>
        private const string ReviveRpcName = "ReviveCharacter";

        // Lifts the teleport watchdog on whichever machine owns this character.
        private static void SuppressWatchdogFor(Player player, Character ch, OwnLoadEntryPoints entryPoints, ManualLogSource log)
        {
            try
            {
                PhotonView playerView = player.GetComponent<PhotonView>();
                if (playerView != null && playerView.IsMine)
                {
                    entryPoints?.Network?.Watchdog?.SuppressForRestoredDeath();
                    return;
                }
                if (playerView != null)
                    entryPoints?.Network?.SuppressWatchdogForRestoredDeath(playerView, NetworkingUtilities.GetUserId(ch.player));
            }
            catch (Exception e)
            {
                log?.LogWarning($"DeathStateRestore: could not suppress the teleport watchdog for "
                    + $"{ch.characterName} (non-fatal, they may see a spurious teleport-bug hint): {e.Message}");
            }
        }

        /// <summary>Reads OwnSaveData.isDead from this player's own file. Anything unreadable or missing defaults to alive.</summary>
        private static bool WasSavedDead(SaveSelection selection, string userId, ManualLogSource log)
        {
            if (!selection.TryGetPlayerFile(userId, out string path))
            {
                log.Trace($"DeathStateRestore: '{userId}' has no file in this checkpoint's save event "
                    + "(joined after it was written, most likely) - restoring them alive.");
                return false;
            }

            try
            {
                var data = JsonConvert.DeserializeObject<OwnSaveData>(File.ReadAllText(path));
                return data != null && data.isDead;
            }
            catch (Exception e)
            {
                log?.LogWarning($"DeathStateRestore: could not read the save for '{userId}' ({e.Message}) - restoring them alive.");
                return false;
            }
        }
    }
}
