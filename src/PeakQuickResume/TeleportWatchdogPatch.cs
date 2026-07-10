using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Reports every local-player warp to <see cref="TeleportWatchdog.OnLocalWarp"/> by
    /// Harmony-postfixing the VANILLA <c>Character.WarpPlayerRPC(Vector3 position, bool
    /// poof)</c>, the actual RPC method Photon invokes on whichever machine owns the
    /// character being warped
    ///
    /// This replaces an earlier approach that hooked the checkpoint mod's own
    /// <c>CustomJumpToSegment</c> coroutine directly: that coroutine only ever *runs*
    /// on the host (only the host calls <c>LoadPlayerCoop</c>/<c>LoadPlayerOffline</c>),
    /// so a client's own game process never executed that method at all and never
    /// recorded a pending target, silently defeating detection for clients (the
    /// case that actually matters most, see ROADMAP.md Phase 6 step 1 test notes).
    /// <c>WarpPlayerRPC</c> is the opposite: it's the one call in the whole teleport
    /// chain that Photon actually delivers to each affected machine individually
    /// (the host warping itself via <c>TeleportToPosition</c>, and each client being
    /// warped via <c>TeleportClientsToHost</c> both funnel through this same RPC), so
    /// patching it here fires wherever the real teleport actually happens
    ///
    /// <c>Character</c> is a vanilla game type we already reference directly
    /// elsewhere, no reflection needed for this one
    ///
    /// Gated by <see cref="TeleportWatchdog"/>'s own "load in progress" flag (set by
    /// <see cref="OwnTeleportSequence"/>), so unrelated vanilla warps (falling into the
    /// void recovery, other mods, boss abilities, etc.) outside of one of our loads are
    /// ignored
    /// </summary>
    public static class TeleportWatchdogPatch
    {
        private static ManualLogSource _log;
        private static TeleportWatchdog _watchdog;

        public static void Apply(Harmony harmony, ManualLogSource log, TeleportWatchdog watchdog)
        {
            _log = log;
            _watchdog = watchdog;
            try
            {
                var target = AccessTools.Method(typeof(Character), nameof(Character.WarpPlayerRPC));
                if (target == null)
                {
                    log.LogWarning("TeleportWatchdogPatch: Character.WarpPlayerRPC not found; "
                        + "bad-teleport detection will be inert.");
                    return;
                }

                var postfix = new HarmonyMethod(typeof(TeleportWatchdogPatch), nameof(Postfix));
                harmony.Patch(target, postfix: postfix);
                log.LogInfo("TeleportWatchdogPatch: patched Character.WarpPlayerRPC (bad-teleport detection armed).");
            }
            catch (Exception e)
            {
                log.LogError($"TeleportWatchdogPatch.Apply failed (non-fatal): {e}");
            }
        }

        private static void Postfix(Character __instance, Vector3 position)
        {
            try
            {
                if (__instance == null || __instance != Character.localCharacter) return;
                _watchdog?.OnLocalWarp(position);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"TeleportWatchdogPatch.Postfix failed (non-fatal): {e.Message}");
            }
        }
    }
}
