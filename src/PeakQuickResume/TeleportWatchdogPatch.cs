using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Reports every local-player warp to <see cref="TeleportWatchdog.OnLocalWarp"/> by
    /// Harmony-postfixing vanilla <c>Character.WarpPlayerRPC</c>, the RPC Photon actually
    /// delivers to each affected machine individually. Replaces an earlier approach hooking
    /// the checkpoint mod's <c>CustomJumpToSegment</c> coroutine directly, which only runs on
    /// the host and silently missed clients (see ROADMAP.md Phase 6 step 1). Gated by
    /// <see cref="TeleportWatchdog"/>'s "load in progress" flag, so unrelated vanilla warps
    /// (void recovery, other mods, boss abilities) are ignored.
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
