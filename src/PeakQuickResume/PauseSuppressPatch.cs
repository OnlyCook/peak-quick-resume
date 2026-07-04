using System;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Prevents the vanilla pause menu from opening as a side effect of the SAME Escape
    /// press that just closed our F7 save picker (<see cref="SavePicker"/>)
    ///
    /// The obvious fix, clearing <c>Character.localCharacter.input.pauseWasPressed</c>
    /// right after we close, does NOT work: <c>CharacterInput</c> re-derives that field
    /// straight from the new Input System's <c>WasPressedThisFrame()</c> every single
    /// frame, so whatever we set it to gets silently overwritten back to true the next
    /// time that runs, regardless of ordering. Chasing that field is a dead end
    ///
    /// Instead we Harmony-prefix the actual method that opens the menu
    /// (<c>GUIManager.UpdatePaused</c>) and skip its body entirely, exactly once, right
    /// after our own picker closes on Escape. This never depends on which MonoBehaviour's
    /// Update() happens to run first this frame, unlike clearing a shared input flag
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

        /// <summary>
        /// Call the moment Escape closes the F7 picker: the game's very next
        /// <c>UpdatePaused()</c> call (later this same frame) is skipped entirely, so it
        /// cannot open the pause menu from that press. Self-resetting; never lingers
        /// into a later frame even if, for some reason, no call ever consumes it
        /// </summary>
        public static void SuppressNextOpen() => _suppressOnce = true;

        private static bool Prefix()
        {
            if (!_suppressOnce) return true;
            _suppressOnce = false;
            return false; // skip UpdatePaused's own body entirely for this call
        }
    }
}
