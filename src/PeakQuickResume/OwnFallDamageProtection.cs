using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Temporary fall/lava-damage immunity window during a teleport: three Harmony
    /// prefixes on vanilla types that short-circuit while the timer hasn't elapsed.
    /// Uses our own static field rather than the checkpoint mod's, since this needs
    /// to be armed directly by <c>OwnTeleportSequence</c>.
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
