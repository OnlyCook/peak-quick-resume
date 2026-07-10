using System;
using System.Collections;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using Zorro.Core;

namespace PEAKQuickResume
{
    /// <summary>
    /// Crossfades the game's own real "LOADING..." screen (the same
    /// <c>LoadingScreenHandler</c>/<c>LoadingScreen</c> "Basic" prefab used for actual scene
    /// loads, via <see cref="RunLauncher"/>) in and out around a Quick Resume teleport, instead
    /// of the checkpoint mod's abrupt instant on/off overlay
    ///
    /// Deliberately bypasses <c>LoadingScreenHandler.Load</c>/<c>LoadingScreen.LoadingRoutine</c>
    /// entirely (those drive Photon scene-load machinery we don't want - message-queue toggling,
    /// <c>PhotonNetwork.LoadLevel</c>, character-spawn waits). Instead we pull the prefab
    /// reference directly off the handler and instantiate/drive/destroy it ourselves: flip its
    /// <c>Canvas.enabled</c> on, crossfade its own <c>CanvasGroup.alpha</c> for the fade (matching
    /// how the game's own <c>Transition_CanvasGroup</c> fades - unscaled time, so a paused/slowed
    /// timescale can't stall it), and destroy it on our own schedule rather than the prefab's
    /// default 6-second self-destruct
    ///
    /// The dot-cycling text is normally entirely self-driving (<c>LoadingScreenAnimationSimple</c>
    /// on the prefab writes "LOADING" + dots to its own TMP_Text field every second, pulling the
    /// base word from the game's OWN localization). We want "LOADING SAVE" instead, and that base
    /// word is hardcoded inside that component's private coroutine, not something we can override
    /// via a public field - so on FadeIn we stop its coroutine, disable the component, and drive
    /// the SAME TMP_Text field ourselves with our own localized base text (see
    /// <see cref="MsgKey.LoadingSaveScreen"/>) at the same cadence
    /// </summary>
    public class OwnLoadingScreen : MonoBehaviour
    {
        // Above OwnMessageOverlay's 31000, so status text never shows through the black screen
        private const int SortingOrder = 32000;

        private ManualLogSource _log;
        private LoadingScreen _screen;
        private Coroutine _textCoroutine;

        public void Init(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>Instantiates the native Basic loading-screen prefab and crossfades it in from transparent</summary>
        public IEnumerator FadeIn(float duration)
        {
            if (_screen != null)
            {
                _log?.LogWarning("OwnLoadingScreen.FadeIn: a loading screen is already active; skipping duplicate instantiate.");
            }
            else
            {
                try
                {
                    LoadingScreenHandler handler = RetrievableResourceSingleton<LoadingScreenHandler>.Instance;
                    LoadingScreen prefab = handler?.GetLoadingScreenPrefab(LoadingScreen.LoadingScreenType.Basic);
                    if (prefab == null)
                    {
                        _log?.LogWarning("OwnLoadingScreen.FadeIn: could not resolve the native Basic loading-screen prefab; skipping.");
                    }
                    else
                    {
                        _screen = UnityEngine.Object.Instantiate(prefab);
                        _screen.canvas.enabled = true;
                        _screen.canvas.sortingOrder = SortingOrder;

                        // The prefab's own Animator (private field on LoadingScreen, only driven
                        // via the "Finish" trigger LoadingRoutine fires - which we never call)
                        // otherwise keeps evaluating its default/idle state every frame, which
                        // appears to hold CanvasGroup.alpha pinned at 1 - fighting our own script-
                        // driven lerp below so only the hard canvas.enabled toggle and the final
                        // Destroy() ever actually showed up as a visible change (session-reported:
                        // the loading screen "just disappears" instead of crossfading). Disabling
                        // it hands full control of the CanvasGroup to us
                        Animator anim = _screen.GetComponent<Animator>();
                        if (anim != null) anim.enabled = false;

                        if (_screen.group != null) _screen.group.alpha = 0f;
                        TakeOverLoadingText();
                        _log?.LogInfo("OwnLoadingScreen: native Basic loading screen instantiated, fading in.");
                    }
                }
                catch (Exception e)
                {
                    _log?.LogWarning($"OwnLoadingScreen.FadeIn: failed to instantiate the native loading screen (non-fatal): {e.Message}");
                    _screen = null;
                }
            }

            yield return Crossfade(0f, 1f, duration);
        }

        /// <summary>Crossfades the loading screen out to transparent, then destroys it</summary>
        public IEnumerator FadeOut(float duration)
        {
            yield return Crossfade(1f, 0f, duration);

            if (_textCoroutine != null)
            {
                StopCoroutine(_textCoroutine);
                _textCoroutine = null;
            }

            if (_screen != null)
            {
                try { UnityEngine.Object.Destroy(_screen.gameObject); }
                catch (Exception e) { _log?.LogWarning($"OwnLoadingScreen.FadeOut: failed to destroy the loading screen (non-fatal): {e.Message}"); }
                _screen = null;
                _log?.LogInfo("OwnLoadingScreen: faded out and destroyed.");
            }
        }

        private void TakeOverLoadingText()
        {
            try
            {
                LoadingScreenAnimationSimple anim = _screen.GetComponent<LoadingScreenAnimationSimple>();
                if (anim == null || anim.loading == null) return;

                anim.StopAllCoroutines();
                anim.enabled = false;

                float yieldTime = anim.yieldTime > 0f ? anim.yieldTime : 1f;
                _textCoroutine = StartCoroutine(DriveLoadingText(anim.loading, yieldTime));
            }
            catch (Exception e)
            {
                _log?.LogWarning($"OwnLoadingScreen.TakeOverLoadingText failed (non-fatal, native \"LOADING\" text stays): {e.Message}");
            }
        }

        private IEnumerator DriveLoadingText(TMP_Text text, float yieldTime)
        {
            string baseText = MessagesLocalization.Get(MsgKey.LoadingSaveScreen);
            string[] frames = { baseText, baseText + ".", baseText + "..", baseText + "..." };
            int i = 0;
            while (true)
            {
                text.text = frames[i % frames.Length];
                i++;
                yield return new WaitForSeconds(yieldTime);
            }
        }

        private IEnumerator Crossfade(float from, float to, float duration)
        {
            if (_screen == null || _screen.group == null) yield break;

            CanvasGroup group = _screen.group;
            duration = Mathf.Max(0f, duration);

            if (duration <= 0f)
            {
                group.alpha = to;
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                group.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
                yield return null;
            }

            group.alpha = to;
        }
    }
}
