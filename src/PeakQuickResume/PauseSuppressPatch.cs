using System;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Prevents the vanilla pause menu from opening as a side effect of the same Escape
    /// press that just closed our F7 save picker (<see cref="SavePicker"/>). Clearing
    /// <c>Character.localCharacter.input.pauseWasPressed</c> doesn't work, since
    /// <c>CharacterInput</c> re-derives it from the Input System every frame, so instead
    /// this Harmony-prefixes <c>GUIManager.UpdatePaused</c> and skips it entirely, once,
    /// right after the picker closes.
    /// </summary>
    public static class PauseSuppressPatch
    {
        private static ManualLogSource _log;
        private static bool _suppressOnce;

        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            try
            {
                var target = AccessTools.Method(typeof(GUIManager), "UpdatePaused");
                if (target == null)
                {
                    log.LogWarning("PauseSuppressPatch: GUIManager.UpdatePaused not found; "
                        + "closing the F7 picker with Escape may also open the pause menu.");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(PauseSuppressPatch), nameof(Prefix)));
                log.LogInfo("PauseSuppressPatch: patched GUIManager.UpdatePaused.");
            }
            catch (Exception e)
            {
                log.LogError($"PauseSuppressPatch.Apply failed (non-fatal): {e}");
            }
        }

        /// <summary>Call the moment Escape closes the F7 picker; skips the next UpdatePaused() call. Self-resetting.</summary>
        public static void SuppressNextOpen() => _suppressOnce = true;

        private static bool Prefix()
        {
            if (!_suppressOnce) return true;
            _suppressOnce = false;
            return false; // skip UpdatePaused's own body entirely for this call
        }
    }
}
