using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace PEAKQuickResume
{
    /// <summary>
    /// Replaces the checkpoint mod's F1 tutorial overlay (a screen-wide TMP text block)
    /// with <see cref="HelpScreen"/>, a small proper menu built from the same visual
    /// primitives as the F7 save picker. We never touch the checkpoint mod's DLL: this
    /// works by Harmony-postfixing its <c>ShowTutorialMessage(bool active, string
    /// message)</c>, immediately hiding its own overlay right after it shows itself, and
    /// driving our own screen open/closed in lockstep instead - so we ride entirely on
    /// the checkpoint mod's own F1/tutorial-key detection (its <c>Update()</c>, which we
    /// never touch) rather than needing our own key handling for this
    ///
    /// Only the BUILT-IN tutorial (called with an empty <c>message</c>) is replaced; a
    /// custom popup through this same method (e.g. its mod-version-mismatch warning) is
    /// left completely alone
    /// </summary>
    public static class TutorialPatch
    {
        private static ManualLogSource _log;
        private static HelpScreen _helpScreen;
        private static FieldInfo _tutorialOverlayField;

        public static void Apply(Harmony harmony, Type checkpointType, ManualLogSource log, HelpScreen helpScreen)
        {
            _log = log;
            _helpScreen = helpScreen;
            try
            {
                var target = AccessTools.Method(checkpointType, "ShowTutorialMessage");
                if (target == null)
                {
                    log.LogWarning("TutorialPatch: ShowTutorialMessage not found; F1 will show the original tutorial unchanged.");
                    return;
                }
                _tutorialOverlayField = AccessTools.Field(checkpointType, "_tutorialOverlay");
                if (_tutorialOverlayField == null)
                    log.LogWarning("TutorialPatch: _tutorialOverlay not found; the original tutorial overlay may show alongside ours.");

                var postfix = new HarmonyMethod(typeof(TutorialPatch).GetMethod(nameof(Postfix),
                    BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(target, postfix: postfix);
                log.LogInfo("TutorialPatch: patched ShowTutorialMessage (F1 replaced with HelpScreen).");
            }
            catch (Exception e)
            {
                log.LogError($"TutorialPatch.Apply failed (non-fatal): {e}");
            }
        }

        // Runs after the checkpoint mod's own ShowTutorialMessage, which itself just
        // toggled _tutorialOverlay active/inactive - we override that same toggle here
        private static void Postfix(bool active, string message, object __instance)
        {
            try
            {
                // Only the built-in tutorial (message == ""); a custom popup (e.g. the
                // mod-version-mismatch warning) is left showing exactly as the checkpoint
                // mod intended
                if (!string.IsNullOrEmpty(message)) return;

                object overlay = _tutorialOverlayField?.GetValue(__instance);
                if (overlay is UnityEngine.GameObject go) go.SetActive(false);

                if (active) _helpScreen?.Open();
                else _helpScreen?.Close();
            }
            catch (Exception e)
            {
                _log?.LogWarning($"TutorialPatch.Postfix failed (non-fatal): {e.Message}");
            }
        }
    }
}
