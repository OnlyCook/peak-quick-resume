using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our own copy of the checkpoint mod's temporary fall/lava-damage immunity
    /// window used during a teleport (decompile 184-224, activated via
    /// <c>RPC_RequestFalldamageProtection</c> -> <c>ActivateFallDamageProtection</c>,
    /// lines 528-536, 956-959): three Harmony prefixes on vanilla types
    /// (<c>CharacterMovement.CheckFallDamage</c>, <c>Lava.HitPlayer</c>, <c>Lava.Heat</c>)
    /// that short-circuit while <see cref="Until"/> hasn't elapsed yet
    ///
    /// Ported as our own copy (a distinct static field, not reflecting into the
    /// checkpoint mod's own <c>NoFallDamageUntil</c>) because it's real per-load
    /// protective behavior <c>OwnTeleportSequence</c> needs to arm itself, not
    /// something we can leave the checkpoint mod's instance to provide once we stop
    /// calling into its RPC for our own restore path
    /// </summary>
    public static class OwnFallDamageProtection
    {
        private static float _until;

        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(CharacterMovement), "CheckFallDamage"),
                    prefix: new HarmonyMethod(typeof(OwnFallDamageProtection), nameof(PrefixSkipIfProtected)));
                harmony.Patch(AccessTools.Method(typeof(Lava), "HitPlayer"),
                    prefix: new HarmonyMethod(typeof(OwnFallDamageProtection), nameof(PrefixSkipIfProtected)));
                harmony.Patch(AccessTools.Method(typeof(Lava), "Heat"),
                    prefix: new HarmonyMethod(typeof(OwnFallDamageProtection), nameof(PrefixSkipIfProtected)));
                log.LogInfo("OwnFallDamageProtection: patched CharacterMovement.CheckFallDamage / Lava.HitPlayer / Lava.Heat.");
            }
            catch (Exception e)
            {
                log.LogError($"OwnFallDamageProtection.Apply failed (non-fatal): {e}");
            }
        }

        /// <summary>Arms the protection window for <paramref name="seconds"/> from now</summary>
        public static void Activate(float seconds) => _until = Time.time + seconds;

        private static bool PrefixSkipIfProtected() => !(Time.time < _until);
    }
}
