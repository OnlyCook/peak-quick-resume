using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Stops a checkpoint load from sounding like you are being shot: re-attaching a saved
    /// arrow replayed the arrow-impact sound (<c>SFX_PlayOneShot</c> component, fired via
    /// Unity's <c>Start()</c> on <c>SetActive</c>).
    ///
    /// A Harmony prefix rather than toggling <c>playOnStart</c>: Unity runs <c>Start()</c>
    /// after <c>SetActive</c>, not during it, so clearing/restoring the flag around the
    /// re-attach loop would restore it before Start ever ran. A prefix that declines the
    /// call for a moment needs no game state changed and expires on its own. Scoped tightly:
    /// only the specific thorn objects being restored, for a couple of seconds.
    /// </summary>
    internal static class ThornRestoreSilencer
    {
        private static ManualLogSource _log;
        private static readonly List<Transform> _silenced = new List<Transform>();
        private static float _silencedUntil;
        private static bool _patched;

        public static void Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            try
            {
                // PlayOneShot, not Play: other callers reach it directly, and Start/OnEnable both funnel through it.
                var target = AccessTools.Method(typeof(SFX_PlayOneShot), "PlayOneShot");
                if (target == null)
                {
                    log.LogWarning("ThornRestoreSilencer: SFX_PlayOneShot.PlayOneShot not found - "
                        + "restoring arrows will replay their impact sound.");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(typeof(ThornRestoreSilencer), nameof(PlayOneShotPrefix)));
                _patched = true;
                log.LogInfo("ThornRestoreSilencer: patched SFX_PlayOneShot.PlayOneShot (silent thorn/arrow restore).");
            }
            catch (Exception e)
            {
                log.LogError($"ThornRestoreSilencer.Apply failed (non-fatal): {e}");
            }
        }

        /// <summary>
        /// Declines one-shots coming from the thorn objects currently being restored.
        /// Returning false skips the original method
        /// </summary>
        private static bool PlayOneShotPrefix(SFX_PlayOneShot __instance)
        {
            try
            {
                if (_silenced.Count == 0 || Time.realtimeSinceStartup >= _silencedUntil) return true;
                if (__instance == null) return true;

                Transform t = __instance.transform;
                foreach (Transform root in _silenced)
                {
                    // IsChildOf is also true for the transform itself.
                    if (root != null && t.IsChildOf(root)) return false;
                }
            }
            catch { /* never let a diagnostic-shaped guard break the game's audio */ }
            return true;
        }

        /// <summary>
        /// Silences the given thorn objects for a moment. <paramref name="seconds"/> only
        /// needs to outlast the next frame (Start runs before the following Update).
        /// </summary>
        public static void SilenceDuringRestore(CharacterAfflictions afflictions, List<ushort> thornIndices, float seconds = 2f)
        {
            if (!_patched || afflictions == null || thornIndices == null) return;

            try
            {
                List<ThornOnMe> thorns = afflictions.physicalThorns;
                if (thorns == null) return;

                _silenced.Clear();
                foreach (ushort index in thornIndices)
                {
                    if (index >= thorns.Count) continue;
                    ThornOnMe thorn = thorns[index];
                    if (thorn != null) _silenced.Add(thorn.transform);
                }

                if (_silenced.Count == 0) return;
                _silencedUntil = Time.realtimeSinceStartup + seconds;
                _log.Trace($"ThornRestoreSilencer: silencing {_silenced.Count} thorn/arrow object(s) for {seconds:0.#}s.");
            }
            catch (Exception e)
            {
                _silenced.Clear();
                _log?.LogWarning($"ThornRestoreSilencer: could not arm the silence window ({e.Message}); "
                    + "the restore may replay impact sounds.");
            }
        }
    }
}
