using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Stops a checkpoint load from sounding like you are being shot: re-attaching a saved
    /// arrow replayed the arrow-impact sound, once per arrow.
    ///
    /// THE MECHANISM (confirmed from a live capture, after two wrong guesses - see the
    /// comments in <see cref="ThornsAndTicksRestore"/> for what it was NOT):
    /// <code>
    /// AddThorn -> RPC_EnableThorn -> ThornOnMe.EnableThorn -> gameObject.SetActive(true)
    ///   -> Unity Start() on the arrow's SFX_PlayOneShot component
    ///     -> playOnStart == true -> Play() -> PlayOneShot() -> SFX_Player.PlaySFX
    ///        ("SFXI Arrow Hit 1/2/3" - one component plays its `sfx` plus every entry
    ///         in its `sfxs[]` array, which is why ONE arrow made three sounds)
    /// </code>
    /// The sound is a <c>SFX_PlayOneShot</c> COMPONENT, not an <c>AudioSource</c> - which
    /// is why sweeping the arrow objects for AudioSources found nothing.
    ///
    /// WHY A PATCH RATHER THAN TOGGLING <c>playOnStart</c>: Unity does not run
    /// <c>Start()</c> during <c>SetActive</c>, it runs it later, before the next Update.
    /// Clearing the flag and putting it back around the re-attach loop would therefore put
    /// it back BEFORE Start ever ran, and the sound would play anyway. Leaving the flag
    /// off instead would silently change the object for the rest of the run. A prefix that
    /// declines the call for a moment needs no game state changed and expires by itself.
    ///
    /// Scope is deliberately tight: only components on (or under) the specific thorn
    /// objects being restored, and only for a couple of seconds. Anything else that wants
    /// to play a one-shot during a load - including a genuine arrow hit on a DIFFERENT
    /// body part - is untouched
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
                // PlayOneShot rather than Play: Play is a one-line forwarder to it, but
                // other callers reach PlayOneShot directly, and the Start/OnEnable paths
                // both funnel through it
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
                    // IsChildOf is true for the transform itself as well, so this covers
                    // the component sitting directly on the arrow or on a child of it
                    if (root != null && t.IsChildOf(root)) return false;
                }
            }
            catch { /* never let a diagnostic-shaped guard break the game's audio */ }
            return true;
        }

        /// <summary>
        /// Silences the given thorn objects for a moment, covering the Unity callback that
        /// fires after they are switched back on. <paramref name="seconds"/> only has to
        /// outlast the next frame - <c>Start</c> runs before the following Update, and
        /// <c>playOnEnable</c>'s coroutine waits a single end-of-frame - but a small margin
        /// costs nothing because the filter is per-object
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
