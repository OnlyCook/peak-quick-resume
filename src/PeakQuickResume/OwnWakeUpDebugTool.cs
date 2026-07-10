using System;
using System.Collections;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// TEMPORARY debug aid (remove once the wake-up beat is confirmed working end to end):
    /// press F10 anytime in a level to run the SAME sequence/ordering
    /// <see cref="OwnTeleportSequence"/> uses for a real resume - fade to the loading screen
    /// first, simulate the hidden teleport work with a plain wait, then collapse into the
    /// passed-out pose, fade the loading screen back out (revealing the player already lying
    /// down), and finally wake them back up - completely outside of a real Quick Resume
    /// teleport, so it can be observed/tuned without the rest of the sequence's timing (segment
    /// jump, warps, ReviveDeadPlayers, watchdog, etc.) as a confounding factor
    ///
    /// Also logs <c>CharacterData.passedOut</c>/<c>fullyPassedOut</c>/<c>currentRagdollControll</c>
    /// every ~0.2s for the duration of the beat, so a session log shows whether the values we set
    /// actually stick from frame to frame or get reverted by something else
    /// </summary>
    public class OwnWakeUpDebugTool : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;
        private OwnWakeUpEffect _wakeUpEffect;
        private OwnLoadingScreen _loadingScreen;
        private bool _running;

        public void Init(ManualLogSource log, PluginConfig cfg, OwnWakeUpEffect wakeUpEffect, OwnLoadingScreen loadingScreen)
        {
            _log = log;
            _cfg = cfg;
            _wakeUpEffect = wakeUpEffect;
            _loadingScreen = loadingScreen;
        }

        private void Update()
        {
            if (_running) return;
            if (Input.GetKeyDown(KeyCode.F10))
            {
                _log?.LogInfo("[F10 DEBUG] Manually triggering the loading-screen + wake-up presentation in isolation (mirrors the real resume ordering).");
                StartCoroutine(RunDebugSequence());
            }
        }

        private IEnumerator RunDebugSequence()
        {
            _running = true;

            Character character = null;
            try { character = Character.localCharacter; }
            catch (Exception e) { _log?.LogWarning($"[F10 DEBUG] Character.localCharacter threw: {e.Message}"); }

            _log?.LogInfo($"[F10 DEBUG] Character.localCharacter = {(character == null ? "NULL" : character.name)}");
            if (character != null) LogState(character, "before");

            bool showLoadingScreen = !_cfg.DebugDisableLoadingScreen.Value;
            if (!showLoadingScreen)
                _log?.LogInfo("[F10 DEBUG] disable-loading-screen is on; skipping the overlay.");
            else if (_loadingScreen != null)
                yield return _loadingScreen.FadeIn(_cfg.OwnLoadingScreenFadeTime.Value);
            else
                _log?.LogWarning("[F10 DEBUG] _loadingScreen is null - was Init() ever called?");

            _log?.LogInfo("[F10 DEBUG] Simulating hidden teleport work (1.5s wait)...");
            yield return new WaitForSeconds(1.5f);

            if (_wakeUpEffect != null)
                _wakeUpEffect.Collapse();
            else
                _log?.LogWarning("[F10 DEBUG] _wakeUpEffect is null - was Init() ever called?");

            _log?.LogInfo($"[F10 DEBUG] Holding behind the still-opaque loading screen for {_cfg.OwnWakeUpSettleHoldTime.Value:F1}s to let the collapse settle...");
            yield return new WaitForSeconds(Mathf.Max(0f, _cfg.OwnWakeUpSettleHoldTime.Value));

            if (showLoadingScreen && _loadingScreen != null)
                yield return _loadingScreen.FadeOut(_cfg.OwnLoadingScreenFadeTime.Value);

            Coroutine watcher = character != null ? StartCoroutine(WatchState(character)) : null;

            if (_wakeUpEffect != null)
                yield return _wakeUpEffect.Wake(_cfg.OwnWakeUpStandTime.Value);

            if (watcher != null) StopCoroutine(watcher);
            if (character != null) LogState(character, "after wake-up");

            _log?.LogInfo("[F10 DEBUG] Sequence complete.");
            _running = false;
        }

        private IEnumerator WatchState(Character character)
        {
            while (true)
            {
                LogState(character, "tick");
                yield return new WaitForSeconds(0.2f);
            }
        }

        private void LogState(Character character, string label)
        {
            try
            {
                _log?.LogInfo($"[F10 DEBUG] ({label}) passedOut={character.data.passedOut} "
                    + $"fullyPassedOut={character.data.fullyPassedOut} "
                    + $"currentRagdollControll={character.data.currentRagdollControll:F2} "
                    + $"warping={character.warping} "
                    + $"headY={character.Head.y:F2}");
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[F10 DEBUG] LogState failed: {e.Message}");
            }
        }
    }
}
