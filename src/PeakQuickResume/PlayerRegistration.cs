using System;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// "Has every player in the room actually finished spawning?", answered from the HOST by
    /// looking at <c>Player.character</c> rather than by asking anyone.
    ///
    /// WHY IT IS NEEDED
    /// Two separate session-confirmed co-op bugs come from acting while a client is still
    /// mid-spawn:
    ///  1. RESTART sent <c>kiosk.StartGame</c> the moment the HOST was done loading the Airport.
    ///     A client still spawning in has <c>LoadingScreenHandler.loading == true</c>, and
    ///     <c>LoadingScreenHandler.Load</c> refuses outright in that state ("Tried to load while
    ///     already loading!") - so the island load RPC was simply dropped on their end and they
    ///     were left standing in the previous level, in a biome that no longer existed for
    ///     anyone else. The host's log shows it plainly: the spawn requests for the client are
    ///     still being repeated on both sides of <c>StartRun</c>
    ///  2. The RESUME's segment jump advances the run, and <c>CharacterSpawner</c> kills anyone
    ///     who finishes spawning after that point (see OwnTeleportSequence's call site)
    ///
    /// WHY NOT OUR OWN READY RPC
    /// <see cref="OwnNetwork"/> already has a readiness handshake, but it only exists on machines
    /// running this mod, and both bugs reproduce with a completely unmodded client. Watching the
    /// characters the host can already see works for everyone
    ///
    /// A non-null <c>Character</c> alone is NOT sufficient - see <see cref="AllRegistered"/>
    /// </summary>
    internal static class PlayerRegistration
    {
        /// <summary>
        /// True when every player in the room has a Character that is actually usable.
        ///
        /// The transform check is the point of this method. A player who is respawning (a
        /// reconnect, or a fresh spawn replacing an old body) briefly still has a reference to a
        /// Character whose GameObject is already being torn down; counting that as "registered"
        /// is what let a segment jump run against a half-destroyed player list, where
        /// <c>Character.Center</c> dereferences a dead transform and throws - which stranded a
        /// restore on "LOADING SAVE..." forever in a real session
        /// </summary>
        internal static bool AllRegistered() => AllRegistered(out _, out _);

        internal static bool AllRegistered(out int registered, out int expected)
        {
            registered = 0;
            expected = 0;
            try
            {
                expected = PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.PlayerCount : 0;
                foreach (var p in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None))
                {
                    if (p == null) continue;

                    Character ch = p.character;
                    if (ch == null || ch.refs == null || ch.refs.ragdoll == null) continue;
                    if (ch.transform == null) continue;
                    registered++;
                }
            }
            catch
            {
                // Mid-scene-load churn; report "not yet" and let the caller try again
                return false;
            }

            return expected > 0 && registered >= expected;
        }

        /// <summary>Short description for logs, e.g. "2/3 player character(s) registered"</summary>
        internal static string Describe()
        {
            AllRegistered(out int registered, out int expected);
            return $"{registered}/{expected} player character(s) registered";
        }

        internal static void LogState(ManualLogSource log, string when)
        {
            try { log?.Trace($"PlayerRegistration [{when}]: {Describe()}."); }
            catch { }
        }
    }
}
