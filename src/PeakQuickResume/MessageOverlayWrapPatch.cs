using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Fixes the checkpoint mod's own top-of-screen message overlay (ShowMessage -
    /// "Save game loaded!", the teleport-bug hint, our own resume status text, etc.)
    /// running off both edges of the screen for longer translated strings.
    ///
    /// Its RectTransform is built with anchorMin == anchorMax (a single point, zero
    /// width) and textWrappingMode = NoWrap (see the checkpoint mod's own
    /// EnsureMessageOverlay), so the text was never actually constrained or wrapped,
    /// it just happened to fit for short English strings and overflowed symmetrically
    /// off both sides for longer translations (e.g. German). Harmony-postfixes
    /// ShowMessage to widen that rect to a fixed fraction of the screen, centered on
    /// the checkpoint mod's own configured X position, and turns wrapping on so text
    /// that doesn't fit on one line wraps to the next instead.
    /// </summary>
    public static class MessageOverlayWrapPatch
    {
        private const float WidthFraction = 0.8f;

        private static ManualLogSource _log;
        private static FieldInfo _messageTmpField;

        public static void Apply(Harmony harmony, Type checkpointType, ManualLogSource log)
        {
            _log = log;
            try
            {
                _messageTmpField = AccessTools.Field(checkpointType, "_messageTMP");
                if (_messageTmpField == null)
                {
                    log.LogWarning("MessageOverlayWrapPatch: _messageTMP not found; long translated "
                        + "messages may still run off-screen.");
                    return;
                }

                var target = AccessTools.Method(checkpointType, "ShowMessage",
                    new[] { typeof(string), typeof(Color), typeof(float), typeof(bool) });
                if (target == null)
                {
                    log.LogWarning("MessageOverlayWrapPatch: ShowMessage not found; long translated "
                        + "messages may still run off-screen.");
                    return;
                }

                harmony.Patch(target, postfix: new HarmonyMethod(typeof(MessageOverlayWrapPatch), nameof(Postfix)));
                log.LogInfo("MessageOverlayWrapPatch: patched ShowMessage (message overlay now wraps instead of overflowing).");
            }
            catch (Exception e)
            {
                log.LogError($"MessageOverlayWrapPatch.Apply failed (non-fatal): {e}");
            }
        }

        private static void Postfix(object __instance)
        {
            try
            {
                var tmp = _messageTmpField?.GetValue(__instance) as TextMeshProUGUI;
                if (tmp == null) return;

                tmp.textWrappingMode = TextWrappingModes.Normal;

                var rect = tmp.rectTransform;
                float centerX = (rect.anchorMin.x + rect.anchorMax.x) * 0.5f;
                float half = WidthFraction * 0.5f;
                rect.anchorMin = new Vector2(Mathf.Clamp01(centerX - half), rect.anchorMin.y);
                rect.anchorMax = new Vector2(Mathf.Clamp01(centerX + half), rect.anchorMax.y);
            }
            catch (Exception e)
            {
                _log?.LogWarning($"MessageOverlayWrapPatch.Postfix failed (non-fatal): {e.Message}");
            }
        }
    }
}
