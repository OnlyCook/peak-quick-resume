using System;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// "Has every player in the room actually finished spawning?", answered from the host by
    /// looking at <c>Player.character</c> rather than asking anyone. Acting while a client is
    /// still mid-spawn causes two co-op bugs: a Restart's island-load RPC gets dropped while
    /// <c>LoadingScreenHandler.loading</c> is still true, and a Resume's segment jump can get
    /// <c>CharacterSpawner</c> to kill anyone who finishes spawning after it (see
    /// OwnTeleportSequence's call site). Doesn't use <see cref="OwnNetwork"/>'s own readiness
    /// RPC, since both bugs reproduce with a completely unmodded client.
    /// </summary>
    internal static class PlayerRegistration
    {
        /// <summary>
        /// True when every player in the room has a Character that is actually usable. The
        /// transform check matters: a respawning player briefly has a reference to a Character
        /// whose GameObject is already being torn down, and counting that as registered let a
        /// segment jump run against a half-destroyed player list and throw.
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
