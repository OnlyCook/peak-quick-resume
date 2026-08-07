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
    /// Own addition, no decompile counterpart: puts each player's ALIVE/DEAD state back
    /// the way it was when the checkpoint was written (see <see cref="OwnSaveData.isDead"/>).
    /// Co-op only - solo can never save while dead (the one player being dead is the whole
    /// team being dead, which ends the run), so there is nothing to restore offline
    ///
    /// This exists because of a real co-op bug (session-reported 2026-07-25): a friend who
    /// joins a run already in progress is deliberately killed on arrival by the game itself
    /// (<c>DeathOnArrival</c> -> <c>KillImmediately</c> -> <c>RPCA_Die</c>), so they spawn as
    /// a spectating ghost. Loading a checkpoint left them stuck exactly like that - watching
    /// the host - even though they had never died in the run being resumed. The load's own
    /// revive step (<c>OwnTeleportSequence.ReviveDeadPlayers</c>, a literal port) only ever
    /// wrote <c>Character.data.dead = false</c> on the HOST's own local copy of that client's
    /// character, which is never visible on the client's own machine: <c>dead</c> is only
    /// ever synced through the <c>RPCA_Die</c>/<c>RPCA_Revive</c>/<c>RPCA_SetDead</c> RPC
    /// family (plus <c>RPC_SyncOnJoin</c>), never through the continuous character-sync
    /// stream. That step now broadcasts <c>RPCA_Revive</c> instead - see its own remarks -
    /// which is the half of this feature that actually unsticks the joined-late client
    ///
    /// This class is the other half: once everyone is alive again, whoever the checkpoint
    /// recorded as dead is put back into that state via <c>RPCA_SetDead</c>. Deliberately
    /// NOT <c>RPCA_Die</c>: that also drops the character's whole inventory into the world,
    /// which would be wrong twice over - a player who was dead at save time had already
    /// dropped everything before the save, so their restored inventory is empty by
    /// construction, and dropping "again" would only scatter phantom loot around the
    /// campfire. Vanilla's own reconnect path does exactly the same thing for exactly the
    /// same reason (<c>SetDeadAfterReconnect</c> -> <c>RPCA_SetDead</c>, no drop)
    ///
    /// Split in two on purpose (see <see cref="ResolveSavedDeadUserIds"/> /
    /// <see cref="ApplySavedDeaths"/>): who ends up dead is decided UP FRONT, before the
    /// per-player restore runs, so <see cref="OwnInventoryRestore.RestoreAll"/> can skip
    /// those players entirely (there is nothing worth putting back onto a corpse), and the
    /// death itself lands while the loading screen is still fully opaque on every machine.
    /// Session-reported: doing it at the very end instead meant the player watched
    /// themselves get restored, stand up, and only THEN visibly drop dead
    ///
    /// A player with NO file in the loaded save event is always restored alive. That is the
    /// joined-late case by definition (they weren't in the run when it was saved), and it is
    /// also the safe default for every other reason a file can be missing
    /// </summary>
    public static class DeathStateRestore
    {
        /// <summary>
        /// Host-side, co-op only. Works out - up front, before any per-player restore has
        /// run - which currently-connected players the loaded checkpoint recorded as dead,
        /// as a set of userIds. Always empty offline, on a client, or when the answer would
        /// be "everybody" (see the guard below), so callers can treat a non-empty result as
        /// "these players are being restored as a corpse, skip restoring anything onto them"
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

                // Vanilla's own Character.CheckIfAllPlayersDead ends the run outright the
                // moment every character reads as dead. Restoring a state that trips it
                // would turn a load into an instant game over, so if this would leave
                // nobody standing (a hand-edited save, or a checkpoint written in some
                // state we didn't anticipate), leave everyone alive instead and say so
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
        /// Host-side, co-op only. Marks everyone in <paramref name="deadUserIds"/> (from
        /// <see cref="ResolveSavedDeadUserIds"/>) dead again. Expects to run once the
        /// teleport and the per-player restore have settled but while the loading screen is
        /// still fully opaque everywhere - so the player is already a spectator by the time
        /// anything is revealed, instead of visibly dropping dead in front of themselves
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
                    if (ch.data.dead) continue; // already there (nothing revived them), leave it alone

                    try
                    {
                        // Tell the machine this death is about to land on that we did it on
                        // purpose, BEFORE the death itself: TeleportWatchdog's post-load
                        // watch window treats the local player dying right after a load as
                        // one of the known bad-teleport symptoms ("knocked out / died
                        // shortly after load") and would otherwise pop its "teleport bug
                        // detected" hint on a player we deliberately restored as dead.
                        // Standing the watch down is the right response either way - every
                        // symptom it polls for (fall-through, never-teleported, warp loop)
                        // is meaningless for a ghost, whose corpse vanilla itself drags
                        // below the map. The suppression is sticky on the receiving side,
                        // so it holds whichever side of that machine's own load-window
                        // arming it lands on (see TeleportWatchdog.SuppressForRestoredDeath).
                        // Only reaches players running Quick Resume, which is exactly
                        // right: a vanilla client has no watchdog to suppress
                        SuppressWatchdogFor(player, ch, entryPoints, log);

                        // RpcTarget.All, on that character's OWN view: the owning client is
                        // the only machine where being dead actually means anything (ghost
                        // spawn, spectate camera), and the host writing ch.data.dead
                        // locally would never reach it - see the class remarks
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

        // Lifts the teleport watchdog on whichever machine owns this character - locally when
        // that's us (the host restoring its own saved death), otherwise via our own targeted
        // RPC. See the call site for why
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

        /// <summary>
        /// Reads just <see cref="OwnSaveData.isDead"/> out of this player's OWN file within
        /// the loaded save event (same per-player file resolution as
        /// <see cref="OwnInventoryRestore.RestoreAll"/> - never the host's file, never a
        /// near-miss from another event). Anything that isn't a clear, readable "was dead"
        /// answers false: no file for this player, an unreadable file, or a save written
        /// before this field existed all mean the player stays alive
        /// </summary>
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
